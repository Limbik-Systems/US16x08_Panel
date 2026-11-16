using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace US16x08_AbletonPanel.Models;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    public void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// Hardware lookup tables and exact DSP parameter conversions reverse-engineered from US-16x08_SettingsPanel.exe
/// </summary>
public static class TascamDspTables
{
    // Full 64-step DSP Frequency Table (0x00..0x3F) shared across Low (0x00..0x1F), Mid (0x00..0x3F), and High (0x20..0x3F)
    public static readonly double[] AllBandFrequencies64 =
    {
        // 0x00..0x1F (Band 1 Low: 32 Hz .. 1.6 kHz)
        32, 36, 40, 45, 50, 56, 63, 70,
        80, 90, 100, 110, 125, 140, 160, 180,
        200, 225, 250, 280, 315, 355, 400, 450,
        500, 560, 630, 710, 800, 1000, 1250, 1600,
        // 0x20..0x3F (Band 4 High: 1.7 kHz .. 18 kHz)
        1700, 1800, 2000, 2200, 2500, 2800, 3150, 3550,
        4000, 4500, 5000, 5600, 6300, 7100, 8000, 8500,
        9000, 9500, 10000, 10600, 11200, 11800, 12500, 13200,
        14000, 15000, 16000, 16500, 17000, 17500, 17800, 18000
    };

    // Band 1 (LOW): 32Hz to 1.6kHz (32 discrete steps, DSP code 0x00..0x1F)
    public static readonly double[] LowBandFrequencies = AllBandFrequencies64[..32];

    // Band 2 (LOW-MID) & Band 3 (HIGH-MID): 32Hz to 18kHz (64 discrete steps, DSP code 0x00..0x3F)
    public static readonly double[] MidBandFrequencies = AllBandFrequencies64;

    // Band 4 (HIGH): 1.7kHz to 18kHz (32 discrete steps, DSP code 0x20..0x3F)
    public static readonly double[] HighBandFrequencies = AllBandFrequencies64[32..];

    // Q Factor for Low-Mid and High-Mid (7 discrete steps, code 0..6)
    public static readonly double[] QValues = { 0.25, 0.5, 1.0, 2.0, 4.0, 8.0, 16.0 };

    // Compressor Ratio (15 discrete steps from 0x432f40)
    public static readonly string[] CompRatioLabels =
    {
        "1.0 : 1", "1.1 : 1", "1.3 : 1", "1.5 : 1", "1.7 : 1",
        "2.0 : 1", "2.5 : 1", "3.0 : 1", "3.5 : 1", "4.0 : 1",
        "5.0 : 1", "6.0 : 1", "8.0 : 1", "16.0 : 1", "∞ : 1"
    };

    public static readonly double[] CompRatioValues =
    {
        1.0, 1.1, 1.3, 1.5, 1.7,
        2.0, 2.5, 3.0, 3.5, 4.0,
        5.0, 6.0, 8.0, 16.0, 100.0
    };

    // Exact byte code sent in TLV tag 0x92 (from 0x432f40)
    public static readonly byte[] CompRatioDspCodes =
    {
        0x0A, 0x0B, 0x0D, 0x0F, 0x11,
        0x14, 0x19, 0x1E, 0x23, 0x28,
        0x32, 0x3C, 0x50, 0xA0, 0xFF
    };

    // 41 discrete Pan byte values for L20..C..R20 (from 0x43b940)
    public static readonly byte[] PanDspCodes =
    {
        0x81, 0x87, 0x8E, 0x94, 0x9A, 0xA1, 0xA7, 0xAD, 0xB4, 0xBA,
        0xC0, 0xC7, 0xCD, 0xD4, 0xDA, 0xE0, 0xE7, 0xED, 0xF3, 0xFA,
        0x00, // Center
        0x06, 0x0D, 0x13, 0x19, 0x20, 0x26, 0x2C, 0x33, 0x39, 0x40,
        0x46, 0x4C, 0x53, 0x59, 0x5F, 0x66, 0x6C, 0x72, 0x79, 0x7F
    };

