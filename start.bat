@echo off
setlocal
cd /d "%~dp0"

if /i "%~1"=="--build" goto buildOnly
if /i "%~1"=="--rebuild" goto rebuild
if /i "%~1"=="--launch" goto launch
if not "%~1"=="" goto usage

:menu
echo.
echo ETS2LA - personal build
if exist "publish\ETS2LA.exe" (
    echo [L] Launch previous build
) else (
    echo [L] Launch previous build - no build yet
)
echo [R] Rebuild and launch
echo [B] Build EXE without launching
echo [Q] Quit
choice /c LRBQ /n /m "Choose L, R, B or Q: "
if errorlevel 4 exit /b 0
if errorlevel 3 goto buildOnly
if errorlevel 2 goto rebuild
if errorlevel 1 goto launch

:rebuild
call :build
if errorlevel 1 goto failed
goto launch

:buildOnly
call :build
if errorlevel 1 goto failed
echo.
echo Ready: %~dp0publish\ETS2LA.exe
if "%~1"=="" pause
exit /b 0

:build
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build.ps1"
exit /b %errorlevel%

:launch
if not exist "publish\ETS2LA.exe" (
    echo No previous build found. Choose R or B to compile it first.
    if "%~1"=="" goto menu
    exit /b 1
)
start "ETS2LA" /d "%~dp0publish" "%~dp0publish\ETS2LA.exe"
exit /b 0

:failed
echo.
echo Build failed. Any previous build is still available.
echo See .build\build.log for details.
if "%~1"=="" pause
exit /b 1

:usage
echo Usage: start.bat [--build ^| --rebuild ^| --launch]
exit /b 2
