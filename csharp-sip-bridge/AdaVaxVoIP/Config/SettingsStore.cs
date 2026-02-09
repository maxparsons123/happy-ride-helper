using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdaVaxVoIP.Config;

/// <summary>
/// Loads/saves AppSettings from appsettings.json — same format as AdaMain.
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static string DefaultPath =>
        Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    public static AppSettings Load(string? path = null)
    {
        var file = path ?? DefaultPath;
        if (!File.Exists(file)) return new AppSettings();

        try
        {
            var json = File.ReadAllText(file);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings, string? path = null)
    {
        var file = path ?? DefaultPath;
        var json = JsonSerializer.Serialize(settings, JsonOpts);
        File.WriteAllText(file, json);
    }

    public static AppSettings Clone(AppSettings src)
    {
        var json = JsonSerializer.Serialize(src, JsonOpts);
        return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
    }
}
