using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using US16x08_AbletonPanel.Models;

namespace US16x08_AbletonPanel.Services;

/// <summary>
/// Loads and saves the Control &amp; Sync tab state (internal clock, audio-channel-to-synth links, controller mappings)
/// to %LOCALAPPDATA%\TASCAM_US16x08_AbletonPanel\control_sync_v2.json.
/// </summary>
public sealed class ControlSettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly object _writeLock = new();

    public ControlSettingsService()
    {
        string appDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TASCAM_US16x08_AbletonPanel");
        Directory.CreateDirectory(appDir);
        _filePath = Path.Combine(appDir, "control_sync_v2.json");
    }

    public ControlSyncSettings Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                string json = File.ReadAllText(_filePath);
                var loaded = JsonSerializer.Deserialize<ControlSyncSettings>(json, JsonOpts);
                if (loaded != null)
                {
                    loaded.ChannelLinks ??= new();
                    loaded.ControlMappings ??= new();
                    loaded.FocusControllerPort ??= "";
                    return loaded;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable file: fall through to defaults
        }

        return new ControlSyncSettings();
    }

    /// <summary>Serializes on the calling thread (so the snapshot is consistent) and writes on a background thread.</summary>
    public void SaveInBackground(ControlSyncSettings settings)
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(settings, JsonOpts);
        }
        catch
        {
            return;
        }

        _ = Task.Run(() => WriteJson(json));
    }

    /// <summary>Blocking save, used on application exit.</summary>
    public void SaveNow(ControlSyncSettings settings)
    {
        try
        {
            WriteJson(JsonSerializer.Serialize(settings, JsonOpts));
        }
        catch
        {
            // Ignore IO errors
        }
    }

    private void WriteJson(string json)
    {
        lock (_writeLock)
        {
            try
            {
                File.WriteAllText(_filePath, json);
            }
            catch
            {
                // Ignore IO errors
            }
        }
    }
}
