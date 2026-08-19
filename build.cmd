@echo off
REM ---------------------------------------------------------------------------
REM  Build ICTSMCStrategy.dll and drop it into a deploy folder.
REM
REM    build.cmd                          -> deploys to %USERPROFILE%\Desktop\IliaICTSMC
REM    build.cmd "D:\somewhere"           -> deploys there instead
REM    build.cmd "D:\somewhere" "C:\Program Files\ATAS X"   -> also pins the ATAS folder
REM
REM  For an older .NET 8 based ATAS install add:  -p:AtasTfm=net8.0-windows
REM ---------------------------------------------------------------------------
setlocal EnableDelayedExpansion

set "OUTDIR=%~1"
if "%OUTDIR%"=="" set "OUTDIR=%USERPROFILE%\Desktop\IliaICTSMC"

set "ATASARG="
if not "%~2"=="" set "ATASARG=-p:AtasPath=%~2"

echo.
echo   Output folder : %OUTDIR%
if not "%~2"=="" echo   ATAS folder   : %~2

where dotnet >nul 2>nul
if errorlevel 1 (
  echo.
  echo   ERROR: the .NET SDK is not on PATH. Install it from https://dotnet.microsoft.com/download
  exit /b 1
)

if not exist "%OUTDIR%" mkdir "%OUTDIR%"

echo.
echo   Building...
dotnet build "%~dp0src\ICTSMCStrategy\ICTSMCStrategy.csproj" -c Release %ATASARG% -p:OutDir="%OUTDIR%\\"
if errorlevel 1 (
  echo.
  echo   BUILD FAILED.
  echo   Most common cause: ATAS.Indicators.dll / OFT.Rendering.dll were not found.
  echo   Pass your ATAS install folder explicitly, e.g.
  echo       build.cmd "%OUTDIR%" "C:\Program Files\ATAS X"
  exit /b 1
)

echo.
if exist "%OUTDIR%\ICTSMCStrategy.dll" (
  echo   OK  ICTSMCStrategy.dll  ^-^>  %OUTDIR%
  echo.
  echo   To load it in ATAS, copy the DLL into:
  echo       %USERPROFILE%\Documents\ATAS\Indicators
  echo   then restart ATAS and add "ICT/SMC Strategy" from the Order Flow category.
) else (
  echo   BUILD REPORTED SUCCESS BUT ICTSMCStrategy.dll IS NOT IN %OUTDIR%
  exit /b 1
)
endlocal
