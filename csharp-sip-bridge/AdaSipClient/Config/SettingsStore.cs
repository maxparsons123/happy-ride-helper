using System.Text.Json;
using AdaSipClient.Core;

namespace AdaSipClient.Config;

/// <summary>
/// Persists AppState to a JSON file. Load on startup, save on change.
/// File location: %APPDATA%/AdaSipClient/settings.json
/// </summary>
public static class SettingsStore
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AdaSipClient");

    private static readonly string SettingsFile =
        Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Load saved settings into the given AppState.
    /// </summary>
    public static void Load(AppState state)
    {
        try
        {
            if (!File.Exists(SettingsFile)) return;

            var json = File.ReadAllText(SettingsFile);
            var saved = JsonSerializer.Deserialize<SavedSettings>(json, JsonOpts);
            if (saved == null) return;

            state.SipServer = saved.SipServer ?? "";
            state.SipPort = saved.SipPort > 0 ? saved.SipPort : 5060;
            state.SipUser = saved.SipUser ?? "";
            state.SipPassword = saved.SipPassword ?? "";
            state.Transport = saved.Transport ?? "UDP";
            state.Mode = Enum.TryParse<CallMode>(saved.Mode, out var m) ? m : CallMode.AutoBot;
            state.InputVolumePercent = Math.Clamp(saved.InputVolume, 0, 200);
            state.OutputVolumePercent = Math.Clamp(saved.OutputVolume, 0, 200);
            state.OpenAiApiKey = saved.OpenAiApiKey ?? "";
            state.WebSocketUrl = saved.WebSocketUrl ?? "";
            state.SimliApiKey = saved.SimliApiKey ?? "";
            state.SimliFaceId = saved.SimliFaceId ?? "";
            state.SimliEnabled = saved.SimliEnabled;
        }
        catch
        {
            // Corrupted file — ignore, use defaults
        }
    }

    /// <summary>
    /// Save the current AppState to disk.
    /// </summary>
    public static void Save(AppState state)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);

            var saved = new SavedSettings
            {
                SipServer = state.SipServer,
                SipPort = state.SipPort,
                SipUser = state.SipUser,
                SipPassword = state.SipPassword,
                Transport = state.Transport,
                Mode = state.Mode.ToString(),
                InputVolume = state.InputVolumePercent,
                OutputVolume = state.OutputVolumePercent,
                OpenAiApiKey = state.OpenAiApiKey,
                WebSocketUrl = state.WebSocketUrl,
                SimliApiKey = state.SimliApiKey,
                SimliFaceId = state.SimliFaceId,
                SimliEnabled = state.SimliEnabled
            };

            var json = JsonSerializer.Serialize(saved, JsonOpts);
            File.WriteAllText(SettingsFile, json);
        }
        catch
        {
            // Silently fail — don't crash on save errors
        }
    }

    private sealed class SavedSettings
    {
        public string? SipServer { get; set; }
        public int SipPort { get; set; }
        public string? SipUser { get; set; }
        public string? SipPassword { get; set; }
        public string? Transport { get; set; }
        public string? Mode { get; set; }
        public int InputVolume { get; set; } = 100;
        public int OutputVolume { get; set; } = 100;
        public string? OpenAiApiKey { get; set; }
        public string? WebSocketUrl { get; set; }
        public string? SimliApiKey { get; set; }
        public string? SimliFaceId { get; set; }
        public bool SimliEnabled { get; set; }
    }
}
