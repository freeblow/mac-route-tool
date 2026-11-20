namespace SRM.Core;

public static class AppInfo
{
    public const string ProductName = "SRM";
    public const string Version = "2.8.0";
    public static string ConfigDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SRM");
    public static string ConfigPath => Environment.GetEnvironmentVariable("SRM_CONFIG") is { Length: > 0 } custom
        ? custom
        : Path.Combine(ConfigDirectory, "routes.json");
}
