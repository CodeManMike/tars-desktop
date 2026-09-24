"""Local speech-to-text for the TARS desktop client.

The client streams 16 kHz mono Int16 frames over a local WebSocket (/stt/stream). Silero VAD (streaming, CPU) cuts
utterances; faster-whisper transcribes them (large-v3-turbo on the GPU, or a small CPU model in game mode).

Client -> sidecar (text JSON):
  {"type":"config","model":"large-v3-turbo","device":"cuda"|"cpu","hotwords":"TARS",
   "speaker":"<voiceprint .npy>"|"","speaker_threshold":0.67}
  {"type":"ptt","state":"start"|"end"}     push-to-talk bracket: everything in between is one utterance
  {"type":"reset"}                         drop any partial utterance (e.g. TARS started speaking)
Client -> sidecar (binary): PCM16 LE, 16 kHz, mono, any frame size.

Sidecar -> client (text JSON):
  {"type":"vad","speech":true|false}
  {"type":"transcribing"}
  {"type":"utterance","text":..,"source":"vad"|"ptt","speech_ms":..,"duration_ms":..,"stt_ms":..,"logprob":..,
   "no_speech_prob":..,"model":..,"speaker_sim":..}
  {"type":"rejected","reason":..,"text":..}

Voice lock: with a voiceprint configured, hands-free (VAD) utterances of 2 s or more from anyone else (YouTube, TV,
people in the room) are dropped before Whisper runs. PTT is never checked: holding the key already says it's you, so
PTT speech refines the voiceprint instead. POST /stt/enroll {"wav":path,"out":path} builds the voiceprint.

Privacy: transcripts are logged at DEBUG only; INFO lines carry timings and scores, never what was said.
"""
from __future__ import annotations

import asyncio
import json
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
HANGOVER_MS = 900               # silence that ends an utterance (natural mid-sentence pauses survive)
MAX_UTTERANCE_S = 25
LOCK_MIN_MS = 2000              # voiceprints are unreliable on shorter clips (a lone "TARS" scores like a stranger)
ADAPT_MIN_MS = 2000             # PTT speech this long refines the voiceprint (PTT is always the owner)
ADAPT_RATE = 0.1
NAME_ONLY = re.compile(r"^\W*(hey |ok |okay |yo )?(tars|tarz|tarss)\W*$", re.I)
GATE_FLOOR_MIN = 10 ** (-62 / 20)   # adaptive gate: skip Silero while the level is within ~6 dB of the noise floor
GATE_OVER_FLOOR = 2.0                # +6 dB

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
        """Speech probability for one 512-sample chunk."""
        x = np.concatenate([self.ctx, chunk])[None, :].astype(np.float32)
        out, self.h, self.c = self.session.run(None, {"input": x, "h": self.h, "c": self.c})
        self.ctx = chunk[-64:]
        return float(np.asarray(out).reshape(-1)[0])


class SpeakerLock:
    """Speaker verification with Chatterbox's voice encoder (CPU, ~0.1 s per check)."""

    def __init__(self):
        self.ve = None
        self.lock = threading.Lock()
        self.profiles: dict[str, np.ndarray] = {}

    def _encoder(self):
        if self.ve is None:
            from huggingface_hub import hf_hub_download
            from safetensors.torch import load_file
            from chatterbox.models.voice_encoder import VoiceEncoder
            ve = VoiceEncoder()
            ve.load_state_dict(load_file(hf_hub_download("ResembleAI/chatterbox", "ve.safetensors")))
            ve.eval()
            self.ve = ve
        return self.ve

    def embed(self, audio16k: np.ndarray) -> np.ndarray:
        """A unit-length speaker embedding for 16 kHz audio."""
        with self.lock:
            return self._encoder().embeds_from_wavs([audio16k], RATE, as_spk=True).reshape(-1)

    def enroll(self, wav_path: str, out_path: str) -> dict:
        """Build a voiceprint from the voiced chunks of a recording; ValueError with under 8 s of speech."""
        import librosa
        import soundfile as sf
        a, sr = sf.read(wav_path, dtype="float32")
        if a.ndim > 1:
            a = a.mean(axis=1)
        a = librosa.resample(a, orig_sr=sr, target_sr=RATE)
        # Only real speech makes a voiceprint: silence or TV in the room would produce one that rejects the owner.
        vad = StreamVad()
        voiced = [a[i:i + CHUNK] for i in range(0, len(a) - CHUNK + 1, CHUNK) if vad.prob(a[i:i + CHUNK]) >= START_PROB]
        voiced_s = len(voiced) * CHUNK / RATE
        if voiced_s < 8:
            raise ValueError(f"only {voiced_s:.1f} s of speech heard (need 8+): speak for the whole 20 s, closer to the mic")
        e = self.embed(np.concatenate(voiced))
        np.save(out_path, e)
        self.profiles.pop(out_path, None)
        return {"ok": True, "seconds": round(len(a) / RATE, 1), "speech_s": round(voiced_s, 1), "out": out_path}

    def adapt(self, profile_path: str, audio16k: np.ndarray):
        """Blend a known-owner clip (PTT) into the voiceprint, so it learns the owner's everyday voice."""
        prof = self.profiles.get(profile_path)
        if prof is None:
            prof = np.load(profile_path)
        e = self.embed(audio16k)
        new = (1 - ADAPT_RATE) * prof + ADAPT_RATE * e
        new = new / np.linalg.norm(new)
        np.save(profile_path, new)
        self.profiles[profile_path] = new

    def similarity(self, profile_path: str, audio16k: np.ndarray) -> float:
        """Cosine similarity between the voiceprint and this clip."""
        prof = self.profiles.get(profile_path)
        if prof is None:
            prof = np.load(profile_path)
            self.profiles[profile_path] = prof
        return float(np.dot(prof, self.embed(audio16k)))


