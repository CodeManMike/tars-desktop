# TARS Desktop

A native Windows client for **TARS**, the home assistant running on `cmm-media`. It's a small always-on-top
90s terminal (orange phosphor on black, VT323, scanlines, square corners, no Windows chrome) that lives in the tray,
listens hands-free or on a global push-to-talk key, and **generates TARS's voice locally on the GPU**.

Spec: [`docs/desktop-client-spec.md`](docs/desktop-client-spec.md) (access key redacted; the client fetches it from
the server itself and seals it with DPAPI).

## Install

Run `TARS-Setup-<version>.exe` (per-user, no admin). It installs to `%LOCALAPPDATA%\Programs\TARS`, adds a Start menu
entry, optionally starts with Windows, and optionally installs the local voice engine (~4.5 GB, once).
Uninstall from *Apps & Features*.

## Build

```powershell
dotnet build TarsDesktop.slnx          # client, setup and tests (warnings are errors)
dotnet test TarsDesktop.slnx           # NUnit: golden-JSON protocol tests, parsers, audio helpers
pwsh build/build-installer.ps1         # artifacts/TARS-Setup-<version>.exe
pwsh tts-sidecar/install.ps1           # the voice venv at %LOCALAPPDATA%\TARS\tts-venv
```

Requires the .NET 10 SDK; the voice engine needs Python 3.11 (or `uv`) and an NVIDIA GPU.

## Layout

| Path | What |
|---|---|
| `src/TarsClient` | WPF client (.NET 10): window, tray, protocol, audio, hotkeys, settings, admin screens |
| `src/TarsSetup` | the retro installer / uninstaller (embeds the published client) |
| `tests/TarsClient.Tests` | NUnit tests |
| `tts-sidecar` | local voice and speech-to-text: FastAPI; `/v1/audio/speech` (Chatterbox Turbo / Chatterbox on the GPU, Kokoro on the CPU) and `/stt/stream` (Silero VAD + faster-whisper) |
| `build` | installer build script, local deploy script, icon generator |

Contributing (people or agents): read [`AGENTS.md`](AGENTS.md) first.

## Voice

All speech is generated on this PC. The client declares `tts:"local"`, the server sends text (`say`), and the sidecar
voices it:

- **turbo** (default): Chatterbox Turbo on the GPU, ~3 GB VRAM, ~0.3–1 s per sentence.
- **kokoro**: CPU. Used automatically in game mode (fullscreen app or <3 GB free VRAM) and while the GPU model warms.
- The Windows voice is the last resort if the sidecar is down, so a reply is never lost.
- **TARS FX** (client side): slight pitch/pace drop, band-limited "speaker in a chassis", faint metallic ring.
- The default reference voice is an original deep synthetic voice rendered locally by Kokoro. Swap in a 15–20 s clip
  of your own dry, deadpan reading under **SET → VOICE → REFERENCE**. Don't clone real actors from film audio.

The GPU model unloads after 10 idle minutes (configurable) and reloads on the next reply.

## Files at runtime

| Path | |
|---|---|
| `%APPDATA%\TARS\client.json` | settings (access key DPAPI-sealed) |
| `%APPDATA%\TARS\client.log`, `tts.log` | logs |
| `%APPDATA%\TARS\voices\tars-ref.wav` | default reference clip |
| `%LOCALAPPDATA%\TARS\tts-venv` | voice engine venv |

## Keys

| | |
|---|---|
| Right Ctrl (global, configurable; keyboard keys only) | hold to talk |
| Space (in the window) | hold to talk |
| Ctrl+Alt+S (global) | stop speech / dismiss alarm |
| F2 or `[SET]` | settings (Ctrl+←/→ switch tabs) |
| Esc | stop / dismiss / close |
| double-click title | compact ↔ expanded |
