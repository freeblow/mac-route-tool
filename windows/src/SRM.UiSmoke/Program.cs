using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SRM.App.Dialogs;
using SRM.Core;
using SRM.Core.Localization;

namespace SRM.UiSmoke;

internal static class Program
{
    private const int Width = 1300;
    private const int Height = 860;

    [STAThread]
    private static int Main(string[] args)
    {
        var outputDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "srm-ui");
        Directory.CreateDirectory(outputDir);
        var configPath = args.Length > 1 ? args[1] : CreateSampleConfig(outputDir);
        Environment.SetEnvironmentVariable("SRM_CONFIG", configPath);

        var app = new SRM.App.App();
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        var window = new SRM.App.MainWindow { Width = Width, Height = Height };
        Pump(2500);

        var root = (FrameworkElement)window.Content;
        window.Content = null;
        foreach (var item in SystemRoutes.Query(RouteConfigStore.Load().Routes))
        {
            Console.WriteLine($"  [预览·系统路由] {item.Destination} → {item.Gateway} · {item.Source} · 可删={item.Removable}");
        }
        var host = new Grid { Background = (Brush)app.Resources["WindowBrush"] };
        host.Children.Add(root);
        host.Width = Width;
        host.Height = Height;
        host.Measure(new Size(Width, Height));
        host.Arrange(new Rect(0, 0, Width, Height));

        SelectRow(window, "RouteGrid", 1);
        Settle(host);
        foreach (var grid in Descendants<DataGrid>(host))
        {
            Console.WriteLine($"  [预览·控件] {grid.Name} 可见={grid.Visibility} 行数={grid.Items.Count} 高={grid.ActualHeight:0.#}");
        }
        Render(host, Path.Combine(outputDir, "routes.png"));
        DumpColumns(host);

        SetTab(window, "TabNics");
        SelectRow(window, "NicGrid", 6);
        Settle(host);
        Render(host, Path.Combine(outputDir, "nics.png"));

        SelectRow(window, "NicGrid", 6);
        var before = SelectedName(window, "NicGrid");
        Pump(4300);
        var after = SelectedName(window, "NicGrid");
        Console.WriteLine($"定时刷新后选中行：{(before.Length > 0 && before == after ? "保持" : "丢失")}（{before} -> {after}）");

        SetTab(window, "TabScan");
        if (Field(window, "ScanTargetText") is TextBox targetText) targetText.Text = "192.168.15.240/28";
        Pump(300);
        if (Field(window, "ScanStartButton") is Button scanButton)
        {
            scanButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            for (var i = 0; i < 60 && Field(window, "_scanning") is true; i++) Pump(500);
        }
        Settle(host);
        Console.WriteLine($"  [预览·扫描] 行数={(Field(window, "ScanGrid") as DataGrid)?.Items.Count} 摘要={ScanSummary(window)}");
        Render(host, Path.Combine(outputDir, "scan.png"));
        RenderCopyMenu(host, (Field(window, "ScanGrid") as DataGrid)?.ContextMenu, Path.Combine(outputDir, "copy-menu.png"));
        VerifyCopy(window, host);

        var entry = new RouteEntry
        {
            Destination = "10.40.85.0/24",
            Gateway = "10.40.81.254",
            Note = "办公网段",
            Enabled = true,
        };
        RenderWindow(new RouteEditorWindow(entry, SampleNics()), Path.Combine(outputDir, "editor.png"));
        RenderWindow(CreateConfirmDialog(), Path.Combine(outputDir, "confirm.png"));
        RenderWindow(CreateCoexistDialog(), Path.Combine(outputDir, "coexist.png"));

        Console.WriteLine(CopyFailures.Count == 0
            ? "  [复制·自检] 全部通过：右键菜单的复制真的把内容写进了剪贴板"
            : $"  [复制·自检] 失败 {CopyFailures.Count} 项：{string.Join("、", CopyFailures)}");

