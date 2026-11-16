using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows.Input;
using US16x08_AbletonPanel.Hardware;
using US16x08_AbletonPanel.Models;
using US16x08_AbletonPanel.Services;

namespace US16x08_AbletonPanel.ViewModels;

/// <summary>
/// V2.1 "Control &amp; Sync" features (tab 5):
///   1. Internal MIDI clock generator with tap tempo and transport
///   2. User-assignable audio input channel -> MIDI synth port / MIDI channel links ("keyboard follows selected track")
///   3. MIDI control surface mapping with MIDI Learn (controller CC / notes -> mixer parameters)
/// Per-route velocity curves and CC remapping live on <see cref="MidiRouteModel"/> and are edited from the same tab.
/// </summary>
public sealed partial class MainViewModel
{
    private ControlSettingsService? _controlSettingsService;
    private MidiClockGenerator? _clock;
    private bool _controlSyncReady;
    private bool _controlSettingsDirty;
    private int _controlSaveCountdown;
    private bool _batchingMidiRoutes;
    private bool _suppressLinkCallbacks;

    // ---- Internal clock state ----
    private double _clockBpm = 120.0;
    private bool _clockWhileStopped;
    private int _clockRateIndex = 1;
    private bool _clockBeatLit;
    private bool _isClockRunning;
    private readonly List<long> _tapTimestamps = new();
    private List<string>? _savedClockOutputPorts;

    // ---- Channel link state ----
    private bool _keyboardFollowsTrack;
    private string _focusControllerPort = "";

    // ---- Control surface state ----
    private bool _controlSurfaceEnabled = true;
    private bool _isLearning;
    private bool _learnAutoAdvance;
    private int _newMappingTargetIndex;
    private int _newMappingTrackIndex = 1;
    private MidiRoutingService.LearnCapture _lastLearnCapture;
    private long _lastLearnCaptureTimestamp;
    private readonly Dictionary<int, ControlMappingModel> _mappingsById = new();
    private readonly Dictionary<int, int> _pendingContinuousValues = new();

    public ObservableCollection<ChannelMidiLinkModel> ChannelLinks { get; } = new();
    public ObservableCollection<string> LinkPortChoices { get; } = new();
    public ObservableCollection<string> ControllerPortChoices { get; } = new();
    public ObservableCollection<ControlMappingModel> ControlMappings { get; } = new();

    public string[] AvailableClockRates { get; } = { "÷ 2  (HALF TIME)", "× 1  (NORMAL)", "× 2  (DOUBLE TIME)" };
    private static readonly double[] ClockRateMultipliers = { 0.5, 1.0, 2.0 };

    public string[] AvailableVelocityCurves { get; } =
    {
        "LINEAR (as played)",
        "SOFT TOUCH (louder at light velocity)",
        "HARD TOUCH (needs harder playing)",
        "FIXED VELOCITY"
    };

    public string[] AvailableCcSources { get; } = BuildCcNameList(includeOff: true);
    public string[] AvailableCcTargets { get; } = BuildCcNameList(includeOff: false);

    public string[] AvailableLinkMidiChannels { get; } =
    {
        "PORT CH",
        "CH 1", "CH 2", "CH 3", "CH 4", "CH 5", "CH 6", "CH 7", "CH 8",
        "CH 9", "CH 10", "CH 11", "CH 12", "CH 13", "CH 14", "CH 15", "CH 16"
    };

    public string[] AvailableControlTargets => ControlMappingModel.TargetLabels;

    public string[] AvailableMappingTracks { get; } =
    {
        "SELECTED TRACK",
        "IN 1", "IN 2", "IN 3", "IN 4", "IN 5", "IN 6", "IN 7", "IN 8",
        "IN 9", "IN 10", "IN 11", "IN 12", "IN 13", "IN 14", "IN 15", "IN 16"
    };

    // Commands (assigned in InitializeControlSync, which the constructor always calls)
    public ICommand ClockPlayCommand { get; private set; } = null!;
    public ICommand ClockStopCommand { get; private set; } = null!;
    public ICommand ClockContinueCommand { get; private set; } = null!;
    public ICommand ClockTapCommand { get; private set; } = null!;
    public ICommand ClockNudgeCommand { get; private set; } = null!;
    public ICommand FocusSelectedTrackCommand { get; private set; } = null!;
    public ICommand ClearChannelLinksCommand { get; private set; } = null!;
    public ICommand RemoveControlMappingCommand { get; private set; } = null!;
    public ICommand ClearControlMappingsCommand { get; private set; } = null!;

