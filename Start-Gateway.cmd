@echo off
setlocal EnableExtensions DisableDelayedExpansion

set "TOOLS_EXE=%~dp0ZVT2SumUp.Tools.exe"
if not exist "%TOOLS_EXE%" (
  echo FEHLER: ZVT2SumUp.Tools.exe wurde neben diesem Skript nicht gefunden. 1>&2
  exit /b 1
)

echo ZVT2SumUp startet im Konsolenmodus. Strg+C beendet das Gateway sicher.
"%TOOLS_EXE%" run-console
set "GATEWAY_EXIT=%ERRORLEVEL%"

if not "%GATEWAY_EXIT%"=="0" echo Gateway wurde mit Exitcode %GATEWAY_EXIT% beendet. 1>&2
exit /b %GATEWAY_EXIT%
