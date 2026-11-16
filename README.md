# US-16x08 Panel — DSP & MIDI Console V2

A C# / .NET 10 **WPF** control surface for the [TASCAM US-16x08](https://tascam.com/us/product/us-16x08/) USB audio interface, built as an Ableton Live-style remake of TASCAM's stock `US-16x08_SettingsPanel.exe`. It talks directly to the interface's on-board DSP over the USB vendor protocol, adds a line-out patchbay, and bundles a full USB/DIN MIDI router with a control-surface layer.

> Windows desktop app. Not affiliated with or endorsed by TASCAM/TEAC. Proprietary TASCAM/TEAC binaries (the stock panel and the audio driver) are **not** redistributed in this repository.

---

## What it does

Five tabs, switched from the top bar:

| Tab | Name | Purpose |
|-----|------|---------|
| 1 | **MIXER** | Full channel-strip mixer driving the hardware DSP: fader, pan, mute/solo, phase, 4-band EQ, and a compressor per channel. |
| 2 | **ROUTING** | Line-out patchbay — route any source to the interface's physical line outputs. |
| 3 | **MIDI** | USB/DIN MIDI patchbay and router with per-route transforms and an internal clock. |
| 4 | **CONTROL** | Control & Sync: MIDI-learn control-surface mapping, track↔synth links, clock settings, per-patch velocity/CC. |
| 5 | **ASIO** | ASIO / TLV inspector for the raw hardware protocol. |

### Mixer (hardware DSP)
- Per-channel **4-band EQ** and **compressor**, driven straight to the US-16x08 DSP over the USB TLV vendor protocol.
- **Clickable EQ4 / COMP strip badges** that toggle each block on and off, kept in sync with the rack switches.
- **Per-channel brickwall limiter** — the `LIMIT` toggle reuses the channel's single dynamics block as an ∞:1 limiter (forces ∞:1 ratio + 2 ms attack, relabels Threshold as *CEILING*). No DSP rewrite; a limiter is just the compressor at ∞:1.
- **Collapsible channel strips** — fold unused channels to a thin strip via the `‹` chevron, or `EXPAND ALL` to restore. Collapse state is saved per scene.
- Channel faders scale with the window the same way the master fader does.
- **Scene memory** — save/recall the full mixer, routing, and link state as named scenes (JSON).

### MIDI router & Control/Sync
- Lock-free router thread moving messages between USB and DIN MIDI ports, with per-route transforms and control-surface matching.
- **Control-surface mapping with MIDI Learn** — bind a controller's CCs/notes to mixer parameters.
- **Track ↔ synth links** — assign each audio input channel to a MIDI synth port + channel, with *keyboard-follows-selected-track*: selecting a mixer strip switches the controller to that strip's synth.
- **Internal MIDI clock** — BPM, tap tempo, Start/Stop/Continue, rate, and per-output enable (24 PPQN).
- **Per-patch velocity curve** and one CC remap.
- **Held-note release** — each output tracks notes sent without a Note Off; panics and port close send explicit Note Offs before CC120/CC123 (needed because some synths, e.g. the Moog Model 15, ignore All-Notes-Off).

### Desktop integration
- **Minimizes to the system tray** — both Minimize and Close (X) hide the window (no taskbar button); the app keeps running and the MIDI router stays live. Exit is only from the tray icon's right-click menu.
- Flat dark-mode mixing-board **app icon**.
- `--self-test <outDir>` opens each tab, saves a PNG per tab, and exits — used by the build script.

## Target hardware
- **TASCAM US-16x08** USB audio interface (16-in / 8-out).
- A Novation Launchkey MK4 49 as the primary control surface.
- Eight USB/DIN synths in the author's rig (CAT, WASP Deluxe, Model 15, K-2, SDS-3, Kobol Expander, Pro-800, Syncussion SY-1).
- Windows MIDI Services (`MidiSrv.exe`).

## Architecture

```
Hardware/
  TascamHardwareService.cs   USB vendor-control TLV protocol to the US-16x08 DSP
  MidiRoutingService.cs      winmm port lifecycle, lock-free router thread, transforms, clock out
  MidiClockGenerator.cs      internal 24 PPQN clock thread
Models/                      MixerModels, MidiModels, ControlModels
Services/                    scene memory + Control & Sync settings (JSON in %LOCALAPPDATA%)
ViewModels/                  MainViewModel (+ partial MainViewModel.ControlSync)
Controls/                    custom WPF faders, knobs, meters, EQ/comp curve editors, patchbays
MainWindow.xaml              the five-tab UI
```

**Threading rule:** the MIDI router, clock, and USB worker threads never touch WPF objects or raise `PropertyChanged`; they stage values that a 33 ms UI timer syncs. All winmm open/close calls are serialized under a single port-lifecycle lock.

Settings (scenes, routes, links, Control & Sync) persist as JSON in `%LOCALAPPDATA%\TASCAM_US16x08_AbletonPanel`.

## Build & run

Requires the .NET 10 SDK.

```
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish_v2
```

`build_v2.cmd` does this, copies the result to `US16x08_Panel.exe`, and launches it. Self-test:

```
US16x08_Panel.exe --self-test <outDir>
```

### Installer
`build_installer.cmd` produces a per-user Windows installer with [Inno Setup 6](https://jrsoftware.org/isinfo.php): a self-contained publish (no .NET needed on the target PC) compiled into `installer\Output\`. An optional task installs an autostart shortcut. The installer source is in `installer\US16x08_Panel.iss`.

> **Note on the driver:** the author's local installer also bundles TEAC's official signed US-16x08 driver and installs it only when absent. That driver binary is **excluded from this repository** — redistributing TEAC's driver may breach their license. Build the installer with your own licensed copy if you need the bundled-driver behaviour.

## Project layout notes
- The executable is `US16x08_Panel.exe`. The project file, C# namespace, and settings folder still read `US16x08_AbletonPanel` on purpose — renaming the settings folder would orphan saved scenes, routes, and links.
- Development milestones are kept as `V1/`, `V2_backup_before_control_sync/`, and `V3_backup_before_note_release/` source snapshots.
- Current version: **2.4.0**.

## Not (yet) implemented
Launchkey LED/display feedback · auto-solo of the linked audio strip · per-output clock division · poly-chain voice dispatch across the monosynths.

Real per-channel audio processing the hardware DSP lacks (e.g. a true noise gate) is intentionally **not** faked in software — the panel is a control surface, not in the audio path, so such features belong in the DAW or in firmware.
