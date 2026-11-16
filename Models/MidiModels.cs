using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;

namespace US16x08_AbletonPanel.Models;

public enum MidiPortCategory
{
    TascamDin,      // US-16x08 5-pin DIN MIDI In/Out
    UsbController,  // Launchkey MK4 49, keyboards/controllers
    UsbSynth,       // Behringer / Hardware USB Synths (CAT, WASP, MODEL 15, K-2, PRO 800, KOBOL, SDS-3, SYNCUSSION, etc.)
    SoftwareSynth   // Microsoft GS Wavetable / General MIDI Synth
}

public sealed class MidiPortModel : ObservableObject
{
    private int _deviceIndex;
    private string _name = "";
    private bool _isInput;
    private bool _isOpen;
    private bool _isEnabled = true;
    private double _activityLevel; // 0.0 to 1.0 (UI thread only)
    private string _lastEventSummary = "Idle";
    private long _messageCount;
    private MidiPortCategory _category;
    private int _preferredChannel; // 0 = Pass-Thru, 1..16 = Hardware Synth MIDI Receive Channel
    private bool _sendClock;       // Internal MIDI clock generator sends 0xF8 / Start / Stop to this output

    // Background thread atomic staging fields (never fire PropertyChanged off UI thread)
    private int _bgIsOpen;
    private int _bgActivityFlag;
    private long _bgMessageCount;
    private volatile string? _bgLastEventSummary;

    public int DeviceIndex
    {
        get => _deviceIndex;
        set => SetProperty(ref _deviceIndex, value);
    }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                Category = ClassifyPort(value);
                OnPropertyChanged(nameof(CategoryLabel));
                OnPropertyChanged(nameof(AccentColorHex));
                OnPropertyChanged(nameof(ShortDisplayName));
            }
        }
    }

    public bool IsInput
    {
        get => _isInput;
        set => SetProperty(ref _isInput, value);
    }

    [JsonIgnore]
    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (SetProperty(ref _isOpen, value))
                OnPropertyChanged(nameof(StatusBadgeText));
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    [JsonIgnore]
    public double ActivityLevel
    {
        get => _activityLevel;
        set
        {
            if (SetProperty(ref _activityLevel, value))
                OnPropertyChanged(nameof(IsActiveNow));
        }
    }

    [JsonIgnore]
    public bool IsActiveNow => ActivityLevel > 0.15;

    [JsonIgnore]
    public string LastEventSummary
    {
        get => _lastEventSummary;
        set => SetProperty(ref _lastEventSummary, value);
    }

    [JsonIgnore]
    public long MessageCount
    {
        get => _messageCount;
        set => SetProperty(ref _messageCount, value);
    }

    [JsonIgnore]
    public MidiPortCategory Category
    {
        get => _category;
        private set => SetProperty(ref _category, value);
    }

    [JsonIgnore]
    public string CategoryLabel => Category switch
    {
        MidiPortCategory.TascamDin => "5-PIN DIN",
        MidiPortCategory.UsbController => "USB CTRL",
        MidiPortCategory.UsbSynth => "USB SYNTH",
        _ => "SW SYNTH"
    };

    [JsonIgnore]
    public string AccentColorHex => Category switch
    {
        MidiPortCategory.TascamDin => "#00D4FF",     // Ableton Cyan for US-16x08 DIN
        MidiPortCategory.UsbController => "#FFB000", // Ableton Amber for Launchkey Controller
        MidiPortCategory.UsbSynth => "#00E676",      // Emerald Green for Hardware USB Synths
        _ => "#9E9E9E"                               // Grey for Software Synth
    };

    [JsonIgnore]
    public string StatusBadgeText => IsOpen ? "ONLINE" : "CLOSED";

    public int PreferredChannel
    {
        get => _preferredChannel;
        set
        {
            int clamped = Math.Clamp(value, 0, 16);
            if (SetProperty(ref _preferredChannel, clamped))
                OnPropertyChanged(nameof(PreferredChannelBadge));
        }
    }

    /// <summary>
    /// When true, the internal MIDI clock generator sends Clock / Start / Continue / Stop to this output port.
    /// Persisted by port name in control_sync_v2.json (not on the port model itself).
    /// </summary>
    [JsonIgnore]
    public bool SendClock
    {
        get => _sendClock;
        set
        {
            if (SetProperty(ref _sendClock, value))
                SendClockChanged?.Invoke(this);
        }
    }

    [JsonIgnore]
    public Action<MidiPortModel>? SendClockChanged { get; set; }

    [JsonIgnore]
    public string PreferredChannelBadge => _preferredChannel == 0 ? "CH:PASS" : $"CH {_preferredChannel}";

    [JsonIgnore]
    public string ShortDisplayName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name)) return "MIDI Port";
            if (Name.StartsWith("MIDIOUT2 (", StringComparison.OrdinalIgnoreCase) && !Name.EndsWith(')'))
                return Name + ")";
            return Name;
        }
    }

    public void SetOpenStateFromBackground(bool isOpen)
    {
        Interlocked.Exchange(ref _bgIsOpen, isOpen ? 1 : 0);
    }

    /// <summary>
    /// Thread-safe, lock-free method called by the MIDI router thread without touching WPF Dispatcher or PropertyChanged.
    /// </summary>
    public void RecordActivityFromBackground(string? summary)
    {
        Interlocked.Exchange(ref _bgActivityFlag, 1);
        Interlocked.Increment(ref _bgMessageCount);
        if (summary != null)
            _bgLastEventSummary = summary;
    }

    /// <summary>
    /// Called on the UI thread at 30 FPS to sync activity LEDs and counters safely.
    /// </summary>
    public bool SyncUiFromTimer()
    {
        bool changed = false;
        bool openNow = Volatile.Read(ref _bgIsOpen) == 1;
        if (openNow != _isOpen)
        {
            IsOpen = openNow;
            changed = true;
        }

        if (Interlocked.Exchange(ref _bgActivityFlag, 0) == 1)
        {
            ActivityLevel = 1.0;
            changed = true;
        }
        else if (_activityLevel > 0.01)
        {
            ActivityLevel = _activityLevel * 0.80;
            changed = true;
        }
        else if (_activityLevel != 0.0)
        {
            ActivityLevel = 0.0;
            changed = true;
        }

        long cnt = Interlocked.Read(ref _bgMessageCount);
        if (cnt != _messageCount)
            MessageCount = cnt;

        string? sum = _bgLastEventSummary;
        if (sum != null && !string.Equals(sum, _lastEventSummary, StringComparison.Ordinal))
            LastEventSummary = sum;

        return changed;
    }

    public static MidiPortCategory ClassifyPort(string portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
            return MidiPortCategory.UsbSynth;

        string u = portName.ToUpperInvariant();
        if (u.Contains("US-16X08"))
            return MidiPortCategory.TascamDin;
        if (u.Contains("LAUNCHKEY") || u.Contains("KEYSTEP") || u.Contains("ARTURIA") || u.Contains("NOVATION") || u.Contains("MPK") || u.Contains("PAD"))
            return MidiPortCategory.UsbController;
        if (u.Contains("WAVETABLE") || u.Contains("GENERAL MIDI"))
            return MidiPortCategory.SoftwareSynth;
        return MidiPortCategory.UsbSynth;
    }
}

