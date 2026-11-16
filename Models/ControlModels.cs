using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace US16x08_AbletonPanel.Models;

/// <summary>
/// Mixer / transport parameters that a MIDI controller (CC or Note) can be mapped to.
/// Names are persisted as strings in control_sync_v2.json, so do not rename existing members.
/// </summary>
public enum ControlTargetKind
{
    TrackFader,
    TrackPan,
    TrackMute,
    TrackSolo,
    MasterFader,
    MasterMute,
    EqLowGain,
    EqLowMidGain,
    EqHighMidGain,
    EqHighGain,
    EqEnable,
    CompThreshold,
    CompMakeup,
    CompEnable,
    SelectTrack,
    ClockPlayStop,
    ClockTap
}

/// <summary>
/// One learned controller assignment: (input port, MIDI channel, CC# or Note#) -> mixer parameter.
/// </summary>
public sealed class ControlMappingModel : ObservableObject
{
    private static int _nextId;

    private string _sourcePortName = "";
    private int _midiChannel = 1;   // 1..16
    private bool _isNote;           // false = Control Change, true = Note
    private int _number;            // CC# or Note#
    private ControlTargetKind _target;
    private int _trackNumber;       // 0 = currently selected track, 1..16 = fixed input channel
    private string _lastValueDisplay = "";

    public ControlMappingModel()
    {
        Id = System.Threading.Interlocked.Increment(ref _nextId);
    }

    /// <summary>Process-unique id used to match queued router-thread events back to this mapping.</summary>
    [JsonIgnore]
    public int Id { get; }

    public string SourcePortName
    {
        get => _sourcePortName;
        set
        {
            if (SetProperty(ref _sourcePortName, value ?? ""))
                OnPropertyChanged(nameof(SourceDisplay));
        }
    }

    public int MidiChannel
    {
        get => _midiChannel;
        set
        {
            if (SetProperty(ref _midiChannel, Math.Clamp(value, 1, 16)))
                OnPropertyChanged(nameof(SourceDisplay));
        }
    }

    public bool IsNote
    {
        get => _isNote;
        set
        {
            if (SetProperty(ref _isNote, value))
                OnPropertyChanged(nameof(SourceDisplay));
        }
    }

    public int Number
    {
        get => _number;
        set
        {
            if (SetProperty(ref _number, Math.Clamp(value, 0, 127)))
                OnPropertyChanged(nameof(SourceDisplay));
        }
    }

    [JsonConverter(typeof(JsonStringEnumConverter<ControlTargetKind>))]
    public ControlTargetKind Target
    {
        get => _target;
        set
        {
            if (SetProperty(ref _target, value))
                OnPropertyChanged(nameof(TargetDisplay));
        }
    }

    public int TrackNumber
    {
        get => _trackNumber;
        set
        {
            if (SetProperty(ref _trackNumber, Math.Clamp(value, 0, 16)))
                OnPropertyChanged(nameof(TargetDisplay));
        }
    }

    [JsonIgnore]
    public string LastValueDisplay
    {
        get => _lastValueDisplay;
        set => SetProperty(ref _lastValueDisplay, value);
    }

    [JsonIgnore]
    public string SourceDisplay =>
        $"{(string.IsNullOrEmpty(SourcePortName) ? "ANY PORT" : SourcePortName)}  ·  CH {MidiChannel}  ·  " +
        (IsNote ? $"NOTE {MidiRouteModel.FormatNoteName(Number)} ({Number})" : $"CC {Number}");

    [JsonIgnore]
    public string TargetDisplay
    {
        get
        {
            string label = GetTargetLabel(Target);
            if (!IsTrackScoped(Target))
                return label;
            return TrackNumber == 0 ? $"{label}  →  SELECTED TRACK" : $"{label}  →  IN {TrackNumber}";
        }
    }

    /// <summary>Labels in <see cref="ControlTargetKind"/> declaration order, for a ComboBox bound by SelectedIndex.</summary>
    public static readonly string[] TargetLabels =
    {
        "FADER",
        "PAN",
        "MUTE (button)",
        "SOLO (button)",
        "MASTER FADER",
        "MASTER MUTE (button)",
        "EQ LOW GAIN",
        "EQ LOW-MID GAIN",
        "EQ HI-MID GAIN",
        "EQ HIGH GAIN",
        "EQ ON/OFF (button)",
        "COMP THRESHOLD",
        "COMP MAKEUP GAIN",
        "COMP ON/OFF (button)",
        "SELECT TRACK 1-16 (knob)",
        "CLOCK PLAY / STOP (button)",
        "CLOCK TAP TEMPO (button)"
    };