        // 切成英文再渲染一遍：顺带验证「切语言 → 界面上的文案（含列头、右键菜单）跟着换」这条路径
        Loc.SetLanguage(Loc.English, persist: false);
        var enWindow = new Window
        {
            Width = Width,
            Height = Height,
            Left = -4000,
            Top = -4000,
            ShowInTaskbar = false,
            Content = host,
        };
        enWindow.Show();
        Pump(2500);
        SetTab(window, "TabRoutes");
        Settle(host);
        Render(host, Path.Combine(outputDir, "routes-en.png"));
        SetTab(window, "TabNics");
        Settle(host);
        Render(host, Path.Combine(outputDir, "nics-en.png"));
        SetTab(window, "TabScan");
        Settle(host);
        Render(host, Path.Combine(outputDir, "scan-en.png"));
        RenderWindow(new RouteEditorWindow(entry, SampleNics()), Path.Combine(outputDir, "editor-en.png"));
        RenderWindow(CreateConfirmDialog(), Path.Combine(outputDir, "confirm-en.png"));
        Console.WriteLine($"  [预览·英文] 标题={window.Title}｜网卡页标签={(Field(window, "TabNics") as RadioButton)?.Content}｜添加按钮={(Field(window, "AddRouteButton") as Button)?.Content}");
        Console.WriteLine($"  [预览·英文] 检测摘要={ScanSummary(window)}");
        enWindow.Close();
        Console.WriteLine($"已生成预览：{outputDir}");
        return CopyFailures.Count == 0 ? 0 : 1;
    }

    private static readonly List<string> CopyFailures = new();

    /// <summary>复制自检：真的走一遍右键菜单里的「复制」，再从剪贴板读回来核对内容。</summary>
    private static void VerifyCopy(SRM.App.MainWindow window, FrameworkElement host)
    {
        var saved = ReadClipboard();
        var scan = (DataGrid?)Field(window, "ScanGrid");
        if (scan is not null && scan.Items.Count > 0)
        {
            scan.SelectedIndex = 0;
            host.UpdateLayout();
            var expected = ((SRM.App.ScanRowVm)scan.Items[0]).AddressText;
            InvokeHandler(window, "ScanCopyRow_Click");
            var text = ReadClipboard();
            Report("IP 检测·复制整行", text.Contains(expected) && text.Contains('\t'), $"应含 {expected}，实际「{Show(text)}」");
        }
        else
        {
            Report("IP 检测·复制整行", false, "列表里没有结果");
        }

        var nic = (DataGrid?)Field(window, "NicGrid");
        if (nic is not null && nic.Items.Count > 0)
        {
            nic.SelectedIndex = 0;
            host.UpdateLayout();
            var row = (SRM.App.NicRowVm)nic.Items[0];
            InvokeHandler(window, "NicCopyRow_Click");
            var text = ReadClipboard();
            Report("网卡·复制这块网卡的信息", text.Contains(row.NameText) && text.Contains('\t'), $"应含 {row.NameText}，实际「{Show(text)}」");
        }

        if (saved is not null) WriteClipboard(saved);
    }

    /// <summary>右键菜单平时只在弹窗里出现，画面的视觉树里看不到：用真实的样式和真实的菜单项搭一份一样的，渲染成 PNG 方便看。</summary>
    private static void RenderCopyMenu(FrameworkElement host, ContextMenu? menu, string path)
    {
        if (menu is null) return;
        try
        {
            var column = new StackPanel { Background = menu.Background };
            foreach (var item in menu.Items)
            {
                if (item is MenuItem source)
                {
                    column.Children.Add(new MenuItem { Header = source.Header, Style = source.Style });
                }
                else if (item is Separator)
                {
                    var line = new Border
                    {
                        Height = 1,
                        Margin = new Thickness(6, 4, 6, 4),
                        Background = (Brush)(host.TryFindResource("GridLineBrush") ?? Brushes.LightGray),
                    };
                    column.Children.Add(line);
                }
            }

            var frame = new Border
            {
                Background = menu.Background,
                BorderBrush = menu.BorderBrush,
                BorderThickness = menu.BorderThickness,
                Padding = menu.Padding,
                CornerRadius = new CornerRadius(4),
                SnapsToDevicePixels = true,
                Child = column,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };
            var stage = new Grid { Background = Brushes.Transparent, Width = 300, Height = 220 };
            stage.Children.Add(frame);
            stage.Measure(new Size(300, 220));
            stage.Arrange(new Rect(0, 0, 300, 220));
            stage.UpdateLayout();

            var width = (int)Math.Ceiling(Math.Max(frame.ActualWidth, 1));
            var height = (int)Math.Ceiling(Math.Max(frame.ActualHeight, 1));
            var backdrop = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
            backdrop.Render(host);
            var menuBitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            menuBitmap.Render(frame);

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(backdrop, new Rect(0, 0, Width, Height));
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(36, 0, 0, 0)), null, new Rect(0, 0, Width, Height));
                dc.DrawImage(menuBitmap, new Rect(470, 300, width, height));
            }
            var composed = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
            composed.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(composed));
            using (var stream = File.Create(path)) encoder.Save(stream);
            Console.WriteLine($"  [预览·复制菜单] 已生成 {Path.GetFileName(path)}（{width}x{height}，共 {column.Children.Count} 项）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [预览·复制菜单] 跳过：{ex.Message}");
        }
    }
    private static void InvokeHandler(SRM.App.MainWindow window, string method)
    {
        var info = window.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"找不到 {method}");
        info.Invoke(window, new object[] { window, new RoutedEventArgs() });
    }

    private static void Report(string name, bool ok, string detail)
    {
        if (!ok) CopyFailures.Add(name);
        Console.WriteLine($"  [复制·自检] {(ok ? "通过" : "失败")} {name}：{detail}");
    }

    private static string Show(string text) => text.Replace("\r", string.Empty).Replace("\n", "⏎").Replace("\t", " ⇥ ");

    private static string ReadClipboard()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty; }
        catch (Exception) { return string.Empty; }
    }

    private static void WriteClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch (Exception) { }
    }

    private static IReadOnlyList<NicInfo> SampleNics() => new List<NicInfo>
    {
        new() { Index = 6, Name = "以太网", Address = "10.40.81.63", Status = "已连接", Metric = 100 },
        new() { Index = 7, Name = "WLAN", Status = "已断开", Metric = 10 },
    };

    private static Window CreateConfirmDialog()
    {
        return CreateDialog(DialogKind.Error, "禁用网卡",
            "确定要禁用「TAP-Windows Adapter V9」吗？",
            "效果等同于在「网络连接」里右键禁用：会立刻断开这块网卡的连接，之后可以随时再启用。",
            "禁用网卡", "取消");
    }

    private static Window CreateCoexistDialog()
    {
        var detail = "· VMware Network Adapter VMnet8：改为仅内网（不抢上网出口）" + Environment.NewLine
            + "· WLAN：开启跨网卡收发" + Environment.NewLine + Environment.NewLine
            + "仅内网的网卡依然能访问自己网段里的设备和服务器，只是不再抢上网出口；「跨网卡收发」用来避免多块网卡互相丢包。设置重启后依然有效。";
        return CreateDialog(DialogKind.Question, "一键共存", "保留「以太网」负责上网，其余网卡改为仅内网？", detail, "开始调整", "取消");
    }

    private static Window CreateDialog(DialogKind kind, string title, string message, string detail, string confirmText, string cancelText)
    {
        var dialog = (Window)Activator.CreateInstance(typeof(MessageDialog), nonPublic: true)!;
        var configure = typeof(MessageDialog).GetMethod("Configure", BindingFlags.Instance | BindingFlags.NonPublic)!;
        configure.Invoke(dialog, new object[] { kind, title, message, detail, confirmText, cancelText });
        return dialog;
    }

    private static void RenderWindow(Window window, string path)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -4000;
        window.Top = -4000;
        window.ShowInTaskbar = false;
        window.Show();
        Pump(500);
        var content = (FrameworkElement)window.Content;
        content.UpdateLayout();
        var width = (int)Math.Ceiling(content.ActualWidth);
        var height = (int)Math.Ceiling(content.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
        window.Close();
        Pump(120);
    }

    private static string SelectedName(Window window, string fieldName) =>
        (Field(window, fieldName) as DataGrid)?.SelectedItem is SRM.App.NicRowVm nic ? nic.Nic.Name : string.Empty;

    private static string ScanSummary(Window window) =>
        Field(window, "ScanSummaryText") is TextBlock text ? text.Text : string.Empty;
    private static void SetTab(Window window, string fieldName)
    {
        if (Field(window, fieldName) is RadioButton tab) tab.IsChecked = true;
    }

    private static void SelectRow(Window window, string fieldName, int index)
    {
        if (Field(window, fieldName) is DataGrid grid && index < grid.Items.Count) grid.SelectedIndex = index;
    }

    private static object? Field(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(target);

    private static void Settle(FrameworkElement element)
    {
        for (var i = 0; i < 2; i++)
        {
            element.UpdateLayout();
            Pump(220);
        }
        element.UpdateLayout();
    }

    private static void Render(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void DumpColumns(DependencyObject root)
    {
        foreach (var grid in Descendants<DataGrid>(root))
        {
            Console.WriteLine($"grid {grid.Name}: {grid.Columns.Count} 列, 视口 {grid.ActualWidth:0} x {grid.ActualHeight:0}");
            foreach (var column in grid.Columns)
            {
                Console.WriteLine($"    [{column.Header}] width={column.Width} actual={column.ActualWidth:0.0} min={column.MinWidth}");
            }
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(milliseconds),
            DispatcherPriority.Background,
            (_, _) => frame.Continue = false,
            Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private static string CreateSampleConfig(string outputDir)
    {
        var path = Path.Combine(outputDir, "sample-routes.json");
        const string json = """
        {
          "version": 1,
          "routes": [
            {
              "id": "preview-default",
              "destination": "0.0.0.0/0",
              "gateway": "10.40.81.254",
              "interfaceIndex": 6,
              "enabled": true,
              "note": "默认出口（示例）"
            },
            {
              "id": "preview-office",
              "destination": "10.30.0.0/16",
              "gateway": "10.40.81.254",
              "interfaceIndex": 6,
              "enabled": true,
              "note": "公司内网（示例）"
            },
            {
              "id": "preview-lab",
              "destination": "192.168.99.0/24",
              "gateway": "10.40.81.254",
              "enabled": true,
              "note": "实验室设备（示例）"
            },
            {
              "id": "preview-off",
              "destination": "172.16.0.0/12",
              "gateway": "10.40.81.254",
              "enabled": false,
              "note": "暂时停用（示例）"
            }
          ]
        }
        """;
        File.WriteAllText(path, json);
        return path;
    }
}
