namespace SRM.Core;

/// <summary>
/// 把每次写操作的原话（命令、退出码、原始输出、测量结果）记到 %ProgramData%\SRM\srm.log，
/// 出问题时能翻账。只保留最近 256 KB，多出来的滚到 srm.log.1。
/// </summary>
public static class OperationLog
{
    private const long MaxBytes = 256 * 1024;

    public static string LogPath => System.IO.Path.Combine(AppInfo.ConfigDirectory, "srm.log");

    public static void Write(string text)
    {
        try
        {
            var path = LogPath;
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) System.IO.Directory.CreateDirectory(directory);
            var info = new System.IO.FileInfo(path);
            if (info.Exists && info.Length > MaxBytes)
            {
                var archived = path + ".1";
                if (System.IO.File.Exists(archived)) System.IO.File.Delete(archived);
                System.IO.File.Move(path, archived);
            }
            System.IO.File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text}{Environment.NewLine}", System.Text.Encoding.UTF8);
        }
        catch
        {
            // 记日志失败不能影响功能
        }
    }
}