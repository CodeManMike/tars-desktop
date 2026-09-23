# TARS Desktop Client: spec

A native Windows client for **TARS** (*Tau-Ceti Assistant, Rocky Style*). It replaces the browser window with a small, resizable, always-on-top **retro 90s terminal**: orange on black, with its own title bar and no Windows frame. It talks to the existing TARS server on `cmm-media`. The client also:
- **speaks TARS's replies itself** with a much better local voice engine running on MIKES-PC's GPU (section 6); and
- **edits the server's settings**: config, TARS's knowledge files, personality, status and logs, and restart (section 7).

**The server needs no changes**; everything below is already live.

> Build target: MIKES-PC. Windows 11 Pro 25H2, i7-14700KF, 64 GB RAM, **RTX 4070 Ti SUPER 16 GB**. Owner: Michael (C# developer).

---

## 1. Goals

1. **Always available.** Starts with Windows, lives in the tray, and listens hands-free ("Say TARS") or via a global push-to-talk key, even while a game has focus.
2. **Looks like a 1990s terminal.** Orange phosphor on black, a pixel font, a custom title bar, square corners and optional scanlines. No default Windows chrome anywhere.
3. **Tiny when idle, bigger when needed.** A compact strip (about 380×150) shows status, the last exchange and timers. Drag to resize into a full terminal with scrollback and settings.
4. **Thin client.** All intelligence stays on the server. The client captures the mic, voices replies with a local TTS engine (section 6), plays audio and draws state.

Non-goals: offline operation, local speech recognition, multi-user support.

---

## 2. Technology

| Choice | Why |
|---|---|
| **C# / WPF on .NET 10 (LTS)** | Michael's stack; frameless windows, `Topmost` and custom chrome are straightforward in WPF |
| **NAudio 2.x** | mic capture at 16 kHz, gapless playback, volume, device selection |
| **System.Net.WebSockets.ClientWebSocket** | the server protocol is plain WebSocket over TLS |
| **CommunityToolkit.Mvvm** | view models / commands |
| **H.NotifyIcon.Wpf** | tray icon and menu |
| **Local TTS sidecar (Python, CUDA)** | TARS's voice on the RTX 4070 Ti SUPER, behind a local OpenAI-compatible `/v1/audio/speech` endpoint (section 6) |
| System.Text.Json | protocol messages and the client settings file |

Alternative, if WPF feels heavy: Tauri (Rust + WebView2). The protocol and behaviour in this spec apply unchanged.

Suggested solution layout:
```
TarsClient/
  App.xaml(.cs)                 single instance (named mutex), tray, startup
  Views/MainWindow.xaml         compact + expanded layouts, custom title bar
  Views/SettingsView.xaml       VOICE / PERSONALITY / AUDIO / SYSTEM tabs
  ViewModels/MainViewModel.cs   state machine, bindings
  Services/ServerConnection.cs  ClientWebSocket, JSON, reconnect, TLS pinning
  Services/AudioCapture.cs      16 kHz mono PCM frames, level meter
  Services/AudioPlayback.cs     WAV queue, gapless output, volume, chime
  Services/HotkeyService.cs     low-level keyboard/mouse hook for hold-to-talk
  Services/SettingsStore.cs     %APPDATA%\TARS\client.json
  Services/LocalVoice.cs        talks to the TTS sidecar, health checks, fallback to server voice
  Services/AdminApi.cs          HttpClient for /api/admin/* (server settings, files, status)
  Views/ServerView.xaml         SERVER / FILES / STATUS screens
  Assets/VT323-Regular.ttf, tars.ico, tars-ca.crt
tts-sidecar/                    Python venv + chosen engine server, start script, voice reference clip
```

---

## 3. Look and feel

### Palette
| Token | Value | Use |
|---|---|---|
| `Bg` | `#000000` | everything |
| `Fg` | `#FF8800` | primary text, borders, title bar text |
| `FgBright` | `#FFB347` | TARS's replies, active states, alarm |
| `FgDim` | `#7A4000` | secondary text (your last line, labels, idle meter cells) |
| `Sel` | `#2A1500` | selection / hover background |
| Glow | DropShadowEffect, colour `#FF8800`, blur 6–8, depth 0, opacity 0.5 | on text; toggleable |

### Type
- **VT323** (Google Fonts, SIL OFL), embedded as a resource. 18 px compact, 20 px expanded; user-adjustable.
- Alternative: *Px437 IBM VGA 8x16* (Ultimate Oldschool PC Font Pack, CC BY-SA 4.0).
- Everything is monospace, including buttons.

### Retro details
- 1 px orange border around the whole window; square corners. On Windows 11 call `DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE=33, DWMWCP_DONOTROUND=1)`, or DWM will round them.
- Optional **scanlines**: a non-hit-testable overlay of 1 px black lines at 35% opacity every 3 px.
- **Blinking block cursor** `▌` (530 ms) after the latest reply while TARS speaks, and in the input prompt.
- **Typewriter reveal** of replies, about 90 chars/s. It never lags behind the audio; if the next sentence arrives, finish the current one instantly.
- Buttons are bracketed text: `[PTT]`, `[PIN]`. Hover inverts them (black on orange). No icons except the tray icon.
- Status tags in capitals and brackets: `[ STANDBY ]` `[ LISTENING ]` `[ HEARING ]` `[ TRANSCRIBING ]` `[ THINKING ]` `[ SPEAKING ]` `[ ALARM ]` `[ NO CARRIER ]`.
- Meters as block characters: `MIC ▮▮▮▮▯▯▯▯`, `VOL ███████░░ 80%`.

### Window chrome (no default frame)
- `WindowStyle=None`, `ResizeMode=CanResize`, `Background=Bg`, plus WPF `WindowChrome`:
  `CaptionHeight=28`, `ResizeBorderThickness=6`, `GlassFrameThickness=0`, `CornerRadius=0`, `UseAeroCaptionButtons=False`.
  This keeps native drag, Aero Snap and resize while drawing everything ourselves. Title-bar buttons set `WindowChrome.IsHitTestVisibleInChrome="True"`.
- Minimum size 260×110. Remember position, size, layout and topmost across runs (clamp to a visible monitor on restore).

### Title bar (28 px, black, orange text)
```
■ TARS  [ LISTENING ]                          [PIN] [▭] [_] [×]
```
- `■` is a solid orange block. It flashes during an alarm.
- `[PIN]`: always-on-top toggle, shown inverted when on (default on).
- `[▭]`: toggle compact/expanded (double-clicking the title bar does the same).
- `[_]`: minimize. `[×]`: hide to tray (don't exit). Quit is in the tray menu.

### Compact layout (default about 380×150)
```
┌──────────────────────────────────────────────────┐
│■ TARS  [ LISTENING ]              [PIN][▭][_][×] │
│> will it rain tomorrow                           │  FgDim: your last utterance
│TARS: Tomorrow fog, high 19, low 14.▌             │  FgBright: TARS's reply (typewriter)
│RICE 11:42 · PASTA 04:03 · 15:00 call mom         │  timers/reminders, hidden when none
│MIC ▮▮▮▮▯▯▯▯  VOL ███████░░ 80%     [WAKE] [PTT]  │  footer
└──────────────────────────────────────────────────┘
```
- The reply wraps to 2–3 lines and scrolls up if longer.
- `[WAKE]` shows the mode; click to cycle PTT → WAKE → OPEN. `[PTT]` is a hold-to-talk button (mouse down/up).
- Scrolling the mouse wheel over VOL changes volume in 5% steps.
- Typing any printable key while the window is focused opens a `> _` prompt line in place of your last utterance. Enter sends, Esc cancels.

### Expanded layout (drag larger, or `[▭]`)
- Terminal scrollback of the session:
  ```
  > what's the capital of kenya
  TARS: Nairobi.
  > set a rice timer for 12 minutes
  TARS: Rice timer set for 12 minutes.
  ```
  Keep the last 200 lines, and don't persist them.
- A bottom prompt line `> _` that is always visible, plus the footer from compact.
- `F2` (or `[SET]` in the footer) opens **Settings** as a full-window retro menu with tabs, arrow-key navigable:
  - **PERSONALITY:** Humor / Honesty / Brevity sliders 0–100, step 5, drawn as `HUMOR  [███████░░░] 75%`. Values come from, and are sent to, the server.
  - **VOICE:** engine `LOCAL` (the sidecar, default) or `SERVER` (Kokoro on cmm-media, the fallback). For LOCAL: reference clip, exaggeration, pace, and a voice lab (section 6.6). For SERVER: voice list (from `voices` plus the current value, e.g. a blend like `am_fenrir:0.6,am_michael:0.4`), Speed, Pitch, Chord. `[TEST VOICE]` for both.
  - **AUDIO:** input device, output device, volume 0–150%, echo tail (ms), PTT hotkey (press-to-capture), stop hotkey.
  - **SYSTEM:** server URL, access key, start with Windows, start minimized to tray, always on top, scanlines, glow, font size, typewriter speed, and "show details" (timings and ignored sounds in the scrollback).
  - **SERVER / FILES / STATUS:** server-side settings (section 7).

### Tray
- Icon: an orange `T` or hexagon on black (`tars.ico`, 16/32/48 px). Tooltip: `TARS · LISTENING`.
- Left-click: show/hide. Menu: Show, Mode ▸ (Push to talk / Say "TARS" / Always listening), Always on top ✓, Mute mic ✓, Settings, Quit.

---

## 4. Behaviour

### State machine (drives the title-bar tag)
`NO CARRIER` → connecting. After `hello`: `STANDBY` (PTT mode) or `LISTENING` (WAKE/OPEN).
Server `status` messages override: `hearing`, `transcribing`, `thinking`, `idle`.
While audio is playing: `SPEAKING`. While an alarm is active: `ALARM` (highest priority).

### Microphone
- Capture the default (or chosen) input at **16 kHz, mono, 16-bit signed little-endian PCM**. With NAudio: `WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 32 }` gives 512-sample (1,024-byte) frames. `WasapiCapture` plus a resampler also works.
- Send each frame as one **binary** WebSocket message, following the sending rules below. Any frame size is accepted; 512 samples is ideal.
- Compute RMS per frame for the MIC meter.
- Microphone privacy: if Windows denies access, show `[ MIC BLOCKED ]` and a hint to enable *Settings → Privacy → Microphone → Let desktop apps access your microphone*.

**Sending rules (important):**
| Mode | Send frames when |
|---|---|
| PTT | only between `ptt start` and `ptt end`. Keep sending for **300 ms after the key is released** before sending `ptt end`, so the last word isn't cut. |
| WAKE / OPEN | always, **except** while TARS audio or the chime is playing and for an *echo tail* of 300 ms after it ends. There's no acoustic echo cancellation, so this half-duplex rule prevents TARS hearing itself. |

### Push-to-talk
- **Global hold-to-talk** via a low-level hook (`SetWindowsHookEx` `WH_KEYBOARD_LL`, and optionally `WH_MOUSE_LL` for mouse side buttons), so it works while a game has focus. `RegisterHotKey` can't detect key-up, so it isn't enough.
- Default key: **Right Ctrl**. Configurable to any key or **Mouse 4/5**. Don't swallow the key (pass it on) unless the user opts in.
- Key down: stop playback, send `{"type":"ptt","state":"start"}`, start streaming. Key up: stream 300 ms more, then `{"type":"ptt","state":"end"}`.
- When the window has focus, holding **Space** also does push-to-talk, unless the prompt line is active.
- A global **stop** hotkey (default `Ctrl+Alt+S`) stops speech and dismisses alarms.

### Playback
- Each **binary** message from the server is a complete **WAV** file (RIFF, PCM 16-bit, mono, 24 kHz): one sentence or canned line. Play them **in arrival order, gaplessly**. Decode with `WaveFileReader(new MemoryStream(bytes))` into a `BufferedWaveProvider` (24 kHz mono) that feeds a `VolumeSampleProvider` and then `WasapiOut`/`WaveOutEvent`.
- Volume 0–150%. Above 100%, apply a soft clip so it doesn't crackle.
- Tell the server about playback: send `{"type":"playback","state":"start"}` when the first queued chunk starts, and `{"type":"playback","state":"end"}` once the queue has been empty for 250 ms. The server uses this for its 8-second follow-up window (no name needed right after a reply) and to reset its voice detector.
- **Stop** (Esc, stop hotkey, a server `stop` message, or starting PTT): clear the queue and silence immediately, then send `playback end`.

With the local voice engine active (section 6) the server sends **text** (`say`) instead of WAV. The same queue, ordering, volume, stop and `playback` rules apply to the audio the sidecar produces.

### Timers, reminders, alarms
- The server pushes the full list on connect and on every change: `timers` → `[{id, kind, label, due, text}]` plus `now` (server epoch seconds). Compute `offset = now − localEpoch` once per message and render countdowns every second: timers as `RICE 11:42`, reminders as `15:00 call mom`. Clicking an item (or pressing Del when it's selected in expanded view) sends `cancel_timer`.
- On `alarm`: set `ALARM` state, flash the `■` and the border between orange and black at 2 Hz, and show `⏰ Rice timer done!` as the reply line. Play the **chime** (three sine blips: 880 Hz, 880 Hz, 1,175 Hz, 180 ms each, 220 ms apart, peak 0.5, through the same volume), then the spoken WAV that follows. Repeat the chime every 10 s, at most 8 times.
  If the window is hidden or minimized, show it (topmost) for the alarm and flash the taskbar button (`FlashWindowEx`).
- Dismiss (click anywhere in the window, Esc, or the stop hotkey): stop the chime and send `{"type":"dismiss"}`. The server then sends `stop` to every client, so the web UI and any tablet go quiet too.

### Connection
- Reconnect forever with exponential backoff: 0.5 s, 1 s, 2 s … capped at 10 s. Show `[ NO CARRIER ]` meanwhile (and, as a joke, `ATDT cmm-media…` in the scrollback on the first failure).
- On every (re)connect, send the current `mode`.
- Close code **4403** means this PC isn't on the server allowlist. Show `ACCESS DENIED: add this PC to ALLOWED_CLIENTS or set an access key` and stop retrying until the settings change.

---

## 5. Server contract

**Base:** `https://192.168.86.243:8765` (the name `cmm-media.lan` also works if the PC uses the router for DNS).
**WebSocket:** `wss://192.168.86.243:8765/ws`

### Auth
- MIKES-PC (`192.168.86.26`) is already allowlisted by IP; no key needed.
- Other devices: send header `x-tars-key: <TARS_KEY>` on the WebSocket upgrade (or `?key=<TARS_KEY>`). The key is `TARS_KEY` in `~/tars/.env` on the server.
- **The admin API (`/api/admin/*`) always needs `x-tars-key`**, even from MIKES-PC. Store the key in the client settings (DPAPI-protected, see section 8).

### Credentials for this install
Filled in by the server when this spec is downloaded, so they're always current.
- **Access key** (`x-tars-key` header): `<redacted: the server fills it in when the spec is downloaded; the client fetches it itself>`
- **Home CA certificate.** Pin it: save as `Assets/tars-ca.crt` and embed it as a resource.
```pem
-----BEGIN CERTIFICATE-----
MIIDrDCCApSgAwIBAgIUFGTbtNrUyVKVxBKzKixtPhbKtPowDQYJKoZIhvcNAQEL
BQAwKDEmMCQGA1UEAwwdVEFSUyBIb21lIENBIChjbW0tbWVkaWEgb25seSkwHhcN
MjYwOTIzMTgwMDU0WhcNMzYwOTIwMTgwMDU0WjAoMSYwJAYDVQQDDB1UQVJTIEhv
bWUgQ0EgKGNtbS1tZWRpYSBvbmx5KTCCASIwDQYJKoZIhvcNAQEBBQADggEPADCC
AQoCggEBAJ8F1wwbrwSCuDCV7mnXFCLhnyCRERHZSbflsLwYqkzOHqGzIX5ypvIo
Pv8lmuQykt/LzWyUgRuTyYvyUWkghWAyG2pl9VKU2+c8whzzZsacLHpspHBtRsR8
lXZ+hfo3ovoI7m2NONMMDOl6zWVAwUVDD4T0dtt57vIud6eVoHkn+H5pkinT1ABp
iuE8qxQUr8ERlihtfvQ7M+RlUUrBQ4sHUrkYEd2nvHk/GKOKgFmPb8W0tLn0Wmpx
5vK3wr2TXlwzZyzvcx7mfOlX3I3tm/eObXzzGYeSSRdaqng7IQdNOGFcn1wDEKVE
2GPwWk774bAzfpyuFs5RMXlvshc9UxkCAwEAAaOBzTCByjAdBgNVHQ4EFgQUUExQ
afNj0ZHuE3o00qUusW28dAowHwYDVR0jBBgwFoAUUExQafNj0ZHuE3o00qUusW28
dAowEgYDVR0TAQH/BAgwBgEB/wIBADAOBgNVHQ8BAf8EBAMCAQYwZAYDVR0eAQH/
BFowWKBWMAuCCWNtbS1tZWRpYTAPgg1jbW0tbWVkaWEubGFuMBGCD2NtbS1tZWRp
YS5sb2NhbDALgglsb2NhbGhvc3QwCocIwKhWAP///wAwCocIfwAAAP8AAAAwDQYJ
KoZIhvcNAQELBQADggEBAIKbyRLAmG77R6XnQoo2/yI52LnYqYz1vBztR9UD89aZ
c7jWsSU+KShdMceXs6iTGtBFlOnUavye0S15WSbXEAma438VzF1eUL/DXjgzVZq0
8lg9o8HKQ4KfPMy/exBxlhospE39yEq7qqCCmSIFNSDqPA+onAPVgnSFB/LT9NO/
Zy3o5PX6UOccM0CzlkSh269DampbL/gM4XvpsgxehEG4XhqygXnOiAfbflyYrswM
P8yJPiZt6itDLnu+HL5ZKhhbU7DolNik4QqCSy7Fk7PT4Gk13z8GtFd4AsrRl1Z1
PPOwFsAZ2jcX7ZtG36VxBKAAs0S1i4Vlk6Fc/ZpobsM=
-----END CERTIFICATE-----
```
- Not needed by the client: the **Groq API key** and **Home Assistant token** stay on the server. The SERVER screen (section 7) shows them masked and can replace them.
- Don't commit this file or `client.json` to a public repository.

### TLS
The server certificate is issued by **"TARS Home CA (cmm-media only)"** (name-constrained to this LAN). Either:
1. The CA is installed in the Windows user Root store (the server's `/setup-windows.bat` does this), so default validation just works; or
2. **Pin it:** embed `tars-ca.crt` (download from `https://192.168.86.243:8765/ca.crt`) and validate with
   `options.RemoteCertificateValidationCallback = (s, cert, chain, errors) => { var c = new X509Chain(); c.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust; c.ChainPolicy.CustomTrustStore.Add(pinnedCa); c.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; return c.Build(new X509Certificate2(cert)); };`
   Never accept arbitrary certificates.

### Client → server (text frames, JSON)
| Message | Meaning |
|---|---|
| `{"type":"mode","mode":"ptt"\|"wake"\|"open"}` | listening mode. Send on connect and on change |
| `{"type":"ptt","state":"start"}` / `{"type":"ptt","state":"end"}` | push-to-talk bracket (frames in between are one utterance) |
| `{"type":"text","text":"...","speak":true}` | typed message. `speak:false` = text-only reply |
| `{"type":"playback","state":"start"\|"end"}` | playback state (see Playback) |
| `{"type":"settings","humor":75,"honesty":95,"brevity":85,"voice":"am_fenrir:0.6,am_michael:0.4","speed":1.0,"pitch":0.97,"chord":0}` | any subset. The server saves it and broadcasts `settings` to all clients |
| `{"type":"test_voice"}` | TARS says a short test line |
| `{"type":"cancel_timer","id":"a1b2c3d4"}` | cancel one timer or reminder |
| `{"type":"dismiss"}` | stop a ringing alarm everywhere |
| `{"type":"client","name":"tars-desktop","tts":"local"\|"server","stt":"local"\|"server"}` | send right after connect (and whenever the voice or STT engine changes). `tts:"local"` = send me `say` text, I voice it; `server` = send WAV. `stt:"local"` = I transcribe myself and send `utterance` (PCM frames and `ptt` are still accepted as a fallback) |
| `{"type":"utterance","text":"...","source":"ptt"\|"wake"\|"open","speech_ms":1200,"stt_ms":180,"logprob":-0.21,"no_speech_prob":0.02,"model":"large-v3-turbo"}` | text from client-side STT (see Client-side STT). Only `text` is required, but send the stats: they feed the noise and hallucination guard |

### Client → server (binary frames)
16 kHz mono Int16 LE PCM (see Sending rules).

### Server → client (text frames, JSON)
| Message | Meaning / what to do |
|---|---|
| `hello {name, llm_ready, ha, model}` | first after connect. Use `name` ("TARS") in the title bar |
| `settings {settings:{voice,speed,pitch,chord,humor,honesty,brevity}, voices:[...]}` | fill the Settings screen. Sent on connect and after any change |
| `timers {timers:[{id,kind:"timer"\|"reminder",label,due,text}], now}` | replace the list (see Timers) |
| `status {state:"idle"\|"hearing"\|"transcribing"\|"thinking"}` | title-bar tag |
| `heard {accepted, text, reason, stt_ms, speech_ms, logprob, stt}` | an utterance was transcribed (`stt`: `cloud`, `local` or `client`). If `accepted:false` it was ignored (noise, no wake name, busy); show it only in "details" mode |
| `user {text}` | what TARS is answering: show as `> text` |
| `reply {text}` | a sentence of TARS's reply. **Append** to the current reply with a space; a turn can have several |
| `tool {name, args}` | TARS used a tool (`web_search`, `home`, `home_state`, `remember`). Details mode only, e.g. `  · web_search: rugby world cup` |
| `done {via, timings}` | turn finished. Details mode: `  · cloud:gpt-oss-120b · voice 1.3 s · total 3.8 s`. Then return to idle |
| `alarm {id, kind, text}` | ring (see Alarms). The spoken WAV follows as a binary frame |
| `stop {}` | stop playback and alarm now |
| `error {text}` | show as an error line |
| `client_ok {tts, stt}` | acknowledges `client`; each is `local` or `server` |
| `say {text, speak, seq, kind}` | **local voice only.** Voice `speak` (normalised for speech: name as "Tars", units, powers); display uses `reply`. `seq` orders chunks within a turn. `kind`: `reply`, `filler` (quick "Checking."/"One moment." before a search: speak it quickly and lightly), `alarm` (after the chime) |

### Server → client (binary frames)
Server voice only: one WAV per sentence (PCM16, mono, 24 kHz). Play in order. Not sent to a client that declared `tts:"local"`.

### Client-side STT (`utterance`)
MIKES-PC transcribes with faster-whisper on the GPU and sends text instead of PCM. The server runs the same gate as for its own STT, so noise can't reach TARS whichever side transcribes.

**Server gate, in order** (`app/stt.py` `judge_client`/`judge`, `app/main.py` `gate`):
1. A turn is already running → `heard accepted:false reason:"busy, still answering"`.
2. `speech_ms` < 250 → "no speech".
3. `logprob` < −1.0, or `no_speech_prob` > 0.6 with `logprob` < −0.5 → "low confidence".
4. Non-speech tags stripped (`[..]`, `(..)`, `♪♫*~`); nothing left → "no words".
5. Hallucination guard: "thank you", "thanks for watching", "subscribe", "bye", "you", "so", "…subtitles by…" and similar are accepted only with `speech_ms` ≥ 450, `logprob` > −0.45 and `no_speech_prob` < 0.25.
6. Wake name: needed only when `source` is `wake` **and** more than 8 s have passed since the last `playback end`. Pattern: `\b(hey |ok |okay |yo )?(tars|tarz|tarss)\b` (from `WAKE_WORDS`). `ptt` and `open` never need it.
7. Name only ("TARS.") → TARS says "Yes?" and the 8-second follow-up window opens after playback.

Missing stats skip their checks (and count as strong evidence for rule 5), so always send them. `logprob` = mean `avg_logprob` of the kept segments, `no_speech_prob` = max over them, `speech_ms` = Silero-voiced milliseconds, `stt_ms` = transcription time. `source` defaults to the session's mode if missing.

**Mirror on the client** (saves a round trip, the server re-checks anyway): clip ≥ 300 ms; Silero voiced ≥ 250 ms; drop segments with (nsp > 0.6 and lp < −0.5), lp < −1.0 or compression ratio > 2.4; give Whisper the name via `initial_prompt: "Hey TARS."` or `hotwords: "TARS"`.

### HTTP (optional helpers)
- `GET /api/health` → `{"ok":true,"name":"TARS","llm_ready":true,"cloud":"openai/gpt-oss-120b","cloud_stt":true,"ha":true,...}`. Use it as a connection test in Settings.
- `POST /api/ask {"text":"..."}` → `{"reply":"...","via":"..."}`: text-only, no audio.
- `GET /api/tts?text=...` → `audio/wav` in TARS's voice.
- `GET /ca.crt`: the home CA certificate.

---

## 6. TARS's voice on MIKES-PC (local TTS)

Kokoro on the server is fast but flat: robotic pacing, little inflection. MIKES-PC's RTX 4070 Ti SUPER can run modern expressive voice models in real time, so the client voices replies locally and the server just sends text.

### 6.1 Architecture
```
TARS server ──wss── say {text, speak, seq, kind} ──► WPF client ──HTTP──► TTS sidecar (127.0.0.1:8880, CUDA)
                                                        ▲                      │ PCM/WAV stream
                                                        └──── playback queue ◄─┘
```
- The **sidecar** is a local Python process exposing an **OpenAI-compatible** `POST /v1/audio/speech` (`{"model","input","voice","response_format":"wav"|"pcm","speed"}`), plus engine-specific extras (exaggeration and so on) as additional JSON fields. Because the client speaks one standard API, engines are swappable.
- The client **starts the sidecar** when it starts (hidden window), health-checks it (`GET /health` or a one-word synthesis), and stops it on exit. Log its output to `%APPDATA%\TARS\tts.log`.
- Use **streaming** responses (chunked PCM) so playback starts before synthesis finishes.

### 6.2 Engine: pick by listening, not by spec sheet
Candidates as of 2026, all running on a 16 GB NVIDIA card:

| Engine | Why | VRAM (approx.) | Notes |
|---|---|---|---|
| **Chatterbox Turbo** (Resemble AI, MIT) | expressive, natural pacing; zero-shot voice from a 10–20 s reference clip; *exaggeration* and *cfg/pace* controls; reported <200 ms latency | ~4–6 GB | **Start here.** OpenAI-compatible community servers exist (e.g. *Chatterbox-TTS-Server*); verify the current repo and its Windows/CUDA install notes. |
| **Orpheus TTS 3B** (Canopy Labs) | very natural prosody; inline emotion tags (`<sigh>`, `<chuckle>`…); preset voices | ~6–8 GB | *Orpheus-FastAPI*-style servers expose an OpenAI-compatible endpoint (llama.cpp/LM Studio backend). |
| Qwen3-TTS, IndexTTS-2, CosyVoice 2 | strong prosody (IndexTTS-2: emotion and duration control; CosyVoice 2: ~150 ms streaming) | varies | Check each licence before adopting. |

Check the current versions, licences and Windows support when building. The model landscape moves fast.

### 6.3 Voice direction for TARS
- **Deep, calm, dry, measured.** Even delivery, slight pauses between sentences and before a punchline, and no sing-song rises at sentence ends. Jokes are delivered exactly like facts.
- Chatterbox starting point: exaggeration **0.3–0.4** (lower means flatter and more deadpan, but not robotic), cfg/pace **0.3–0.4** (lower means slower and more deliberate), temperature about 0.7. Tune by ear.
- **Reference voice:** an *original* voice. Record your own 15–20 s of calm, dry reading, use a consenting friend, or use a voice licensed for cloning. **Don't clone an actor's voice from film clips**: it's someone's likeness, and clean-room is the better path anyway.
- Use `speak` (not `text`) as the input; it is already normalised for speech.
- `kind: "filler"` lines ("Checking.", "One moment.") are short and frequent, so pre-render and cache them per voice setting. `kind: "alarm"` plays after the chime.

### 6.4 Latency and phrasing
- Target: **first audio ≤ 400 ms** after a `say` arrives for a one-sentence reply. Stream PCM and begin playback on the first chunk.
- The server sends reflex and canned replies as one chunk and LLM replies sentence by sentence. Synthesize chunks in `seq` order; if the next chunk arrives while the previous is still synthesizing, the client may append it to the same request for smoother phrasing.
- Keep the model warm: keep the process resident, or send a tiny synthesis every 10 minutes when idle. See 6.5 for gaming.

### 6.5 Gaming and GPU memory
MIKES-PC is also the gaming PC, and Star Citizen can use most of 16 GB.
- **Game mode (default on):** when a fullscreen/exclusive app is running (`SHQueryUserNotificationState` → `QUNS_RUNNING_D3D_FULL_SCREEN` or `QUNS_BUSY`), or free VRAM drops below 3 GB (NVML), switch to `tts:"server"` (Kokoro audio from cmm-media) and optionally unload the model. Switch back when the game closes.
- Idle unload after N minutes (default 30) with a lazy reload on the next `say`; show `[ VOICE: WARMING ]` during reload.

### 6.6 Fallback and voice lab
- If the sidecar isn't healthy, or no audio arrives within **3 s** of a `say`, send `{"type":"client","tts":"server"}` and play server WAVs until the sidecar recovers. Then send `tts:"local"` again. Never leave a reply unspoken.
- **Voice lab** (VOICE tab): the same 8 TARS lines (a fact, a number, a timer confirmation, a joke, a correction, a weather report, an alarm, a filler) rendered by each installed engine and setting, with a play button, first-audio time and a star rating, for A/B choices by ear.

---

## 7. Server settings from the desktop (admin API)

All routes need header `x-tars-key: <TARS_KEY>` (401 without it). Base `https://192.168.86.243:8765/api/admin`.

| Route | Purpose |
|---|---|
| `GET /config` | server settings: `{fields:[{key,type,secret,description,value,pending_restart}], restart_required}`. Secrets come back masked (`••••92sY`). Types: `str`, `int`, `bool`, `list` (comma-separated), `choice:a,b,c` |
| `PATCH /config?restart=true` | body `{"KEY":"value",...}`. Validated (400 with a reason on bad input). Sending a masked secret back unchanged is ignored. `restart=true` restarts the server; the WebSocket drops, and the client shows `[ NO CARRIER ]` and reconnects within about 10 s |
| `GET /settings`, `PATCH /settings` | live voice and personality settings (same as the WS `settings` message; no restart) |
| `GET /files` | TARS's knowledge and persona: `[{area:"knowledge"\|"persona", name, bytes, modified, deletable}]` |
| `GET /files/{area}/{name}` | a file as plain text (Markdown) |
| `PUT /files/{area}/{name}` | save it (plain-text body, max 200 kB). Knowledge files are re-indexed within seconds; persona changes apply on the next answer |
| `DELETE /files/knowledge/{name}` | delete a knowledge file (`core.md` and the persona are protected) |
| `GET /status?lines=200` | `{uptime_s, health, ollama_loaded, cloud_cooldown_s, clients, timers, log:[...]}` |
| `POST /restart` | restart the server process |

Server settings exposed: `ASSISTANT_NAME, NAME_PRONOUNCE, NAME_SPOKEN, WAKE_WORDS, USER_NAME, TZ, LLM_MODEL, LLM_CTX, WHISPER_MODEL, CLOUD_API_KEY*, CLOUD_MODEL, CLOUD_REASONING, CLOUD_STT, CLOUD_STT_MODEL, HA_TOKEN*, NOTIFY_SERVICE, ALLOWED_CLIENTS, TARS_KEY*, OLLAMA_URL, HA_URL, SEARX_URL` (* secret).

### Client screens (expanded layout, retro style)
- **SERVER:** one line per field: `CLOUD_MODEL ........ openai/gpt-oss-120b`. Enter edits inline; bools toggle; choices cycle. Secrets show masked with `[SET]` to replace. `PENDING RESTART` marks changed fields, and `[APPLY + RESTART]` sends `PATCH /config?restart=true`. Warn before changing `TARS_KEY` (update the client's stored key first) or `ALLOWED_CLIENTS` (don't lock MIKES-PC out).
- **FILES:** a list on the left and a monospace editor on the right (orange caret, line numbers). `Ctrl+S` saves via PUT. Show "re-indexed" once saved. `people.md`, `routine.md`, `michael.md` and `memories.md` are where TARS's knowledge of Michael lives; `persona/tars.md` is its personality.
- **STATUS:** uptime, models, cloud cooldown, connected clients, timers, and a live-tail of the server log (poll every 2 s while open), plus `[RESTART SERVER]`.

---

## 8. Client settings file
`%APPDATA%\TARS\client.json`:
```json
{
  "serverUrl": "wss://192.168.86.243:8765/ws",
  "accessKey": "",
  "mode": "wake",
  "volume": 1.0,
  "topmost": true,
  "layout": "compact",
  "bounds": { "compact": [1500, 60, 380, 150], "expanded": [1200, 60, 640, 520] },
  "pttKey": "RightCtrl",
  "stopHotkey": "Ctrl+Alt+S",
  "inputDevice": "",
  "outputDevice": "",
  "echoTailMs": 300,
  "startWithWindows": true,
  "startMinimized": false,
  "scanlines": true,
  "glow": true,
  "fontSize": 18,
  "typewriterCps": 90,
  "showDetails": false,
  "voiceEngine": "local",
  "tts": { "sidecarUrl": "http://127.0.0.1:8880", "engine": "chatterbox", "referenceClip": "voices/tars-ref.wav",
           "exaggeration": 0.35, "pace": 0.35, "gameMode": true, "idleUnloadMinutes": 30 },
  "accessKeyProtected": "<TARS_KEY encrypted with DPAPI (ProtectedData, CurrentUser)>"
}
```
Start with Windows: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` → `"TARS" = "<exe path>" --minimized` (when startMinimized).

---

## 9. Acceptance checklist
1. Launches as a frameless black window with an orange 1 px border, **square corners**, a custom title bar and VT323 text. No Windows caption or frame is visible anywhere.
2. Drag by the title bar; resize from any edge or corner; Aero Snap works; always-on-top toggles with `[PIN]` and persists.
3. Compact ↔ expanded via `[▭]` and double-click, and each remembers its own size.
4. Connects to `wss://192.168.86.243:8765/ws` with a **pinned** CA. The title shows `TARS`; stopping the server shows `[ NO CARRIER ]` and it reconnects by itself.
5. PTT: holding Right Ctrl **while a game or another app has focus** records; "what time is it" gets a spoken answer within about 3 s. The last word isn't clipped.
6. WAKE mode: "TARS, what's the date today?" is answered; "the stars look amazing tonight" is ignored. TARS never answers its own voice.
7. Typed messages work, and text replies typewrite in sync with speech.
8. "set a test timer for 10 seconds" shows a countdown, then alarms with chime, flash and speech. Click dismisses it everywhere.
9. Personality sliders change TARS live ("what's your humor setting" matches), and the voice test works.
10. Volume 0–150% affects only TARS audio, with no crackle at 150%.
11. Close hides to tray; Quit exits; single instance only; starts with Windows when enabled.
12. Headset unplugged or replugged: capture recovers within about 2 s without restarting.
13. **Local voice:** replies are voiced by the sidecar, with first audio ≤ 400 ms for short replies. Killing the sidecar mid-conversation falls back to server audio within 3 s, and restarting it switches back. The voice lab A/B plays all installed engines.
14. **Game mode:** starting a fullscreen game switches to server voice; closing it switches back.
15. **Server settings:** SERVER shows all fields with secrets masked. Changing `CLOUD_REASONING` plus `[APPLY + RESTART]` round-trips, and the client reconnects by itself. FILES edits `people.md`, and TARS knows the change within seconds. STATUS tails the log.

## 10. Build order
1. `ServerConnection` + a text-only console test (connect, `hello`, send `text`, print `reply`).
2. `AudioPlayback` (WAV queue, volume, playback start/end) → "test_voice" audible.
3. `AudioCapture` + PTT in-window (Space) → a spoken round trip.
4. Frameless retro `MainWindow` (compact), state tags, typewriter.
5. Global PTT hook, tray, settings file, start with Windows.
6. Timers/alarms, expanded layout, Settings screen.
7. Polish: scanlines, glow, snap/restore edge cases, device hot-swap.
8. `AdminApi` + SERVER / FILES / STATUS screens.
9. TTS sidecar: install Chatterbox Turbo (CUDA) behind the OpenAI-compatible endpoint, `LocalVoice` with `client` switching, fallback, game mode. Then the voice lab, and only then consider a second engine.

The browser UI at `https://192.168.86.243:8765` stays available as a fallback and as a reference implementation of this protocol (`app/static/index.html` on the server).
