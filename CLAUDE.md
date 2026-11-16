# US16x08_AbletonPanel

C# / .NET 10 WPF remake of TASCAM's `US-16x08_SettingsPanel.exe` with an Ableton Live-style UI, a line-out patchbay, and a USB/DIN MIDI router. Target hardware: TASCAM US-16x08, Launchkey MK4 49, eight USB synths (CAT, WASP DELUXE, MODEL 15, K-2, SDS-3, KOBOL EXPANDER, PRO 800, SYNCUSSION SY-1). Windows MIDI Services (`MidiSrv.exe`) is installed.

## Build and run

```
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish_v2
```

`build_v2.cmd` does this, writes `build_log.txt`, copies the result to `US16x08_Panel.exe` and launches it.

- The exe is `US16x08_Panel.exe` (`AssemblyName` in the csproj). The project file, the code namespace and the settings folder still say `US16x08_AbletonPanel` on purpose. Renaming the settings folder would orphan saved scenes, routes and links. `US16x08_AbletonPanel.exe` in the root is the last build before the rename (2026-11-14 15:55).
- The panel minimizes to the system tray: both Minimize and Close (X) hide it (no taskbar button), app keeps running; Exit is only from the tray icon's right-click menu. Tray via `System.Windows.Forms.NotifyIcon` (`UseWindowsForms` on; its implicit `System.Drawing`/`System.Windows.Forms` usings are `<Using Remove>`d to avoid WPF clashes, and tray code fully-qualifies those types). `--self-test` does NOT enable the tray (App only calls `win.EnableTrayIcon()` in normal mode), so it still exits via `Shutdown(0)`.
- Because X now hides instead of exits, a graceful `taskkill` no longer closes a running panel. To exit it, set the named auto-reset event `US16x08_Panel_QuitSignal_v1` (the window registers a wait on it → real Close → MIDI ports freed). `build_v2.cmd` and the installer's `ClosePanelGracefully` do this via PowerShell `EventWaitHandle.TryOpenExisting(...).Set()`, with graceful `taskkill` as the fallback for pre-tray builds. Never force-kill (`/F`): it leaves an orphaned WinMM session in `MidiSrv.exe` holding all MIDI ports BUSY.
- `US16x08_Panel.exe --self-test <outDir>` opens each tab, saves a PNG per tab, prints `SELF_TEST_OK`, and exits.
- `build_installer.cmd` builds a Windows installer: a self-contained publish to `publish_installer\` (no .NET needed on the target PC), then Inno Setup 6 (per-user install at `%LOCALAPPDATA%\Programs\Inno Setup 6`) compiles `installer\US16x08_Panel.iss` to `installer\Output\US16x08_Panel_Setup_<version>.exe`, logging to `installer_build_log.txt`. The version is `<Version>` in the csproj (2.4.0). Never change the AppId GUID in the .iss: upgrades depend on it. Setup and uninstall close a running panel with a graceful `taskkill` and keep the settings folder. The installer is unsigned and uses the app icon. A task ticked by default ("Start the console automatically when I sign in") puts a shortcut in `{autostartup}`; the uninstaller removes it. It closes a running tray build via the quit signal on both install and uninstall (tested). Installer 2.3.0 also bundles TASCAM's official, TEAC-signed driver installer (`installer\redist\TASCAM_US-16x08_4.00.exe`, itself Inno Setup) and runs it `/VERYSILENT` only when the `us-16x08` service registry key is absent (`DriverMissing()` in the .iss gates both the bundled-file extraction and the run). Verified on this machine that an already-driven PC skips the driver step and the driver is untouched; the install-on-a-clean-PC path is UNtested (driver is already installed here, not removed to test). Note: bundling TEAC's driver is fine for personal use but redistributing the combined installer may breach TEAC's license. At sign-out WPF raises `Exit`, and `App.xaml.cs` disposes the view model there, which closes the MIDI ports. Autostart at an actual sign-in hasn't been tested.
- App icon: `Assets\US16x08_Panel.ico` (flat dark mixing board, 16-256 px), used as `ApplicationIcon` and as the `MainWindow` icon. Regenerate it with `dotnet run --file Assets\make_icon.cs -- Assets`. `Assets\*.cs` is excluded from the app build.
- Run file-based C# scripts with `dotnet run --file <script.cs>`. A bare `dotnet run <script.cs>` from this folder builds and launches the panel project instead.
- `midi.exe enum sessions --all` (in `C:\Program Files\Windows MIDI Services\Tools`) shows which process holds which MIDI endpoints.

## Layout

- `Hardware/TascamHardwareService.cs` — USB vendor-control TLV protocol to the US-16x08 DSP.
- `Hardware/MidiRoutingService.cs` — winmm port lifecycle, lock-free router thread, route transforms, control-surface matching, clock output.
- `Hardware/MidiClockGenerator.cs` — internal 24 PPQN clock thread.
- `Models/` — `MixerModels.cs`, `MidiModels.cs`, `ControlModels.cs`.
- `Services/` — scene memory and Control & Sync settings (JSON in `%LOCALAPPDATA%\TASCAM_US16x08_AbletonPanel`).
- `ViewModels/MainViewModel.cs` plus partial `MainViewModel.ControlSync.cs`.
- `MainWindow.xaml` — five tabs, switched by `ActiveTabIndex` (0 mixer, 1 line-out routing, 2 MIDI patchbay, 3 ASIO/TLV inspector, 4 Control & Sync). The tab buttons show a different order and short names: `1 // MIXER`, `2 // ROUTING`, `3 // MIDI`, `4 // CONTROL` (index 4), `5 // ASIO` (index 3). Window title: `TASCAM US-16x08 // DSP & MIDI Console V2`. Mixer channels collapse to a thin strip via the `‹` chevron in each strip header (`TrackChannelModel.IsCollapsed`, persisted per scene); a collapsed strip shows the channel number + `›` and expands on click; `EXPAND ALL` in the top bar (`ExpandAllTracksCommand`) restores all. The per-strip `EQ4`/`COMP` badges are clickable on/off toggles (ToggleButtons bound to `IsEqEnabled` / `Compressor.IsEnabled`, lit when on, styles `StripEqToggleStyle` / `StripCompToggleStyle`), kept in sync with the rack's EQ/Comp `ON` switches; `HasActiveEq`/`HasActiveComp` are now unused by the UI. Per-channel brickwall limiter: the comp panel's `LIMIT` toggle (`CompressorModel.IsLimiterMode`) reuses the channel's single dynamics block — forces ∞:1 ratio + 2 ms attack, relabels Threshold as CEILING, dims/locks the ratio & attack knobs, and the strip dynamics toggle reads `LIM` (`TrackChannelModel.DynamicsLabel`). No DSP rewrite (firmware is not modifiable — see [[firmware-update-format]]); limiter == compressor at ∞:1. Persisted per scene (`CompLimiterMode`); on load it is set before the other comp params.
- `V1/` — archived V1 source, excluded from the build. `V2_backup_before_control_sync/` — `.bak` copies of the five files as they were before the Control & Sync work.
- `US16x08_AbletonPanel_V2.exe` — last known-good V2 build (2026-11-14 11:49). Do not overwrite it.