    // ASIO Buffer sizes supported by US-16x08 driver (HKEY_CURRENT_USER\Software\TASCAM\US-16x08\ASIODRIVER\blockFrames)
    public static readonly int[] AsioBufferSizes = { 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048 };

    // Line Out Routing Sources (indices 0..9 in original panel, plus direct Track 1..16 if supported by group 4)
    public static readonly string[] LineOutSourceNames =
    {
        "MASTER L",
        "MASTER R",
        "COMPUTER 1",
        "COMPUTER 2",
        "COMPUTER 3",
        "COMPUTER 4",
        "COMPUTER 5",
        "COMPUTER 6",
        "COMPUTER 7",
        "COMPUTER 8"
    };

    public static int FindNearestFrequencyIndex(double freqHz, double[] table)
    {
        int bestIdx = 0;
        double bestDiff = double.MaxValue;
        for (int i = 0; i < table.Length; i++)
        {
            double diff = Math.Abs(Math.Log(freqHz) - Math.Log(table[i]));
            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestIdx = i;
            }
        }
        return bestIdx;
    }

    public static int FindNearestQIndex(double q)
    {
        int bestIdx = 0;
        double bestDiff = double.MaxValue;
        for (int i = 0; i < QValues.Length; i++)
        {
            double diff = Math.Abs(Math.Log(Math.Max(0.1, q)) - Math.Log(QValues[i]));
            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestIdx = i;
            }
        }
        return bestIdx;
    }

    public static byte EncodePan(double panNormalized) // -20 (L20) to +20 (R20)
    {
        int idx = (int)Math.Round(Math.Clamp(panNormalized, -20, 20)) + 20;
        return PanDspCodes[Math.Clamp(idx, 0, 40)];
    }

    public static byte EncodeFaderDb(double db) // -90 dB (-inf) to +6 dB
    {
        if (db <= -89.5)
            return 0x81; // -127 = -infinity in US-16x08 DSP
        int intDb = (int)Math.Round(Math.Clamp(db, -90, 6));
        return unchecked((byte)(sbyte)intDb);
    }

    public static byte EncodeEqGain(int bandOneBased, double gainDb, bool isLowCutEnabled = false)
    {
        // In 0x43EE47 and 0x4321F0:
        // If Band 1 Low-Cut filter is enabled, the gain byte (tag 0x52) is set to 0xFF.
        // Otherwise, EQ Gain (-12.0 to +12.0 dB) maps to 0..24 (0x00 = -12 dB, 0x0C = 0 dB, 0x18 = +12 dB).
        if (bandOneBased == 1 && isLowCutEnabled)
            return 0xFF;

        int code = (int)Math.Round(Math.Clamp(gainDb, -12.0, 12.0)) + 12;
        return (byte)Math.Clamp(code, 0, 24);
    }
}

public class EqBandModel : ObservableObject
{
    private int _bandNumber; // 1 = Low, 2 = Low-Mid, 3 = High-Mid, 4 = High
    private string _bandName = "";
    private double _frequencyHz;
    private double _gainDb;
    private double _qFactor = 1.0;
    private bool _hasQ;

    public int BandNumber
    {
        get => _bandNumber;
        set => SetProperty(ref _bandNumber, value);
    }

    public string BandName
    {
        get => _bandName;
        set => SetProperty(ref _bandName, value);
    }

    public bool HasQ
    {
        get => _hasQ;
        set => SetProperty(ref _hasQ, value);
    }

    public double[] FrequencyTable => BandNumber switch
    {
        1 => TascamDspTables.LowBandFrequencies,
        4 => TascamDspTables.HighBandFrequencies,
        _ => TascamDspTables.MidBandFrequencies
    };

    public double MinFreq => FrequencyTable[0];
    public double MaxFreq => FrequencyTable[^1];

