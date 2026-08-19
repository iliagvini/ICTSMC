@echo off
REM ===========================================================================
REM  Build ICTSMCStrategy.dll and copy it into a deploy folder.
REM
REM    build.cmd
REM        -> deploys to %USERPROFILE%\Desktop\IliaICTSMC
REM    build.cmd "D:\deploy"
REM        -> deploys there
REM    build.cmd "D:\deploy" "C:\Program Files\ATAS X"
REM        -> also pins the ATAS folder (use if auto-detection fails)
REM ===========================================================================
setlocal

set "OUTDIR=%~1"
if "%OUTDIR%"=="" set "OUTDIR=%USERPROFILE%\Desktop\IliaICTSMC"

set "ATASDIR=%~2"

REM ---- locate ATAS if it was not supplied -----------------------------------
if not "%ATASDIR%"=="" goto :haveatas
if exist "C:\Program Files\ATAS X\ATAS.Indicators.dll"            set "ATASDIR=C:\Program Files\ATAS X"
if "%ATASDIR%"=="" if exist "C:\Program Files\ATAS Platform\ATAS.Indicators.dll"        set "ATASDIR=C:\Program Files\ATAS Platform"
if "%ATASDIR%"=="" if exist "C:\Program Files (x86)\ATAS Platform\ATAS.Indicators.dll"  set "ATASDIR=C:\Program Files (x86)\ATAS Platform"
:haveatas

if "%ATASDIR%"=="" (
  echo.
  echo   ERROR: could not find your ATAS installation.
  echo.
  echo   ATAS must be installed on THIS machine - the indicator links against
  echo   ATAS.Indicators.dll and OFT.Rendering.dll from the ATAS program folder.
  echo.
  echo   Pass the folder explicitly, e.g.
  echo       build.cmd "%OUTDIR%" "C:\Program Files\ATAS X"
  echo.
  exit /b 1
)

REM ---- ATAS X ships .NET 10 assemblies; older ATAS Platform is .NET 8 -------
set "TFM=net10.0-windows"
echo %ATASDIR% | find /i "ATAS X" >nul
if errorlevel 1 set "TFM=net8.0-windows"

echo.
echo   ATAS folder   : %ATASDIR%
echo   Target        : %TFM%
echo   Output folder : %OUTDIR%
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
  echo   ERROR: the .NET SDK is not installed / not on PATH.
  echo   Download it from https://dotnet.microsoft.com/download
  echo   You need the SDK matching %TFM%.
  exit /b 1
)

if not exist "%OUTDIR%" mkdir "%OUTDIR%"

echo   Building...
echo.
dotnet build "%~dp0src\ICTSMCStrategy\ICTSMCStrategy.csproj" -c Release -p:AtasTfm=%TFM% -p:AtasPath="%ATASDIR%" -p:OutDir="%OUTDIR%\\"
if errorlevel 1 (
  echo.
  echo   BUILD FAILED - see the error above.
  echo   If it mentions a missing .NET %TFM% targeting pack, install that SDK version.
  exit /b 1
)

echo.
if not exist "%OUTDIR%\ICTSMCStrategy.dll" (
  echo   Build reported success but ICTSMCStrategy.dll is not in %OUTDIR%
  exit /b 1
)

echo   ================================================================
echo    OK   ICTSMCStrategy.dll  ->  %OUTDIR%
echo   ================================================================
echo.
echo   Install it by copying the DLL to:
echo       %USERPROFILE%\Documents\ATAS\Indicators
echo   then restart ATAS and add "ICT/SMC Strategy" (Order Flow category).
echo.
endlocal