public sealed class MidiRouteModel : ObservableObject
{
    private string _sourcePortName = "";
    private string _destPortName = "";
    private bool _isEnabled = true;

    // Channel Filter: 0 = ALL (1..16), 1..16 = Only pass messages matching SourceChannelFilter
    private int _sourceChannelFilter;

    // Channel Remap: 0 = PASS-THRU (preserve original channel), 1..16 = Remap to target channel
    private int _targetChannelRemap;

    // Semitone transpose (-36..+36) and Note Split Range (0..127)
    private int _transposeSemitones;
    private int _minNote = 0;
    private int _maxNote = 127;

    // Message Type Filters
    private bool _passNotes = true;
    private bool _passControlChange = true;
    private bool _passPitchBend = true;
    private bool _passAftertouch = true;
    private bool _passProgramChange = true;
    private bool _passClockAndTransport = true;
    private bool _passSysEx = true;

    // Velocity curve (0 = Linear, 1 = Soft touch, 2 = Hard touch, 3 = Fixed) applied to Note On velocity
    private int _velocityCurve;
    private int _fixedVelocity = 100;

    // CC remap: -1 = off, 0..127 = incoming controller number rewritten to CcRemapTo
    private int _ccRemapFrom = -1;
    private int _ccRemapTo = 74;

    // Live UI state (updated only on UI thread)
    private double _routeActivity;
    private long _routedCount;

    // Background staging fields
    private int _bgActivityFlag;
    private long _bgRoutedCount;