    public double FrequencyHz
    {
        get => _frequencyHz;
        set
        {
            double clamped = Math.Clamp(value, MinFreq, MaxFreq);
            if (SetProperty(ref _frequencyHz, clamped))
            {
                OnPropertyChanged(nameof(FrequencyDisplay));
                OnPropertyChanged(nameof(FrequencyCode));
                ParameterChanged?.Invoke(this);
            }
        }
    }

    public double GainDb
    {
        get => _gainDb;
        set
        {
            double clamped = Math.Round(Math.Clamp(value, -12.0, 12.0) * 2.0) / 2.0;
            if (SetProperty(ref _gainDb, clamped))
            {
                OnPropertyChanged(nameof(GainDisplay));
                ParameterChanged?.Invoke(this);
            }
        }
    }

    public double QFactor
    {
        get => _qFactor;
        set
        {
            double clamped = Math.Clamp(value, 0.25, 16.0);
            if (SetProperty(ref _qFactor, clamped))
            {
                OnPropertyChanged(nameof(QDisplay));
                OnPropertyChanged(nameof(QCode));
                ParameterChanged?.Invoke(this);
            }
        }
    }

    [JsonIgnore]
    public byte FrequencyCode
    {
        get
        {
            int idx = TascamDspTables.FindNearestFrequencyIndex(FrequencyHz, FrequencyTable);
            return BandNumber switch
            {
                1 => (byte)Math.Clamp(idx, 0, 31),           // 0x00..0x1F (0x432380)
                4 => (byte)Math.Clamp(0x20 + idx, 0x20, 0x3F), // 0x20..0x3F (0x432990)
                _ => (byte)Math.Clamp(idx, 0, 63)            // 0x00..0x3F (0x432570)
            };
        }
    }

    [JsonIgnore]
    public byte QCode => HasQ ? (byte)TascamDspTables.FindNearestQIndex(QFactor) : (byte)0xFF;

    [JsonIgnore]
    public string FrequencyDisplay => FrequencyHz >= 1000
        ? $"{FrequencyHz / 1000.0:0.00} kHz"
        : $"{FrequencyHz:0} Hz";

    [JsonIgnore]
    public string GainDisplay => GainDb > 0 ? $"+{GainDb:0.0} dB" : $"{GainDb:0.0} dB";

    [JsonIgnore]
    public string QDisplay => HasQ ? $"{TascamDspTables.QValues[QCode]:0.##}" : "Shelf";

    [JsonIgnore]
    public Action<EqBandModel>? ParameterChanged { get; set; }
}

