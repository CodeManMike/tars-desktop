"""TARS local voice sidecar.

An OpenAI-compatible speech endpoint (POST /v1/audio/speech) backed by Chatterbox on the local GPU.
The desktop client starts this process, health-checks it, and falls back to the Windows voice when it's down.

Engines:
  turbo      ResembleAI/chatterbox-turbo  GPU. fast, natural; clones the reference clip; ignores exaggeration / cfg
  chatterbox ResembleAI/chatterbox        GPU. slower; supports exaggeration and cfg ("pace")
  kokoro     hexgrad/Kokoro-82M           CPU. light: game mode and fallback; also renders the default reference clip
All voice generation is local: nothing here talks to the TARS server.

Speech-to-text (stt.py): /stt/stream WebSocket, Silero VAD + faster-whisper (large-v3-turbo on the GPU).

Audio: 24 kHz mono. response_format "pcm" streams raw Int16 LE sentence by sentence (header X-Sample-Rate);
"wav" returns one complete file.
"""
from __future__ import annotations

import argparse
import asyncio
import io
import logging
import os
import re
import struct
import sys
import threading
import time
from pathlib import Path

os.environ.setdefault("TQDM_DISABLE", "1")

import numpy as np
import uvicorn
from fastapi import FastAPI, HTTPException
from fastapi.responses import JSONResponse, Response, StreamingResponse
from pydantic import BaseModel

SAMPLE_RATE = 24000
ENGINES = ("turbo", "chatterbox", "kokoro")
GPU_ENGINES = ("turbo", "chatterbox")
KOKORO_VOICE = "am_fenrir:0.6,am_michael:0.4"   # deep, calm male blend

log = logging.getLogger("tars-tts")


class SpeechRequest(BaseModel):
    model: str | None = None            # engine name; "tts-1" etc. map to the default engine
    input: str
    voice: str | None = None            # path to a reference clip, or "default"
    response_format: str = "pcm"
    speed: float = 1.0
    exaggeration: float = 0.35
    pace: float = 0.35                  # cfg_weight for the original model
    temperature: float = 0.7
    kokoro_voice: str | None = None     # e.g. "am_fenrir:0.6,am_michael:0.4"