    private static string[] BuildCcNameList(bool includeOff)
    {
        var list = new List<string>(129);
        if (includeOff)
            list.Add("OFF");
        for (int cc = 0; cc < 128; cc++)
        {
            string? name = cc switch
            {
                1 => "Mod Wheel",
                2 => "Breath",
                5 => "Portamento Time",
                7 => "Volume",
                10 => "Pan",
                11 => "Expression",
                64 => "Sustain",
                65 => "Portamento On/Off",
                71 => "Resonance",
                72 => "Release",
                73 => "Attack",
                74 => "Filter Cutoff",
                91 => "Reverb",
                93 => "Chorus",
                _ => null
            };
            list.Add(name == null ? $"CC {cc}" : $"CC {cc}  ({name})");
        }
        return list.ToArray();
    }

    // ================================================================================================
    // Initialization / persistence
    // ================================================================================================

    private void InitializeControlSync()
    {
        _controlSettingsService = new ControlSettingsService();
        var settings = _controlSettingsService.Load();

        _clock = new MidiClockGenerator((status, isBeat) => _midiService.SendRealtimeToClockOutputs(status, isBeat));

        // Clock
        _clockBpm = Math.Round(Math.Clamp(settings.ClockBpm, MidiClockGenerator.MinBpm, MidiClockGenerator.MaxBpm) * 10.0) / 10.0;
        _clockRateIndex = Math.Clamp(settings.ClockRateIndex, 0, ClockRateMultipliers.Length - 1);
        _clockWhileStopped = settings.ClockWhileStopped;
        _savedClockOutputPorts = settings.ClockOutputPorts;
        _clock.Bpm = _clockBpm;
        _clock.RateMultiplier = ClockRateMultipliers[_clockRateIndex];

        // Channel links (always exactly 16 rows, one per audio input)
        _keyboardFollowsTrack = settings.KeyboardFollowsTrack;
        _focusControllerPort = settings.FocusControllerPort ?? "";
        ChannelLinks.Clear();
        for (int ch = 1; ch <= 16; ch++)
        {
            var saved = settings.ChannelLinks.FirstOrDefault(l => l.ChannelNumber == ch);
            var link = new ChannelMidiLinkModel
            {
                ChannelNumber = ch,
                PortName = saved?.PortName ?? "",
                MidiChannel = saved?.MidiChannel ?? 0,
                Track = Tracks[ch - 1]
            };
            link.Changed = OnChannelLinkChanged;
            ChannelLinks.Add(link);
        }

        // Control surface mappings
        _controlSurfaceEnabled = settings.ControlSurfaceEnabled;
        ControlMappings.Clear();
        foreach (var m in settings.ControlMappings)
            ControlMappings.Add(m);

        ClockPlayCommand = new RelayCommand(_ => StartClock());
        ClockStopCommand = new RelayCommand(_ => StopClock());
        ClockContinueCommand = new RelayCommand(_ => ContinueClock());
        ClockTapCommand = new RelayCommand(_ => TapTempo());
        ClockNudgeCommand = new RelayCommand(p =>
        {
            if (double.TryParse(p?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double delta))
                ClockBpm += delta;
        });

        FocusSelectedTrackCommand = new RelayCommand(_ => ApplyTrackFocus(SelectedTrack, force: true));
        ClearChannelLinksCommand = new RelayCommand(_ =>
        {
            _suppressLinkCallbacks = true;
            try
            {
                foreach (var link in ChannelLinks)
                {
                    link.PortName = "";
                    link.MidiChannel = 0;
                }
            }
            finally
            {
                _suppressLinkCallbacks = false;
            }
            MarkControlSettingsDirty();
            StatusMessage = "Cleared all audio channel → MIDI synth links.";
        });

        RemoveControlMappingCommand = new RelayCommand(p =>
        {
            if (p is ControlMappingModel m && ControlMappings.Remove(m))
            {
                RebuildControlBindings();
                MarkControlSettingsDirty();
                StatusMessage = $"Removed controller mapping: {m.SourceDisplay}";
            }
        });
        ClearControlMappingsCommand = new RelayCommand(_ =>
        {
            ControlMappings.Clear();
            RebuildControlBindings();
            MarkControlSettingsDirty();
            StatusMessage = "Cleared all controller mappings.";
        });

        _controlSyncReady = true;
        OnMidiPortsRescanned();
        RebuildControlBindings();
        _clock.ClockWhileStopped = _clockWhileStopped;
    }