public class CompressorModel : ObservableObject
{
    private bool _isEnabled;
    private double _thresholdDb = -16.0; // -32 to 0 dB
    private int _ratioIndex = 5;          // 0..13 (default 5 -> 2.0:1)
    private double _attackMs = 20.0;      // 2 to 200 ms
    private double _releaseMs = 100.0;    // 10 to 1000 ms
    private double _makeupGainDb = 0.0;   // 0 to 20 dB
    private double _gainReductionDb;      // Live meter value (0 to 24 dB)

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
                ParameterChanged?.Invoke();
        }
    }

    public double ThresholdDb
    {
        get => _thresholdDb;
        set
        {
            double clamped = Math.Round(Math.Clamp(value, -32.0, 0.0));
            if (SetProperty(ref _thresholdDb, clamped))
            {
                OnPropertyChanged(nameof(ThresholdDisplay));
                ParameterChanged?.Invoke();
            }
        }
    }

    public int RatioIndex
    {
        get => _ratioIndex;
        set
        {
            int clamped = Math.Clamp(value, 0, TascamDspTables.CompRatioValues.Length - 1);
            if (SetProperty(ref _ratioIndex, clamped))
            {
                OnPropertyChanged(nameof(RatioValue));
                OnPropertyChanged(nameof(RatioDisplay));
                OnPropertyChanged(nameof(RatioKnobValue));
                ParameterChanged?.Invoke();
            }
        }
    }

    [JsonIgnore]
    public double RatioKnobValue
    {
        get => RatioIndex;
        set => RatioIndex = (int)Math.Round(value);
    }

    [JsonIgnore]
    public double RatioValue => TascamDspTables.CompRatioValues[RatioIndex];

    [JsonIgnore]
    public string RatioDisplay => TascamDspTables.CompRatioLabels[RatioIndex];

    public double AttackMs
    {
        get => _attackMs;
        set
        {
            double clamped = Math.Round(Math.Clamp(value, 2.0, 200.0));
            if (SetProperty(ref _attackMs, clamped))
            {
                OnPropertyChanged(nameof(AttackDisplay));
                ParameterChanged?.Invoke();
            }
        }
    }

    public double ReleaseMs
    {
        get => _releaseMs;
        set
        {
            double clamped = Math.Round(Math.Clamp(value, 10.0, 1000.0) / 10.0) * 10.0;
            if (SetProperty(ref _releaseMs, clamped))
            {
                OnPropertyChanged(nameof(ReleaseDisplay));
                ParameterChanged?.Invoke();
            }
        }
    }

    public double MakeupGainDb
    {
        get => _makeupGainDb;
        set
        {
            double clamped = Math.Round(Math.Clamp(value, 0.0, 20.0));
            if (SetProperty(ref _makeupGainDb, clamped))
            {
                OnPropertyChanged(nameof(MakeupGainDisplay));
                ParameterChanged?.Invoke();
            }
        }
    }

    [JsonIgnore]
    public double GainReductionDb
    {
        get => _gainReductionDb;
        set
        {
            if (SetProperty(ref _gainReductionDb, value))
                OnPropertyChanged(nameof(GainReductionDisplay));
        }
    }

    [JsonIgnore]
    public string ThresholdDisplay => $"{ThresholdDb:0} dB";

    [JsonIgnore]
    public string AttackDisplay => $"{AttackMs:0} ms";

    [JsonIgnore]
    public string ReleaseDisplay => $"{ReleaseMs:0} ms";

    [JsonIgnore]
    public string MakeupGainDisplay => $"+{MakeupGainDb:0} dB";

    [JsonIgnore]
    public string GainReductionDisplay => GainReductionDb > 0.1 ? $"-{GainReductionDb:0.0} dB" : "0.0 dB";

    [JsonIgnore]
    public Action? ParameterChanged { get; set; }
}

public class TrackChannelModel : ObservableObject
{
    private int _channelNumber; // 1..16
    private string _customName = "";
    private bool _isSelected;
    private bool _isStereoLinked;
    private bool _isStereoRightPartner;
    private bool _isPhaseInverted;
    private bool _isLowCutEnabled;
    private bool _isEqEnabled = true;
    private bool _isMuted;
    private bool _isSolo;
    private double _pan; // -20 (L20) to +20 (R20)
    private double _faderDb; // -90.0 (-inf) to +6.0 dB
    private double _peakLevelLinear; // 0.0 to 1.0
    private double _peakHoldLinear;  // 0.0 to 1.0
    private bool _isClipping;
    private string _trackColorHex = "#FFB000";

