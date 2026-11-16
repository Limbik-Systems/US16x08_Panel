using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using US16x08_AbletonPanel.Models;

namespace US16x08_AbletonPanel.Services;

public sealed class SceneMemoryService
{
    private readonly string _scenesFilePath;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public SceneMemoryService()
    {
        string appDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TASCAM_US16x08_AbletonPanel");
        Directory.CreateDirectory(appDir);
        _scenesFilePath = Path.Combine(appDir, "scenes_v2.json");
    }

    public List<SceneMemoryModel> LoadOrCreateDefaultScenes()
    {
        try
        {
            if (File.Exists(_scenesFilePath))
            {
                string json = File.ReadAllText(_scenesFilePath);
                var loaded = JsonSerializer.Deserialize<List<SceneMemoryModel>>(json, JsonOpts);
                if (loaded is { Count: 10 })
                    return loaded;
            }
        }
        catch
        {
            // Fall through to factory defaults
        }

        var scenes = new List<SceneMemoryModel>
        {
            CreateFlatZeroPreset(1, "Flat Reference 0dB"),
            CreateStudioTrackingPreset(2, "Live Band Session"),
            CreateDrumFocusPreset(3, "Punchy Drum Tracking"),
            CreateBroadcastPreset(4, "Podcast / Broadcast 4x"),
            CreateDawStemReturnPreset(5, "8-Ch Analog Summing"),
            CreateFlatZeroPreset(6, "User Scene 06"),
            CreateFlatZeroPreset(7, "User Scene 07"),
            CreateFlatZeroPreset(8, "User Scene 08"),
            CreateFlatZeroPreset(9, "User Scene 09"),
            CreateFlatZeroPreset(10, "User Scene 10")
        };

        SaveScenesToDisk(scenes);
        return scenes;
    }

    public void SaveScenesToDisk(IEnumerable<SceneMemoryModel> scenes)
    {
        try
        {
            string json = JsonSerializer.Serialize(scenes, JsonOpts);
            File.WriteAllText(_scenesFilePath, json);
        }
        catch
        {
            // Ignore IO errors
        }
    }

    public static SceneMemoryModel CaptureSnapshot(
        int slotNumber,
        string name,
        IReadOnlyList<TrackChannelModel> tracks,
        double masterFaderDb,
        bool masterMuted,
        bool computerToStereoBus,
        bool trueBypass,
        IReadOnlyList<LineOutRouteModel> lineOutRoutes)
    {
        var scene = new SceneMemoryModel
        {
            SlotNumber = slotNumber,
            Name = name,
            LastModified = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            MasterFaderDb = masterFaderDb,
            MasterMuted = masterMuted,
            ComputerToStereoBus = computerToStereoBus,
            TrueBypass = trueBypass,
            LineOutSources = new int[8]
        };

        for (int i = 0; i < Math.Min(8, lineOutRoutes.Count); i++)
            scene.LineOutSources[i] = lineOutRoutes[i].SourceIndex;

        foreach (var t in tracks)
        {
            scene.Tracks.Add(new TrackChannelSnapshot
            {
                ChannelNumber = t.ChannelNumber,
                CustomName = t.CustomName,
                IsStereoLinked = t.IsStereoLinked,
                IsPhaseInverted = t.IsPhaseInverted,
                IsLowCutEnabled = t.IsLowCutEnabled,
                IsEqEnabled = t.IsEqEnabled,
                IsMuted = t.IsMuted,
                IsSolo = t.IsSolo,
                Pan = t.Pan,
                FaderDb = t.FaderDb,
                LowFreq = t.LowBand.FrequencyHz,
                LowGain = t.LowBand.GainDb,
                LowMidFreq = t.LowMidBand.FrequencyHz,
                LowMidGain = t.LowMidBand.GainDb,
                LowMidQ = t.LowMidBand.QFactor,
                HighMidFreq = t.HighMidBand.FrequencyHz,
                HighMidGain = t.HighMidBand.GainDb,
                HighMidQ = t.HighMidBand.QFactor,
                HighFreq = t.HighBand.FrequencyHz,
                HighGain = t.HighBand.GainDb,
                CompEnabled = t.Compressor.IsEnabled,
                CompThreshold = t.Compressor.ThresholdDb,
                CompRatioIndex = t.Compressor.RatioIndex,
                CompAttack = t.Compressor.AttackMs,
                CompRelease = t.Compressor.ReleaseMs,
                CompGain = t.Compressor.MakeupGainDb
            });
        }

        return scene;
    }

