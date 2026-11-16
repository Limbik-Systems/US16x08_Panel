@echo off
rem Builds the current source into US16x08_Panel.exe and writes build_log.txt next to this file.
rem US16x08_AbletonPanel_V2.exe (the last known-good V2 build) is left untouched.
cd /d "%~dp0"
echo Closing any running panel (quit signal first, then graceful taskkill, so MIDI ports are released)...
rem Tray-resident builds hide to tray on WM_CLOSE, so taskkill alone no longer exits them; set the quit event.
powershell -NoProfile -Command "$e=$null; if([System.Threading.EventWaitHandle]::TryOpenExisting('US16x08_Panel_QuitSignal_v1',[ref]$e)){$e.Set();$e.Dispose()}" >nul 2>&1
timeout /t 2 /nobreak >nul
taskkill /IM US16x08_Panel.exe >nul 2>&1
rem Builds made before the rename are named US16x08_AbletonPanel.exe
taskkill /IM US16x08_AbletonPanel.exe >nul 2>&1
taskkill /IM US16x08_AbletonPanel_V2.exe >nul 2>&1
timeout /t 3 /nobreak >nul
echo Building...
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish_v2 > build_log.txt 2>&1
if errorlevel 1 (
  echo BUILD FAILED>> build_log.txt
  echo.
  echo BUILD FAILED - details are in build_log.txt
  pause
  exit /b 1
)
copy /Y "publish_v2\US16x08_Panel.exe" "US16x08_Panel.exe" >> build_log.txt 2>&1
if errorlevel 1 (
  echo COPY FAILED - is the panel still running?>> build_log.txt
  echo COPY FAILED - close the panel and run this again.
  pause
  exit /b 1
)
echo BUILD OK>> build_log.txt
echo BUILD OK - launching...
start "" "US16x08_Panel.exe"