    public static string GetTargetLabel(ControlTargetKind kind)
    {
        int idx = (int)kind;
        return idx >= 0 && idx < TargetLabels.Length ? TargetLabels[idx] : kind.ToString();
    }

    /// <summary>True when the target applies to one input channel (and so uses <see cref="TrackNumber"/>).</summary>
    public static bool IsTrackScoped(ControlTargetKind kind) => kind switch
    {
        ControlTargetKind.MasterFader or
        ControlTargetKind.MasterMute or
        ControlTargetKind.SelectTrack or
        ControlTargetKind.ClockPlayStop or
        ControlTargetKind.ClockTap => false,
        _ => true
    };

    /// <summary>True for on/off targets that toggle on each button press instead of following a 0..127 value.</summary>
    public static bool IsButtonTarget(ControlTargetKind kind) => kind switch
    {
        ControlTargetKind.TrackMute or
        ControlTargetKind.TrackSolo or
        ControlTargetKind.MasterMute or
        ControlTargetKind.EqEnable or
        ControlTargetKind.CompEnable or
        ControlTargetKind.ClockPlayStop or
        ControlTargetKind.ClockTap => true,
        _ => false
    };

    public bool HasSameSource(string portName, int channelOneBased, bool isNote, int number) =>
        IsNote == isNote &&
        Number == number &&
        MidiChannel == channelOneBased &&
        string.Equals(SourcePortName, portName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// User assignment of one US-16x08 audio input channel to the MIDI output port and MIDI channel of the synth plugged into it.
/// </summary>
public sealed class ChannelMidiLinkModel : ObservableObject
{
    public const string NonePortLabel = "(NOT LINKED)";

    private int _channelNumber = 1; // 1..16 audio input channel
    private string _portName = "";  // "" = not linked
    private int _midiChannel;       // 0 = use the port's own channel setting, 1..16 = force this MIDI channel
    private TrackChannelModel? _track;

    public int ChannelNumber
    {
        get => _channelNumber;
        set
        {
            if (SetProperty(ref _channelNumber, Math.Clamp(value, 1, 16)))
                OnPropertyChanged(nameof(ChannelLabel));
        }
    }

    public string PortName
    {
        get => _portName;
        set
        {
            if (SetProperty(ref _portName, value ?? ""))
            {
                OnPropertyChanged(nameof(PortChoice));
                OnPropertyChanged(nameof(IsLinked));
                OnPropertyChanged(nameof(Summary));
                Changed?.Invoke(this);
            }
        }
    }

    public int MidiChannel
    {
        get => _midiChannel;
        set
        {
            if (SetProperty(ref _midiChannel, Math.Clamp(value, 0, 16)))
            {
                OnPropertyChanged(nameof(Summary));
                Changed?.Invoke(this);
            }
        }
    }

    /// <summary>The live mixer strip for this channel (so the row can show the editable track name).</summary>
    [JsonIgnore]
    public TrackChannelModel? Track
    {
        get => _track;
        set => SetProperty(ref _track, value);
    }

    [JsonIgnore]
    public string ChannelLabel => $"IN {ChannelNumber:00}";

    [JsonIgnore]
    public bool IsLinked => !string.IsNullOrEmpty(_portName);

    /// <summary>
    /// ComboBox SelectedItem helper. WPF writes null here whenever the item list is rebuilt, which must never clear the link.
    /// </summary>
    [JsonIgnore]
    public string PortChoice
    {
        get => IsLinked ? _portName : NonePortLabel;
        set
        {
            if (value == null) return;
            PortName = string.Equals(value, NonePortLabel, StringComparison.Ordinal) ? "" : value;
        }
    }

    [JsonIgnore]
    public string Summary => !IsLinked
        ? "—"
        : (MidiChannel == 0 ? $"{PortName} (port channel)" : $"{PortName} CH {MidiChannel}");

    [JsonIgnore]
    public Action<ChannelMidiLinkModel>? Changed { get; set; }
}

/// <summary>
/// Everything the Control &amp; Sync tab persists (clock, channel links, controller mappings).
/// </summary>
public sealed class ControlSyncSettings
{
    public double ClockBpm { get; set; } = 120.0;
    public bool ClockWhileStopped { get; set; }
    public int ClockRateIndex { get; set; } = 1;

    /// <summary>Output port names that receive the internal clock. Null = never configured (use defaults).</summary>
    public List<string>? ClockOutputPorts { get; set; }

    public bool KeyboardFollowsTrack { get; set; }
    public string FocusControllerPort { get; set; } = "";
    public List<ChannelMidiLinkModel> ChannelLinks { get; set; } = new();

    public bool ControlSurfaceEnabled { get; set; } = true;
    public List<ControlMappingModel> ControlMappings { get; set; } = new();
}