    /// <summary>
    /// Called after every MIDI port scan (new <see cref="MidiPortModel"/> instances): re-applies clock destinations
    /// and refreshes the port pick-lists without ever clearing them (clearing would null the bound selections).
    /// </summary>
    private void OnMidiPortsRescanned()
    {
        if (!_controlSyncReady) return;

        foreach (var outPort in MidiOutputPorts)
        {
            outPort.SendClockChanged = null;
            outPort.SendClock = _savedClockOutputPorts != null
                ? _savedClockOutputPorts.Contains(outPort.Name, StringComparer.OrdinalIgnoreCase)
                : outPort.Category != MidiPortCategory.SoftwareSynth;
            outPort.SendClockChanged = OnPortSendClockChanged;
        }

        AddChoiceIfMissing(LinkPortChoices, ChannelMidiLinkModel.NonePortLabel);
        foreach (var outPort in MidiOutputPorts)
            AddChoiceIfMissing(LinkPortChoices, outPort.Name);
        foreach (var link in ChannelLinks)
        {
            if (link.IsLinked)
                AddChoiceIfMissing(LinkPortChoices, link.PortName); // keep links to synths that are currently switched off
        }

        foreach (var inPort in MidiInputPorts)
            AddChoiceIfMissing(ControllerPortChoices, inPort.Name);

        if (string.IsNullOrEmpty(_focusControllerPort))
        {
            var controller = MidiInputPorts.FirstOrDefault(p => p.Name.StartsWith("Launchkey", StringComparison.OrdinalIgnoreCase))
                             ?? MidiInputPorts.FirstOrDefault(p => p.Category == MidiPortCategory.UsbController);
            if (controller != null)
            {
                _focusControllerPort = controller.Name;
                OnPropertyChanged(nameof(FocusControllerPort));
            }
        }
        else
        {
            AddChoiceIfMissing(ControllerPortChoices, _focusControllerPort);
        }

        OnPropertyChanged(nameof(ClockOutputSummary));
    }

    private static void AddChoiceIfMissing(ObservableCollection<string> list, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        foreach (var existing in list)
        {
            if (string.Equals(existing, value, StringComparison.OrdinalIgnoreCase))
                return;
        }
        list.Add(value);
    }

    private void OnPortSendClockChanged(MidiPortModel port)
    {
        _savedClockOutputPorts = MidiOutputPorts.Where(p => p.SendClock).Select(p => p.Name).ToList();
        OnPropertyChanged(nameof(ClockOutputSummary));
        MarkControlSettingsDirty();
    }

    private void MarkControlSettingsDirty()
    {
        if (!_controlSyncReady) return;
        _controlSettingsDirty = true;
        _controlSaveCountdown = 30; // ~1 s of 33 ms timer ticks after the last change
    }

    private ControlSyncSettings CaptureControlSettings()
    {
        return new ControlSyncSettings
        {
            ClockBpm = _clockBpm,
            ClockWhileStopped = _clockWhileStopped,
            ClockRateIndex = _clockRateIndex,
            ClockOutputPorts = _savedClockOutputPorts ?? MidiOutputPorts.Where(p => p.SendClock).Select(p => p.Name).ToList(),
            KeyboardFollowsTrack = _keyboardFollowsTrack,
            FocusControllerPort = _focusControllerPort,
            ChannelLinks = ChannelLinks
                .Where(l => l.IsLinked || l.MidiChannel != 0)
                .Select(l => new ChannelMidiLinkModel { ChannelNumber = l.ChannelNumber, PortName = l.PortName, MidiChannel = l.MidiChannel })
                .ToList(),
            ControlSurfaceEnabled = _controlSurfaceEnabled,
            ControlMappings = ControlMappings.ToList()
        };
    }

    /// <summary>Runs on the UI thread from the 33 ms meter timer.</summary>
    private void ControlSyncTimerTick()
    {
        if (!_controlSyncReady || _clock == null) return;

        // Beat LED + transport state
        bool running = _clock.IsTransportRunning;
        if (running != _isClockRunning)
        {
            _isClockRunning = running;
            OnPropertyChanged(nameof(IsClockRunning));
            OnPropertyChanged(nameof(ClockTransportLabel));
        }
        bool beat = _clock.IsEmitting && (_clock.PulseCount % 24) < 8 && _clock.PulseCount > 0;
        if (beat != _clockBeatLit)
        {
            _clockBeatLit = beat;
            OnPropertyChanged(nameof(ClockBeatLit));
        }

        DrainLearnCaptures();
        DrainControlSurfaceEvents();

        if (_controlSettingsDirty && --_controlSaveCountdown <= 0)
        {
            _controlSettingsDirty = false;
            _controlSettingsService?.SaveInBackground(CaptureControlSettings());
        }
    }