    public static void ExportTascamIni(string filePath, SceneMemoryModel scene)
    {
        StringBuilder sb = new();
        sb.AppendLine("[Panel]");
        sb.AppendLine($"SceneName={scene.Name}");
        sb.AppendLine($"SendStereoBus={(scene.ComputerToStereoBus ? 1 : 0)}");
        sb.AppendLine($"TrueBypass={(scene.TrueBypass ? 1 : 0)}");
        sb.AppendLine();
        sb.AppendLine("[Master]");
        sb.AppendLine($"Fader={scene.MasterFaderDb:0.0}");
        sb.AppendLine($"Mute={(scene.MasterMuted ? 1 : 0)}");
        sb.AppendLine();
        sb.AppendLine("[LineOut]");
        for (int i = 0; i < scene.LineOutSources.Length; i++)
            sb.AppendLine($"LineOut{i + 1}={scene.LineOutSources[i]}");
        sb.AppendLine();

        foreach (var t in scene.Tracks)
        {
            sb.AppendLine($"[Track{t.ChannelNumber}]");
            sb.AppendLine($"Name={t.CustomName}");
            sb.AppendLine($"StereoLink={(t.IsStereoLinked ? 1 : 0)}");
            sb.AppendLine($"Phase={(t.IsPhaseInverted ? 1 : 0)}");
            sb.AppendLine($"LowCut={(t.IsLowCutEnabled ? 1 : 0)}");
            sb.AppendLine($"EqEnable={(t.IsEqEnabled ? 1 : 0)}");
            sb.AppendLine($"Mute={(t.IsMuted ? 1 : 0)}");
            sb.AppendLine($"Solo={(t.IsSolo ? 1 : 0)}");
            sb.AppendLine($"Pan={t.Pan:0}");
            sb.AppendLine($"Fader={t.FaderDb:0.0}");
            sb.AppendLine($"LowFreq={t.LowFreq:0}");
            sb.AppendLine($"LowGain={t.LowGain:0.0}");
            sb.AppendLine($"LowMidFreq={t.LowMidFreq:0}");
            sb.AppendLine($"LowMidGain={t.LowMidGain:0.0}");
            sb.AppendLine($"LowMidQ={t.LowMidQ:0.##}");
            sb.AppendLine($"HighMidFreq={t.HighMidFreq:0}");
            sb.AppendLine($"HighMidGain={t.HighMidGain:0.0}");
            sb.AppendLine($"HighMidQ={t.HighMidQ:0.##}");
            sb.AppendLine($"HighFreq={t.HighFreq:0}");
            sb.AppendLine($"HighGain={t.HighGain:0.0}");
            sb.AppendLine($"CompEnable={(t.CompEnabled ? 1 : 0)}");
            sb.AppendLine($"CompThreshold={t.CompThreshold:0}");
            sb.AppendLine($"CompRatioIndex={t.CompRatioIndex}");
            sb.AppendLine($"CompAttack={t.CompAttack:0}");
            sb.AppendLine($"CompRelease={t.CompRelease:0}");
            sb.AppendLine($"CompGain={t.CompGain:0}");
            sb.AppendLine();
        }

        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
    }

