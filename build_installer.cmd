@echo off
rem Builds a Windows installer: publishes a self-contained build (no .NET install needed on the target PC)
rem to publish_installer\, then compiles installer\US16x08_Panel.iss with Inno Setup 6.
rem Output: installer\Output\US16x08_Panel_Setup_<version>.exe. The version comes from <Version> in the csproj.
rem Writes installer_build_log.txt. Does not touch US16x08_Panel.exe or the saved V1/V2 exes.
cd /d "%~dp0"

set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (
  echo Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup
  exit /b 1
)

echo Publishing self-contained build...
if exist publish_installer rmdir /s /q publish_installer
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish_installer > installer_build_log.txt 2>&1
if errorlevel 1 (
  echo PUBLISH FAILED>> installer_build_log.txt
  echo PUBLISH FAILED - details are in installer_build_log.txt
  exit /b 1
)

echo Compiling installer...
"%ISCC%" /Q "installer\US16x08_Panel.iss" >> installer_build_log.txt 2>&1
if errorlevel 1 (
  echo INSTALLER FAILED>> installer_build_log.txt
  echo INSTALLER FAILED - details are in installer_build_log.txt
  exit /b 1
)

echo INSTALLER OK>> installer_build_log.txt
echo INSTALLER OK - see installer\Output
