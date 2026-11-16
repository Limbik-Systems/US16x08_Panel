; Inno Setup script for the TASCAM US-16x08 DSP & MIDI Console (US16x08_Panel.exe).
; Build with build_installer.cmd, which publishes a self-contained build to ..\publish_installer first.
; The version comes from the published exe, which gets it from <Version> in US16x08_AbletonPanel.csproj.

#define AppExeName "US16x08_Panel.exe"
#define PublishDir AddBackslash(SourcePath) + "..\publish_installer"
#define AppVersion GetVersionNumbersString(PublishDir + "\" + AppExeName)
#define AppName "US-16x08 DSP & MIDI Console"

[Setup]
; Keep this AppId unchanged forever: it is how a newer installer finds and upgrades an existing install.
AppId={{B9EF92EA-DDA3-4471-86AA-F25756959D95}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\US-16x08 Console
DefaultGroupName=US-16x08 Console
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
; Per-user install by default (no admin prompt); the first page lets the user choose "all users" instead.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=Output
OutputBaseFilename=US16x08_Panel_Setup_{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\Assets\US16x08_Panel.ico
; The panel is closed by [Code] below with a graceful taskkill. Restart Manager is turned off because a
; forced shutdown leaves an orphaned WinMM session in MidiSrv.exe that holds every MIDI port as BUSY.
CloseApplications=no
RestartApplications=no

[Messages]
WelcomeLabel2=This will install [name/ver] on your computer.%n%nIf the TASCAM US-16x08 audio driver is not already installed, setup will run TASCAM's official driver installer too (this may prompt for administrator permission). Windows MIDI Services is also required for the MIDI router.%n%nYour saved scenes, MIDI patches and synth links are kept when you upgrade or uninstall.

[Tasks]
; Ticked by default. Adds a shortcut to the Startup folder (the user's own, or All Users for an all-users install),
; so it shows in Task Manager > Startup apps and is removed by the uninstaller.
Name: "autostart"; Description: "Start the console automatically when I sign in to Windows"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
; TASCAM's official, TEAC-signed driver+ASIO installer (itself an Inno Setup), bundled as-is and run only
; when the us-16x08 driver is not already present. Extracted to {tmp} and auto-deleted. Adds ~12 MB.
Source: "redist\TASCAM_US-16x08_4.00.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: DriverMissing

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon
Name: "{autostartup}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: autostart

[Run]
; Driver first (only if missing). TASCAM's installer self-elevates (UAC); waituntilterminated waits for it.
Filename: "{tmp}\TASCAM_US-16x08_4.00.exe"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART"; StatusMsg: "Installing the TASCAM US-16x08 audio driver (may prompt for administrator)..."; Flags: waituntilterminated; Check: DriverMissing
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// True when the TASCAM us-16x08 kernel driver is NOT installed (its service registry key is absent).
// Gates both bundling-extraction and running TASCAM's installer, so an already-driven PC skips both.
function DriverMissing(): Boolean;
begin
  Result := not RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\us-16x08');
end;

function PanelIsRunning(): Boolean;
var
  ResultCode: Integer;
begin
  // find.exe returns 0 when the image name appears in the tasklist output
  Exec(ExpandConstant('{cmd}'), '/C tasklist /FI "IMAGENAME eq {#AppExeName}" /NH | find /I "{#AppExeName}"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := (ResultCode = 0);
end;

// Asks a running panel to close and waits up to 15 seconds. Tray-resident builds hide to tray on
// WM_CLOSE, so we first set the named quit event (which exits them cleanly, releasing MIDI ports);
// taskkill without /F is the fallback for older builds. Returns True when no panel is running.
function ClosePanelGracefully(): Boolean;
var
  ResultCode, Waited: Integer;
begin
  Result := not PanelIsRunning();
  if Result then Exit;

  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -Command "$e=$null; if([System.Threading.EventWaitHandle]::TryOpenExisting(''US16x08_Panel_QuitSignal_v1'',[ref]$e)){$e.Set();$e.Dispose()}"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1500);

  if PanelIsRunning() then
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Waited := 0;
  while PanelIsRunning() and (Waited < 15000) do
  begin
    Sleep(500);
    Waited := Waited + 500;
  end;
  Result := not PanelIsRunning();
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not ClosePanelGracefully() then
    Result := 'The US-16x08 Console is still running. Close it, then run setup again.';
end;

function InitializeUninstall(): Boolean;
begin
  Result := ClosePanelGracefully();
  if not Result then
    MsgBox('The US-16x08 Console is still running. Close it, then uninstall again.', mbError, MB_OK);
end;
