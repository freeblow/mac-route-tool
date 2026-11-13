using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SRM.Core;

public static class RouteConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static RouteConfig Load()
    {
        try
        {
            if (!File.Exists(AppInfo.ConfigPath)) return new RouteConfig();
            var json = File.ReadAllText(AppInfo.ConfigPath, Encoding.UTF8);
            var config = JsonSerializer.Deserialize<RouteConfig>(json, Options);
            return config ?? new RouteConfig();
        }
        catch
        {
            return new RouteConfig();
        }
    }

    public static void Save(RouteConfig config)
    {
        Directory.CreateDirectory(AppInfo.ConfigDirectory);
        var json = JsonSerializer.Serialize(config, Options);
        var tempPath = AppInfo.ConfigPath + ".tmp";
        File.WriteAllText(tempPath, json, new UTF8Encoding(false));
        if (File.Exists(AppInfo.ConfigPath)) File.Delete(AppInfo.ConfigPath);
        File.Move(tempPath, AppInfo.ConfigPath);
    }
}
