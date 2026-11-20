using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SRM.Core.Localization;

/// <summary>
/// 每用户一份的界面偏好，存在 %AppData%\SRM\settings.json（和 %ProgramData% 里的路由配置分开）。
/// 读写失败一律当默认值：设置读不出来只是语言回到默认，不能影响功能。
/// </summary>
internal static class UserSettings
{
    private sealed class Data
    {
        public string Language { get; set; } = string.Empty;
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SRM", "settings.json");

    public static string LoadLanguage()
    {
        try
        {
            if (!File.Exists(FilePath)) return string.Empty;
            var json = File.ReadAllText(FilePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<Data>(json, Options)?.Language ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static void SaveLanguage(string language)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(new Data { Language = language }, Options);
            var tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, json, new UTF8Encoding(false));
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(tempPath, FilePath);
        }
        catch
        {
            // 设置存不下去就只在这次运行里生效
        }
    }
}
