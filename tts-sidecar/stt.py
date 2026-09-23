"""Local speech-to-text for the TARS desktop client.

The client streams 16 kHz mono Int16 frames over a local WebSocket (/stt/stream). Silero VAD (streaming, CPU) cuts
utterances; faster-whisper transcribes them (large-v3-turbo on the GPU, or a small CPU model in game mode).

Client -> sidecar (text JSON):
  {"type":"config","model":"large-v3-turbo","device":"cuda"|"cpu","hotwords":"TARS"}
  {"type":"ptt","state":"start"|"end"}     push-to-talk bracket: everything in between is one utterance
  {"type":"reset"}                         drop any partial utterance (e.g. TARS started speaking)
Client -> sidecar (binary): PCM16 LE, 16 kHz, mono, any frame size.

Sidecar -> client (text JSON):
  {"type":"vad","speech":true|false}
  {"type":"transcribing"}
  {"type":"utterance","text":...,"source":"vad"|"ptt","speech_ms":..,"stt_ms":..,"logprob":..,"no_speech_prob":..,"model":..}
  {"type":"rejected","reason":...,"text":...}
"""
from __future__ import annotations

import asyncio
import logging
import re
import threading
import time

import numpy as np
from fastapi import WebSocket, WebSocketDisconnect   # module level: annotations are strings here

log = logging.getLogger("tars-stt")

RATE = 16000
CHUNK = 512                     # Silero's native window at 16 kHz (32 ms)
PREROLL_CHUNKS = 10             # ~320 ms kept before speech onset
START_PROB, END_PROB = 0.5, 0.35
MIN_SPEECH_MS = 250             # Silero-voiced time
MIN_CLIP_MS = 300               # whole clip
HANGOVER_MS = 700               # silence that ends an utterance
MAX_UTTERANCE_S = 25
QUIET_RMS = 10 ** (-50 / 20)    # below this a chunk can't be speech: skip the VAD model (idle CPU)

# Whisper's favourite things to say to silence and noise.
HALLUCINATIONS = re.compile(
    r"^(thank you\.?|thanks for watching!?|thank you for watching\.?|please subscribe\.?|you\.?|bye\.?|"
    r"\.+|okay\.?|so\.?|uh\.?|um\.?|hmm\.?|\[.*\]|\(.*\))$", re.I)


class StreamVad:
    """Silero VAD over a continuous stream: carries LSTM state and 64-sample context between 512-sample chunks."""

    def __init__(self):
        from faster_whisper.vad import get_vad_model
        self.session = get_vad_model().session
        self.reset()

    def reset(self):
        self.h = np.zeros((1, 1, 128), dtype=np.float32)
        self.c = np.zeros((1, 1, 128), dtype=np.float32)
        self.ctx = np.zeros(64, dtype=np.float32)

    def prob(self, chunk: np.ndarray) -> float:
        x = np.concatenate([self.ctx, chunk])[None, :].astype(np.float32)
        out, self.h, self.c = self.session.run(None, {"input": x, "h": self.h, "c": self.c})
        self.ctx = chunk[-64:]
        return float(np.asarray(out).reshape(-1)[0])


class Transcriber:
    """faster-whisper, loaded lazily, reloaded when the model/device changes. One transcription at a time."""

    def __init__(self):
        self.model = None
        self.key = None
        self.lock = threading.Lock()
        self.model_name = "large-v3-turbo"
        self.device = "cuda"
        self.hotwords = "TARS"

    def configure(self, model: str | None, device: str | None, hotwords: str | None):
        if model:
            self.model_name = model
        if device:
            self.device = device
        if hotwords is not None:
            self.hotwords = hotwords

    def _ensure(self):
        key = (self.model_name, self.device)
        if self.model is not None and self.key == key:
            return
        from faster_whisper import WhisperModel
        t0 = time.time()
        self.model = None
        compute = "float16" if self.device == "cuda" else "int8"
        self.model = WhisperModel(self.model_name, device=self.device, compute_type=compute,
                                  cpu_threads=4 if self.device == "cpu" else 0)
        self.key = key
        log.info("stt: loaded %s on %s (%s) in %.1f s", self.model_name, self.device, compute, time.time() - t0)

    def unload(self):
        with self.lock:
            self.model = None
            self.key = None

    @property
    def loaded(self) -> str | None:
        return f"{self.key[0]}@{self.key[1]}" if self.key else None

    def transcribe(self, audio: np.ndarray) -> dict:
        with self.lock:
            self._ensure()
            t0 = time.time()
            segs, info = self.model.transcribe(
                audio, language="en", beam_size=5, vad_filter=False, condition_on_previous_text=False,
                without_timestamps=True, hotwords=self.hotwords or None)
            # Per-segment filter, mirroring the server's gate: drop low-confidence / repetitive segments.
            segs = [s for s in segs
                    if not ((s.no_speech_prob > 0.6 and s.avg_logprob < -0.5) or s.avg_logprob < -1.0
                            or s.compression_ratio > 2.4)]
            text = " ".join(s.text.strip() for s in segs).strip()
            logprob = float(np.mean([s.avg_logprob for s in segs])) if segs else -10.0
            no_speech = float(max((s.no_speech_prob for s in segs), default=1.0))
            return {"text": text, "logprob": round(logprob, 3), "no_speech_prob": round(no_speech, 3),
                    "stt_ms": round((time.time() - t0) * 1000), "model": self.model_name}