    private void DisposeControlSync()
    {
        if (!_controlSyncReady) return;
        _controlSyncReady = false;

        try
        {
            _midiService.DisarmLearn();
            if (_clock != null)
            {
                _clock.ClockWhileStopped = false;
                _clock.Stop(); // sends MIDI Stop (0xFC) to the synths if the transport was running
                _clock.Dispose();
            }
            _controlSettingsService?.SaveNow(CaptureControlSettings());
        }
        catch
        {
            // Never block application exit
        }
    }

    // ================================================================================================
    // 1. Internal MIDI clock
    // ================================================================================================

    public double ClockBpm
    {
        get => _clockBpm;
        set
        {
            if (double.IsNaN(value)) return;
            double clamped = Math.Round(Math.Clamp(value, MidiClockGenerator.MinBpm, MidiClockGenerator.MaxBpm) * 10.0) / 10.0;
            if (SetProperty(ref _clockBpm, clamped))
            {
                if (_clock != null)
                    _clock.Bpm = clamped;
                OnPropertyChanged(nameof(ClockBpmDisplay));
                MarkControlSettingsDirty();
            }
        }
    }

    public string ClockBpmDisplay => $"{_clockBpm:0.0}";

    public bool IsClockRunning => _isClockRunning;

    public string ClockTransportLabel => _isClockRunning ? "▶ RUNNING" : "■ STOPPED";

    public bool ClockBeatLit => _clockBeatLit;

    public bool ClockWhileStopped
    {
        get => _clockWhileStopped;
        set
        {
            if (SetProperty(ref _clockWhileStopped, value))
            {
                if (_clock != null)
                    _clock.ClockWhileStopped = value;
                MarkControlSettingsDirty();
                StatusMessage = value
                    ? "Internal clock: 0xF8 pulses keep running while stopped (arpeggiators stay in tempo)."
                    : "Internal clock: pulses stop with the transport.";
            }
        }
    }

    public int ClockRateIndex
    {
        get => _clockRateIndex;
        set
        {
            int clamped = Math.Clamp(value, 0, ClockRateMultipliers.Length - 1);
            if (SetProperty(ref _clockRateIndex, clamped))
            {
                if (_clock != null)
                    _clock.RateMultiplier = ClockRateMultipliers[clamped];
                MarkControlSettingsDirty();
            }
        }
    }

    public string ClockOutputSummary
    {
        get
        {
            int n = MidiOutputPorts.Count(p => p.SendClock);
            return n == 0 ? "NO CLOCK OUTPUTS SELECTED" : $"CLOCK → {n} OF {MidiOutputPorts.Count} OUTPUTS";
        }
    }

    private void StartClock()
    {
        if (_clock == null) return;
        _clock.Start();
        StatusMessage = $"Internal MIDI clock START at {_clockBpm:0.0} BPM → {MidiOutputPorts.Count(p => p.SendClock)} output(s).";
    }

    private void StopClock()
    {
        if (_clock == null) return;
        _clock.Stop();
        StatusMessage = "Internal MIDI clock STOP (0xFC).";
    }

    private void ContinueClock()
    {
        if (_clock == null) return;
        _clock.Continue();
        StatusMessage = $"Internal MIDI clock CONTINUE at {_clockBpm:0.0} BPM.";
    }

    /// <summary>Averages up to the last four tap intervals; a pause longer than 2 s starts a new measurement.</summary>
    private void TapTempo()
    {
        long now = Stopwatch.GetTimestamp();
        if (_tapTimestamps.Count > 0)
        {
            double sinceLast = (now - _tapTimestamps[^1]) / (double)Stopwatch.Frequency;
            if (sinceLast > 2.0)
                _tapTimestamps.Clear();
        }

        _tapTimestamps.Add(now);
        while (_tapTimestamps.Count > 5)
            _tapTimestamps.RemoveAt(0);

        if (_tapTimestamps.Count >= 2)
        {
            double span = (_tapTimestamps[^1] - _tapTimestamps[0]) / (double)Stopwatch.Frequency;
            double avgInterval = span / (_tapTimestamps.Count - 1);
            if (avgInterval > 0.0)
            {
                ClockBpm = 60.0 / avgInterval;
                StatusMessage = $"Tap tempo: {_clockBpm:0.0} BPM ({_tapTimestamps.Count} taps).";
            }
        }
        else
        {
            StatusMessage = "Tap tempo: tap again to set the BPM.";
        }
    }

