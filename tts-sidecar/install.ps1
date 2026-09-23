# Installs the TARS voice sidecar: a Python 3.11 venv with CUDA PyTorch and Chatterbox.
# Venv: %LOCALAPPDATA%\TARS\tts-venv (outside the app folder, so reinstalling the app keeps it).
# Downloads roughly 6.5 GB (PyTorch CUDA wheels + model weights). Safe to re-run.
param(
    [string]$Venv = (Join-Path $env:LOCALAPPDATA 'TARS\tts-venv'),
    [switch]$SkipModels
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
New-Item -ItemType Directory -Force (Split-Path $Venv) | Out-Null

$uv = (Get-Command uv -ErrorAction SilentlyContinue).Source
$py = Join-Path $Venv 'Scripts\python.exe'

Write-Host "== TARS voice sidecar =="
Write-Host "venv: $Venv"

if (-not (Test-Path $py)) {
    if ($uv) { & $uv venv --seed --python 3.11 $Venv }
    else     { & py -3.11 -m venv $Venv }
    if ($LASTEXITCODE -ne 0) { throw "Could not create a Python 3.11 venv (install Python 3.11 or uv)." }
}

function PipInstall([string[]]$pkgs) {
    if ($uv) { & $uv pip install --python $py @pkgs }
    else     { & $py -m pip install --disable-pip-version-check @pkgs }
    if ($LASTEXITCODE -ne 0) { throw "pip install failed: $pkgs" }
}

Write-Host "-- PyTorch 2.6 (CUDA 12.4)"
PipInstall @('torch==2.6.0', 'torchaudio==2.6.0', '--index-url', 'https://download.pytorch.org/whl/cu124')

Write-Host "-- Chatterbox + server"
PipInstall @('-r', (Join-Path $here 'requirements.txt'))

if (-not $SkipModels) {
    Write-Host "-- Model weights (Hugging Face cache)"
    & $py -c "from chatterbox.tts_turbo import ChatterboxTurboTTS as T; T.from_pretrained(device='cpu'); print('turbo ok')"
    & $py -c "from chatterbox.tts import ChatterboxTTS as T; T.from_pretrained(device='cpu'); print('chatterbox ok')"
    & $py -c "from kokoro import KPipeline as K; K(lang_code='a', device='cpu', repo_id='hexgrad/Kokoro-82M'); print('kokoro ok')"
    & $py -c "from faster_whisper import WhisperModel as W; W('large-v3-turbo', device='cpu', compute_type='int8'); W('small.en', device='cpu', compute_type='int8'); print('whisper ok')"
}

& $py -c "import torch; print('cuda:', torch.cuda.is_available(), torch.cuda.get_device_name(0) if torch.cuda.is_available() else '')"
Write-Host "== done =="
