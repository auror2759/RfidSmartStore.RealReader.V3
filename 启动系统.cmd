@echo off
setlocal
set "APP_DIR=%~dp0bin\Release"
set "APP_EXE=%APP_DIR%\RfidSmartStore.RealReader.V3.exe"
set "APP_URL=http://127.0.0.1:8789/"

if exist "%APP_EXE%" goto app_found
echo ERROR: RfidSmartStore.RealReader.V3.exe was not found.
echo Run the rebuild script first.
pause
exit /b 1

:app_found
call :check_service
if not errorlevel 1 goto open_page

echo Starting KL9007U real reader V3...
start "KL9007U Real Reader V3" /D "%APP_DIR%" "%APP_EXE%" --no-open
set /a WAIT_COUNT=0

:wait_loop
ping 127.0.0.1 -n 2 >nul
call :check_service
if not errorlevel 1 goto open_page
set /a WAIT_COUNT+=1
if %WAIT_COUNT% LSS 15 goto wait_loop

echo.
echo ERROR: The local service did not start within 15 seconds.
echo Keep this window open and send a screenshot to Codex.
pause
exit /b 1

:check_service
powershell -NoProfile -Command "try { $r=Invoke-WebRequest -UseBasicParsing '%APP_URL%api/status' -TimeoutSec 2; if($r.StatusCode -eq 200){exit 0}else{exit 1} } catch { exit 1 }" >nul 2>&1
exit /b %errorlevel%

:open_page
echo Service is ready. Opening the web page...
start "" "%APP_URL%"
exit /b 0