    // ================================================================================================
    // 2. Audio channel -> MIDI synth links
    // ================================================================================================

    /// <summary>When on, selecting a mixer strip points the controller keyboard at the synth linked to that strip.</summary>
    public bool KeyboardFollowsTrack
    {
        get => _keyboardFollowsTrack;
        set
        {
            if (SetProperty(ref _keyboardFollowsTrack, value))
            {
                MarkControlSettingsDirty();
                if (value)
                    ApplyTrackFocus(SelectedTrack, force: false);
                else
                    StatusMessage = "Keyboard no longer follows the selected track (MIDI patches stay as they are).";
            }
        }
    }

    public string FocusControllerPort
    {
        get => _focusControllerPort;
        set
        {
            if (value == null) return; // WPF writes null when the choice list changes
            if (SetProperty(ref _focusControllerPort, value))
                MarkControlSettingsDirty();
        }
    }

    private void OnChannelLinkChanged(ChannelMidiLinkModel link)
    {
        if (_suppressLinkCallbacks || !_controlSyncReady) return;

        // Picking a synth for the first time: pre-fill the MIDI channel that synth is already known to listen on
        if (link.IsLinked && link.MidiChannel == 0)
        {
            int pref = _midiService.GetPreferredChannelForOutput(link.PortName);
            if (pref > 0)
            {
                _suppressLinkCallbacks = true;
                try { link.MidiChannel = pref; }
                finally { _suppressLinkCallbacks = false; }
            }
        }

        MarkControlSettingsDirty();
        StatusMessage = link.IsLinked
            ? $"Linked {link.ChannelLabel} → {link.Summary}"
            : $"Unlinked {link.ChannelLabel} from MIDI.";

        if (_keyboardFollowsTrack && SelectedTrack != null && SelectedTrack.ChannelNumber == link.ChannelNumber)
            ApplyTrackFocus(SelectedTrack, force: false);
    }

    private void OnSelectedTrackChangedForMidiFocus(TrackChannelModel track)
    {
        if (!_controlSyncReady || !_keyboardFollowsTrack) return;
        ApplyTrackFocus(track, force: false);
    }

