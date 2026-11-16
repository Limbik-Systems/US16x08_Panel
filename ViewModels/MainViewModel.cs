using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using US16x08_AbletonPanel.Hardware;
using US16x08_AbletonPanel.Models;
using US16x08_AbletonPanel.Services;

namespace US16x08_AbletonPanel.ViewModels;

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly TascamHardwareService _hardware;
    private readonly MidiRoutingService _midiService;
    private readonly SceneMemoryService _sceneService;
    private readonly DispatcherTimer _meterTimer;
    private bool _suppressSync;

    private TrackChannelModel _selectedTrack;
    private SceneMemoryModel _selectedScene;
    private MidiRouteModel? _selectedMidiRoute;
    private int _activeTabIndex; // 0 = Session Mixer, 1 = Line Out Routing, 2 = USB & DIN MIDI Patchbay, 3 = ASIO & USB TLV Inspector, 4 = Control & Sync
    private double _masterFaderDb;
    private bool _masterMuted;
    private double _masterLeftPeak;
    private double _masterRightPeak;
    private double _masterLeftHold;
    private double _masterRightHold;
    private bool _computerToStereoBus = true;
    private bool _trueBypass;
    private int _asioBufferSize = 256;
    private string _statusMessage = "Ready — TASCAM US-16x08 DSP & USB/DIN MIDI Router Active";
    private string _editableSceneName = "Flat Reference 0dB";
    private string _midiClockDisplay = "NO CLOCK";

    public ObservableCollection<TrackChannelModel> Tracks { get; } = new();
    public ObservableCollection<LineOutRouteModel> LineOutRoutes { get; } = new();
    public ObservableCollection<SceneMemoryModel> Scenes { get; } = new();
    public ObservableCollection<string> PacketLog { get; } = new();

    // V2 MIDI Collections
    public ObservableCollection<MidiPortModel> MidiInputPorts { get; } = new();
    public ObservableCollection<MidiPortModel> MidiOutputPorts { get; } = new();
    public ObservableCollection<MidiRouteModel> MidiRoutes { get; } = new();
    public ObservableCollection<MidiMonitorEvent> MidiEventLog { get; } = new();

    public int[] AvailableBufferSizes => TascamDspTables.AsioBufferSizes;
    public int[] AvailableSampleRates { get; } = { 44100, 48000, 88200, 96000 };
    public string[] AvailableLineOutSources => TascamDspTables.LineOutSourceNames;

    public string[] AvailableSourceChannelFilters { get; } =
    {
        "ALL CH (1-16)",
        "CH 1", "CH 2", "CH 3", "CH 4", "CH 5", "CH 6", "CH 7", "CH 8",
        "CH 9", "CH 10", "CH 11", "CH 12", "CH 13", "CH 14", "CH 15", "CH 16"
    };

    public string[] AvailableTargetChannelRemaps { get; } =
    {
        "PASS-THRU (Orig Ch)",
        "REMAP -> CH 1", "REMAP -> CH 2", "REMAP -> CH 3", "REMAP -> CH 4",
        "REMAP -> CH 5", "REMAP -> CH 6", "REMAP -> CH 7", "REMAP -> CH 8",
        "REMAP -> CH 9", "REMAP -> CH 10", "REMAP -> CH 11", "REMAP -> CH 12",
        "REMAP -> CH 13", "REMAP -> CH 14", "REMAP -> CH 15", "REMAP -> CH 16"
    };

    public TrackChannelModel SelectedTrack
    {
        get => _selectedTrack;
        set
        {
            if (_selectedTrack == value || value == null) return;
            foreach (var t in Tracks)
                t.IsSelected = (t == value);
            SetProperty(ref _selectedTrack, value);
            OnSelectedTrackChangedForMidiFocus(value);
        }
    }

    public SceneMemoryModel SelectedScene
    {
        get => _selectedScene;
        set
        {
            if (SetProperty(ref _selectedScene, value) && value != null)
            {
                EditableSceneName = value.Name;
            }
        }
    }

    public MidiRouteModel? SelectedMidiRoute
    {
        get => _selectedMidiRoute;
        set
        {
            if (SetProperty(ref _selectedMidiRoute, value))
            {
                OnPropertyChanged(nameof(HasSelectedMidiRoute));
            }
        }
    }

    public bool HasSelectedMidiRoute => _selectedMidiRoute != null;

    public string EditableSceneName
    {
        get => _editableSceneName;
        set => SetProperty(ref _editableSceneName, value);
    }

    public int ActiveTabIndex
    {
        get => _activeTabIndex;
        set => SetProperty(ref _activeTabIndex, value);
    }

    public double MasterFaderDb
    {
        get => _masterFaderDb;
        set
        {
            double clamped = Math.Round(Math.Clamp(value, -90.0, 6.0) * 10.0) / 10.0;
            if (SetProperty(ref _masterFaderDb, clamped))
            {
                OnPropertyChanged(nameof(MasterFaderDisplay));
                if (!_suppressSync)
                    _hardware.SetMasterFader(clamped);
            }
        }
    }

    public string MasterFaderDisplay => MasterFaderDb <= -89.5 ? "-∞ dB" : (MasterFaderDb > 0 ? $"+{MasterFaderDb:0.0} dB" : $"{MasterFaderDb:0.0} dB");

    public bool MasterMuted
    {
        get => _masterMuted;
        set
        {
            if (SetProperty(ref _masterMuted, value))
            {
                OnPropertyChanged(nameof(IsMasterActive));
                if (!_suppressSync)
                    _hardware.SetMasterMute(value);
            }
        }
    }

    public bool IsMasterActive
    {
        get => !_masterMuted;
        set => MasterMuted = !value;
    }

    public double MasterLeftPeak
    {
        get => _masterLeftPeak;
        set => SetProperty(ref _masterLeftPeak, value);
    }

    public double MasterRightPeak
    {
        get => _masterRightPeak;
        set => SetProperty(ref _masterRightPeak, value);
    }

    public double MasterLeftHold
    {
        get => _masterLeftHold;
        set => SetProperty(ref _masterLeftHold, value);
    }

    public double MasterRightHold
    {
        get => _masterRightHold;
        set => SetProperty(ref _masterRightHold, value);
    }

    public bool ComputerToStereoBus
    {
        get => _computerToStereoBus;
        set
        {
            if (SetProperty(ref _computerToStereoBus, value) && !_suppressSync)
            {
                _hardware.SetComputerToStereoBus(value);
                StatusMessage = value
                    ? "Computer Out -> Stereo Bus ENABLED (Tag 0x44 = 1)"
                    : "Computer Out -> Stereo Bus MUTED (Tag 0x44 = 0)";
            }
        }
    }

    public bool TrueBypass
    {
        get => _trueBypass;
        set
        {
            if (SetProperty(ref _trueBypass, value) && !_suppressSync)
            {
                _hardware.SetTrueBypass(value);
                StatusMessage = value
                    ? "MIXER TRUE BYPASS ACTIVE — Direct Hardware Pass-Through (Tag 0x45 = 1)"
                    : "DSP Mixer Active (Tag 0x45 = 0)";
            }
        }
    }

    public int AsioBufferSize
    {
        get => _asioBufferSize;
        set
        {
            if (SetProperty(ref _asioBufferSize, value))
            {
                _hardware.SetAsioBufferSize(value);
                OnPropertyChanged(nameof(LatencyEstimateText));
                StatusMessage = $"ASIO Buffer Size updated to {value} samples ({LatencyEstimateText})";
            }
        }
    }

    public int CurrentSampleRate
    {
        get => _hardware.SampleRate;
        set
        {
            _hardware.SetSimulatedSampleRate(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(SampleRateDisplay));
            OnPropertyChanged(nameof(LatencyEstimateText));
        }
    }

    public string SampleRateDisplay => $"{CurrentSampleRate / 1000.0:0.0} kHz";

    public string LatencyEstimateText
    {
        get
        {
            double ms = (AsioBufferSize / (double)Math.Max(44100, CurrentSampleRate)) * 1000.0;
            return $"{ms:0.00} ms @ {SampleRateDisplay}";
        }
    }

    public bool IsHardwareConnected => _hardware.IsHardwareConnected;
    public string ConnectionBadgeText => _hardware.IsHardwareConnected ? "USB DSP ONLINE" : "HARDWARE OFFLINE";
    public string DevicePathText => _hardware.DevicePath;
    public string FirmwareVersionText => _hardware.FirmwareVersion;
    public string DriverVersionText => _hardware.DriverVersion;

    public string MidiBadgeText => $"{MidiInputPorts.Count} IN / {MidiOutputPorts.Count} OUT // {MidiRoutes.Count(r => r.IsEnabled)} PATCHES";

    public string MidiClockDisplay
    {
        get => _midiClockDisplay;
        set => SetProperty(ref _midiClockDisplay, value);
    }

    public bool FilterMidiClockFromLog
    {
        get => _midiService.FilterClockFromMonitor;
        set
        {
            _midiService.FilterClockFromMonitor = value;
            OnPropertyChanged();
        }
    }

    public bool IsMidiMonitorPaused
    {
        get => _midiService.IsMonitorPaused;
        set
        {
            _midiService.IsMonitorPaused = value;
            OnPropertyChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    // Audio & Mixer Commands
    public ICommand SelectTrackCommand { get; }
    public ICommand LoadSceneCommand { get; }
    public ICommand SaveSceneCommand { get; }
    public ICommand InitMixerCommand { get; }
    public ICommand ExportSceneIniCommand { get; }
    public ICommand ClearAllSolosCommand { get; }
    public ICommand UnmuteAllCommand { get; }
    public ICommand ExpandAllTracksCommand { get; }
    public ICommand ResetSelectedEqCommand { get; }
    public ICommand ResetSelectedCompCommand { get; }
    public ICommand ApplyRoutingPresetCommand { get; }
    public ICommand ReconnectHardwareCommand { get; }
    public ICommand ClearPacketLogCommand { get; }

    // V2 MIDI Commands
    public ICommand RescanMidiPortsCommand { get; }
    public ICommand GlobalMidiPanicCommand { get; }
    public ICommand PortMidiPanicCommand { get; }
    public ICommand AuditionMidiPortCommand { get; }
    public ICommand ApplyMidiPresetCommand { get; }
    public ICommand RemoveSelectedMidiRouteCommand { get; }
    public ICommand ResetSelectedMidiRouteFilterCommand { get; }
    public ICommand TransposeOctaveCommand { get; }
    public ICommand ClearMidiMonitorCommand { get; }

    public event Action? MidiVisualsChanged;

    public MainViewModel()
    {
        _hardware = new TascamHardwareService();
        _hardware.PacketLogged += line =>
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                PacketLog.Insert(0, line);
                while (PacketLog.Count > 120)
                    PacketLog.RemoveAt(PacketLog.Count - 1);
            });
        };

        _midiService = new MidiRoutingService();

        _sceneService = new SceneMemoryService();
        _asioBufferSize = _hardware.GetAsioBufferSize();

        // Initialize 16 Input Tracks
        for (int i = 1; i <= 16; i++)
        {
            var trk = new TrackChannelModel(i)
            {
                TrackParameterChanged = OnTrackParameterChanged
            };
            Tracks.Add(trk);
        }

        // Initialize 8 Line Out Routes (Default: Out 1-2 = Master L/R, Out 3-8 = Computer 3-8)
        for (int i = 1; i <= 8; i++)
        {
            var route = new LineOutRouteModel
            {
                OutputNumber = i,
                SourceIndex = i <= 2 ? i - 1 : i + 1,
                RouteChanged = OnLineOutRouteChanged
            };
            LineOutRoutes.Add(route);
        }

        // Scan and open all USB & 5-pin DIN MIDI Ports
        InitializeMidiPortsAndRoutes();

        // Load 10 Scenes
        var loadedScenes = _sceneService.LoadOrCreateDefaultScenes();
        foreach (var sc in loadedScenes)
            Scenes.Add(sc);

        _selectedTrack = Tracks[0];
        _selectedTrack.IsSelected = true;
        _selectedScene = Scenes[0];
        _editableSceneName = _selectedScene.Name;

        // Wire Audio Commands
        SelectTrackCommand = new RelayCommand(p =>
        {
            if (p is TrackChannelModel trk)
                SelectedTrack = trk;
        });

        LoadSceneCommand = new RelayCommand(_ => ApplySceneSnapshot(SelectedScene));
        SaveSceneCommand = new RelayCommand(_ => SaveCurrentToSelectedScene());
        InitMixerCommand = new RelayCommand(_ =>
        {
            var flat = SceneMemoryService.CreateFlatZeroPreset(SelectedScene.SlotNumber, "Init Flat 0dB");
            ApplySceneSnapshot(flat);
            StatusMessage = "Mixer initialized to flat 0.0 dB studio default.";
        });
        ExportSceneIniCommand = new RelayCommand(_ => ExportCurrentSceneDialog());
        ClearAllSolosCommand = new RelayCommand(_ =>
        {
            foreach (var t in Tracks) t.IsSolo = false;
            StatusMessage = "Cleared all soloed tracks.";
        });
        UnmuteAllCommand = new RelayCommand(_ =>
        {
            foreach (var t in Tracks) t.IsMuted = false;
            MasterMuted = false;
            StatusMessage = "All channels unmuted.";
        });
        ExpandAllTracksCommand = new RelayCommand(_ =>
        {
            int n = 0;
            foreach (var t in Tracks) if (t.IsCollapsed) { t.IsCollapsed = false; n++; }
            StatusMessage = n > 0 ? $"Expanded {n} collapsed channel(s)." : "No channels are collapsed.";
        });
        ResetSelectedEqCommand = new RelayCommand(_ =>
        {
            SelectedTrack.IsLowCutEnabled = false;
            SelectedTrack.LowBand.FrequencyHz = 100;
            SelectedTrack.LowBand.GainDb = 0;
            SelectedTrack.LowMidBand.FrequencyHz = 400;
            SelectedTrack.LowMidBand.GainDb = 0;
            SelectedTrack.LowMidBand.QFactor = 1.0;
            SelectedTrack.HighMidBand.FrequencyHz = 2500;
            SelectedTrack.HighMidBand.GainDb = 0;
            SelectedTrack.HighMidBand.QFactor = 1.0;
            SelectedTrack.HighBand.FrequencyHz = 8000;
            SelectedTrack.HighBand.GainDb = 0;
            StatusMessage = $"Reset 4-Band EQ on {SelectedTrack.HeaderTitle}.";
        });
        ResetSelectedCompCommand = new RelayCommand(_ =>
        {
            var c = SelectedTrack.Compressor;
            c.ThresholdDb = -16;
            c.RatioIndex = 5;
            c.AttackMs = 20;
            c.ReleaseMs = 100;
            c.MakeupGainDb = 0;
            StatusMessage = $"Reset Compressor on {SelectedTrack.HeaderTitle}.";
        });
        ApplyRoutingPresetCommand = new RelayCommand(param =>
        {
            string mode = param?.ToString() ?? "Studio";
            if (mode == "Studio")
            {
                for (int i = 0; i < 8; i++)
                    LineOutRoutes[i].SourceIndex = i < 2 ? i : i + 2;
                StatusMessage = "Routing Preset: Line Out 1-2 -> Master L/R, Line Out 3-8 -> Computer 3-8.";
            }
            else if (mode == "DirectDAW")
            {
                for (int i = 0; i < 8; i++)
                    LineOutRoutes[i].SourceIndex = i + 2;
                StatusMessage = "Routing Preset: Line Out 1-8 -> Computer 1-8 (8-Channel DAW Stem Output).";
            }
            else if (mode == "AllMaster")
            {
                for (int i = 0; i < 8; i++)
                    LineOutRoutes[i].SourceIndex = i % 2;
                StatusMessage = "Routing Preset: All Line Outs 1-8 -> Master L/R Pairs.";
            }
        });
        ReconnectHardwareCommand = new RelayCommand(_ =>
        {
            bool ok = _hardware.TryConnectHardware();
            OnPropertyChanged(nameof(IsHardwareConnected));
            OnPropertyChanged(nameof(ConnectionBadgeText));
            OnPropertyChanged(nameof(DevicePathText));
            OnPropertyChanged(nameof(FirmwareVersionText));
            OnPropertyChanged(nameof(CurrentSampleRate));
            OnPropertyChanged(nameof(SampleRateDisplay));
            if (ok)
            {
                _hardware.ResetStateCache();
                SyncEntireMixerToHardware();
                StatusMessage = $"Connected to physical TASCAM US-16x08 ({_hardware.DevicePath})";
            }
            else
            {
                StatusMessage = "US-16x08 hardware not detected on USB — running in offline mode.";
            }
        });
        ClearPacketLogCommand = new RelayCommand(_ => PacketLog.Clear());

        // Wire V2 MIDI Commands
        RescanMidiPortsCommand = new RelayCommand(_ =>
        {
            RescanMidiPorts();
            StatusMessage = $"Scanned Windows MIDI Bus: {MidiInputPorts.Count} Inputs & {MidiOutputPorts.Count} Outputs active.";
        });

        GlobalMidiPanicCommand = new RelayCommand(_ =>
        {
            _midiService.SendGlobalPanic();
            StatusMessage = $"MIDI PANIC SENT — All Notes Off (CC#123) & All Sound Off (CC#120) on Ch 1–16 across all {MidiOutputPorts.Count} outputs.";
        });

        PortMidiPanicCommand = new RelayCommand(p =>
        {
            string? portName = p switch
            {
                MidiPortModel pm => pm.Name,
                string s => s,
                _ => SelectedMidiRoute?.DestPortName
            };
            if (!string.IsNullOrWhiteSpace(portName))
            {
                _midiService.SendPanicToPort(portName);
                StatusMessage = $"Sent MIDI Panic (All Notes Off Ch 1-16) to {portName}.";
            }
        });

        AuditionMidiPortCommand = new RelayCommand(p =>
        {
            string? portName = p switch
            {
                MidiPortModel pm => pm.Name,
                string s => s,
                _ => SelectedMidiRoute?.DestPortName
            };
            if (!string.IsNullOrWhiteSpace(portName))
            {
                var routeForPort = MidiRoutes.FirstOrDefault(r => r.IsEnabled && string.Equals(r.DestPortName, portName, StringComparison.OrdinalIgnoreCase))
                                   ?? MidiRoutes.FirstOrDefault(r => string.Equals(r.DestPortName, portName, StringComparison.OrdinalIgnoreCase));
                int prefCh = _midiService.GetPreferredChannelForOutput(portName);
                int ch = routeForPort?.TargetChannelRemap > 0
                    ? routeForPort.TargetChannelRemap
                    : (prefCh > 0 ? prefCh : 1);
                _midiService.SendAuditionNote(portName, ch, 60, 100);
                StatusMessage = $"Sent C4 (Note 60, Ch {ch}) audition pulse to {portName}.";
            }
        });

        ApplyMidiPresetCommand = new RelayCommand(p =>
        {
            string preset = p?.ToString() ?? "LaunchkeyAllSynths";
            ApplyMidiPreset(preset);
        });

        RemoveSelectedMidiRouteCommand = new RelayCommand(_ =>
        {
            if (SelectedMidiRoute != null)
            {
                var toRemove = SelectedMidiRoute;
                MidiRoutes.Remove(toRemove);
                SelectedMidiRoute = MidiRoutes.FirstOrDefault();
                SyncMidiRoutesSnapshot();
                StatusMessage = $"Removed MIDI patch: {toRemove.RouteTitle}";
            }
        });

        ResetSelectedMidiRouteFilterCommand = new RelayCommand(_ =>
        {
            if (SelectedMidiRoute != null)
            {
                SelectedMidiRoute.SourceChannelFilter = 0;
                SelectedMidiRoute.TargetChannelRemap = 0;
                SelectedMidiRoute.TransposeSemitones = 0;
                SelectedMidiRoute.MinNote = 0;
                SelectedMidiRoute.MaxNote = 127;
                SelectedMidiRoute.PassNotes = true;
                SelectedMidiRoute.PassControlChange = true;
                SelectedMidiRoute.PassPitchBend = true;
                SelectedMidiRoute.PassAftertouch = true;
                SelectedMidiRoute.PassProgramChange = true;
                SelectedMidiRoute.PassClockAndTransport = true;
                SelectedMidiRoute.PassSysEx = true;
                SelectedMidiRoute.VelocityCurve = 0;
                SelectedMidiRoute.FixedVelocity = 100;
                SelectedMidiRoute.CcRemapFrom = -1;
                SelectedMidiRoute.CcRemapTo = 74;
                SyncMidiRoutesSnapshot();
                StatusMessage = $"Reset MIDI filter & channel remap for {SelectedMidiRoute.RouteTitle}.";
            }
        });

        TransposeOctaveCommand = new RelayCommand(p =>
        {
            if (SelectedMidiRoute == null) return;
            if (int.TryParse(p?.ToString(), out int delta))
            {
                if (delta == 0)
                    SelectedMidiRoute.TransposeSemitones = 0;
                else
                    SelectedMidiRoute.TransposeSemitones = Math.Clamp(SelectedMidiRoute.TransposeSemitones + delta, -36, 36);
            }
        });

        ClearMidiMonitorCommand = new RelayCommand(_ => MidiEventLog.Clear());

        // Apply Scene 01 on startup
        ApplySceneSnapshot(Scenes[0]);

        // Internal MIDI clock, audio channel -> synth links, controller mappings (MainViewModel.ControlSync.cs)
        InitializeControlSync();

        // Start 60 FPS meter & MIDI LED decay timer
        _meterTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _meterTimer.Tick += OnMeterTimerTick;
        _meterTimer.Start();
    }

    private void InitializeMidiPortsAndRoutes()
    {
        var (inputs, outputs) = _midiService.ScanAndOpenPorts();
        MidiInputPorts.Clear();
        foreach (var inp in inputs)
            MidiInputPorts.Add(inp);

        MidiOutputPorts.Clear();
        foreach (var outp in outputs)
            MidiOutputPorts.Add(outp);

        var initialRoutes = _midiService.LoadSavedRoutesOrDefault(inputs, outputs);
        MidiRoutes.Clear();
        foreach (var r in initialRoutes)
        {
            r.RouteModified = _ => SyncMidiRoutesSnapshot();
            MidiRoutes.Add(r);
        }

        SelectedMidiRoute = MidiRoutes.FirstOrDefault();
        SyncMidiRoutesSnapshot();
    }

    public void RescanMidiPorts()
    {
        var (inputs, outputs) = _midiService.ScanAndOpenPorts();
        MidiInputPorts.Clear();
        foreach (var inp in inputs)
            MidiInputPorts.Add(inp);

        MidiOutputPorts.Clear();
        foreach (var outp in outputs)
            MidiOutputPorts.Add(outp);

        OnMidiPortsRescanned();
        SyncMidiRoutesSnapshot();
        OnPropertyChanged(nameof(MidiBadgeText));
    }

    public void HandleMidiRouteToggle(string sourcePortName, string destPortName, bool toggleIfAlreadyExists)
    {
        var existing = MidiRoutes.FirstOrDefault(r =>
            string.Equals(r.SourcePortName, sourcePortName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.DestPortName, destPortName, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            if (toggleIfAlreadyExists)
            {
                MidiRoutes.Remove(existing);
                if (ReferenceEquals(SelectedMidiRoute, existing))
                    SelectedMidiRoute = MidiRoutes.FirstOrDefault();
                SyncMidiRoutesSnapshot();
                StatusMessage = $"Disconnected MIDI patch: {sourcePortName} \u2192 {destPortName}";
            }
            else
            {
                existing.IsEnabled = true;
                SelectedMidiRoute = existing;
                SyncMidiRoutesSnapshot();
                StatusMessage = $"Selected MIDI patch: {existing.RouteTitle}";
            }
        }
        else
        {
            int prefCh = _midiService.GetPreferredChannelForOutput(destPortName);
            var newRoute = new MidiRouteModel
            {
                SourcePortName = sourcePortName,
                DestPortName = destPortName,
                IsEnabled = true,
                TargetChannelRemap = prefCh,
                RouteModified = _ => SyncMidiRoutesSnapshot()
            };
            MidiRoutes.Add(newRoute);
            SelectedMidiRoute = newRoute;
            SyncMidiRoutesSnapshot();
            string chInfo = prefCh > 0 ? $" (Auto-Remapped \u2192 Ch {prefCh})" : "";
            StatusMessage = $"Patched MIDI: {sourcePortName} \u2192 {destPortName}{chInfo}";
        }
    }

    public void HandlePortChannelCycle(string destPortName, int delta)
    {
        var outPort = MidiOutputPorts.FirstOrDefault(p => string.Equals(p.Name, destPortName, StringComparison.OrdinalIgnoreCase));
        var activeRoute = MidiRoutes.FirstOrDefault(r => string.Equals(r.DestPortName, destPortName, StringComparison.OrdinalIgnoreCase));

        int current = activeRoute != null
            ? activeRoute.TargetChannelRemap
            : (outPort?.PreferredChannel ?? _midiService.GetPreferredChannelForOutput(destPortName));

        int next = (current + delta) % 17;
        if (next < 0) next += 17;

        _midiService.SetPreferredChannelForOutput(destPortName, next, true);
        if (outPort != null)
            outPort.PreferredChannel = next;

        bool updatedRoutes = false;
        foreach (var r in MidiRoutes.Where(r => string.Equals(r.DestPortName, destPortName, StringComparison.OrdinalIgnoreCase)))
        {
            r.TargetChannelRemap = next;
            SelectedMidiRoute = r;
            updatedRoutes = true;
        }

        if (updatedRoutes)
            SyncMidiRoutesSnapshot();

        string label = next == 0 ? "PASS-THRU (Original Ch)" : $"MIDI Channel {next}";
        StatusMessage = $"Set {destPortName} target channel to {label}.";
    }

    public void HandlePortDisconnectAll(string portName, bool isInput)
    {
        var toRemove = MidiRoutes
            .Where(r => isInput
                ? string.Equals(r.SourcePortName, portName, StringComparison.OrdinalIgnoreCase)
                : string.Equals(r.DestPortName, portName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (toRemove.Count == 0) return;

        foreach (var r in toRemove)
            MidiRoutes.Remove(r);

        if (SelectedMidiRoute != null && toRemove.Contains(SelectedMidiRoute))
            SelectedMidiRoute = MidiRoutes.FirstOrDefault();

        SyncMidiRoutesSnapshot();
        StatusMessage = $"Unplugged {toRemove.Count} MIDI patch cable(s) from {portName} (Sent All Notes Off).";
    }

    private void ApplyMidiPreset(string preset)
    {
        MidiRoutes.Clear();

        var launchkeyIn = MidiInputPorts.FirstOrDefault(p => p.Name.StartsWith("Launchkey", StringComparison.OrdinalIgnoreCase))
                          ?? MidiInputPorts.FirstOrDefault(p => p.Category == MidiPortCategory.UsbController);
        var tascamIn = MidiInputPorts.FirstOrDefault(p => p.Category == MidiPortCategory.TascamDin);
        var tascamOut = MidiOutputPorts.FirstOrDefault(p => p.Category == MidiPortCategory.TascamDin);

        if (preset == "LaunchkeyAllSynths")
        {
            var controller = launchkeyIn ?? tascamIn ?? MidiInputPorts.FirstOrDefault();
            if (controller != null)
            {
                foreach (var outPort in MidiOutputPorts.Where(o => o.Category is MidiPortCategory.UsbSynth or MidiPortCategory.TascamDin))
                {
                    var r = new MidiRouteModel
                    {
                        SourcePortName = controller.Name,
                        DestPortName = outPort.Name,
                        IsEnabled = true,
                        TargetChannelRemap = _midiService.GetPreferredChannelForOutput(outPort.Name),
                        RouteModified = _ => SyncMidiRoutesSnapshot()
                    };
                    MidiRoutes.Add(r);
                }
                StatusMessage = $"MIDI Preset: Routed {controller.ShortDisplayName} to all {MidiRoutes.Count} hardware USB synths & US-16x08 DIN.";
            }
        }
        else if (preset == "DinToUsbBridge")
        {
            if (tascamIn != null)
            {
                foreach (var outPort in MidiOutputPorts.Where(o => o.Category == MidiPortCategory.UsbSynth))
                {
                    MidiRoutes.Add(new MidiRouteModel
                    {
                        SourcePortName = tascamIn.Name,
                        DestPortName = outPort.Name,
                        IsEnabled = true,
                        TargetChannelRemap = _midiService.GetPreferredChannelForOutput(outPort.Name),
                        RouteModified = _ => SyncMidiRoutesSnapshot()
                    });
                }
            }
            if (launchkeyIn != null && tascamOut != null)
            {
                MidiRoutes.Add(new MidiRouteModel
                {
                    SourcePortName = launchkeyIn.Name,
                    DestPortName = tascamOut.Name,
                    IsEnabled = true,
                    TargetChannelRemap = _midiService.GetPreferredChannelForOutput(tascamOut.Name),
                    RouteModified = _ => SyncMidiRoutesSnapshot()
                });
            }
            StatusMessage = "MIDI Preset: US-16x08 5-Pin DIN <-> USB Hardware Synth Bridge active.";
        }
        else if (preset == "SplitSynthStack")
        {
            // Keyboard split example: Lower half (C-1..B3) to first 2 synths, Upper half (C4..G9) to remaining synths
            var controller = launchkeyIn ?? tascamIn ?? MidiInputPorts.FirstOrDefault();
            var synths = MidiOutputPorts.Where(o => o.Category == MidiPortCategory.UsbSynth).ToList();
            if (controller != null && synths.Count > 0)
            {
                for (int i = 0; i < synths.Count; i++)
                {
                    bool isBass = i < 2;
                    MidiRoutes.Add(new MidiRouteModel
                    {
                        SourcePortName = controller.Name,
                        DestPortName = synths[i].Name,
                        IsEnabled = true,
                        TargetChannelRemap = _midiService.GetPreferredChannelForOutput(synths[i].Name),
                        MinNote = isBass ? 0 : 60,
                        MaxNote = isBass ? 59 : 127,
                        RouteModified = _ => SyncMidiRoutesSnapshot()
                    });
                }
                StatusMessage = $"MIDI Preset: Split {controller.ShortDisplayName} at C4 (60) — Lower Bass Synths (0..59) / Upper Lead Synths (60..127).";
            }
        }
        else if (preset == "ClearAll")
        {
            StatusMessage = "Cleared all virtual MIDI patch cables (Sent All Notes Off to disconnected synths).";
        }

        SelectedMidiRoute = MidiRoutes.FirstOrDefault();
        SyncMidiRoutesSnapshot();
    }

    private void SyncMidiRoutesSnapshot()
    {
        if (_batchingMidiRoutes) return; // a batch edit (track focus) syncs once when it finishes
        _midiService.UpdateRoutesSnapshot(MidiRoutes);
        OnPropertyChanged(nameof(MidiBadgeText));
    }

    private void OnMeterTimerTick(object? sender, EventArgs e)
    {
        _hardware.UpdateMeters(Tracks, SelectedTrack, out double mL, out double mR);
        MasterLeftPeak = mL;
        MasterRightPeak = mR;
        double nextHoldL = mL >= MasterLeftHold ? mL : Math.Max(mL, MasterLeftHold * 0.975);
        double nextHoldR = mR >= MasterRightHold ? mR : Math.Max(mR, MasterRightHold * 0.975);
        MasterLeftHold = nextHoldL < 0.0002 ? 0.0 : nextHoldL;
        MasterRightHold = nextHoldR < 0.0002 ? 0.0 : nextHoldR;

        // Sync MIDI port & route activity LEDs on UI thread
        bool midiChanged = false;
        for (int i = 0; i < MidiInputPorts.Count; i++)
        {
            if (MidiInputPorts[i].SyncUiFromTimer())
                midiChanged = true;
        }

        for (int i = 0; i < MidiOutputPorts.Count; i++)
        {
            if (MidiOutputPorts[i].SyncUiFromTimer())
                midiChanged = true;
        }

        for (int i = 0; i < MidiRoutes.Count; i++)
        {
            if (MidiRoutes[i].SyncUiFromTimer())
                midiChanged = true;
        }

        // Drain queued monitor events on UI thread (at most 12 per frame so UI stays 100% responsive)
        int drained = 0;
        while (drained < 12 && _midiService.PendingMonitorEvents.TryDequeue(out var evt))
        {
            MidiEventLog.Insert(0, evt);
            while (MidiEventLog.Count > 120)
                MidiEventLog.RemoveAt(MidiEventLog.Count - 1);
            drained++;
        }

        if (midiChanged && ActiveTabIndex == 2)
        {
            MidiVisualsChanged?.Invoke();
        }

        ControlSyncTimerTick();

        double bpm = _midiService.CurrentClockBpm;
        if (_clock != null && _clock.IsEmitting)
            MidiClockDisplay = $"INT {ClockBpm:0.0} BPM";
        else
            MidiClockDisplay = bpm > 1.0 ? $"{bpm:0.0} BPM" : "NO CLOCK";
    }

    private TrackChannelModel? GetStereoPartner(TrackChannelModel track)
    {
        int idx = track.ChannelNumber - 1;
        int partnerIdx = track.IsOddChannel ? idx + 1 : idx - 1;
        return partnerIdx >= 0 && partnerIdx < Tracks.Count ? Tracks[partnerIdx] : null;
    }

    private void OnTrackParameterChanged(TrackChannelModel track, string propertyTag)
    {
        if (_suppressSync) return;

        var partner = GetStereoPartner(track);

        if (propertyTag == "StereoLink")
        {
            _suppressSync = true;
            try
            {
                if (partner != null)
                {
                    partner.IsStereoLinked = track.IsStereoLinked;
                    var odd = track.IsOddChannel ? track : partner;
                    var even = track.IsOddChannel ? partner : track;
                    odd.IsStereoRightPartner = false;
                    even.IsStereoRightPartner = track.IsStereoLinked;

                    if (track.IsStereoLinked)
                    {
                        odd.Pan = -20;
                        even.Pan = 20;
                        CopyChannelDspParameters(odd, even);
                    }
                    else
                    {
                        odd.Pan = 0;
                        even.Pan = 0;
                    }
                    _hardware.SetTrackPan(odd.ChannelNumber, odd.Pan);
                    _hardware.SetTrackPan(even.ChannelNumber, even.Pan);
                    _hardware.SetTrackCompressor(odd);
                    _hardware.SetTrackCompressor(even);
                }
            }
            finally
            {
                _suppressSync = false;
            }
            StatusMessage = track.IsStereoLinked
                ? $"Stereo Linked Channels {track.StereoPairLabel}"
                : $"Unlinked Channels {track.StereoPairLabel} (Dual Mono)";
            return;
        }

        if (track.IsStereoLinked && partner != null && propertyTag != "Pan")
        {
            _suppressSync = true;
            try
            {
                CopyChannelDspParameters(track, partner);
            }
            finally
            {
                _suppressSync = false;
            }
        }

        DispatchTrackHardwareCommand(track, propertyTag);
        if (track.IsStereoLinked && partner != null && propertyTag != "Pan")
        {
            DispatchTrackHardwareCommand(partner, propertyTag);
        }
    }

    private static void CopyChannelDspParameters(TrackChannelModel src, TrackChannelModel dst)
    {
        dst.FaderDb = src.FaderDb;
        dst.IsMuted = src.IsMuted;
        dst.IsSolo = src.IsSolo;
        dst.IsPhaseInverted = src.IsPhaseInverted;
        dst.IsLowCutEnabled = src.IsLowCutEnabled;
        dst.IsEqEnabled = src.IsEqEnabled;

        dst.LowBand.FrequencyHz = src.LowBand.FrequencyHz;
        dst.LowBand.GainDb = src.LowBand.GainDb;

        dst.LowMidBand.FrequencyHz = src.LowMidBand.FrequencyHz;
        dst.LowMidBand.GainDb = src.LowMidBand.GainDb;
        dst.LowMidBand.QFactor = src.LowMidBand.QFactor;

        dst.HighMidBand.FrequencyHz = src.HighMidBand.FrequencyHz;
        dst.HighMidBand.GainDb = src.HighMidBand.GainDb;
        dst.HighMidBand.QFactor = src.HighMidBand.QFactor;

        dst.HighBand.FrequencyHz = src.HighBand.FrequencyHz;
        dst.HighBand.GainDb = src.HighBand.GainDb;

        dst.Compressor.IsEnabled = src.Compressor.IsEnabled;
        dst.Compressor.ThresholdDb = src.Compressor.ThresholdDb;
        dst.Compressor.RatioIndex = src.Compressor.RatioIndex;
        dst.Compressor.AttackMs = src.Compressor.AttackMs;
        dst.Compressor.ReleaseMs = src.Compressor.ReleaseMs;
        dst.Compressor.MakeupGainDb = src.Compressor.MakeupGainDb;
    }

    private void DispatchTrackHardwareCommand(TrackChannelModel track, string propertyTag)
    {
        switch (propertyTag)
        {
            case "Fader":
                _hardware.SetTrackFader(track.ChannelNumber, track.FaderDb);
                break;
            case "Pan":
                _hardware.SetTrackPan(track.ChannelNumber, track.Pan);
                break;
            case "Mute":
                _hardware.SetTrackMute(track.ChannelNumber, track.IsMuted);
                break;
            case "Solo":
                _hardware.SetTrackSolo(track.ChannelNumber, track.IsSolo);
                break;
            case "Phase":
                _hardware.SetTrackPhase(track.ChannelNumber, track.IsPhaseInverted);
                break;
            case "LowCut":
            case "EQ:1":
                _hardware.SetTrackEqBand(track, track.LowBand);
                break;
            case "EQ:2":
                _hardware.SetTrackEqBand(track, track.LowMidBand);
                break;
            case "EQ:3":
                _hardware.SetTrackEqBand(track, track.HighMidBand);
                break;
            case "EQ:4":
                _hardware.SetTrackEqBand(track, track.HighBand);
                break;
            case "EqEnable":
                _hardware.SetTrackEqBand(track, track.LowBand);
                _hardware.SetTrackEqBand(track, track.LowMidBand);
                _hardware.SetTrackEqBand(track, track.HighMidBand);
                _hardware.SetTrackEqBand(track, track.HighBand);
                break;
            case "Comp":
                _hardware.SetTrackCompressor(track);
                break;
        }
    }

    private void OnLineOutRouteChanged(LineOutRouteModel route)
    {
        if (_suppressSync) return;
        _hardware.SetLineOutRoute(route.OutputNumber, route.SourceIndex);
        StatusMessage = $"Line Out {route.OutputNumber} routed from {route.SourceName}";
    }

    public void ApplySceneSnapshot(SceneMemoryModel scene)
    {
        if (scene == null) return;

        _suppressSync = true;
        try
        {
            MasterFaderDb = scene.MasterFaderDb;
            MasterMuted = scene.MasterMuted;
            ComputerToStereoBus = scene.ComputerToStereoBus;
            TrueBypass = scene.TrueBypass;

            for (int i = 0; i < Math.Min(LineOutRoutes.Count, scene.LineOutSources.Length); i++)
            {
                LineOutRoutes[i].SourceIndex = scene.LineOutSources[i];
            }

            foreach (var snap in scene.Tracks)
            {
                int idx = snap.ChannelNumber - 1;
                if (idx < 0 || idx >= Tracks.Count) continue;
                var t = Tracks[idx];

                if (!string.IsNullOrWhiteSpace(snap.CustomName))
                    t.CustomName = snap.CustomName;
                t.IsStereoLinked = snap.IsStereoLinked;
                t.IsStereoRightPartner = snap.IsStereoLinked && !t.IsOddChannel;
                t.IsPhaseInverted = snap.IsPhaseInverted;
                t.IsLowCutEnabled = snap.IsLowCutEnabled;
                t.IsEqEnabled = snap.IsEqEnabled;
                t.IsMuted = snap.IsMuted;
                t.IsSolo = snap.IsSolo;
                t.IsCollapsed = snap.IsCollapsed;
                t.Pan = snap.Pan;
                t.FaderDb = snap.FaderDb;

                t.LowBand.FrequencyHz = snap.LowFreq;
                t.LowBand.GainDb = snap.LowGain;
                t.LowMidBand.FrequencyHz = snap.LowMidFreq;
                t.LowMidBand.GainDb = snap.LowMidGain;
                t.LowMidBand.QFactor = snap.LowMidQ;
                t.HighMidBand.FrequencyHz = snap.HighMidFreq;
                t.HighMidBand.GainDb = snap.HighMidGain;
                t.HighMidBand.QFactor = snap.HighMidQ;
                t.HighBand.FrequencyHz = snap.HighFreq;
                t.HighBand.GainDb = snap.HighGain;

                // Limiter mode first: it forces ratio/attack, which the explicit values below then confirm.
                t.Compressor.IsLimiterMode = snap.CompLimiterMode;
                t.Compressor.IsEnabled = snap.CompEnabled;
                t.Compressor.ThresholdDb = snap.CompThreshold;
                t.Compressor.RatioIndex = snap.CompRatioIndex;
                t.Compressor.AttackMs = snap.CompAttack;
                t.Compressor.ReleaseMs = snap.CompRelease;
                t.Compressor.MakeupGainDb = snap.CompGain;
            }

            // If the scene has saved MIDI routes, restore them
            if (scene.MidiRoutes is { Count: > 0 })
            {
                MidiRoutes.Clear();
                foreach (var mr in scene.MidiRoutes)
                {
                    var clone = mr.Clone();
                    clone.RouteModified = _ => SyncMidiRoutesSnapshot();
                    MidiRoutes.Add(clone);
                }
                SelectedMidiRoute = MidiRoutes.FirstOrDefault();
                SyncMidiRoutesSnapshot();
            }
        }
        finally
        {
            _suppressSync = false;
        }

        SyncEntireMixerToHardware();
        StatusMessage = $"Loaded Scene {scene.SlotNumber:00}: \"{scene.Name}\"";
    }

    public void SaveCurrentToSelectedScene()
    {
        if (SelectedScene == null) return;
        int slotIdx = Scenes.IndexOf(SelectedScene);
        if (slotIdx < 0) return;

        string sceneName = string.IsNullOrWhiteSpace(EditableSceneName)
            ? $"Scene {SelectedScene.SlotNumber:00}"
            : EditableSceneName.Trim();

        var updated = SceneMemoryService.CaptureSnapshot(
            SelectedScene.SlotNumber,
            sceneName,
            Tracks,
            MasterFaderDb,
            MasterMuted,
            ComputerToStereoBus,
            TrueBypass,
            LineOutRoutes,
            MidiRoutes);

        Scenes[slotIdx] = updated;
        SelectedScene = updated;
        _sceneService.SaveScenesToDisk(Scenes);
        _midiService.SaveRoutesToDisk(MidiRoutes);
        StatusMessage = $"Saved current console & MIDI patch state to Scene {updated.SlotNumber:00}: \"{updated.Name}\"";
    }

    private void ExportCurrentSceneDialog()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export TASCAM US-16x08 Scene Preset",
            Filter = "TASCAM Scene INI (*.ini)|*.ini|All Files (*.*)|*.*",
            FileName = $"{EditableSceneName.Replace(' ', '_')}.ini"
        };

        if (dlg.ShowDialog() == true)
        {
            var snap = SceneMemoryService.CaptureSnapshot(
                SelectedScene?.SlotNumber ?? 1,
                EditableSceneName,
                Tracks,
                MasterFaderDb,
                MasterMuted,
                ComputerToStereoBus,
                TrueBypass,
                LineOutRoutes,
                MidiRoutes);
            SceneMemoryService.ExportTascamIni(dlg.FileName, snap);
            StatusMessage = $"Exported scene to {dlg.FileName}";
        }
    }

    public void SyncEntireMixerToHardware()
    {
        _hardware.SetComputerToStereoBus(ComputerToStereoBus);
        _hardware.SetTrueBypass(TrueBypass);
        _hardware.SetMasterFader(MasterFaderDb);
        _hardware.SetMasterMute(MasterMuted);

        foreach (var route in LineOutRoutes)
            _hardware.SetLineOutRoute(route.OutputNumber, route.SourceIndex);

        foreach (var t in Tracks)
        {
            _hardware.SetTrackFader(t.ChannelNumber, t.FaderDb);
            _hardware.SetTrackPan(t.ChannelNumber, t.Pan);
            _hardware.SetTrackMute(t.ChannelNumber, t.IsMuted);
            _hardware.SetTrackSolo(t.ChannelNumber, t.IsSolo);
            _hardware.SetTrackPhase(t.ChannelNumber, t.IsPhaseInverted);
            _hardware.SetTrackEqBand(t, t.LowBand);
            _hardware.SetTrackEqBand(t, t.LowMidBand);
            _hardware.SetTrackEqBand(t, t.HighMidBand);
            _hardware.SetTrackEqBand(t, t.HighBand);
            _hardware.SetTrackCompressor(t);
        }
    }

    public void Dispose()
    {
        _meterTimer.Stop();
        DisposeControlSync();
        _midiService.Dispose();
        _hardware.Dispose();
    }
}
