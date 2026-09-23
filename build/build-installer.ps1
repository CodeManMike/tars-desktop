# Builds artifacts\TARS-Setup-<version>.exe: the self-contained client, zipped and embedded in the setup program.
#   pwsh build\build-installer.ps1
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$art = Join-Path $root 'artifacts'
$app = Join-Path $art 'app'
$zip = Join-Path $art 'payload.zip'

Remove-Item $art -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $art | Out-Null

Write-Host '== publish client (self-contained, win-x64)'
dotnet publish (Join-Path $root 'src\TarsClient\TarsClient.csproj') -c $Configuration -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=none -o $app
if ($LASTEXITCODE) { throw 'client publish failed' }

Write-Host '== payload.zip'
Compress-Archive -Path (Join-Path $app '*') -DestinationPath $zip -CompressionLevel Optimal

Write-Host '== publish setup (single file, payload embedded)'
dotnet publish (Join-Path $root 'src\TarsSetup\TarsSetup.csproj') -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -p:PayloadZip=$zip -o (Join-Path $art 'setup')
if ($LASTEXITCODE) { throw 'setup publish failed' }

$version = ([xml](Get-Content (Join-Path $root 'src\TarsClient\TarsClient.csproj'))).Project.PropertyGroup.Version | Select-Object -First 1
$out = Join-Path $art "TARS-Setup-$version.exe"
Move-Item (Join-Path $art 'setup\TARS-Setup.exe') $out -Force
Write-Host ("== {0} ({1:N0} MB)" -f $out, ((Get-Item $out).Length / 1MB))
