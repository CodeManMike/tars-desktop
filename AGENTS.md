# AGENTS.md

Instructions for AI coding agents (and people) working on TARS Desktop. This file is canonical; `CLAUDE.md` points
here. General standards live in `E:\CODING-STANDARDS.md`; this file only adds what is specific to this repo.

## What this is

A native WPF (.NET 10) client for the TARS home assistant server, plus a local Python sidecar that does all voice
generation (Chatterbox Turbo / Chatterbox on the GPU, Kokoro on the CPU) and speech-to-text (Silero VAD +
faster-whisper). See `README.md` for layout and `docs/desktop-client-spec.md` for the server protocol.

## Build, test, deploy

```powershell
dotnet build TarsDesktop.slnx -c Release       # warnings are errors; XML docs are required on public members
dotnet test TarsDesktop.slnx -c Release        # NUnit, incl. golden-JSON tests for every wire message
pwsh build/build-installer.ps1                 # artifacts/TARS-Setup-<version>.exe (+ TARS-Uninstall.exe)
```

Deploying to this PC from an agent session: **run the setup through Explorer**, never directly:

```powershell
Start-Process explorer.exe -ArgumentList '"E:\TARS Frontend\build\deploy-local.cmd"'
```

The Claude desktop app is an MSIX package; anything it starts has its `%APPDATA%`/`%LOCALAPPDATA%`/HKCU writes
virtualized into `%LOCALAPPDATA%\Packages\Claude_*\LocalCache`. A setup run from it "succeeds" but the real TARS then
can't see its Run key, Start menu entry, settings or voice venv. `deploy-local.cmd` writes `artifacts\deploy.done` when
it finishes. Don't use `Start-Process -Wait` on the setup: it also waits for the TARS it launches.

## Rules for this repo

- **The protocol is frozen.** `ClientMessages` (client → server) and `SidecarMessages` (client → sidecar) are the
  only places messages are built, and the golden-JSON tests pin them. Any wire change is agreed with the server's
  Claude session first (see the memory note on the server session), then the spec and the tests are updated together.
  Adding fields is allowed only by agreement; renaming or removing never is.
- **Secrets never enter the repo.** The access key is fetched from the server's spec at runtime and DPAPI-sealed in
  `%APPDATA%\TARS\client.json`; `docs/desktop-client-spec.md` keeps it redacted. The Groq key and Home Assistant token
  are not the client's business at all. TLS trusts the pinned home CA only; never add an "accept any certificate"
  path.
- **Voice stays local.** The client declares `tts:"local"`; the server sends text, never audio we play. Don't add a
  server-voice fallback; the Windows voice is the last resort.
- **Always-on, low resource.** Idle is ~1 % of a core. Timers and animations stop when the window is hidden; the
  sidecar's VAD sits behind an adaptive noise gate; GPU models unload when idle and in game mode. Measure before and
  after anything that runs continuously.
- **Privacy.** Transcripts are logged at DEBUG only in the sidecar and never at all in `client.log`.
- One public type per file; large view models are split into partial files by concern (`MainViewModel.Speech.cs`,
  `SettingsViewModel.Voice.cs`, ...).

## Python sidecar

`tts-sidecar/` runs from `%LOCALAPPDATA%\TARS\tts-venv` (Python 3.11 via `uv`, torch 2.6 cu124), created by
`install.ps1`. Known pins: `setuptools<81` (perth uses `pkg_resources`), `websockets` (uvicorn WebSocket support),
`en_core_web_sm` preinstalled. Import the Chatterbox modules on the main thread before any warm-up thread starts
(transformers' lazy loader isn't thread-safe). Check a change with the venv's `python -m py_compile`.