## Current state (2026-11-14)

Control & Sync (`4 // CONTROL`) builds cleanly, passes `--self-test`, and has been checked on the real hardware:

1. Control surface mapping with MIDI Learn (controller CC/notes to mixer parameters).
2. User-assignable audio input channel to MIDI synth port + MIDI channel links, with "keyboard follows selected track". Confirmed working on hardware: selecting a mixer strip switches the Launchkey to that strip's synth.
3. Internal MIDI clock: BPM, tap tempo, Start/Stop/Continue, rate, per-output enable.
4. Per-patch velocity curve and one CC remap.

Held-note release: each output tracks the notes it has sent without a Note Off yet (`OpenOutputHandle.TrackSentMessage` / `ReleaseHeldNotes`, guarded by `WriteLock`). Every panic, and closing the ports, sends explicit Note Offs before the CC120/CC123 panic. This was needed because the MODEL 15 ignores CC120/CC123, so a note held across a track switch hung. Confirmed fixed on hardware. `V3_backup_before_note_release/` holds the build and sources from before this change.

Still unmeasured: clock jitter on this machine. Unexplained: the MIDI tab header showed "CLOCK: 546.9 BPM" while the internal clock was stopped at 120.

Not implemented: Launchkey LED/display feedback, auto-solo of the linked audio strip, per-output clock division, poly-chain voice dispatch across the monosynths.

## Working conventions

- James works in milestone versions: save a named version, then start the next. Ask before overwriting a saved version.
- Never fire `PropertyChanged` or touch WPF objects from the MIDI router, clock, or USB worker threads; stage values and sync them from the 33 ms UI timer, as the existing models do.
- All winmm open/close calls stay serialized under `_portLifecycleLock`.