    public string SourcePortName
    {
        get => _sourcePortName;
        set
        {
            if (SetProperty(ref _sourcePortName, value))
                OnPropertyChanged(nameof(RouteTitle));
        }
    }

    public string DestPortName
    {
        get => _destPortName;
        set
        {
            if (SetProperty(ref _destPortName, value))
                OnPropertyChanged(nameof(RouteTitle));
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                OnPropertyChanged(nameof(FilterSummary));
                RouteModified?.Invoke(this);
            }
        }
    }

    public int SourceChannelFilter
    {
        get => _sourceChannelFilter;
        set
        {
            int clamped = Math.Clamp(value, 0, 16);
            if (SetProperty(ref _sourceChannelFilter, clamped))
            {
                OnPropertyChanged(nameof(SourceChannelLabel));
                OnPropertyChanged(nameof(FilterSummary));
                RouteModified?.Invoke(this);
            }
        }
    }

    public int TargetChannelRemap
    {
        get => _targetChannelRemap;
        set
        {
            int clamped = Math.Clamp(value, 0, 16);
            if (SetProperty(ref _targetChannelRemap, clamped))
            {
                OnPropertyChanged(nameof(TargetChannelLabel));
                OnPropertyChanged(nameof(FilterSummary));
                RouteModified?.Invoke(this);
            }
        }
    }

    public int TransposeSemitones
    {
        get => _transposeSemitones;
        set
        {
            int clamped = Math.Clamp(value, -36, 36);
            if (SetProperty(ref _transposeSemitones, clamped))
            {
                OnPropertyChanged(nameof(TransposeDisplay));
                OnPropertyChanged(nameof(FilterSummary));
                RouteModified?.Invoke(this);
            }
        }
    }

    public int MinNote
    {
        get => _minNote;
        set
        {
            int clamped = Math.Clamp(value, 0, 127);
            if (SetProperty(ref _minNote, clamped))
            {
                OnPropertyChanged(nameof(MinNoteDisplay));
                OnPropertyChanged(nameof(FilterSummary));
                RouteModified?.Invoke(this);
            }
        }
    }

    public int MaxNote
    {
        get => _maxNote;
        set
        {
            int clamped = Math.Clamp(value, 0, 127);
            if (SetProperty(ref _maxNote, clamped))
            {
                OnPropertyChanged(nameof(MaxNoteDisplay));
                OnPropertyChanged(nameof(FilterSummary));
                RouteModified?.Invoke(this);
            }
        }
    }

    public bool PassNotes
    {
        get => _passNotes;
        set
        {
            if (SetProperty(ref _passNotes, value))
            {
                OnPropertyChanged(nameof(FilterSummary));
                RouteModified?.Invoke(this);
            }
        }
    }

    public bool PassControlChange
    {
        get => _passControlChange;
        set
        {
            if (SetProperty(ref _passControlChange, value))
            {
                OnPropertyChanged(nameof(FilterSummary));
                RouteModified?.Invoke(this);
            }
        }
    }

    public bool PassPitchBend
    {
        get => _passPitchBend;
        set
        {
            if (SetProperty(ref _passPitchBend, value))
            {
                OnPropertyChanged(nameof(FilterSummary));
                RouteModified?.Invoke(this);
            }
        }
    }

    public bool PassAftertouch
    {
        get => _passAftertouch;
        set
        {
            if (SetProperty(ref _passAftertouch, value))
            {
                OnPropertyChanged(nameof(FilterSummary));
                RouteModified?.Invoke(this);
            }
        }
    }

    public bool PassProgramChange
    {
        get => _passProgramChange;
        set
        {
            if (SetProperty(ref _passProgramChange, value))
            {
                OnPropertyChanged(nameof(FilterSummary));
                RouteModified?.Invoke(this);
            }
        }
    }

    public bool PassClockAndTransport
    {
        get => _passClockAndTransport;
        set
        {
            if (SetProperty(ref _passClockAndTransport, value))
            {
                OnPropertyChanged(nameof(FilterSummary));
                RouteModified?.Invoke(this);
            }
        }
    }

    public bool PassSysEx
    {
        get => _passSysEx;
        set
        {
            if (SetProperty(ref _passSysEx, value))
            {
                OnPropertyChanged(nameof(FilterSummary));
                RouteModified?.Invoke(this);
            }
        }
    }

    public int VelocityCurve
    {
        get => _velocityCurve;
        set
        {
            int clamped = Math.Clamp(value, 0, 3);
            if (SetProperty(ref _velocityCurve, clamped))
            {
                OnPropertyChanged(nameof(IsFixedVelocity));
                OnPropertyChanged(nameof(FilterSummary));
                RouteModified?.Invoke(this);
            }
        }
    }

    public int FixedVelocity
    {
        get => _fixedVelocity;
        set
        {
            int clamped = Math.Clamp(value, 1, 127);
            if (SetProperty(ref _fixedVelocity, clamped))
            {
                OnPropertyChanged(nameof(FilterSummary));
                RouteModified?.Invoke(this);
            }
        }
    }

    [JsonIgnore]
    public bool IsFixedVelocity => _velocityCurve == 3;

    public int CcRemapFrom
    {
        get => _ccRemapFrom;
        set
        {
            int clamped = Math.Clamp(value, -1, 127);
            if (SetProperty(ref _ccRemapFrom, clamped))
            {
                OnPropertyChanged(nameof(CcRemapFromIndex));
                OnPropertyChanged(nameof(IsCcRemapActive));
                OnPropertyChanged(nameof(FilterSummary));
                RouteModified?.Invoke(this);
            }
        }
    }

    public int CcRemapTo
    {
        get => _ccRemapTo;
        set
        {
            int clamped = Math.Clamp(value, 0, 127);
            if (SetProperty(ref _ccRemapTo, clamped))
            {
                OnPropertyChanged(nameof(FilterSummary));
                RouteModified?.Invoke(this);
            }
        }
    }

    /// <summary>ComboBox index helper: 0 = OFF, 1..128 = CC 0..127.</summary>
    [JsonIgnore]
    public int CcRemapFromIndex
    {
        get => _ccRemapFrom + 1;
        set => CcRemapFrom = value - 1;
    }

    [JsonIgnore]
    public bool IsCcRemapActive => _ccRemapFrom >= 0;

    [JsonIgnore]
    public double RouteActivity
    {
        get => _routeActivity;
        set => SetProperty(ref _routeActivity, value);
    }

    [JsonIgnore]
    public long RoutedCount
    {
        get => _routedCount;
        set => SetProperty(ref _routedCount, value);
    }

    public void RecordRouteActivityFromBackground()
    {
        Interlocked.Exchange(ref _bgActivityFlag, 1);
        Interlocked.Increment(ref _bgRoutedCount);
    }

    public bool SyncUiFromTimer()
    {
        bool changed = false;
        if (Interlocked.Exchange(ref _bgActivityFlag, 0) == 1)
        {
            RouteActivity = 1.0;
            changed = true;
        }
        else if (_routeActivity > 0.01)
        {
            RouteActivity = _routeActivity * 0.80;
            changed = true;
        }
        else if (_routeActivity != 0.0)
        {
            RouteActivity = 0.0;
            changed = true;
        }

        long cnt = Interlocked.Read(ref _bgRoutedCount);
        if (cnt != _routedCount)
            RoutedCount = cnt;

        return changed;
    }

    [JsonIgnore]
    public string RouteTitle => $"{SourcePortName}  \u2192  {DestPortName}";

    [JsonIgnore]
    public string SourceChannelLabel => SourceChannelFilter == 0 ? "ALL CH (1-16)" : $"CH {SourceChannelFilter}";

    [JsonIgnore]
    public string TargetChannelLabel => TargetChannelRemap == 0 ? "PASS-THRU" : $"CH {TargetChannelRemap}";

    [JsonIgnore]
    public string TransposeDisplay => TransposeSemitones == 0
        ? "0 ST"
        : (TransposeSemitones > 0 ? $"+{TransposeSemitones} ST" : $"{TransposeSemitones} ST");

    [JsonIgnore]
    public string MinNoteDisplay => $"{FormatNoteName(MinNote)} ({MinNote})";

    [JsonIgnore]
    public string MaxNoteDisplay => $"{FormatNoteName(MaxNote)} ({MaxNote})";

    [JsonIgnore]
    public string FilterSummary
    {
        get
        {
            if (!IsEnabled) return "MUTED";
            string chPart = (SourceChannelFilter == 0 && TargetChannelRemap == 0)
                ? "ALL CH"
                : $"{(SourceChannelFilter == 0 ? "ALL" : $"CH{SourceChannelFilter}")}\u2192{(TargetChannelRemap == 0 ? "PASS" : $"CH{TargetChannelRemap}")}";

            List<string> mods = new() { chPart };
            if (TransposeSemitones != 0)
                mods.Add(TransposeSemitones > 0 ? $"+{TransposeSemitones}st" : $"{TransposeSemitones}st");
            if (MinNote > 0 || MaxNote < 127)
                mods.Add($"{FormatNoteName(MinNote)}..{FormatNoteName(MaxNote)}");
            if (!PassClockAndTransport)
                mods.Add("NoClk");
            if (!PassControlChange)
                mods.Add("NoCC");
            if (!PassNotes)
                mods.Add("NoNote");
            if (VelocityCurve != 0)
                mods.Add(VelocityCurve switch { 1 => "Vel:Soft", 2 => "Vel:Hard", _ => $"Vel:{FixedVelocity}" });
            if (CcRemapFrom >= 0)
                mods.Add($"CC{CcRemapFrom}\u2192{CcRemapTo}");
            return string.Join(" | ", mods);
        }
    }

    [JsonIgnore]
    public Action<MidiRouteModel>? RouteModified { get; set; }

    private static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    public static string FormatNoteName(int noteNumber)
    {
        int n = Math.Clamp(noteNumber, 0, 127);
        int octave = (n / 12) - 1;
        return $"{NoteNames[n % 12]}{octave}";
    }

    public MidiRouteModel Clone()
    {
        return new MidiRouteModel
        {
            SourcePortName = SourcePortName,
            DestPortName = DestPortName,
            IsEnabled = IsEnabled,
            SourceChannelFilter = SourceChannelFilter,
            TargetChannelRemap = TargetChannelRemap,
            TransposeSemitones = TransposeSemitones,
            MinNote = MinNote,
            MaxNote = MaxNote,
            PassNotes = PassNotes,
            PassControlChange = PassControlChange,
            PassPitchBend = PassPitchBend,
            PassAftertouch = PassAftertouch,
            PassProgramChange = PassProgramChange,
            PassClockAndTransport = PassClockAndTransport,
            PassSysEx = PassSysEx,
            VelocityCurve = VelocityCurve,
            FixedVelocity = FixedVelocity,
            CcRemapFrom = CcRemapFrom,
            CcRemapTo = CcRemapTo
        };
    }

    // 128-entry lookup tables: [0] Linear, [1] Soft touch (light playing gets louder), [2] Hard touch (needs harder playing)
    private static readonly byte[][] VelocityTables = BuildVelocityTables();

    private static byte[][] BuildVelocityTables()
    {
        var tables = new byte[3][];
        double[] exponents = { 1.0, 0.55, 1.8 };
        for (int t = 0; t < 3; t++)
        {
            tables[t] = new byte[128];
            for (int v = 1; v < 128; v++)
            {
                double shaped = Math.Pow(v / 127.0, exponents[t]) * 127.0;
                tables[t][v] = (byte)Math.Clamp((int)Math.Round(shaped), 1, 127);
            }
        }
        return tables;
    }

    /// <summary>
    /// Maps a Note On velocity (1..127) through the route's velocity curve. Velocity 0 (note off) is never altered.
    /// Lock-free and allocation-free so the router thread can call it per message.
    /// </summary>
    public byte ApplyVelocityCurve(byte velocity)
    {
        if (velocity == 0) return 0;
        int curve = _velocityCurve;
        if (curve == 3) return (byte)Math.Clamp(_fixedVelocity, 1, 127);
        if (curve <= 0 || curve > 2) return velocity;
        return VelocityTables[curve][velocity & 0x7F];
    }
}

public sealed class MidiMonitorEvent
{
    public string Timestamp { get; init; } = "";
    public string Direction { get; init; } = ""; // "IN", "ROUTE", "PANIC"
    public string SourcePort { get; init; } = "";
    public string DestPort { get; init; } = "";
    public string RawHex { get; init; } = "";
    public string ChannelText { get; init; } = "";
    public string MessageType { get; init; } = "";
    public string DecodedDetail { get; init; } = "";
    public string AccentHex { get; init; } = "#00E676";

    public string FormattedLine => string.IsNullOrEmpty(DestPort)
        ? $"[{Timestamp}]  {SourcePort,-26} | {RawHex,-10} | {ChannelText,-5} | {MessageType,-11} | {DecodedDetail}"
        : $"[{Timestamp}]  {SourcePort,-22} \u2192 {DestPort,-20} | {RawHex,-10} | {ChannelText,-5} | {MessageType,-11} | {DecodedDetail}";
}
