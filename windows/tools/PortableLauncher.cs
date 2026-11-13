// SRM 便携版启动器：把同目录的 runtime（便携 .NET 运行时）指给子进程，再启动 app\SRM.exe
// 编译：tools\Build-PortableFromLocalRuntime.ps1 用 Windows 自带的 csc.exe（.NET Framework）编译，
//       不需要 SDK、不需要联网；所以这里刻意只用老语法（C# 5），保证老编译器也能编译。
//
// 为什么要这个启动器：目标电脑可能没装 .NET 8 桌面运行时，靠 DOTNET_ROOT 让它只用自带的那一份。
using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

internal static class PortableLauncher
{
    private const string AppRelativePath = @"app\SRM.exe";

    [STAThread]
    private static void Main()
    {
        try
        {
            var root = AppDomain.CurrentDomain.BaseDirectory;
            var runtime = Path.Combine(root, "runtime");
            var target = Path.Combine(root, AppRelativePath);

            if (!Directory.Exists(runtime))
            {
                Warn("便携版要把整个文件夹一起拷贝，不能只拷一个 exe。\r\n\r\n现在缺少：" + runtime);
                return;
            }

            if (!File.Exists(target))
            {
                Warn("便携版要把整个文件夹一起拷贝，不能只拷一个 exe。\r\n\r\n现在缺少：" + target);
                return;
            }

            // 只影响即将启动的这个子进程：让它从自带的 runtime 里找 .NET，不看系统装没装
            Environment.SetEnvironmentVariable("DOTNET_ROOT", runtime, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("DOTNET_ROOT_X64", runtime, EnvironmentVariableTarget.Process);

            var info = new ProcessStartInfo(target);
            info.UseShellExecute = false;
            info.WorkingDirectory = Path.Combine(root, "app");
            Process.Start(info);
        }
        catch (Exception ex)
        {
            Warn("启动失败：" + ex.Message);
        }
    }

    private static void Warn(string message)
    {
        MessageBox.Show(message, "SRM 网络助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
