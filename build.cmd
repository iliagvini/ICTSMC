@echo off
REM ===========================================================================
REM  Build ICTSMCStrategy.dll, copy it to a deploy folder, and install it into
REM  the ATAS Indicators folder.
REM
REM    build.cmd
REM        -> staging copy in <repo>\dist, then installs into ATAS
REM    build.cmd "D:\deploy"
REM    build.cmd "D:\deploy" "C:\Program Files (x86)\ATAS Platform"
REM    build.cmd "D:\deploy" "" nocopy      -> build only, do not touch ATAS
REM ===========================================================================
setlocal

set "OUTDIR=%~1"
REM Default to <repo>\dist. Defaulting to a folder the user is likely to be working
REM in scattered ICTSMCStrategy.dll/.pdb/.deps.json loose among their own files.
if "%OUTDIR%"=="" set "OUTDIR=%~dp0dist"
set "ATASDIR=%~2"
set "NOCOPY=%~3"

REM ---- locate the ATAS program folder --------------------------------------
if not "%ATASDIR%"=="" goto :haveatas
if exist "C:\Program Files\ATAS X\ATAS.Indicators.dll"                       set "ATASDIR=C:\Program Files\ATAS X"
if "%ATASDIR%"=="" if exist "C:\Program Files\ATAS Platform\ATAS.Indicators.dll"       set "ATASDIR=C:\Program Files\ATAS Platform"
if "%ATASDIR%"=="" if exist "C:\Program Files (x86)\ATAS Platform\ATAS.Indicators.dll" set "ATASDIR=C:\Program Files (x86)\ATAS Platform"
:haveatas

if "%ATASDIR%"=="" (
  echo.
  echo   ERROR: could not find your ATAS installation.
  echo   ATAS must be installed on THIS machine - the indicator links against
  echo   ATAS.Indicators.dll and OFT.Rendering.dll from the ATAS program folder.
  echo.
  echo   Pass it explicitly:  build.cmd "%OUTDIR%" "C:\Program Files\ATAS X"
  exit /b 1
)

REM ---- locate the ATAS Indicators folder -----------------------------------
REM  Modern ATAS loads from Roaming AppData. Older layouts used Documents.
set "INDDIR="
if exist "%APPDATA%\ATAS\Indicators"            set "INDDIR=%APPDATA%\ATAS\Indicators"
if "%INDDIR%"=="" if exist "%USERPROFILE%\Documents\ATAS\Indicators" set "INDDIR=%USERPROFILE%\Documents\ATAS\Indicators"
if "%INDDIR%"=="" set "INDDIR=%APPDATA%\ATAS\Indicators"

echo.
echo   ATAS program    : %ATASDIR%
echo   ATAS indicators : %INDDIR%
echo   Output folder   : %OUTDIR%
echo.
echo   (target framework is auto-detected from OFT.Platform.runtimeconfig.json)
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
  echo   ERROR: the .NET SDK is not installed / not on PATH.
  echo   Get it from https://dotnet.microsoft.com/download  ^(SDK, not Runtime^)
  exit /b 1
)

if not exist "%OUTDIR%" mkdir "%OUTDIR%"

echo   Building...
echo.
dotnet build "%~dp0src\ICTSMCStrategy\ICTSMCStrategy.csproj" -c Release -p:AtasPath="%ATASDIR%" -p:OutDir="%OUTDIR%\\"
if errorlevel 1 (
  echo.
  echo   BUILD FAILED - see the error above.
  echo   If it mentions a missing targeting pack, install the matching .NET SDK
  echo   ^(the line above starting "ICTSMC:" shows which framework was selected^).
  exit /b 1
)

if not exist "%OUTDIR%\ICTSMCStrategy.dll" (
  echo.
  echo   Build reported success but ICTSMCStrategy.dll is not in %OUTDIR%
  exit /b 1
)

echo.
echo   Built OK: %OUTDIR%\ICTSMCStrategy.dll

if /i "%NOCOPY%"=="nocopy" goto :done

REM ---- install into ATAS ---------------------------------------------------
if not exist "%INDDIR%" mkdir "%INDDIR%"
copy /y "%OUTDIR%\ICTSMCStrategy.dll" "%INDDIR%\ICTSMCStrategy.dll" >nul
if errorlevel 1 (
  echo.
  echo   Could not copy into %INDDIR%
  echo   ATAS is probably running and holding the old DLL. Close ATAS and re-run,
  echo   or copy the file yourself.
  exit /b 1
)

echo   Installed: %INDDIR%\ICTSMCStrategy.dll

:done
echo.
echo   ================================================================
echo    DONE
echo   ================================================================
echo   Restart ATAS, then add "ICT/SMC Strategy" (Order Flow category).
echo   A restart is required - charts bound to the previous build keep the
echo   old assembly until the indicator is re-added.
echo.
endlocal