class Session:
    """One client stream: VAD state machine plus PTT brackets."""

    def __init__(self, ws, transcriber: Transcriber):
        self.ws = ws
        self.tx = transcriber
        self.vad = StreamVad()
        self.pending = np.zeros(0, dtype=np.float32)
        self.preroll: list[np.ndarray] = []
        self.speech: list[np.ndarray] = []
        self.in_speech = False
        self.voiced_chunks = 0
        self.silence_chunks = 0
        self.ptt = False

    async def send(self, obj: dict):
        try:
            await self.ws.send_json(obj)
        except Exception:
            pass

    def reset(self):
        self.vad.reset()
        self.pending = np.zeros(0, dtype=np.float32)
        self.preroll.clear()
        self.speech.clear()
        self.in_speech = False
        self.voiced_chunks = self.silence_chunks = 0

    async def on_control(self, msg: dict):
        t = msg.get("type")
        if t == "config":
            self.tx.configure(msg.get("model"), msg.get("device"), msg.get("hotwords"))
            # Load (or swap) now, off the event loop, so the first utterance isn't slow.
            asyncio.get_running_loop().run_in_executor(None, self._warm)
        elif t == "ptt":
            if msg.get("state") == "start":
                self.reset()
                self.ptt = True
                self.in_speech = True
            elif self.ptt:
                self.ptt = False
                await self.finish("ptt")
        elif t == "reset":
            if not self.ptt:
                self.reset()

    def _warm(self):
        try:
            with self.tx.lock:
                self.tx._ensure()
        except Exception:
            log.exception("stt: load failed")

    async def on_audio(self, data: bytes):
        samples = np.frombuffer(data, dtype="<i2").astype(np.float32) / 32768.0
        self.pending = np.concatenate([self.pending, samples])
        while len(self.pending) >= CHUNK:
            chunk, self.pending = self.pending[:CHUNK], self.pending[CHUNK:]
            # A quiet room is most of the day: don't run Silero on silence (unless mid-utterance, where
            # the model's state matters for the end-of-speech decision).
            if not self.ptt and not self.in_speech and float(np.sqrt(np.mean(chunk * chunk))) < QUIET_RMS:
                self.preroll.append(chunk)
                if len(self.preroll) > PREROLL_CHUNKS:
                    self.preroll.pop(0)
                continue
            p = self.vad.prob(chunk)
            if self.ptt:
                self.speech.append(chunk)
                if p >= START_PROB:
                    self.voiced_chunks += 1
                continue
            if not self.in_speech:
                self.preroll.append(chunk)
                if len(self.preroll) > PREROLL_CHUNKS:
                    self.preroll.pop(0)
                if p >= START_PROB:
                    self.in_speech = True
                    self.speech = list(self.preroll)
                    self.preroll.clear()
                    self.voiced_chunks = 1
                    self.silence_chunks = 0
                    await self.send({"type": "vad", "speech": True})
                continue
            self.speech.append(chunk)
            if p >= END_PROB:
                self.voiced_chunks += 1
                self.silence_chunks = 0
            else:
                self.silence_chunks += 1
            too_long = len(self.speech) * CHUNK / RATE > MAX_UTTERANCE_S
            if self.silence_chunks * CHUNK * 1000 / RATE >= HANGOVER_MS or too_long:
                await self.send({"type": "vad", "speech": False})
                await self.finish("vad")

    async def finish(self, source: str):
        audio = np.concatenate(self.speech) if self.speech else np.zeros(0, dtype=np.float32)
        voiced_ms = round(self.voiced_chunks * CHUNK * 1000 / RATE)
        self.speech = []
        self.in_speech = False
        self.voiced_chunks = self.silence_chunks = 0
        self.vad.reset()
        if voiced_ms < MIN_SPEECH_MS or len(audio) * 1000 / RATE < MIN_CLIP_MS:
            await self.send({"type": "rejected", "reason": f"no speech ({voiced_ms} ms voiced)", "text": ""})
            return
        await self.send({"type": "transcribing"})
        try:
            r = await asyncio.get_running_loop().run_in_executor(None, self.tx.transcribe, audio)
        except Exception as e:
            log.exception("stt failed")
            await self.send({"type": "rejected", "reason": f"stt error: {e}", "text": ""})
            return
        text = r["text"]
        if not text or HALLUCINATIONS.match(text.strip()):
            await self.send({"type": "rejected", "reason": "noise", "text": text, **r})
            return
        await self.send({"type": "utterance", "text": text, "source": source, "speech_ms": voiced_ms, **r})


def mount(app, transcriber: Transcriber):
    @app.websocket("/stt/stream")
    async def stt_stream(ws: WebSocket):
        await ws.accept()
        s = Session(ws, transcriber)
        try:
            while True:
                m = await ws.receive()
                if m.get("type") == "websocket.disconnect":
                    break
                if m.get("bytes") is not None:
                    await s.on_audio(m["bytes"])
                elif m.get("text") is not None:
                    import json
                    try:
                        await s.on_control(json.loads(m["text"]))
                    except ValueError:
                        pass
        except WebSocketDisconnect:
            pass

    @app.post("/stt/unload")
    async def stt_unload():
        await asyncio.to_thread(transcriber.unload)
        return {"ok": True}