    /// <summary>
    /// Points the focus controller at the synth linked to <paramref name="track"/>: enables (or creates) the patch
    /// controller -> linked synth on the linked MIDI channel, and mutes the controller's patches to the synths linked
    /// to other audio channels. Patches to unlinked destinations are left untouched.
    /// </summary>
    private void ApplyTrackFocus(TrackChannelModel? track, bool force)
    {
        if (!_controlSyncReady || track == null) return;
        if (!force && !_keyboardFollowsTrack) return;

        int idx = track.ChannelNumber - 1;
        if (idx < 0 || idx >= ChannelLinks.Count) return;
        var link = ChannelLinks[idx];

        if (!link.IsLinked)
        {
            if (force)
                StatusMessage = $"{link.ChannelLabel} has no MIDI synth linked — assign one in the 4 // CONTROL tab.";
            return;
        }

        string controller = _focusControllerPort;
        if (string.IsNullOrEmpty(controller))
        {
            StatusMessage = "Choose the keyboard / controller input port in the 4 // CONTROL tab first.";
            return;
        }

        var otherLinkedPorts = new HashSet<string>(
            ChannelLinks.Where(l => l.IsLinked && !string.Equals(l.PortName, link.PortName, StringComparison.OrdinalIgnoreCase))
                        .Select(l => l.PortName),
            StringComparer.OrdinalIgnoreCase);

        int channel = link.MidiChannel > 0 ? link.MidiChannel : _midiService.GetPreferredChannelForOutput(link.PortName);

        _batchingMidiRoutes = true;
        MidiRouteModel? focusRoute = null;
        try
        {
            foreach (var r in MidiRoutes)
            {
                if (!string.Equals(r.SourcePortName, controller, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.Equals(r.DestPortName, link.PortName, StringComparison.OrdinalIgnoreCase))
                {
                    focusRoute ??= r;
                    r.IsEnabled = true;
                    if (link.MidiChannel > 0)
                        r.TargetChannelRemap = link.MidiChannel;
                }
                else if (otherLinkedPorts.Contains(r.DestPortName))
                {
                    r.IsEnabled = false;
                }
            }

            if (focusRoute == null)
            {
                focusRoute = new MidiRouteModel
                {
                    SourcePortName = controller,
                    DestPortName = link.PortName,
                    IsEnabled = true,
                    TargetChannelRemap = channel,
                    RouteModified = _ => SyncMidiRoutesSnapshot()
                };
                MidiRoutes.Add(focusRoute);
            }
        }
        finally
        {
            _batchingMidiRoutes = false;
        }

        SelectedMidiRoute = focusRoute;
        SyncMidiRoutesSnapshot();
        MidiVisualsChanged?.Invoke();

        string chText = focusRoute.TargetChannelRemap > 0 ? $"CH {focusRoute.TargetChannelRemap}" : "original channel";
        StatusMessage = $"Keyboard focus: {controller} → {link.PortName} ({chText}) for {track.HeaderTitle}.";
    }

    // ================================================================================================
    // 3. Control surface mapping + MIDI Learn
    // ================================================================================================

    public bool ControlSurfaceEnabled
    {
        get => _controlSurfaceEnabled;
        set
        {
            if (SetProperty(ref _controlSurfaceEnabled, value))
            {
                RebuildControlBindings();
                MarkControlSettingsDirty();
                StatusMessage = value
                    ? $"Control surface ON — {ControlMappings.Count} mapping(s) active."
                    : "Control surface BYPASSED — mapped controls pass through to the synth routes again.";
            }
        }
    }

    /// <summary>Bound two-way to the MIDI LEARN toggle: setting it from the UI arms or cancels learn.</summary>
    public bool IsLearning
    {
        get => _isLearning;
        set
        {
            if (value == _isLearning) return;
            if (value)
            {
                _lastLearnCaptureTimestamp = 0;
                _midiService.ArmLearn();
                SetLearningState(true);
                StatusMessage = $"MIDI Learn: move a fader / knob or press a button to assign it to {DescribeNewMappingTarget()}.";
            }
            else
            {
                _midiService.DisarmLearn();
                SetLearningState(false);
                StatusMessage = "MIDI Learn cancelled.";
            }
        }
    }

    private void SetLearningState(bool learning)
    {
        if (SetProperty(ref _isLearning, learning, nameof(IsLearning)))
            OnPropertyChanged(nameof(LearnButtonText));
    }

    public string LearnButtonText => _isLearning ? "● LISTENING — MOVE A CONTROL (click to cancel)" : "MIDI LEARN";

    /// <summary>After each learned control, step to the next input channel and keep listening (fast 8/16-fader setup).</summary>
    public bool LearnAutoAdvance
    {
        get => _learnAutoAdvance;
        set => SetProperty(ref _learnAutoAdvance, value);
    }

    public int NewMappingTargetIndex
    {
        get => _newMappingTargetIndex;
        set
        {
            int clamped = Math.Clamp(value, 0, ControlMappingModel.TargetLabels.Length - 1);
            if (SetProperty(ref _newMappingTargetIndex, clamped))
                OnPropertyChanged(nameof(IsNewMappingTrackScoped));
        }
    }

    public int NewMappingTrackIndex
    {
        get => _newMappingTrackIndex;
        set => SetProperty(ref _newMappingTrackIndex, Math.Clamp(value, 0, 16));
    }

    public bool IsNewMappingTrackScoped => ControlMappingModel.IsTrackScoped((ControlTargetKind)_newMappingTargetIndex);

    public string ControlMappingSummary => $"{ControlMappings.Count} MAPPING(S)";

    private string DescribeNewMappingTarget()
    {
        var kind = (ControlTargetKind)_newMappingTargetIndex;
        string label = ControlMappingModel.GetTargetLabel(kind);
        if (!ControlMappingModel.IsTrackScoped(kind))
            return label;
        return _newMappingTrackIndex == 0 ? $"{label} (selected track)" : $"{label} (IN {_newMappingTrackIndex})";
    }

    private void DrainLearnCaptures()
    {
        while (_midiService.PendingLearnCaptures.TryDequeue(out var cap))
        {
            if (!_isLearning)
                continue;

            // A knob or fader that is still moving right after being learned must not also grab the next slot
            long now = Stopwatch.GetTimestamp();
            bool sameAsPrevious = _lastLearnCaptureTimestamp != 0 &&
                                  cap == _lastLearnCapture &&
                                  (now - _lastLearnCaptureTimestamp) < Stopwatch.Frequency * 3 / 2;
            if (sameAsPrevious)
            {
                _lastLearnCaptureTimestamp = now;
                _midiService.ArmLearn();
                continue;
            }

            var kind = (ControlTargetKind)_newMappingTargetIndex;
            bool trackScoped = ControlMappingModel.IsTrackScoped(kind);

            // One physical control drives one parameter: re-learning a control replaces its old assignment
            for (int i = ControlMappings.Count - 1; i >= 0; i--)
            {
                if (ControlMappings[i].HasSameSource(cap.PortName, cap.ChannelOneBased, cap.IsNote, cap.Number))
                    ControlMappings.RemoveAt(i);
            }

            var mapping = new ControlMappingModel
            {
                SourcePortName = cap.PortName,
                MidiChannel = cap.ChannelOneBased,
                IsNote = cap.IsNote,
                Number = cap.Number,
                Target = kind,
                TrackNumber = trackScoped ? _newMappingTrackIndex : 0
            };
            ControlMappings.Add(mapping);
            RebuildControlBindings();
            MarkControlSettingsDirty();

            _lastLearnCapture = cap;
            _lastLearnCaptureTimestamp = now;
            StatusMessage = $"Learned: {mapping.SourceDisplay}  →  {mapping.TargetDisplay}";

            bool advance = _learnAutoAdvance && trackScoped && _newMappingTrackIndex >= 1 && _newMappingTrackIndex < 16;
            if (advance)
            {
                NewMappingTrackIndex = _newMappingTrackIndex + 1;
                _midiService.ArmLearn();
                StatusMessage += $"   |   next: move the control for IN {_newMappingTrackIndex}";
            }
            else
            {
                SetLearningState(false);
            }
        }
    }

    private void RebuildControlBindings()
    {
        _mappingsById.Clear();
        var bindings = new List<MidiRoutingService.ControlBinding>(ControlMappings.Count);
        foreach (var m in ControlMappings)
        {
            _mappingsById[m.Id] = m;
            bindings.Add(new MidiRoutingService.ControlBinding(m.SourcePortName, m.MidiChannel - 1, m.IsNote, m.Number, m.Id));
        }

        _midiService.SetControlBindings(_controlSurfaceEnabled ? bindings : Enumerable.Empty<MidiRoutingService.ControlBinding>());
        OnPropertyChanged(nameof(ControlMappingSummary));
    }

    private void DrainControlSurfaceEvents()
    {
        _pendingContinuousValues.Clear();

        while (_midiService.PendingControlEvents.TryDequeue(out var evt))
        {
            if (!_mappingsById.TryGetValue(evt.MappingId, out var mapping))
                continue;

            if (ControlMappingModel.IsButtonTarget(mapping.Target))
            {
                // Toggle on press only: Note On, or a CC button going high. Release (Note Off / CC 0) is ignored.
                bool pressed = evt.IsNote ? evt.Value > 0 : evt.Value >= 64;
                if (pressed)
                    ApplyControlButton(mapping);
            }
            else
            {
                // Faders and knobs: only the newest value per control matters within one 33 ms frame
                if (evt.IsNote && evt.Value == 0)
                    continue;
                _pendingContinuousValues[evt.MappingId] = evt.Value;
            }
        }

        foreach (var kv in _pendingContinuousValues)
        {
            if (_mappingsById.TryGetValue(kv.Key, out var mapping))
                ApplyControlValue(mapping, kv.Value);
        }
    }

    private TrackChannelModel? ResolveMappingTrack(ControlMappingModel mapping)
    {
        if (mapping.TrackNumber == 0)
            return SelectedTrack;
        int idx = mapping.TrackNumber - 1;
        return idx >= 0 && idx < Tracks.Count ? Tracks[idx] : null;
    }

    private void ApplyControlButton(ControlMappingModel mapping)
    {
        var track = ResolveMappingTrack(mapping);
        switch (mapping.Target)
        {
            case ControlTargetKind.TrackMute:
                if (track != null) { track.IsMuted = !track.IsMuted; mapping.LastValueDisplay = track.IsMuted ? "MUTED" : "ON"; }
                break;
            case ControlTargetKind.TrackSolo:
                if (track != null) { track.IsSolo = !track.IsSolo; mapping.LastValueDisplay = track.IsSolo ? "SOLO" : "OFF"; }
                break;
            case ControlTargetKind.MasterMute:
                MasterMuted = !MasterMuted;
                mapping.LastValueDisplay = MasterMuted ? "MUTED" : "ON";
                break;
            case ControlTargetKind.EqEnable:
                if (track != null) { track.IsEqEnabled = !track.IsEqEnabled; mapping.LastValueDisplay = track.IsEqEnabled ? "EQ ON" : "EQ OFF"; }
                break;
            case ControlTargetKind.CompEnable:
                if (track != null) { track.Compressor.IsEnabled = !track.Compressor.IsEnabled; mapping.LastValueDisplay = track.Compressor.IsEnabled ? "COMP ON" : "COMP OFF"; }
                break;
            case ControlTargetKind.ClockPlayStop:
                if (_clock != null && _clock.IsTransportRunning) StopClock(); else StartClock();
                mapping.LastValueDisplay = _clock != null && _clock.IsTransportRunning ? "PLAY" : "STOP";
                break;
            case ControlTargetKind.ClockTap:
                TapTempo();
                mapping.LastValueDisplay = $"{_clockBpm:0.0} BPM";
                break;
        }
    }

    private void ApplyControlValue(ControlMappingModel mapping, int value)
    {
        double norm = Math.Clamp(value, 0, 127) / 127.0;
        var track = ResolveMappingTrack(mapping);

        switch (mapping.Target)
        {
            case ControlTargetKind.TrackFader:
                if (track != null) { track.FaderDb = FaderNormalizedToDb(norm); mapping.LastValueDisplay = track.FaderDisplay; }
                break;
            case ControlTargetKind.TrackPan:
                if (track != null) { track.Pan = BipolarFromMidi(value, 20.0); mapping.LastValueDisplay = track.PanDisplay; }
                break;
            case ControlTargetKind.MasterFader:
                MasterFaderDb = FaderNormalizedToDb(norm);
                mapping.LastValueDisplay = MasterFaderDisplay;
                break;
            case ControlTargetKind.EqLowGain:
                if (track != null) { track.LowBand.GainDb = BipolarFromMidi(value, 12.0); mapping.LastValueDisplay = track.LowBand.GainDisplay; }
                break;
            case ControlTargetKind.EqLowMidGain:
                if (track != null) { track.LowMidBand.GainDb = BipolarFromMidi(value, 12.0); mapping.LastValueDisplay = track.LowMidBand.GainDisplay; }
                break;
            case ControlTargetKind.EqHighMidGain:
                if (track != null) { track.HighMidBand.GainDb = BipolarFromMidi(value, 12.0); mapping.LastValueDisplay = track.HighMidBand.GainDisplay; }
                break;
            case ControlTargetKind.EqHighGain:
                if (track != null) { track.HighBand.GainDb = BipolarFromMidi(value, 12.0); mapping.LastValueDisplay = track.HighBand.GainDisplay; }
                break;
            case ControlTargetKind.CompThreshold:
                if (track != null) { track.Compressor.ThresholdDb = -32.0 + norm * 32.0; mapping.LastValueDisplay = track.Compressor.ThresholdDisplay; }
                break;
            case ControlTargetKind.CompMakeup:
                if (track != null) { track.Compressor.MakeupGainDb = norm * 20.0; mapping.LastValueDisplay = track.Compressor.MakeupGainDisplay; }
                break;
            case ControlTargetKind.SelectTrack:
                int trackIdx = Math.Clamp(value * 16 / 128, 0, Tracks.Count - 1);
                SelectedTrack = Tracks[trackIdx];
                mapping.LastValueDisplay = $"IN {trackIdx + 1}";
                break;
        }
    }

    /// <summary>Maps 0..127 to -range..+range with MIDI value 64 landing exactly on the centre detent (0).</summary>
    private static double BipolarFromMidi(int value, double range)
    {
        int v = Math.Clamp(value, 0, 127);
        if (v == 64) return 0.0;
        return v < 64
            ? -range + (v / 64.0) * range
            : ((v - 64) / 63.0) * range;
    }

    /// <summary>Same piecewise perceptual taper as the on-screen fader (0 dB at 78 % travel, i.e. around CC value 99).</summary>
    private static double FaderNormalizedToDb(double norm)
    {
        norm = Math.Clamp(norm, 0.0, 1.0);
        if (norm >= 0.78)
            return ((norm - 0.78) / 0.22) * 6.0;
        if (norm >= 0.42)
            return -18.0 + ((norm - 0.42) / 0.36) * 18.0;
        if (norm >= 0.14)
            return -48.0 + ((norm - 0.14) / 0.28) * 30.0;
        return -90.0 + (norm / 0.14) * 42.0;
    }
}