    public TrackChannelModel(int channelNumber)
    {
        _channelNumber = channelNumber;
        _faderDb = 0.0;
        _pan = 0.0;

        // Distinct Ableton Live session clip/track header colors by input group
        _trackColorHex = channelNumber switch
        {
            1 or 2 => "#FF5252",   // Drums / Kick-Snare Red-Orange
            3 or 4 => "#FFB000",   // Overheads / Amber
            5 or 6 => "#00E676",   // Bass / Synth Green
            7 or 8 => "#00D4FF",   // Vocals / Guitar Cyan
            9 or 10 => "#A770FF",  // Balanced Line 9-10 Purple
            11 or 12 => "#FF6EC7", // Line 11-12 Pink
            13 or 14 => "#26C6DA", // Line 13-14 Teal
            _ => "#FFD54F"         // Line 15-16 Gold
        };

        _customName = channelNumber switch
        {
            1 => "Kick In",
            2 => "Snare Top",
            3 => "OH Left",
            4 => "OH Right",
            5 => "Bass DI",
            6 => "Lead Vocal",
            7 => "Elec Gtr L",
            8 => "Elec Gtr R",
            9 => "Synth L",
            10 => "Synth R",
            11 => "Sampler L",
            12 => "Sampler R",
            13 => "FX Ret L",
            14 => "FX Ret R",
            15 => "Ext Line L",
            16 => "Ext Line R",
            _ => $"Track {channelNumber}"
        };

        LowBand = new EqBandModel { BandNumber = 1, BandName = "LOW", FrequencyHz = 100, GainDb = 0, HasQ = false };
        LowMidBand = new EqBandModel { BandNumber = 2, BandName = "LOW-MID", FrequencyHz = 400, GainDb = 0, QFactor = 1.0, HasQ = true };
        HighMidBand = new EqBandModel { BandNumber = 3, BandName = "HI-MID", FrequencyHz = 2500, GainDb = 0, QFactor = 1.0, HasQ = true };
        HighBand = new EqBandModel { BandNumber = 4, BandName = "HIGH", FrequencyHz = 8000, GainDb = 0, HasQ = false };

        LowBand.ParameterChanged = b => OnEqChanged(b);
        LowMidBand.ParameterChanged = b => OnEqChanged(b);
        HighMidBand.ParameterChanged = b => OnEqChanged(b);
        HighBand.ParameterChanged = b => OnEqChanged(b);

        Compressor = new CompressorModel();
        Compressor.ParameterChanged = () =>
        {
            OnPropertyChanged(nameof(HasActiveComp));
            TrackParameterChanged?.Invoke(this, "Comp");
        };
    }

    public int ChannelNumber
    {
        get => _channelNumber;
        set => SetProperty(ref _channelNumber, value);
    }

    public bool IsOddChannel => ChannelNumber % 2 == 1;
    public string StereoPairLabel => IsOddChannel ? $"{ChannelNumber}/{ChannelNumber + 1}" : $"{ChannelNumber - 1}/{ChannelNumber}";

    [JsonIgnore]
    public string InputTypeLabel => ChannelNumber <= 8 ? "MIC/XLR" : (ChannelNumber <= 10 ? "INST/TRS" : "BAL TRS");

    [JsonIgnore]
    public string HeaderTitle => IsStereoLinked && IsOddChannel
        ? $"IN {ChannelNumber}-{ChannelNumber + 1}"
        : $"IN {ChannelNumber}";

    public string CustomName
    {
        get => _customName;
        set => SetProperty(ref _customName, value);
    }

    public string TrackColorHex
    {
        get => _trackColorHex;
        set => SetProperty(ref _trackColorHex, value);
    }

    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsStereoLinked
    {
        get => _isStereoLinked;
        set
        {
            if (SetProperty(ref _isStereoLinked, value))
            {
                OnPropertyChanged(nameof(HeaderTitle));
                TrackParameterChanged?.Invoke(this, "StereoLink");
            }
        }
    }

    public bool IsStereoRightPartner
    {
        get => _isStereoRightPartner;
        set => SetProperty(ref _isStereoRightPartner, value);
    }

    public bool IsPhaseInverted
    {
        get => _isPhaseInverted;
        set
        {
            if (SetProperty(ref _isPhaseInverted, value))
                TrackParameterChanged?.Invoke(this, "Phase");
        }
    }

    public bool IsLowCutEnabled
    {
        get => _isLowCutEnabled;
        set
        {
            if (SetProperty(ref _isLowCutEnabled, value))
            {
                OnPropertyChanged(nameof(HasActiveEq));
                TrackParameterChanged?.Invoke(this, "LowCut");
            }
        }
    }