class Engines:
    """Loads engines lazily, keeps them resident, and serialises GPU work."""

    def __init__(self, default: str, device: str, default_ref: str | None):
        self.default = default
        self.device = device
        self.default_ref = default_ref
        self.models: dict[str, object] = {}
        self.conds_key: dict[str, tuple] = {}
        self.gpu = threading.Lock()
        self.loading: set[str] = set()
        self.last_use = time.time()
        self._kokoro_lock = threading.Lock()

    def resolve(self, name: str | None) -> str:
        """An engine name, or the default for anything else ("tts-1" etc.)."""
        return name if name in ENGINES else self.default

    def load(self, name: str):
        """Load an engine unless it is resident. Callers hold the GPU lock for GPU engines."""
        if name in self.models:
            return self.models[name]
        if name in GPU_ENGINES:
            # One GPU engine at a time: switching turbo <-> chatterbox frees the other's VRAM first.
            for other in [e for e in self.models if e in GPU_ENGINES]:
                del self.models[other]
                self.conds_key.pop(other, None)
            try:
                import torch
                if torch.cuda.is_available():
                    torch.cuda.empty_cache()
            except Exception:
                pass
        self.loading.add(name)
        try:
            t0 = time.time()
            if name == "kokoro":
                import torch
                from kokoro import KPipeline
                torch.set_num_threads(min(4, os.cpu_count() or 4))   # stay polite while a game runs
                model = KPipeline(lang_code="a", device="cpu", repo_id="hexgrad/Kokoro-82M")
            elif name == "turbo":
                from chatterbox.tts_turbo import ChatterboxTurboTTS
                model = ChatterboxTurboTTS.from_pretrained(device=self.device)
            else:
                from chatterbox.tts import ChatterboxTTS
                model = ChatterboxTTS.from_pretrained(device=self.device)
            self.models[name] = model
            log.info("loaded %s on %s in %.1f s", name, "cpu" if name == "kokoro" else self.device, time.time() - t0)
            return model
        finally:
            self.loading.discard(name)

    def unload(self, gpu_only: bool = True):
        """Free the GPU engines (or everything) and hand the VRAM back."""
        with self.gpu:
            for name in list(self.models):
                if name in GPU_ENGINES or not gpu_only:
                    del self.models[name]
                    self.conds_key.pop(name, None)
            try:
                import torch
                if torch.cuda.is_available():
                    torch.cuda.empty_cache()
            except Exception:
                pass
        log.info("models unloaded")

    def synth(self, req: SpeechRequest) -> np.ndarray:
        """Render one chunk of text as 24 kHz float audio."""
        name = self.resolve(req.model)
        if name == "kokoro":
            return self.kokoro(req.input, req.kokoro_voice or KOKORO_VOICE, req.speed)
        with self.gpu:
            model = self.load(name)
            ref = req.voice if req.voice and req.voice != "default" else self.default_ref
            if ref and not Path(ref).is_file():
                log.warning("reference clip not found: %s (using the built-in voice)", ref)
                ref = None
            # prepare_conditionals is the slow part of a voice change: only redo it when the inputs change.
            key = (ref, round(req.exaggeration, 3))
            if ref and self.conds_key.get(name) != key:
                model.prepare_conditionals(ref, exaggeration=req.exaggeration)
                self.conds_key[name] = key
            kwargs = dict(temperature=req.temperature)
            if name == "chatterbox":
                kwargs.update(exaggeration=req.exaggeration, cfg_weight=req.pace)
            wav = model.generate(req.input, **kwargs)
            self.last_use = time.time()
        audio = wav.squeeze().detach().cpu().numpy().astype(np.float32)
        if abs(req.speed - 1.0) > 0.02:
            import librosa
            audio = librosa.effects.time_stretch(audio, rate=float(req.speed))
        return audio

    def kokoro(self, text: str, voice: str, speed: float = 1.0) -> np.ndarray:
        """Kokoro on the CPU with a blended voice ("name:weight,..."). Its own lock: it never waits behind GPU work."""
        with self._kokoro_lock:
            pipe = self.load("kokoro")
            pack = None
            for part in voice.split(","):
                vname, _, w = part.partition(":")
                v = pipe.load_voice(vname.strip()) * (float(w) if w else 1.0)
                pack = v if pack is None else pack + v
            audio = np.concatenate([r.audio.numpy() for r in pipe(text, voice=pack, speed=speed * 0.95)])
            self.last_use = time.time()
        return audio.astype(np.float32)


REFERENCE_TEXT = ("Systems nominal. I have checked the numbers twice, and they are still the numbers. "
                  "The weather tomorrow is fog, then more fog. I would rate that a four out of ten. "
                  "Your timer is set. I will tell you when it is done, and not a moment before. "
                  "That was a joke. You will know the next one by the same pause.")


def to_pcm16(audio: np.ndarray) -> bytes:
    """Float audio to Int16 LE bytes."""
    return (np.clip(audio, -1.0, 1.0) * 32767.0).astype("<i2").tobytes()


def to_wav(pcm: bytes) -> bytes:
    """Wrap 24 kHz mono PCM16 in a WAV header."""
    header = b"RIFF" + struct.pack("<I", 36 + len(pcm)) + b"WAVE"
    header += b"fmt " + struct.pack("<IHHIIHH", 16, 1, 1, SAMPLE_RATE, SAMPLE_RATE * 2, 2, 16)
    header += b"data" + struct.pack("<I", len(pcm))
    return header + pcm


_SENTENCE = re.compile(r"(?<=[.!?…])\s+(?=\S)")


def sentences(text: str) -> list[str]:
    """Split text into the chunks we voice one at a time."""
    parts = [p.strip() for p in _SENTENCE.split(text.strip()) if p.strip()] or [text.strip()]
    # First audio sooner: a long opening sentence is voiced clause first (the comma is a natural pause anyway).
    head = parts[0]
    cut = head.find(", ")
    if len(head) > 48 and 12 <= cut <= len(head) - 12:
        parts[0:1] = [head[:cut + 1], head[cut + 2:]]
    return parts


