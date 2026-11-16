using System;
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

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly TascamHardwareService _hardware;
    private readonly SceneMemoryService _sceneService;
    private readonly DispatcherTimer _meterTimer;
    private bool _suppressSync;

    private TrackChannelModel _selectedTrack;
    private SceneMemoryModel _selectedScene;
    private int _activeTabIndex; // 0 = Session Mixer & Devices, 1 = Line Out Routing Matrix, 2 = Audio/ASIO & Protocol Inspector
    private double _masterFaderDb;
    private bool _masterMuted;
    private double _masterLeftPeak;
    private double _masterRightPeak;
    private double _masterLeftHold;
    private double _masterRightHold;
    private bool _computerToStereoBus = true;
    private bool _trueBypass;
    private int _asioBufferSize = 256;
    private string _statusMessage = "Ready — TASCAM US-16x08 DSP Engine Active";
    private string _editableSceneName = "Live Band Session";

    public ObservableCollection<TrackChannelModel> Tracks { get; } = new();
    public ObservableCollection<LineOutRouteModel> LineOutRoutes { get; } = new();
    public ObservableCollection<SceneMemoryModel> Scenes { get; } = new();
    public ObservableCollection<string> PacketLog { get; } = new();

    public int[] AvailableBufferSizes => TascamDspTables.AsioBufferSizes;
    public int[] AvailableSampleRates { get; } = { 44100, 48000, 88200, 96000 };
    public string[] AvailableLineOutSources => TascamDspTables.LineOutSourceNames;

    public TrackChannelModel SelectedTrack
    {
        get => _selectedTrack;
        set
        {
            if (_selectedTrack == value || value == null) return;
            foreach (var t in Tracks)
                t.IsSelected = (t == value);
            SetProperty(ref _selectedTrack, value);
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

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    // Commands
    public ICommand SelectTrackCommand { get; }
    public ICommand LoadSceneCommand { get; }
    public ICommand SaveSceneCommand { get; }
    public ICommand InitMixerCommand { get; }
    public ICommand ExportSceneIniCommand { get; }
    public ICommand ClearAllSolosCommand { get; }
    public ICommand UnmuteAllCommand { get; }
    public ICommand ResetSelectedEqCommand { get; }
    public ICommand ResetSelectedCompCommand { get; }
    public ICommand ApplyRoutingPresetCommand { get; }
    public ICommand ReconnectHardwareCommand { get; }
    public ICommand ClearPacketLogCommand { get; }

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

        // Load 10 Scenes
        var loadedScenes = _sceneService.LoadOrCreateDefaultScenes();
        foreach (var sc in loadedScenes)
            Scenes.Add(sc);

        _selectedTrack = Tracks[0];
        _selectedTrack.IsSelected = true;
        _selectedScene = Scenes[0];
        _editableSceneName = _selectedScene.Name;

        // Wire Commands
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
                // Out 1-2 Master L/R, Out 3-8 Computer 3-8
                for (int i = 0; i < 8; i++)
                    LineOutRoutes[i].SourceIndex = i < 2 ? i : i + 2;
                StatusMessage = "Routing Preset: Line Out 1-2 -> Master L/R, Line Out 3-8 -> Computer 3-8.";
            }
            else if (mode == "DirectDAW")
            {
                // Out 1-8 -> Computer 1-8
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

        // Apply Scene 01 on startup
        ApplySceneSnapshot(Scenes[0]);

        // Start 60 FPS meter timer
        _meterTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _meterTimer.Tick += OnMeterTimerTick;
        _meterTimer.Start();
    }

    private void OnMeterTimerTick(object? sender, EventArgs e)
    {
        _hardware.UpdateMeters(Tracks, SelectedTrack, out double mL, out double mR);
        MasterLeftPeak = mL;
        MasterRightPeak = mR;
        MasterLeftHold = mL >= MasterLeftHold ? mL : Math.Max(mL, MasterLeftHold * 0.975);
        MasterRightHold = mR >= MasterRightHold ? mR : Math.Max(mR, MasterRightHold * 0.975);
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
                        // Snap pan to hard L20 / R20 and copy odd channel DSP settings to even channel
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

        // Synchronize stereo linked partner parameters
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

        // Dispatch hardware TLV command for track (and partner if linked)
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

                t.Compressor.IsEnabled = snap.CompEnabled;
                t.Compressor.ThresholdDb = snap.CompThreshold;
                t.Compressor.RatioIndex = snap.CompRatioIndex;
                t.Compressor.AttackMs = snap.CompAttack;
                t.Compressor.ReleaseMs = snap.CompRelease;
                t.Compressor.MakeupGainDb = snap.CompGain;
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
            LineOutRoutes);

        Scenes[slotIdx] = updated;
        SelectedScene = updated;
        _sceneService.SaveScenesToDisk(Scenes);
        StatusMessage = $"Saved current console state to Scene {updated.SlotNumber:00}: \"{updated.Name}\"";
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
                LineOutRoutes);
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
        _hardware.Dispose();
    }
}