    public bool IsEqEnabled
    {
        get => _isEqEnabled;
        set
        {
            if (SetProperty(ref _isEqEnabled, value))
            {
                OnPropertyChanged(nameof(HasActiveEq));
                TrackParameterChanged?.Invoke(this, "EqEnable");
            }
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (SetProperty(ref _isMuted, value))
            {
                OnPropertyChanged(nameof(IsTrackActive));
                TrackParameterChanged?.Invoke(this, "Mute");
            }
        }
    }

    /// <summary>
    /// Ableton Live style track activator (lit amber when unmuted, dark when muted)
    /// </summary>
    [JsonIgnore]
    public bool IsTrackActive
    {
        get => !_isMuted;
        set => IsMuted = !value;
    }

    public bool IsSolo
    {
        get => _isSolo;
        set
        {
            if (SetProperty(ref _isSolo, value))
                TrackParameterChanged?.Invoke(this, "Solo");
        }
    }

    public double Pan
    {
        get => _pan;
        set
        {
            double clamped = Math.Round(Math.Clamp(value, -20.0, 20.0));
            if (SetProperty(ref _pan, clamped))
            {
                OnPropertyChanged(nameof(PanDisplay));
                TrackParameterChanged?.Invoke(this, "Pan");
            }
        }
    }

    [JsonIgnore]
    public string PanDisplay
    {
        get
        {
            int p = (int)Math.Round(Pan);
            if (p == 0) return "C";
            return p < 0 ? $"{Math.Abs(p)}L" : $"{p}R";
        }
    }

    public double FaderDb
    {
        get => _faderDb;
        set
        {
            double clamped = Math.Round(Math.Clamp(value, -90.0, 6.0) * 10.0) / 10.0;
            if (SetProperty(ref _faderDb, clamped))
            {
                OnPropertyChanged(nameof(FaderDisplay));
                TrackParameterChanged?.Invoke(this, "Fader");
            }
        }
    }

    [JsonIgnore]
    public string FaderDisplay => FaderDb <= -89.5 ? "-∞ dB" : (FaderDb > 0 ? $"+{FaderDb:0.0}" : $"{FaderDb:0.0}");

    public EqBandModel LowBand { get; set; }
    public EqBandModel LowMidBand { get; set; }
    public EqBandModel HighMidBand { get; set; }
    public EqBandModel HighBand { get; set; }
    public CompressorModel Compressor { get; set; }

    [JsonIgnore]
    public bool HasActiveEq => IsLowCutEnabled ||
                               Math.Abs(LowBand.GainDb) > 0.1 ||
                               Math.Abs(LowMidBand.GainDb) > 0.1 ||
                               Math.Abs(HighMidBand.GainDb) > 0.1 ||
                               Math.Abs(HighBand.GainDb) > 0.1;

    [JsonIgnore]
    public bool HasActiveComp => Compressor.IsEnabled;

    [JsonIgnore]
    public double PeakLevelLinear
    {
        get => _peakLevelLinear;
        set
        {
            if (SetProperty(ref _peakLevelLinear, value))
                OnPropertyChanged(nameof(PeakDbDisplay));
        }
    }

    [JsonIgnore]
    public double PeakHoldLinear
    {
        get => _peakHoldLinear;
        set => SetProperty(ref _peakHoldLinear, value);
    }

    [JsonIgnore]
    public bool IsClipping
    {
        get => _isClipping;
        set => SetProperty(ref _isClipping, value);
    }

    [JsonIgnore]
    public string PeakDbDisplay
    {
        get
        {
            if (PeakHoldLinear <= 0.0001) return "-∞";
            double db = 20.0 * Math.Log10(PeakHoldLinear);
            return db >= 0.0 ? "CLIP" : $"{db:0.0}";
        }
    }

    [JsonIgnore]
    public Action<TrackChannelModel, string>? TrackParameterChanged { get; set; }