def build_app(engines: Engines, transcriber=None) -> FastAPI:
    """The HTTP API: health, speech, engine load/unload, reference rendering, and STT when enabled."""
    app = FastAPI(title="TARS voice sidecar")
    if transcriber is not None:
        import stt
        stt.mount(app, transcriber)

    @app.get("/health")
    def health():
        info = {"ok": True, "engine": engines.default, "engines": list(ENGINES),
                "loaded": sorted(engines.models), "loading": sorted(engines.loading),
                "device": engines.device, "sample_rate": SAMPLE_RATE,
                "idle_s": round(time.time() - engines.last_use),
                "stt": getattr(app.state, "transcriber", None) and app.state.transcriber.loaded}
        try:
            import torch
            if torch.cuda.is_available():
                info["vram_mb"] = round(torch.cuda.memory_allocated() / 2**20)
        except Exception:
            pass
        return info

    @app.get("/v1/models")
    def models():
        return {"object": "list", "data": [{"id": e, "object": "model", "owned_by": "local"} for e in ENGINES]}

    @app.post("/load")
    async def load(model: str | None = None):
        name = engines.resolve(model)
        def _load():
            with engines.gpu:
                engines.load(name)
        await asyncio.to_thread(_load)
        return {"ok": True, "loaded": sorted(engines.models)}

    @app.post("/unload")
    async def unload(all: bool = False):
        await asyncio.to_thread(engines.unload, not all)
        return {"ok": True, "loaded": sorted(engines.models)}

    @app.post("/make-reference")
    async def make_reference(path: str, voice: str = KOKORO_VOICE):
        """Renders a ~19 s calm, dry read with Kokoro: an original synthetic voice for Chatterbox to clone."""
        audio = await asyncio.to_thread(engines.kokoro, REFERENCE_TEXT, voice, 0.97)
        Path(path).parent.mkdir(parents=True, exist_ok=True)
        Path(path).write_bytes(to_wav(to_pcm16(audio)))
        return {"ok": True, "path": path, "seconds": round(len(audio) / SAMPLE_RATE, 1)}

    @app.post("/v1/audio/speech")
    async def speech(req: SpeechRequest):
        if not req.input.strip():
            raise HTTPException(400, "empty input")
        fmt = req.response_format.lower()
        if fmt not in ("pcm", "wav"):
            raise HTTPException(400, "response_format must be pcm or wav")
        chunks = sentences(req.input)

        if fmt == "wav":
            pcm = b"".join([to_pcm16(await asyncio.to_thread(engines.synth, req.model_copy(update={"input": c})))
                            for c in chunks])
            return Response(to_wav(pcm), media_type="audio/wav")

        async def stream():
            for c in chunks:
                audio = await asyncio.to_thread(engines.synth, req.model_copy(update={"input": c}))
                yield to_pcm16(audio)

        return StreamingResponse(stream(), media_type="audio/pcm",
                                 headers={"X-Sample-Rate": str(SAMPLE_RATE), "X-Channels": "1"})

    @app.exception_handler(Exception)
    async def on_error(_, exc: Exception):
        log.exception("request failed")
        return JSONResponse({"error": str(exc)}, status_code=500)

    return app


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=8880)
    ap.add_argument("--engine", default="turbo", choices=ENGINES)
    ap.add_argument("--ref", default=None, help="default reference clip (wav)")
    ap.add_argument("--device", default=None)
    ap.add_argument("--no-warm", action="store_true")
    ap.add_argument("--no-stt", action="store_true")
    args = ap.parse_args()

    logging.basicConfig(level=logging.INFO, stream=sys.stdout,
                        format="%(asctime)s %(name)s %(levelname)s %(message)s")
    import torch
    # Import the engines here, on the main thread: transformers' lazy loader isn't thread-safe,
    # and a warm-up thread importing it while uvicorn starts fails with "cannot import LlamaModel".
    import chatterbox.tts_turbo  # noqa: F401
    import chatterbox.tts  # noqa: F401
    device = args.device or ("cuda" if torch.cuda.is_available() else "cpu")
    engines = Engines(args.engine, device, args.ref)

    if not args.no_warm:
        # Warm the default engine in the background so /health answers immediately.
        def warm():
            try:
                engines.synth(SpeechRequest(input="Ready."))
            except Exception:
                log.exception("warm-up failed")
        threading.Thread(target=warm, daemon=True).start()

    transcriber = None
    if not args.no_stt:
        sys.path.insert(0, str(Path(__file__).parent))
        import stt
        transcriber = stt.Transcriber()
    app = build_app(engines, transcriber)
    app.state.transcriber = transcriber
    uvicorn.run(app, host=args.host, port=args.port, log_level="warning")


if __name__ == "__main__":
    main()