    private static SceneMemoryModel CreateStudioTrackingPreset(int slot, string name)
    {
        var scene = CreateFlatZeroPreset(slot, name);
        scene.IsFactoryPreset = true;

        // Ch 1: Kick In
        var ch1 = scene.Tracks[0];
        ch1.LowFreq = 60; ch1.LowGain = 4.5;
        ch1.LowMidFreq = 315; ch1.LowMidGain = -4.0; ch1.LowMidQ = 2.0;
        ch1.HighMidFreq = 3150; ch1.HighMidGain = 3.5; ch1.HighMidQ = 1.0;
        ch1.HighFreq = 8000; ch1.HighGain = 1.5;
        ch1.CompEnabled = true; ch1.CompThreshold = -18; ch1.CompRatioIndex = 9; // 4.0:1
        ch1.CompAttack = 18; ch1.CompRelease = 120; ch1.CompGain = 3;
        ch1.FaderDb = 0.5;

        // Ch 2: Snare Top
        var ch2 = scene.Tracks[1];
        ch2.IsLowCutEnabled = true;
        ch2.LowFreq = 125; ch2.LowGain = 2.5;
        ch2.LowMidFreq = 500; ch2.LowMidGain = -2.5; ch2.LowMidQ = 2.0;
        ch2.HighMidFreq = 4000; ch2.HighMidGain = 3.0; ch2.HighMidQ = 1.0;
        ch2.HighFreq = 10000; ch2.HighGain = 2.5;
        ch2.CompEnabled = true; ch2.CompThreshold = -16; ch2.CompRatioIndex = 8; // 3.5:1
        ch2.CompAttack = 12; ch2.CompRelease = 90; ch2.CompGain = 2;

        // Ch 3-4: Overheads (Stereo Linked)
        scene.Tracks[2].IsStereoLinked = true; scene.Tracks[3].IsStereoLinked = true;
        scene.Tracks[2].Pan = -16; scene.Tracks[3].Pan = 16;
        scene.Tracks[2].IsLowCutEnabled = true; scene.Tracks[3].IsLowCutEnabled = true;
        scene.Tracks[2].HighFreq = 12000; scene.Tracks[2].HighGain = 3.0;
        scene.Tracks[3].HighFreq = 12000; scene.Tracks[3].HighGain = 3.0;
        scene.Tracks[2].FaderDb = -2.5; scene.Tracks[3].FaderDb = -2.5;

        // Ch 5: Bass DI
        var ch5 = scene.Tracks[4];
        ch5.LowFreq = 80; ch5.LowGain = 3.5;
        ch5.LowMidFreq = 250; ch5.LowMidGain = -2.0; ch5.LowMidQ = 1.0;
        ch5.HighMidFreq = 1600; ch5.HighMidGain = 2.0; ch5.HighMidQ = 1.0;
        ch5.CompEnabled = true; ch5.CompThreshold = -20; ch5.CompRatioIndex = 9; // 4.0:1
        ch5.CompAttack = 25; ch5.CompRelease = 160; ch5.CompGain = 4;

        // Ch 6: Lead Vocal
        var ch6 = scene.Tracks[5];
        ch6.IsLowCutEnabled = true;
        ch6.LowMidFreq = 250; ch6.LowMidGain = -1.5; ch6.LowMidQ = 1.0;
        ch6.HighMidFreq = 3150; ch6.HighMidGain = 2.5; ch6.HighMidQ = 1.0;
        ch6.HighFreq = 12000; ch6.HighGain = 3.5;
        ch6.CompEnabled = true; ch6.CompThreshold = -19; ch6.CompRatioIndex = 7; // 3.0:1
        ch6.CompAttack = 15; ch6.CompRelease = 140; ch6.CompGain = 4;
        ch6.FaderDb = 1.5;

        // Ch 7-8: Electric Guitars (Stereo Linked)
        scene.Tracks[6].IsStereoLinked = true; scene.Tracks[7].IsStereoLinked = true;
        scene.Tracks[6].Pan = -14; scene.Tracks[7].Pan = 14;
        scene.Tracks[6].IsLowCutEnabled = true; scene.Tracks[7].IsLowCutEnabled = true;
        scene.Tracks[6].HighMidFreq = 2500; scene.Tracks[6].HighMidGain = 2.0;
        scene.Tracks[7].HighMidFreq = 2500; scene.Tracks[7].HighMidGain = 2.0;
        scene.Tracks[6].FaderDb = -1.5; scene.Tracks[7].FaderDb = -1.5;

        // Ch 9-10: Synth Stereo
        scene.Tracks[8].IsStereoLinked = true; scene.Tracks[9].IsStereoLinked = true;
        scene.Tracks[8].Pan = -12; scene.Tracks[9].Pan = 12;
        scene.Tracks[8].FaderDb = -3.0; scene.Tracks[9].FaderDb = -3.0;

        return scene;
    }

    private static SceneMemoryModel CreateDrumFocusPreset(int slot, string name)
    {
        var scene = CreateStudioTrackingPreset(slot, name);
        scene.Tracks[0].LowGain = 6.0;
        scene.Tracks[0].CompThreshold = -22;
        scene.Tracks[0].CompRatioIndex = 11; // 6.0:1
        scene.Tracks[1].HighMidGain = 4.5;
        scene.Tracks[1].CompThreshold = -20;
        return scene;
    }

    private static SceneMemoryModel CreateBroadcastPreset(int slot, string name)
    {
        var scene = CreateFlatZeroPreset(slot, name);
        for (int i = 0; i < 4; i++)
        {
            var t = scene.Tracks[i];
            t.CustomName = $"Host Mic {i + 1}";
            t.IsLowCutEnabled = true;
            t.LowFreq = 125; t.LowGain = 2.0;
            t.LowMidFreq = 315; t.LowMidGain = -3.0; t.LowMidQ = 2.0;
            t.HighMidFreq = 4000; t.HighMidGain = 3.0; t.HighMidQ = 1.0;
            t.HighFreq = 10000; t.HighGain = 2.5;
            t.CompEnabled = true;
            t.CompThreshold = -21;
            t.CompRatioIndex = 9; // 4.0:1
            t.CompAttack = 10;
            t.CompRelease = 120;
            t.CompGain = 5;
            t.FaderDb = 0.0;
        }
        for (int i = 4; i < 16; i++)
        {
            scene.Tracks[i].FaderDb = -24.0;
        }
        return scene;
    }

    private static SceneMemoryModel CreateDawStemReturnPreset(int slot, string name)
    {
        var scene = CreateFlatZeroPreset(slot, name);
        scene.LineOutSources = new[] { 2, 3, 4, 5, 6, 7, 8, 9 }; // Computer 1..8 direct to Line Out 1..8
        return scene;
    }

    public static SceneMemoryModel CreateFlatZeroPreset(int slot, string name)
    {
        var dummyTracks = new List<TrackChannelModel>();
        for (int i = 1; i <= 16; i++)
            dummyTracks.Add(new TrackChannelModel(i));

        var dummyRoutes = new List<LineOutRouteModel>();
        for (int i = 1; i <= 8; i++)
            dummyRoutes.Add(new LineOutRouteModel { OutputNumber = i, SourceIndex = i <= 2 ? i - 1 : i + 1 });

        return CaptureSnapshot(slot, name, dummyTracks, 0.0, false, true, false, dummyRoutes);
    }
}
