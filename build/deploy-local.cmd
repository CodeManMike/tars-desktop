@echo off
rem Installs the freshly built setup quietly. Launch it through Explorer
rem   Start-Process explorer.exe -ArgumentList '"E:\TARS Frontend\build\deploy-local.cmd"'
rem so it runs outside any MSIX package (e.g. the Claude desktop app), whose AppData and HKCU writes are virtualized.
set ROOT=%~dp0..
del "%ROOT%\artifacts\deploy.done" 2>nul
start "" /wait "%ROOT%\artifacts\TARS-Setup-1.0.0.exe" --quiet
echo done> "%ROOT%\artifacts\deploy.done"