    private void OnEqChanged(EqBandModel band)
    {
        OnPropertyChanged(nameof(HasActiveEq));
        TrackParameterChanged?.Invoke(this, $"EQ:{band.BandNumber}");
    }
}

public class LineOutRouteModel : ObservableObject
{
    private int _outputNumber; // 1..8
    private int _sourceIndex;  // 0 = MASTER L, 1 = MASTER R, 2..9 = COMPUTER 1..8

    public int OutputNumber
    {
        get => _outputNumber;
        set => SetProperty(ref _outputNumber, value);
    }

    public string OutputLabel => $"LINE OUT {OutputNumber}";

    public int SourceIndex
    {
        get => _sourceIndex;
        set
        {
            int clamped = Math.Clamp(value, 0, TascamDspTables.LineOutSourceNames.Length - 1);
            if (SetProperty(ref _sourceIndex, clamped))
            {
                OnPropertyChanged(nameof(SourceName));
                OnPropertyChanged(nameof(SourceCategory));
                RouteChanged?.Invoke(this);
            }
        }
    }

    [JsonIgnore]
    public string SourceName => TascamDspTables.LineOutSourceNames[SourceIndex];

    [JsonIgnore]
    public string SourceCategory => SourceIndex <= 1 ? "DSP MASTER BUS" : "DAW USB RETURN";

    [JsonIgnore]
    public Action<LineOutRouteModel>? RouteChanged { get; set; }
}

public class SceneMemoryModel : ObservableObject
{
    private int _slotNumber;
    private string _name = "Init Scene";
    private string _lastModified = "";
    private bool _isFactoryPreset;

    public int SlotNumber
    {
        get => _slotNumber;
        set => SetProperty(ref _slotNumber, value);
    }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
                OnPropertyChanged(nameof(DisplayTitle));
        }
    }

    public string LastModified
    {
        get => _lastModified;
        set => SetProperty(ref _lastModified, value);
    }

    public bool IsFactoryPreset
    {
        get => _isFactoryPreset;
        set => SetProperty(ref _isFactoryPreset, value);
    }

    [JsonIgnore]
    public string DisplayTitle => $"{SlotNumber:00} // {Name}";

    public double MasterFaderDb { get; set; } = 0.0;
    public bool MasterMuted { get; set; } = false;
    public bool ComputerToStereoBus { get; set; } = true;
    public bool TrueBypass { get; set; } = false;
    public int[] LineOutSources { get; set; } = { 0, 1, 4, 5, 6, 7, 8, 9 };
    public List<TrackChannelSnapshot> Tracks { get; set; } = new();
}

public class TrackChannelSnapshot
{
    public int ChannelNumber { get; set; }
    public string CustomName { get; set; } = "";
    public bool IsStereoLinked { get; set; }
    public bool IsPhaseInverted { get; set; }
    public bool IsLowCutEnabled { get; set; }
    public bool IsEqEnabled { get; set; } = true;
    public bool IsMuted { get; set; }
    public bool IsSolo { get; set; }
    public double Pan { get; set; }
    public double FaderDb { get; set; }

    public double LowFreq { get; set; } = 100;
    public double LowGain { get; set; } = 0;

    public double LowMidFreq { get; set; } = 400;
    public double LowMidGain { get; set; } = 0;
    public double LowMidQ { get; set; } = 1.0;

    public double HighMidFreq { get; set; } = 2500;
    public double HighMidGain { get; set; } = 0;
    public double HighMidQ { get; set; } = 1.0;

    public double HighFreq { get; set; } = 8000;
    public double HighGain { get; set; } = 0;

    public bool CompEnabled { get; set; }
    public double CompThreshold { get; set; } = -16;
    public int CompRatioIndex { get; set; } = 5;
    public double CompAttack { get; set; } = 20;
    public double CompRelease { get; set; } = 100;
    public double CompGain { get; set; } = 0;
}