SPEAKERS = SpeakerLock()


class Transcriber:
    """faster-whisper, loaded lazily, reloaded when the model/device changes. One transcription at a time."""

    def __init__(self):
        self.model = None
        self.key = None
        self.lock = threading.Lock()
        self.model_name = "large-v3-turbo"
        self.device = "cuda"
        # "TARS" hint: without it a short "TARS" comes out as "Charles". The cost is the name hallucinated on
        # coughs, so a name-only transcript must be clear speech (mirrors the server's name-only guard).
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
        """Transcribe one utterance, dropping low-confidence segments; returns the text and the scores we gate on."""
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
        self.floor = GATE_FLOOR_MIN * 4
        self.skipped: list[np.ndarray] = []
        self.speaker = ""
        self.speaker_threshold = 0.67

    async def send(self, obj: dict):
        """Send to the client. If it has gone, the receive loop ends the session, so we drop the message."""
        try:
            await self.ws.send_json(obj)
        except (RuntimeError, WebSocketDisconnect):
            pass

    def reset(self):
        """Forget any partial utterance and the VAD state."""
        self.vad.reset()
        self.pending = np.zeros(0, dtype=np.float32)
        self.preroll.clear()
        self.speech.clear()
        self.in_speech = False
        self.voiced_chunks = self.silence_chunks = 0

    async def on_control(self, msg: dict):
        """Handle a config, ptt or reset message."""
        t = msg.get("type")
        if t == "config":
            self.tx.configure(msg.get("model"), msg.get("device"), msg.get("hotwords"))
            if "speaker" in msg:
                self.speaker = msg.get("speaker") or ""
            if msg.get("speaker_threshold"):
                self.speaker_threshold = float(msg["speaker_threshold"])
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
        """Load (or swap) the Whisper model ahead of the first utterance."""
        try:
            with self.tx.lock:
                self.tx._ensure()
        except Exception:
            log.exception("stt: load failed")

    async def on_audio(self, data: bytes):
        """Feed PCM through the gate and VAD; an utterance ends after HANGOVER_MS of silence or MAX_UTTERANCE_S."""
        samples = np.frombuffer(data, dtype="<i2").astype(np.float32) / 32768.0
        self.pending = np.concatenate([self.pending, samples])
        while len(self.pending) >= CHUNK:
            chunk, self.pending = self.pending[:CHUNK], self.pending[CHUNK:]
            # Adaptive gate: while the level sits near the room's noise floor, skip Silero (idle CPU). When it rises,
            # first run the skipped pre-roll through Silero so its state is warm and soft onsets ("h" of "hey") count.
            rms = float(np.sqrt(np.mean(chunk * chunk)))
            if not self.ptt and not self.in_speech:
                gate = max(GATE_FLOOR_MIN, self.floor * GATE_OVER_FLOOR)
                if rms < gate:
                    self.floor = 0.98 * self.floor + 0.02 * rms       # ~1.5 s time constant
                    self.preroll.append(chunk)
                    if len(self.preroll) > PREROLL_CHUNKS:
                        self.preroll.pop(0)
                    self.skipped.append(chunk)
                    if len(self.skipped) > PREROLL_CHUNKS:
                        self.skipped.pop(0)
                    continue
                for c in self.skipped:          # warm Silero on what the gate skipped
                    self.vad.prob(c)
                self.skipped.clear()
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
        """Check, transcribe and filter one utterance, then send it (or why we dropped it) to the client."""
        audio = np.concatenate(self.speech) if self.speech else np.zeros(0, dtype=np.float32)
        voiced_ms = round(self.voiced_chunks * CHUNK * 1000 / RATE)
        self.speech = []
        self.in_speech = False
        self.voiced_chunks = self.silence_chunks = 0
        self.vad.reset()
        if voiced_ms < MIN_SPEECH_MS or len(audio) * 1000 / RATE < MIN_CLIP_MS:
            await self.send({"type": "rejected", "reason": f"no speech ({voiced_ms} ms voiced)", "text": ""})
            return
        duration_ms = round(len(audio) * 1000 / RATE)
        sim = None
        if source == "ptt" and self.speaker and voiced_ms >= ADAPT_MIN_MS:
            asyncio.get_running_loop().run_in_executor(None, SPEAKERS.adapt, self.speaker, audio)
        if source == "vad" and self.speaker and voiced_ms >= LOCK_MIN_MS:
            try:
                sim = await asyncio.get_running_loop().run_in_executor(None, SPEAKERS.similarity, self.speaker, audio)
            except Exception as e:
                log.warning("voice lock: %s (letting the utterance through)", e)
            if sim is not None and sim < self.speaker_threshold:
                log.info("stt: rejected, not your voice (%.2f), %d ms", sim, voiced_ms)
                await self.send({"type": "rejected", "reason": f"not your voice ({sim:.2f})", "text": "", "speaker_sim": round(sim, 3)})
                return
        await self.send({"type": "transcribing"})
        try:
            r = await asyncio.get_running_loop().run_in_executor(None, self.tx.transcribe, audio)
        except Exception as e:
            log.exception("stt failed")
            await self.send({"type": "rejected", "reason": f"stt error: {e}", "text": ""})
            return
        text = r["text"]
        if NAME_ONLY.match(text) and (voiced_ms < 300 or r["logprob"] <= -0.6 or r["no_speech_prob"] >= 0.4):
            log.info("stt: rejected unclear name-only (%d ms, lp %.2f)", voiced_ms, r["logprob"])
            log.debug("stt: name-only text %r", text)
            await self.send({"type": "rejected", "reason": "name only, too unclear", "text": text, **r})
            return
        if not text or HALLUCINATIONS.match(text.strip()):
            log.info("stt: rejected as noise (%d ms, lp %.2f)", voiced_ms, r["logprob"])
            log.debug("stt: noise text %r", text)
            await self.send({"type": "rejected", "reason": "noise", "text": text, **r})
            return
        log.info("stt: %s %d ms (%s ms stt, lp %.2f%s)", source, voiced_ms, r["stt_ms"], r["logprob"],
                 "" if sim is None else f", voice {sim:.2f}")
        log.debug("stt: text %r", text)
        await self.send({"type": "utterance", "text": text, "source": source, "speech_ms": voiced_ms,
                         "duration_ms": duration_ms,
                         "speaker_sim": None if sim is None else round(sim, 3), **r})


def mount(app, transcriber: Transcriber):
    """Add the STT routes to the sidecar's FastAPI app."""

    @app.websocket("/stt/stream")
    async def stt_stream(ws: WebSocket):
        """One client stream: binary frames are audio, text frames are control messages."""
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
                    try:
                        await s.on_control(json.loads(m["text"]))
                    except ValueError:
                        pass
        except WebSocketDisconnect:
            pass

    @app.post("/stt/enroll")
    async def stt_enroll(body: dict):
        """Build a voiceprint from a recording: {"wav": path, "out": path.npy}."""
        from fastapi.responses import JSONResponse
        try:
            return await asyncio.to_thread(SPEAKERS.enroll, body["wav"], body["out"])
        except ValueError as e:
            return JSONResponse({"ok": False, "error": str(e)}, status_code=400)

    @app.post("/stt/unload")
    async def stt_unload():
        """Free the Whisper model."""
        await asyncio.to_thread(transcriber.unload)
        return {"ok": True}
