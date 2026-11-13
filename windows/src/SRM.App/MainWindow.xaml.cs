using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SRM.App.Dialogs;
using SRM.Core;

namespace SRM.App;

public partial class MainWindow : Window
{
    /// <summary>防火墙状态多久重新读一次（秒）：读一次要开一次命令行，不适合每 3 秒的刷新都去读。</summary>
    private const int FirewallCacheSeconds = 30;

    private readonly RouteService _service = new();
    private readonly ObservableCollection<RouteRowVm> _routeRows = new();
    private readonly ObservableCollection<NicRowVm> _nicRows = new();
    private readonly ObservableCollection<SystemRouteRowVm> _systemRows = new();
    private readonly ObservableCollection<ScanRowVm> _scanRows = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private List<NicInfo> _lastNics = new();
    private List<SubnetHost> _scanResults = new();
    /// <summary>最近一次右键点在「IP 检测」列表的哪一行（点在表头或空白处时为空）。</summary>
    private ScanRowVm? _scanMenuRow;
    private SubnetScanPlan? _scanPlan;
    private CancellationTokenSource? _scanToken;
    private string? _scanTargetSignature;
    private bool _scanning;
    private bool _scanTargetApplying;
    private int? _discoveryIndex;
    private DiscoveryFirewall.Status? _inboundStatus;
    private DateTime _inboundStamp = DateTime.MinValue;
    private bool _inboundLoading;
    private int _inboundIndex;
    private DispatcherTimer? _shoutTimer;
    private int _shoutIndex;
    private string _shoutAddress = string.Empty;
    private int _shoutSent;
    private DateTime _configStamp = DateTime.MinValue;
    private int _tick;
    private bool _busy;
    private string _statusMessage = "正在读取系统状态…";
    private DateTime _lastRefresh;

    public MainWindow()
    {
        InitializeComponent();
        RouteGrid.ItemsSource = _routeRows;
        NicGrid.ItemsSource = _nicRows;
        SystemRouteGrid.ItemsSource = _systemRows;
        ScanGrid.ItemsSource = _scanRows;
        ElevationBanner.Visibility = NetworkApi.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        ElevationBannerText.Text = "当前未以管理员身份运行：写入系统路由、调整跃点数、启停网卡、修改多网卡共存设置都会失败。请关闭窗口后改为右键「以管理员身份运行」。";
        EnvText.Text = $"配置：{AppInfo.ConfigPath}";
        HeaderInfoText.Text = $"v{AppInfo.Version}";
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
        Dispatcher.InvokeAsync(() => _ = RefreshAllAsync());
    }

    // ================= 刷新 =================

    private async Task RefreshAllAsync()
    {
        if (_busy) return;
        _statusMessage = "正在读取系统状态…";
        UpdateStatusBar();
        try
        {
            ReloadConfigIfChanged(force: true);
            RefreshRouteRows();
            RefreshRouteStatuses();
            await RefreshSystemRoutesAsync();
            await RefreshNicRowsAsync();
            _lastRefresh = DateTime.Now;
            _statusMessage = "就绪";
        }
        catch (Exception ex)
        {
            _statusMessage = "刷新失败：" + Shorten(ex.Message);
        }
        UpdateStatusBar();
        UpdateButtonStates();
    }

    private async Task TickAsync()
    {
        if (_busy) return;
        try
        {
            ReloadConfigIfChanged(force: false);
            RefreshRouteRows();
            RefreshRouteStatuses();
            await RefreshSystemRoutesAsync();
            await RefreshNicRowsAsync();
            _lastRefresh = DateTime.Now;
            UpdateStatusBar();
            UpdateButtonStates();
            _tick++;
            if (_tick % 7 == 0) await AutoSyncAsync();
        }
        catch (Exception ex)
        {
            _statusMessage = "刷新失败：" + ex.Message;
            UpdateStatusBar();
        }
    }

    private async Task AutoSyncAsync()
    {
        if (!NetworkApi.IsElevated) return;
        if (!_routeRows.Any(row => row.NeedsSync)) return;
        await RunBusyAsync("正在自动同步路由…", () => _service.Sync());
        RefreshRouteStatuses();
    }

    private void ReloadConfigIfChanged(bool force)
    {
        var stamp = ConfigStamp();
        if (!force && stamp == _configStamp) return;
        _configStamp = stamp;
        var errors = new Dictionary<string, string>(_service.Errors);
        _service.Reload();
        foreach (var (key, value) in errors) _service.Errors[key] = value;
    }

    private void SaveConfig()
    {
        _service.Save();
        _configStamp = ConfigStamp();
    }

    private static DateTime ConfigStamp()
    {
        try
        {
            var path = AppInfo.ConfigPath;
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private void RefreshRouteRows()
    {
        var entries = _service.Config.Routes;
        for (var i = _routeRows.Count - 1; i >= 0; i--)
        {
            if (!entries.Contains(_routeRows[i].Entry)) _routeRows.RemoveAt(i);
        }
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var row = _routeRows.FirstOrDefault(item => ReferenceEquals(item.Entry, entry));
            if (row is null)
            {
                row = new RouteRowVm(entry);
                _routeRows.Insert(Math.Min(i, _routeRows.Count), row);
            }
            else
            {
                var current = _routeRows.IndexOf(row);
                if (current != i) _routeRows.Move(current, i);
            }
            row.InterfaceText = DescribeInterface(entry.InterfaceIndex);
        }
        RouteEmptyHint.Visibility = _routeRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var enabled = entries.Count(item => item.Enabled);
        RouteSummaryText.Text = entries.Count == 0 ? string.Empty : $"共 {entries.Count} 条 · 已启用 {enabled} 条";
    }

    private void RefreshRouteStatuses()
    {
        List<RouteRow> table;
        try
        {
            table = RouteTable.Query();
        }
        catch
        {
            table = new List<RouteRow>();
        }
        var persistent = PersistentRoutes.Query();
        foreach (var row in _routeRows)
        {
            var route = row.Entry;
            if (!route.Enabled)
            {
                row.NeedsSync = false;
                row.ApplyStatus(new RouteStatus { State = RouteState.Disabled, Message = "已在配置里停用，不会写入系统路由表。" });
                continue;
            }
            if (_service.Errors.TryGetValue(route.Id, out var error))
            {
                row.NeedsSync = true;
                row.ApplyStatus(new RouteStatus { State = RouteState.Failed, Message = error });
                continue;
            }
            if (!Ipv4.TryParseSubnet(route.Destination, out var network, out var mask) || !Ipv4.TryParse(route.Gateway, out var gateway))
            {
                row.NeedsSync = false;
                row.ApplyStatus(new RouteStatus { State = RouteState.Failed, Message = "目标或网关格式不正确，请点「编辑」修正。" });
                continue;
            }
            var prefix = Ipv4.PrefixFromMask(mask);
            var active = table.Any(item => item.Destination == network && item.PrefixLength == prefix && item.NextHop == gateway
                                           && (route.InterfaceIndex is not > 0 || item.InterfaceIndex == route.InterfaceIndex));
            var wanted = $"{Ipv4.ToText(network)},{Ipv4.ToText(mask)},{Ipv4.ToText(gateway)},";
            var persisted = persistent.Any(item => item.StartsWith(wanted, StringComparison.OrdinalIgnoreCase));
            row.NeedsSync = !active || !persisted;
            row.ApplyStatus(active
                ? new RouteStatus { State = RouteState.Active, Message = persisted ? "已写入系统路由表，重启后依然有效。" : "已生效，但还没有写入持久路由表。" }
                 : new RouteStatus { State = RouteState.Pending, Message = "尚未写入系统路由表。" });
        }
    }

    /// <summary>刷新「系统里其它路由」：系统里已经存在、但不在 SRM 配置里的持久路由和网卡静态网关。</summary>
    private async Task RefreshSystemRoutesAsync()
    {
        var managed = _service.Config.Routes.ToList();
        List<SystemRouteEntry> entries;
        try
        {
            entries = await Task.Run(() => SystemRoutes.Query(managed));
        }
        catch
        {
            entries = new List<SystemRouteEntry>();
        }
        ApplySystemRoutes(entries);
    }

    private void ApplySystemRoutes(List<SystemRouteEntry> entries)
    {
        for (var i = _systemRows.Count - 1; i >= 0; i--)
        {
            if (!entries.Any(item => SystemRouteRowVm.KeyOf(item) == _systemRows[i].Key)) _systemRows.RemoveAt(i);
        }
        for (var i = 0; i < entries.Count; i++)
        {
            var key = SystemRouteRowVm.KeyOf(entries[i]);
            var row = _systemRows.FirstOrDefault(item => item.Key == key);
            if (row is null)
            {
                row = new SystemRouteRowVm(entries[i]);
                _systemRows.Insert(Math.Min(i, _systemRows.Count), row);
            }
            else
            {
                var current = _systemRows.IndexOf(row);
                if (current != i) _systemRows.Move(current, i);
            }
        }

        SystemRouteSection.Visibility = _systemRows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_systemRows.Count == 0)
        {
            SystemRouteSummaryText.Text = string.Empty;
            return;
        }
        var deletable = _systemRows.Count(item => item.Entry.Removable);
        SystemRouteSummaryText.Text = deletable == 0
            ? $"共 {_systemRows.Count} 条，都要在网卡属性里改"
            : $"共 {_systemRows.Count} 条 · 可直接删除 {deletable} 条";
    }

    private async Task RefreshNicRowsAsync()
    {
        var nics = await Task.Run(() => NetworkApi.ListNics());
        _lastNics = nics;

        var known = new Dictionary<string, NicRowVm>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _nicRows) known[row.Key] = row;

        var ordered = new List<NicRowVm>(nics.Count);
        foreach (var nic in nics)
        {
            var key = nic.AdapterGuid.Length > 0 ? nic.AdapterGuid : nic.Name;
            if (known.TryGetValue(key, out var row)) row.Apply(nic);
            else row = new NicRowVm(nic);
            ordered.Add(row);
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            var row = ordered[i];
            var current = _nicRows.IndexOf(row);
            if (current < 0) _nicRows.Insert(i, row);
            else if (current != i) _nicRows.Move(current, i);
        }
        while (_nicRows.Count > ordered.Count) _nicRows.RemoveAt(_nicRows.Count - 1);
        for (var i = 0; i < _nicRows.Count; i++) _nicRows[i].Rank = i + 1;
        UpdateNicRoles();

        NicEmptyHint.Visibility = _nicRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var row in _routeRows) row.InterfaceText = DescribeInterface(row.Entry.InterfaceIndex);
        RefreshScanTargets();
        RefreshDiscoveryState();
    }

    /// <summary>标注每块网卡在多网卡共存里的角色，并在多块网卡抢上网出口时给出提示。</summary>
    private void UpdateNicRoles()
    {
        var exit = NicCoexist.PickExit(_nicRows.Select(row => row.Nic).ToList());
        foreach (var row in _nicRows)
        {
            var nic = row.Nic;
            row.ExitRole = nic.IgnoresDefaultRoutes ? NicExitRole.Internal
                : nic.Index == exit?.Index && nic.HasGateway ? NicExitRole.Exit
                : nic.HasDefaultRoute ? NicExitRole.Backup
                : NicExitRole.None;
        }

        var racers = _nicRows.Where(row => row.Nic.JoinsExitRace).ToList();
        if (racers.Count > 1)
        {
            var names = string.Join("、", racers.Select(row => row.NameText));
            CoexistWarnText.Text = $"检测到 {racers.Count} 块网卡同时在抢上网出口（{names}）：容易互相抢路由，出现“起了这块、那块就不好使”。建议点「一键共存」，只留一块网卡上网，其余改成仅内网。";
            CoexistWarnBar.Visibility = Visibility.Visible;
        }
        else
        {
            CoexistWarnBar.Visibility = Visibility.Collapsed;
        }
    }

    private string DescribeInterface(int? index)
    {
        if (index is not int value || value <= 0) return "自动（按网关判断）";
        var nic = _lastNics.FirstOrDefault(item => item.Index == value);
        return nic is null ? $"#{value}" : $"{value} · {nic.Name}";
    }

    private void UpdateStatusBar()
    {
        StatusText.Text = _lastRefresh == default
            ? _statusMessage
            : $"{_statusMessage} · 最后刷新 {_lastRefresh:HH:mm:ss}";
    }

    private void SetStatus(string message)
    {
        _statusMessage = message;
        UpdateStatusBar();
    }

    private void UpdateButtonStates()
    {
        var nic = (NicGrid.SelectedItem as NicRowVm)?.Nic;
        var route = RouteGrid.SelectedItem as RouteRowVm;
        var idle = !_busy && !_scanning;

        RaisePriorityButton.IsEnabled = idle && nic?.Metric is not null;
        LowerPriorityButton.IsEnabled = idle && nic?.Metric is not null;
        AutoMetricButton.IsEnabled = idle && nic is not null && nic.AutomaticMetric == false;
        DisableNicButton.IsEnabled = idle && nic is not null && !nic.IsDisabled;
        EnableNicButton.IsEnabled = idle && nic?.IsDisabled == true;
        RefreshNicButton.IsEnabled = idle;
        CoexistButton.IsEnabled = idle && _nicRows.Count > 0;
        SetExitButton.IsEnabled = idle && nic is not null && !nic.IsDisabled && (nic.ConfiguredGateway.Length > 0 || nic.HasDefaultRoute);
        SetInternalButton.IsEnabled = idle && nic is not null && !nic.IsDisabled && nic.Metric is not null;
        WeakHostButton.IsEnabled = idle && nic is not null && !nic.IsDisabled && nic.Metric is not null;
        WeakHostButton.Content = nic is not null && nic.WeakHostSend && nic.WeakHostReceive ? "禁止跨网卡收发" : "允许跨网卡收发";

        AddRouteButton.IsEnabled = idle;
        SyncRouteButton.IsEnabled = idle;
        HeaderRefreshButton.IsEnabled = idle;
        EditRouteButton.IsEnabled = idle && route is not null;
        DeleteRouteButton.IsEnabled = idle && route is not null;
        ToggleRouteButton.IsEnabled = idle && route is not null;
        TestRouteButton.IsEnabled = idle && route is not null;
        ToggleRouteButton.Content = route is not null && route.Entry.Enabled ? "停用" : "启用";
        ScanStartButton.IsEnabled = idle;
        ScanStopButton.IsEnabled = _scanning;
        ScanExportButton.IsEnabled = !_scanning && _scanResults.Count > 0;
        DiscoveryButton.IsEnabled = idle && nic is not null && !nic.IsDisabled;
        DiscoveryButton.Content = nic is not null && _discoveryIndex == nic.Index ? "局域网发现：改回自动" : "让局域网发现走这块网卡";
        FixDiscoverButton.IsEnabled = idle && nic is not null && !nic.IsDisabled;
        FixDiscoverButton.Content = _inboundStatus is { NeedsFix: true } ? "修复：让局域网能发现这台机器" : "让局域网能发现这台机器";
        ListenDiscoverButton.IsEnabled = idle;
        ShoutDiscoverButton.IsEnabled = idle && nic is not null && !nic.IsDisabled;
        KeepShoutButton.Content = _shoutTimer is null ? "一直喊（每 15 秒一次）" : "停止喊话";
        KeepShoutButton.IsEnabled = _shoutTimer is not null || (idle && nic is not null && !nic.IsDisabled);
    }

    private async Task<bool> RunBusyAsync(string message, Action work)
    {
        if (_busy) return false;
        _busy = true;
        SetStatus(message);
        UpdateButtonStates();
        try
        {
            await Task.Run(work);
            return true;
        }
        catch (Exception ex)
        {
            SetStatus("上一步操作失败");
            AppDialog.Error(this, "操作失败", ex.Message);
            return false;
        }
        finally
        {
            _busy = false;
            UpdateButtonStates();
        }
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (RouteCard is null || NicCard is null) return;
        var showRoutes = TabRoutes.IsChecked == true;
        var showNics = TabNics.IsChecked == true;
        RouteCard.Visibility = showRoutes ? Visibility.Visible : Visibility.Collapsed;
        NicCard.Visibility = showNics ? Visibility.Visible : Visibility.Collapsed;
        if (ScanCard is not null) ScanCard.Visibility = !showRoutes && !showNics ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtonStates();
    }

    private void RouteGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtonStates();

    private void NicGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtonStates();
        RefreshDiscoveryState();
    }

    // ================= 路由操作 =================

    private async void AddRoute_Click(object sender, RoutedEventArgs e)
    {
        var editor = new RouteEditorWindow(null, _lastNics) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null) return;
        _service.Config.Routes.Add(editor.Result);
        SaveConfig();
        RefreshRouteRows();
        RefreshRouteStatuses();
        if (editor.Result.Enabled) await SyncRouteAsync(editor.Result);
        SetStatus($"已添加路由 {editor.Result.Destination}。");
    }

    private async void EditRoute_Click(object sender, RoutedEventArgs e)
    {
        if (RouteGrid.SelectedItem is not RouteRowVm row) return;
        await EditRouteAsync(row);
    }

    private void RouteGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(RouteGrid, source) is not DataGridRow gridRow) return;
        if (gridRow.Item is RouteRowVm row) _ = EditRouteAsync(row);
    }

    private async Task EditRouteAsync(RouteRowVm row)
    {
        var previous = new RouteEntry
        {
            Id = row.Entry.Id,
            Destination = row.Entry.Destination,
            Gateway = row.Entry.Gateway,
            InterfaceIndex = row.Entry.InterfaceIndex,
            Enabled = row.Entry.Enabled,
            Note = row.Entry.Note,
        };

        var editor = new RouteEditorWindow(row.Entry, _lastNics) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null) return;
        SaveConfig();
        row.ApplyEntryEdits();
        row.InterfaceText = DescribeInterface(row.Entry.InterfaceIndex);
        RefreshRouteRows();

        if (previous.Enabled) await RemoveSystemRouteAsync(previous);
        if (row.Entry.Enabled) await SyncRouteAsync(row.Entry);
        RefreshRouteStatuses();
        SetStatus($"已保存 {row.Entry.Destination}。");
    }

    private async void ToggleRoute_Click(object sender, RoutedEventArgs e)
    {
        if (RouteGrid.SelectedItem is not RouteRowVm row) return;
        row.Entry.Enabled = !row.Entry.Enabled;
        SaveConfig();
        if (row.Entry.Enabled) await SyncRouteAsync(row.Entry);
        else await RemoveSystemRouteAsync(row.Entry);
        RefreshRouteRows();
        RefreshRouteStatuses();
        UpdateButtonStates();
        SetStatus(row.Entry.Enabled ? $"已启用 {row.Entry.Destination}。" : $"已停用 {row.Entry.Destination}。");
    }

    private async void DeleteRoute_Click(object sender, RoutedEventArgs e)
    {
        if (RouteGrid.SelectedItem is not RouteRowVm row) return;
        var confirmed = AppDialog.Confirm(this, "删除路由",
            $"确定要删除「{row.Entry.Destination}」吗？",
            row.Entry.Enabled ? "该路由会立刻从系统路由表中移除，配置文件里的记录也会一起删除。" : "配置文件里的这条记录会被删除。",
            "删除", danger: true);
        if (!confirmed) return;
        await RemoveSystemRouteAsync(row.Entry);
        _service.Config.Routes.Remove(row.Entry);
        _service.Errors.Remove(row.Entry.Id);
        SaveConfig();
        RefreshRouteRows();
        RefreshRouteStatuses();
        SetStatus($"已删除 {row.Entry.Destination}。");
    }

    private async void SyncRoutes_Click(object sender, RoutedEventArgs e)
    {
        if (_service.Config.Routes.Count == 0)
        {
            SetStatus("还没有配置任何路由，先点“+ 添加路由”。");
            return;
        }
        await RunBusyAsync("正在重新同步全部路由…", () => _service.Sync());
        RefreshRouteStatuses();
        var failed = _service.Config.Routes.Count(item => _service.Errors.ContainsKey(item.Id));
        SetStatus(failed == 0 ? "同步完成，全部路由都已生效。" : $"同步完成，其中 {failed} 条失败。");
    }

    private async void TestRoute_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RouteRowVm row) await TestRouteAsync(row);
    }

    private async void TestSelectedRoute_Click(object sender, RoutedEventArgs e)
    {
        if (RouteGrid.SelectedItem is RouteRowVm row) await TestRouteAsync(row);
    }

    private async Task TestRouteAsync(RouteRowVm row)
    {
        row.TestText = "正在测试…";
        var text = await PingProbe.ProbeAsync(row.Entry);
        row.TestText = text;
    }

    private async Task SyncRouteAsync(RouteEntry entry)
    {
        await RunBusyAsync($"正在写入路由 {entry.Destination}…", () =>
        {
            try
            {
                _service.EnsureRoute(entry);
                _service.Errors.Remove(entry.Id);
            }
            catch (Exception ex)
            {
                _service.Errors[entry.Id] = ex.Message;
            }
        });
        RefreshRouteStatuses();
        if (_service.Errors.TryGetValue(entry.Id, out var error)) SetStatus($"写入失败：{Shorten(error)}");
    }

    private async Task RemoveSystemRouteAsync(RouteEntry entry)
    {
        await RunBusyAsync($"正在移除路由 {entry.Destination}…", () =>
        {
            try
            {
                _service.RemoveRoute(entry);
                _service.Errors.Remove(entry.Id);
            }
            catch (Exception ex)
            {
                _service.Errors[entry.Id] = ex.Message;
            }
        });
    }

    /// <summary>删除「系统里其它路由」里的一条持久路由（网卡属性里的静态网关不让删，只提示）。</summary>
    private async void DeleteSystemRoute_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if ((sender as FrameworkElement)?.DataContext is not SystemRouteRowVm row) return;
        var entry = row.Entry;
        if (!entry.Removable)
        {
            AppDialog.Info(this, "这条要手动改", entry.Detail);
            return;
        }
        var detail = $"目标 {entry.Destination} · 网关 {entry.Gateway}" + Environment.NewLine + Environment.NewLine + entry.Detail;
        if (!AppDialog.Confirm(this, "删除系统路由", "确定要删除这条路由吗？", detail, "删除", danger: true)) return;
        var ok = await RunBusyAsync($"正在删除路由 {entry.Destination}…", () => SystemRoutes.Remove(entry));
        if (!ok) return;
        RefreshRouteStatuses();
        await RefreshSystemRoutesAsync();
        SetStatus($"已删除系统里的路由 {entry.Destination}（网关 {entry.Gateway}）。");
    }

    private static string Shorten(string text) => text.Length > 160 ? text[..160] + "…" : text;
    // ================= IP 检测 =================

    /// <summary>下拉里的一条「网卡网段」。</summary>
    private sealed class ScanTargetItem
    {
        public ScanTargetItem(string value, string text, bool preferred)
        {
            Value = value;
            Text = text;
            Preferred = preferred;
        }

        public string Value { get; }
        public string Text { get; }
        public bool Preferred { get; }
        public override string ToString() => Text;
    }

    /// <summary>按系统路由表列出本机的直连网段（也就是各块网卡自己所在的网段）。</summary>
    private List<ScanTargetItem> BuildScanTargets()
    {
        var items = new List<ScanTargetItem>();
        List<RouteRow> table;
        try
        {
            table = RouteTable.Query();
        }
        catch
        {
            return items;
        }

        var best = new Dictionary<string, RouteRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in table)
        {
            if (route.NextHop != 0 || route.PrefixLength is 0 or > 30) continue;
            if ((route.Destination >> 24) == 127) continue;
            if ((route.Destination >> 16) == 0xA9FE) continue;
            var key = $"{Ipv4.ToText(route.Destination)}/{route.PrefixLength}";
            if (best.TryGetValue(key, out var existing) && existing.Metric <= route.Metric) continue;
            best[key] = route;
        }

        foreach (var route in best.Values.OrderBy(item => item.InterfaceIndex).ThenBy(item => item.Destination))
        {
            var nic = _lastNics.FirstOrDefault(item => item.Index == route.InterfaceIndex);
            if (nic is null) continue;
            var value = $"{Ipv4.ToText(route.Destination)}/{route.PrefixLength}";
            items.Add(new ScanTargetItem(value, $"{value}（{nic.Name}）", nic.JoinsExitRace && nic.IsUp));
        }
        return items;
    }

    /// <summary>网卡刷新后同步「可选网段」下拉；已经填好的内容不动。</summary>
    private void RefreshScanTargets()
    {
        if (ScanTargetPicker is null) return;
        var items = BuildScanTargets();
        var signature = string.Join("|", items.Select(item => item.Text));
        if (signature == _scanTargetSignature) return;
        _scanTargetSignature = signature;

        var previous = (ScanTargetPicker.SelectedItem as ScanTargetItem)?.Value;
        _scanTargetApplying = true;
        try
        {
            ScanTargetPicker.Items.Clear();
            foreach (var item in items) ScanTargetPicker.Items.Add(item);
            var restore = items.FirstOrDefault(item => item.Value == previous)
                ?? items.FirstOrDefault(item => item.Preferred)
                ?? items.FirstOrDefault();
            if (restore is not null) ScanTargetPicker.SelectedItem = restore;
        }
        finally
        {
            _scanTargetApplying = false;
        }

        if (ScanTargetText.Text.Trim().Length == 0 && ScanTargetPicker.SelectedItem is ScanTargetItem first)
        {
            ScanTargetText.Text = first.Value;
        }
    }

    private void ScanTargetPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_scanTargetApplying) return;
        if (ScanTargetPicker.SelectedItem is ScanTargetItem item) ScanTargetText.Text = item.Value;
    }

    private async void ScanStart_Click(object sender, RoutedEventArgs e)
    {
        if (_scanning || _busy) return;
        if (!SubnetScan.TryParse(ScanTargetText.Text, out var plan, out var error))
        {
            AppDialog.Warning(this, "无法开始检测", "这个网段或地址看不明白。", error);
            return;
        }

        _scanPlan = plan;
        _scanResults = new List<SubnetHost>();
        _scanRows.Clear();
        ScanEmptyHint.Visibility = Visibility.Collapsed;
        _scanToken = new CancellationTokenSource();
        _scanning = true;
        UpdateButtonStates();
        ScanProgressBar.Value = 0;
        ScanProgressBar.Maximum = Math.Max(1, plan.Addresses.Count);
        ScanSummaryText.Text = $"正在检测 {plan.NetworkText}，共 {plan.Addresses.Count} 个地址…";
        SetStatus($"正在检测 {plan.NetworkText}…");

        var progress = new Progress<int>(done =>
        {
            var current = Math.Min(done, plan.Addresses.Count);
            ScanProgressBar.Value = Math.Min(done, ScanProgressBar.Maximum);
            ScanSummaryText.Text = $"正在检测 {plan.NetworkText}：已完成 {current}/{plan.Addresses.Count}…";
        });

        var watch = Stopwatch.StartNew();
        try
        {
            var hosts = await SubnetScan.ScanAsync(plan, progress, _scanToken.Token);
            watch.Stop();
            _scanResults = hosts;
            ApplyScanFilter();
            var summary = BuildScanSummary(plan, hosts, watch.Elapsed);
            ScanSummaryText.Text = summary;
            SetStatus(summary);
        }
        catch (OperationCanceledException)
        {
            watch.Stop();
            ApplyScanFilter();
            ScanSummaryText.Text = "检测已停止，结果不完整；再点一次「开始检测」可以重来。";
            SetStatus("检测已停止。");
        }
        catch (Exception ex)
        {
            watch.Stop();
            ScanSummaryText.Text = "检测失败。";
            SetStatus("检测失败：" + Shorten(ex.Message));
            AppDialog.Error(this, "检测失败", ex.Message);
        }
        finally
        {
            _scanToken?.Dispose();
            _scanToken = null;
            _scanning = false;
            ScanProgressBar.Value = 0;
            UpdateButtonStates();
        }
    }

    private void ScanStop_Click(object sender, RoutedEventArgs e)
    {
        if (!_scanning) return;
        SetStatus("正在停止检测…");
        _scanToken?.Cancel();
    }

    private static string BuildScanSummary(SubnetScanPlan plan, List<SubnetHost> hosts, TimeSpan elapsed)
    {
        var free = hosts.Count(host => host.State == SubnetHostState.Free);
        var occupied = hosts.Count(host => host.State == SubnetHostState.Occupied);
        var mine = hosts.Count(host => host.State is SubnetHostState.Gateway or SubnetHostState.Self);
        var reserved = hosts.Count(host => host.State == SubnetHostState.Reserved);
        var unknown = hosts.Count(host => host.State == SubnetHostState.Unknown);

        var parts = new List<string>
        {
            $"{plan.NetworkText} 共 {hosts.Count} 个地址",
            $"空闲 {free}",
            $"已占用 {occupied}",
        };
        if (mine > 0) parts.Add($"网关/本机 {mine}");
        if (reserved > 0) parts.Add($"保留 {reserved}");
        if (unknown > 0) parts.Add($"查不到 {unknown}");
        if (plan.InterfaceName.Length > 0) parts.Add($"走「{plan.InterfaceName}」");
        if (!plan.OnLink && unknown > 0) parts.Add("不在本机网段，只能按 Ping 判断");
        parts.Add($"用时 {elapsed.TotalSeconds:0.#} 秒");
        return string.Join(" · ", parts);
    }

    private void ApplyScanFilter()
    {
        if (ScanEmptyHint is null || ScanEmptyTitle is null || ScanEmptyDetail is null) return;
        var filter = ScanFilterCombo.SelectedIndex;
        var source = _scanResults.Where(host => filter switch
        {
            1 => host.State is SubnetHostState.Occupied or SubnetHostState.Gateway or SubnetHostState.Self,
            2 => host.State == SubnetHostState.Free,
            _ => true,
        });
        _scanRows.Clear();
        foreach (var host in source) _scanRows.Add(new ScanRowVm(host));
        ScanEmptyHint.Visibility = _scanRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScanEmptyTitle.Text = _scanResults.Count == 0 ? "还没有检测结果" : "当前筛选下没有符合条件的地址";
        ScanEmptyDetail.Text = _scanResults.Count == 0
            ? "填好网段或单个地址，点左上角的「开始检测」"
            : "把右上角「显示」切回「全部」就能看到全部地址。";
    }

    private void ScanFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyScanFilter();

    private void ScanExport_Click(object sender, RoutedEventArgs e)
    {
        if (_scanResults.Count == 0) return;
        var target = (_scanPlan?.NetworkText ?? "ip").Replace('/', '_').Replace(':', '_');
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出检测结果",
            Filter = "CSV 文件|*.csv",
            DefaultExt = "csv",
            FileName = $"ip-scan-{target}-{DateTime.Now:yyyyMMdd-HHmm}.csv",
        };
        if (dialog.ShowDialog(this) != true) return;
        var lines = new List<string> { "地址,状态,MAC 地址,响应,说明" };
        foreach (var host in _scanResults)
        {
            lines.Add(string.Join(",",
                CsvText(host.AddressText),
                CsvText(ScanRowVm.StateTextOf(host.State)),
                CsvText(host.Mac),
                CsvText(host.RttMs is null ? string.Empty : $"{host.RttMs} ms"),
                CsvText(host.Note)));
        }
        try
        {
            File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            AppDialog.Error(this, "导出失败", ex.Message);
            return;
        }
        SetStatus($"已导出 {_scanResults.Count} 个地址到 {Path.GetFileName(dialog.FileName)}。");
        AppDialog.Info(this, "导出完成", "检测结果已经保存到：", dialog.FileName);
    }

    private static string CsvText(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    // ================= 复制（把表格里的内容拷到剪贴板） =================

    /// <summary>右键点在某一行上：先把这一行选中（多选时整批保留），再弹右键菜单。</summary>
    private void GridMenu_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        var row = RowOf(grid, e.OriginalSource);
        if (ReferenceEquals(grid, ScanGrid)) _scanMenuRow = row?.Item as ScanRowVm;
        if (row is null) return;
        if (!row.IsSelected)
        {
            grid.UnselectAll();
            row.IsSelected = true;
        }
        grid.Focus();
    }

    /// <summary>双击一行 = 把这个地址复制走。</summary>
    private void ScanGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RowOf(ScanGrid, e.OriginalSource)?.Item is not ScanRowVm row) return;
        if (TryCopyToClipboard(row.AddressText)) SetStatus($"已复制地址 {row.AddressText}。");
    }

    /// <summary>要复制哪几行：右键点中的那一行，或者当前选中的一批。</summary>
    private List<ScanRowVm> ScanCopyTargets()
    {
        var selected = ScanGrid.SelectedItems.OfType<ScanRowVm>().ToList();
        if (_scanMenuRow is not null && !selected.Contains(_scanMenuRow)) return new List<ScanRowVm> { _scanMenuRow };
        return selected;
    }

    private void ScanCopyAddress_Click(object sender, RoutedEventArgs e)
    {
        var rows = ScanCopyTargets();
        if (rows.Count == 0)
        {
            SetStatus("先选中一行，或者右键点在某一行上，再复制。");
            return;
        }
        if (!TryCopyToClipboard(CopyText.Lines(rows.Select(row => row.AddressText)))) return;
        SetStatus(rows.Count == 1 ? $"已复制地址 {rows[0].AddressText}。" : $"已复制 {rows.Count} 个地址。");
    }

    private void ScanCopyRow_Click(object sender, RoutedEventArgs e)
    {
        var rows = ScanCopyTargets();
        if (rows.Count == 0)
        {
            SetStatus("先选中一行，或者右键点在某一行上，再复制。");
            return;
        }
        if (!TryCopyToClipboard(CopyText.Lines(rows.Select(row => CopyText.SubnetRow(row.Host))))) return;
        SetStatus(rows.Count == 1
            ? $"已复制 {rows[0].AddressText} 这一行（列之间是 Tab，可以直接粘进 Excel）。"
            : $"已复制 {rows.Count} 行。");
    }

    private void ScanCopyAll_Click(object sender, RoutedEventArgs e)
    {
        var hosts = _scanRows.Select(row => row.Host).ToList();
        if (hosts.Count == 0)
        {
            SetStatus("列表里还没有结果可以复制。");
            return;
        }
        if (!TryCopyToClipboard(CopyText.SubnetTable(hosts))) return;
        SetStatus($"已复制列表里的 {hosts.Count} 个地址（含表头）。");
    }

    private void NicCopyRow_Click(object sender, RoutedEventArgs e)
    {
        if (NicGrid.SelectedItem is not NicRowVm row)
        {
            SetStatus("先在列表里点一下要复制的网卡。");
            return;
        }
        if (!TryCopyToClipboard(CopyText.NicRow(row))) return;
        SetStatus($"已复制「{row.NameText}」的信息（列之间是 Tab）。");
    }

    private void NicCopyAddress_Click(object sender, RoutedEventArgs e)
    {
        if (NicGrid.SelectedItem is not NicRowVm row)
        {
            SetStatus("先在列表里点一下要复制的网卡。");
            return;
        }
        if (!TryCopyToClipboard(row.AddressText)) return;
        SetStatus($"已复制 {row.NameText} 的地址 {row.AddressText}。");
    }

    private void RouteCopyRow_Click(object sender, RoutedEventArgs e)
    {
        if (RouteGrid.SelectedItem is not RouteRowVm row)
        {
            SetStatus("先在列表里点一下要复制的路由。");
            return;
        }
        if (!TryCopyToClipboard(CopyText.RouteRow(row))) return;
        SetStatus($"已复制路由 {row.DestinationText}（列之间是 Tab）。");
    }

    private void RouteCopyTarget_Click(object sender, RoutedEventArgs e)
    {
        if (RouteGrid.SelectedItem is not RouteRowVm row)
        {
            SetStatus("先在列表里点一下要复制的路由。");
            return;
        }
        if (!TryCopyToClipboard(row.DestinationText)) return;
        SetStatus($"已复制网段 {row.DestinationText}。");
    }

    private void SystemRouteCopyRow_Click(object sender, RoutedEventArgs e)
    {
        if (SystemRouteGrid.SelectedItem is not SystemRouteRowVm row)
        {
            SetStatus("先在列表里点一下要复制的行。");
            return;
        }
        if (!TryCopyToClipboard(CopyText.SystemRouteRow(row))) return;
        SetStatus($"已复制 {row.DestinationText} 这一行。");
    }

    /// <summary>点到的是哪一行：从鼠标点到的控件往上找 DataGridRow。</summary>
    private static DataGridRow? RowOf(DataGrid grid, object? source) =>
        source is DependencyObject node ? ItemsControl.ContainerFromElement(grid, node) as DataGridRow : null;

    /// <summary>写剪贴板：别的程序可能正占着剪贴板，失败就等一下再试两次。</summary>
    private bool TryCopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            SetStatus("这一行没有可复制的内容。");
            return false;
        }
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(60);
            }
            catch (Exception ex)
            {
                SetStatus("复制失败：" + Shorten(ex.Message));
                return false;
            }
        }
        SetStatus("复制失败：剪贴板正被别的程序占着，稍后再点一次。");
        return false;
    }

    // ================= 网卡操作 =================

    private async void RaisePriority_Click(object sender, RoutedEventArgs e) => await MovePriorityAsync(-1);

    private async void LowerPriority_Click(object sender, RoutedEventArgs e) => await MovePriorityAsync(1);

    private async Task MovePriorityAsync(int step)
    {
        if (NicGrid.SelectedItem is not NicRowVm row) return;
        var ordered = _nicRows.Where(item => item.Nic.Metric is not null).Select(item => item.Nic).ToList();
        List<(int Index, int Metric)> changes;
        try
        {
            changes = NicPriority.PlanMove(ordered, row.Nic.Index, step);
        }
        catch (InvalidOperationException ex)
        {
            AppDialog.Warning(this, "无法调整优先级", ex.Message);
            return;
        }
        if (changes.Count == 0)
        {
            SetStatus(step < 0 ? "已经是列表中优先级最高的网卡了。" : "已经是列表中优先级最低的网卡了。");
            return;
        }
        var name = row.Nic.Name;
        var ok = await RunBusyAsync($"正在调整「{name}」的优先级…", () =>
        {
            foreach (var (index, metric) in changes) NetworkApi.SetNicMetric(index, metric);
        });
        if (!ok) return;
        await RefreshNicRowsAsync();
        SetStatus($"「{name}」的跃点数已更新，重启后依然有效。");
    }

    private async void AutoMetric_Click(object sender, RoutedEventArgs e)
    {
        if (NicGrid.SelectedItem is not NicRowVm row) return;
        if (row.Nic.Metric is null)
        {
            AppDialog.Warning(this, "无法设置", "该网卡没有可用的 IPv4 配置，请先启用网卡或检查驱动。");
            return;
        }
        if (!row.IsAutomatic)
        {
            var confirmed = AppDialog.Confirm(this, "恢复自动跃点",
                $"把「{row.Nic.Name}」交回系统自动排序？",
                "手动设置的跃点数会被清除，Windows 会按网卡速率和连接情况自动决定优先级。",
                "恢复自动");
            if (!confirmed) return;
        }
        var name = row.Nic.Name;
        var ok = await RunBusyAsync($"正在恢复「{name}」的自动跃点…", () => NetworkApi.ResetNicMetric(row.Nic.Index));
        if (!ok) return;
        await RefreshNicRowsAsync();
        SetStatus($"「{name}」已恢复为自动跃点。");
    }

    private async void DisableNic_Click(object sender, RoutedEventArgs e)
    {
        if (NicGrid.SelectedItem is not NicRowVm row) return;
        var confirmed = AppDialog.Confirm(this, "禁用网卡",
            $"确定要禁用「{row.Nic.Name}」吗？",
            "效果等同于在「网络连接」里右键禁用：会立刻断开这块网卡的连接，之后可以随时再启用。",
            "禁用网卡", danger: true);
        if (!confirmed) return;
        var name = row.Nic.Name;
        var ok = await RunBusyAsync($"正在禁用「{name}」…", () => NetworkApi.SetNicEnabled(row.Nic, false));
        if (!ok) return;
        await Task.Delay(800);
        await RefreshNicRowsAsync();
        SetStatus($"「{name}」已禁用。");
    }

    private async void EnableNic_Click(object sender, RoutedEventArgs e)
    {
        if (NicGrid.SelectedItem is not NicRowVm row) return;
        var name = row.Nic.Name;
        var ok = await RunBusyAsync($"正在启用「{name}」…", () => NetworkApi.SetNicEnabled(row.Nic, true));
        if (!ok) return;
        await Task.Delay(800);
        await RefreshNicRowsAsync();
        SetStatus($"「{name}」已启用。");
    }

    // ================= 多网卡共存 =================

    private async void Coexist_Click(object sender, RoutedEventArgs e)
    {
        var nics = _lastNics;
        if (nics.Count == 0)
        {
            AppDialog.Warning(this, "暂时没有网卡", "还没有读到网卡列表，请先刷新一次。");
            return;
        }
        var exit = NicCoexist.PickExit(nics);
        var steps = NicCoexist.Plan(nics, exit?.Index);
        if (steps.Count == 0)
        {
            AppDialog.Info(this, "多网卡已经共存", "当前状态已经是好的：上网出口只由一块网卡承担，在用网卡也都允许跨网卡收发。",
                "如果某块网卡仍然不通，可以在列表里单独选中它，再点「设为仅内网」或「设为上网出口」微调。");
            return;
        }
        var byIndex = nics.ToDictionary(item => item.Index);
        var headline = exit is null
            ? "把所有在用网卡设为可以共存？"
            : $"保留「{exit.Name}」负责上网，其余网卡改为仅内网？";
        var detail = string.Join(Environment.NewLine, steps.Select(step => $"· {step.Name}：{step.Summary}"))
            + Environment.NewLine + Environment.NewLine
            + "仅内网的网卡依然能访问自己网段里的设备和服务器，只是不再抢上网出口；「跨网卡收发」用来避免多块网卡互相丢包。设置重启后依然有效。";
        if (!AppDialog.Confirm(this, "一键共存", headline, detail, "开始调整")) return;

        var count = steps.Count;
        var ok = await RunBusyAsync("正在调整多网卡共存设置…", () =>
        {
            foreach (var step in steps)
            {
                if (step.IgnoreDefaultRoutes != byIndex[step.Index].IgnoresDefaultRoutes)
                {
                    NetworkApi.SetNicIgnoreDefaultRoutes(step.Index, step.IgnoreDefaultRoutes);
                }
                if (step.EnableWeakHost) NetworkApi.SetNicWeakHost(step.Index, true);
            }
        });
        if (!ok) return;
        await RefreshNicRowsAsync();
        SetStatus($"已调整 {count} 块网卡的共存设置。");
    }

    private async void SetExit_Click(object sender, RoutedEventArgs e)
    {
        if (NicGrid.SelectedItem is not NicRowVm row) return;
        var nic = row.Nic;
        if (nic.ConfiguredGateway.Length == 0 && !nic.HasDefaultRoute)
        {
            AppDialog.Warning(this, "无法设为上网出口", $"「{nic.Name}」现在没有默认网关。",
                "先让这块网卡拿到网关（自动获取 IP，或在「网络连接」里手动填网关），再设成上网出口。");
            return;
        }
        var others = _lastNics
            .Where(item => NicCoexist.ShouldGoInternal(item, nic.Index) && !item.IgnoresDefaultRoutes)
            .ToList();
        var detail = others.Count == 0
            ? "其它网卡本来就没有在抢上网出口，这里只会解除这块网卡的「仅内网」限制。"
            : string.Join(Environment.NewLine, others.Select(item => $"· {item.Name}：改为仅内网"))
                + Environment.NewLine + Environment.NewLine
                + "仅内网的网卡依然能访问自己网段里的设备和服务器，只是不再承担上网。";
        if (!AppDialog.Confirm(this, "设为上网出口", $"以后让「{nic.Name}」负责上网？", detail, "设为上网出口")) return;

        var name = nic.Name;
        var ok = await RunBusyAsync($"正在把「{name}」设为上网出口…", () =>
        {
            NetworkApi.SetNicIgnoreDefaultRoutes(nic.Index, false);
            foreach (var item in others) NetworkApi.SetNicIgnoreDefaultRoutes(item.Index, true);
        });
        if (!ok) return;
        await RefreshNicRowsAsync();
        SetStatus(others.Count == 0 ? $"「{name}」已设为上网出口。" : $"「{name}」已设为上网出口，其它网卡改为仅内网。");
    }

    private async void SetInternal_Click(object sender, RoutedEventArgs e)
    {
        if (NicGrid.SelectedItem is not NicRowVm row) return;
        var nic = row.Nic;
        if (nic.IgnoresDefaultRoutes)
        {
            SetStatus($"「{nic.Name}」已经是仅内网。");
            return;
        }
        if (nic.Metric is null)
        {
            AppDialog.Warning(this, "无法设置", "该网卡没有可用的 IPv4 配置，请先启用网卡或检查驱动。");
            return;
        }
        var lastExit = nic.JoinsExitRace && !_lastNics.Any(item => item.Index != nic.Index && item.JoinsExitRace);
        var detail = lastExit
            ? $"「{nic.Name}」是目前唯一的上网出口，改成仅内网后这台电脑就不能靠它上外网了（各网卡自己网段里的设备照常访问）。"
            : "仅内网的网卡依然能访问自己网段里的设备和服务器，只是不再抢上网出口；之后随时可以用「设为上网出口」改回来。";
        if (!AppDialog.Confirm(this, "设为仅内网", $"不再让「{nic.Name}」抢上网出口？", detail, "设为仅内网", danger: lastExit)) return;

        var name = nic.Name;
        var ok = await RunBusyAsync($"正在把「{name}」设为仅内网…", () => NetworkApi.SetNicIgnoreDefaultRoutes(nic.Index, true));
        if (!ok) return;
        await RefreshNicRowsAsync();
        SetStatus($"「{name}」已设为仅内网（不抢上网出口）。");
    }

    private async void WeakHost_Click(object sender, RoutedEventArgs e)
    {
        if (NicGrid.SelectedItem is not NicRowVm row) return;
        var nic = row.Nic;
        if (nic.Metric is null)
        {
            AppDialog.Warning(this, "无法设置", "该网卡没有可用的 IPv4 配置，请先启用网卡或检查驱动。");
            return;
        }
        var enabled = !(nic.WeakHostSend && nic.WeakHostReceive);
        var headline = enabled ? $"允许「{nic.Name}」跨网卡收发？" : $"禁止「{nic.Name}」跨网卡收发？";
        var detail = enabled
            ? "开启后这块网卡可以收发本机其它网卡上的流量（弱主机模式），多网卡同时工作时不至于互相丢包。设置重启后依然有效。"
            : "关闭后恢复 Windows 默认的严格模式：只有目标地址属于这块网卡的流量才会被它接收。";
        if (!AppDialog.Confirm(this, "跨网卡收发", headline, detail, enabled ? "允许" : "禁止")) return;

        var name = nic.Name;
        var ok = await RunBusyAsync(enabled ? $"正在允许「{name}」跨网卡收发…" : $"正在禁止「{name}」跨网卡收发…",
            () => NetworkApi.SetNicWeakHost(nic.Index, enabled));
        if (!ok) return;
        await RefreshNicRowsAsync();
        SetStatus(enabled ? $"「{name}」已允许跨网卡收发。" : $"「{name}」已恢复严格模式。");
    }

    /// <summary>刷新右侧提示：这类“喊话”现在会从哪块网卡出去、入站会不会被防火墙丢掉。</summary>
    private void RefreshDiscoveryState(bool force = false)
    {
        RenderDiscoveryHint();
        var index = (NicGrid.SelectedItem as NicRowVm)?.Nic.Index ?? _discoveryIndex ?? 0;
        if (!force && _inboundIndex == index && (DateTime.UtcNow - _inboundStamp).TotalSeconds < FirewallCacheSeconds) return;
        _inboundIndex = index;
        _ = RefreshFirewallStatusAsync(index);
    }

    /// <summary>只重画提示文字，不去查系统。</summary>
    private void RenderDiscoveryHint()
    {
        var forced = DiscoveryRoute.Find(0);
        _discoveryIndex = forced?.InterfaceIndex;
        var egress = DiscoveryProbe.Lookup();
        var where = DescribeDiscoveryEgress(egress.Address);
        var forcedName = forced is null
            ? string.Empty
            : _lastNics.FirstOrDefault(item => item.Index == forced.InterfaceIndex)?.Name ?? $"#{forced.InterfaceIndex}";
        var head = forced is null
            ? $"局域网里的“喊话”由系统按跃点数挑网卡，现在走 {where}"
            : egress.InterfaceIndex == forced.InterfaceIndex
                ? $"局域网里的“喊话”已指定走 {where}"
                : $"“喊话”已指定走「{forcedName}」，但系统首选仍是 {where}（喊不动就用「喊一声」）";
        DiscoveryHintText.Text = _inboundStatus is null ? head : $"{head}｜入站防火墙：{_inboundStatus.ShortText()}";
        var tooltip = new List<string> { $"“喊话”出口（发往 {DiscoveryRoute.GroupText}）：{where}" };
        tooltip.Add("系统挑哪块网卡发这类“喊话”，是比网卡的跃点数（数越小越优先）；只给这个组播地址加一条路由改不了它。");
        tooltip.Add("想让对面立刻看到这台机器：用网卡页第四行的「喊一声」或「一直喊」——它们把发送套接字直接绑在选中的网卡上，不看系统挑哪块网卡。");
        tooltip.Add(_inboundStatus is null ? "入站防火墙：正在读取…" : "入站防火墙：" + _inboundStatus.Describe());
        DiscoveryHintText.ToolTip = string.Join(Environment.NewLine, tooltip);
    }

    private async Task RefreshFirewallStatusAsync(int interfaceIndex)
    {
        if (_inboundLoading) return;
        _inboundLoading = true;
        _inboundStamp = DateTime.UtcNow;
        try
        {
            _inboundStatus = await Task.Run(() => DiscoveryFirewall.Query(interfaceIndex));
        }
        catch
        {
            // 读不到就沿用上一次的结果
        }
        finally
        {
            _inboundLoading = false;
            RenderDiscoveryHint();
        }
    }

    private string DescribeDiscoveryEgress(uint egress)
    {
        if (egress == 0) return "测不出来（可能没有网络连接）";
        var nic = _lastNics.FirstOrDefault(item => FirstAddressOf(item.Address) == egress);
        return nic is null ? Ipv4.ToText(egress) : $"{nic.Name}（{Ipv4.ToText(egress)}）";
    }

    private static uint FirstAddressOf(string addresses)
    {
        foreach (var part in addresses.Split(" / ", StringSplitOptions.RemoveEmptyEntries))
        {
            if (Ipv4.TryParse(part, out var address)) return address;
        }
        return 0;
    }

    private static uint AddressOfText(string text) => Ipv4.TryParse(text, out var value) ? value : 0;

    private async void Discovery_Click(object sender, RoutedEventArgs e)
    {
        if (NicGrid.SelectedItem is not NicRowVm row) return;
        var nic = row.Nic;
        if (nic.IsDisabled || FirstAddressOf(nic.Address) == 0)
        {
            AppDialog.Warning(this, "无法设置", "这块网卡现在没有可用的 IPv4 地址。", "先在「网络连接」里启用这块网卡，再来设置。");
            return;
        }

        var forcedHere = _discoveryIndex == nic.Index;
        if (!forcedHere)
        {
            var detail = $"给 LocalSend 这类工具用的组播地址（{DiscoveryRoute.GroupText}）加一条只走「{nic.Name}」的路由，"
                + "让它们“我在这里”的喊话从这块网卡发出去。" + Environment.NewLine + Environment.NewLine
                + $"· 设置后：{nic.Name}（{Ipv4.ToText(FirstAddressOf(nic.Address))}）" + Environment.NewLine
                + "· 只影响局域网发现用的组播，不影响上网，也不影响普通访问" + Environment.NewLine
                + "· 想取消：再选这块网卡点一次这个按钮";
            if (!AppDialog.Confirm(this, "局域网发现", $"让局域网里的“喊话”走「{nic.Name}」？", detail, "设置")) return;
            if (!await RunBusyAsync($"正在把局域网发现的出口设为「{nic.Name}」…", () => DiscoveryRoute.Apply(nic.Index))) return;
        }
        else
        {
            if (!AppDialog.Confirm(this, "局域网发现", "恢复成系统自动？",
                $"会删掉只走「{nic.Name}」的那条组播路由，局域网发现重新交给系统自动挑网卡。", "恢复")) return;
            if (!await RunBusyAsync("正在恢复局域网发现的自动选择…", () => DiscoveryRoute.Remove(nic.Index))) return;
        }

        RefreshDiscoveryState(force: true);
        UpdateButtonStates();

        var egress = DiscoveryProbe.Lookup();
        var expected = FirstAddressOf(nic.Address);
        var matched = egress.InterfaceIndex == nic.Index || (egress.InterfaceIndex == 0 && egress.Address == expected);
        if (!forcedHere && matched)
        {
            SetStatus($"局域网发现已改走「{nic.Name}」（{Ipv4.ToText(egress.Address)}）。");
            AppDialog.Info(this, "设置完成", $"现在这类“喊话”从「{nic.Name}」（{Ipv4.ToText(egress.Address)}）发出。",
                "到对方的 LocalSend 刷新一下，看能不能搜到这台机器。" + Environment.NewLine + Environment.NewLine
                + "如果还是搜不到，多半是那个软件自己指定了网卡：LocalSend 可以到「设置 → 高级 → 网络接口」里把不想用的网卡排除掉。");
        }
        else if (!forcedHere)
        {
            SetStatus($"设置已写入，但系统还是挑 {DescribeDiscoveryEgress(egress.Address)} 发“喊话”。");
            AppDialog.Warning(this, "设置写进去了，但没改到出口", $"系统现在仍然挑 {DescribeDiscoveryEgress(egress.Address)} 发这类“喊话”。",
                "原因：这条路由只管“包送到哪块网卡去”，而系统挑哪块网卡发局域网的“喊话”，是按网卡的跃点数来的（数越小越优先）。"
                + Environment.NewLine + Environment.NewLine
                + "点「修复：让局域网能发现这台机器」，它会连跃点数一起调好，而且每一步都实测给你看。");
        }
        else
        {
            SetStatus("局域网发现已恢复成系统自动选择。");
        }
    }

    /// <summary>「听听看（诊断）」：在发现端口上听一阵子，看谁在喊话、本机自己的喊话是从哪个地址出去的。</summary>
    private async void ListenDiscover_Click(object sender, RoutedEventArgs e)
    {
        const int seconds = 10;
        DiscoveryProbe.WatchResult? heard = null;
        var ok = await RunBusyAsync($"正在听 {seconds} 秒，看局域网里谁在喊话…", () => heard = DiscoveryProbe.Watch(seconds));
        if (!ok || heard is null) return;

        if (!heard.Listening)
        {
            SetStatus("听不了：发现端口被别的程序占着。");
            AppDialog.Warning(this, "听不了", heard.Problem, "关掉占着这个端口的程序再点一次；或者直接点「修复：让局域网能发现这台机器」。");
            return;
        }

        var lines = new List<string>();
        if (heard.Mine.Count == 0)
        {
            lines.Add($"· 本机自己的喊话：这 {seconds} 秒里一条都没听到");
        }
        else
        {
            lines.Add($"· 本机自己的喊话：{heard.Mine.Count} 条（源地址就是本机喊话用的地址）");
            foreach (var shout in heard.Mine.Take(3))
            {
                lines.Add($"    来自 {DescribeDiscoveryEgress(AddressOfText(shout.Source))}，{shout.Bytes} 字节");
            }
        }
        lines.Add(string.Empty);
        if (heard.Others.Count == 0)
        {
            lines.Add("· 别人的喊话：一条都没听到");
        }
        else
        {
            var groups = heard.Others.GroupBy(item => item.Source).OrderByDescending(item => item.Count()).ToList();
            lines.Add($"· 别人的喊话：{heard.Others.Count} 条，来自 {groups.Count} 个地址");
            foreach (var group in groups.Take(8))
            {
                var alias = group.Select(item => item.Alias).FirstOrDefault(text => text.Length > 0);
                lines.Add($"    {DescribeDiscoveryEgress(AddressOfText(group.Key))}，{group.Count()} 条" + (alias is null ? string.Empty : $"，设备名“{alias}”"));
            }
        }

        var mine = heard.Mine.Select(item => AddressOfText(item.Source)).Where(value => value != 0).Distinct().ToList();
        var target = (NicGrid.SelectedItem as NicRowVm)?.Nic;
        lines.Add(string.Empty);
        lines.Add("怎么看");
        if (mine.Count == 0)
        {
            lines.Add("· 没听到自己的喊话：说明发现软件这会儿本来就没在喊（它是隔一阵子喊一次，开机时已经喊过了）。");
            lines.Add("· 听得到别人、听不到自己，多半就是本机的喊话没发出去。");
        }
        else
        {
            lines.Add($"· 本机喊话的出口：{string.Join("、", mine.Select(value => DescribeDiscoveryEgress(value)))}");
            lines.Add("· 这里显示哪块网卡，喊话就是从哪块网卡出去的。");
        }
        if (target is not null && mine.Count > 0 && mine.All(value => value != FirstAddressOf(target.Address)))
        {
            lines.Add($"· 现在走的不是选中的「{target.Name}」：点「修复：让局域网能发现这台机器」，它会想办法把出口改过去。");
        }

        SetStatus($"听完了：别人的喊话 {heard.Others.Count} 条，本机自己的 {heard.Mine.Count} 条。");
        AppDialog.Info(this, "听听看", $"听了 {seconds} 秒：别人的喊话 {heard.Others.Count} 条，本机自己的 {heard.Mine.Count} 条。",
            string.Join(Environment.NewLine, lines));
    }

    private async void FixDiscover_Click(object sender, RoutedEventArgs e)
    {
        if (NicGrid.SelectedItem is not NicRowVm row) return;
        var nic = row.Nic;
        if (nic.IsDisabled || FirstAddressOf(nic.Address) == 0)
        {
            AppDialog.Warning(this, "无法修复", "这块网卡现在没有可用的 IPv4 地址。", "先在「网络连接」里启用这块网卡，再来修复。");
            return;
        }
        if (!NetworkApi.IsElevated)
        {
            AppDialog.Warning(this, "需要管理员权限", "改防火墙、改网络位置都需要管理员权限。", "关掉本程序，右键选择「以管理员身份运行」，再点一次这个按钮。");
            return;
        }

        var status = _inboundStatus ?? DiscoveryFirewall.Query(nic.Index);
        var blocks = status.BlockingRules.Count == 0 ? "当前没有" : $"{status.BlockingRules.Count} 条";
        var detail = new List<string>
        {
            $"· 停掉把别人的发现包丢掉的「阻止」入站规则（{blocks}）",
            $"· 把发现端口 {DiscoveryRoute.Port} 放行到所有网络位置（专用 + 公用，TCP + UDP）",
            $"· 把「{nic.Name}」所在网络的“位置”改成「专用」（现在是「{status.CategoryText}」）",
            $"· 让发现用的“喊话”从「{nic.Name}」（{Ipv4.ToText(FirstAddressOf(nic.Address))}）出去",
            $"· 系统要是还挑别的网卡，就把「{nic.Name}」的跃点数调到最低（随时能用「恢复自动跃点」改回来）",
            $"· 用这台机器上 LocalSend 的身份，从「{nic.Name}」主动喊一声“我在这里”（不用看系统挑哪块网卡）",
        };
        detail.Add(string.Empty);
        detail.Add("做完这些，同一个局域网里的其它机器（比如 10.40.81.200 上的 LocalSend）刷新后就能搜到这台机器了。改的都是入站放行和这块网卡的优先级，不影响上网，也不影响普通访问。");
        if (!AppDialog.Confirm(this, "让局域网能发现这台机器", $"修好之后，别人应该就能搜到「{nic.Name}」上的这台机器。",
                string.Join(Environment.NewLine, detail), "开始修复")) return;

        var firewallSteps = new List<string>();
        var discoverySteps = new List<string>();
        var measured = (DiscoveryProbe.Egress?)null;
        var after = (DiscoveryProbe.Egress?)null;
        var boostNote = string.Empty;
        var blockedNote = string.Empty;
        var shoutNote = string.Empty;
        var ok = await RunBusyAsync("正在修防火墙和发现出口…", () =>
        {
            firewallSteps.AddRange(DiscoveryFirewall.Repair(nic.Index));
            DiscoveryRoute.Apply(nic.Index);

            after = DiscoveryProbe.Lookup();
            if (after.InterfaceIndex == nic.Index)
            {
                discoverySteps.Add($"系统现在会从「{nic.Name}」发这类“喊话”");
            }
            else
            {
                discoverySteps.Add($"系统现在还是挑 {DescribeDiscoveryEgress(after.Address)} 发这类“喊话”");
                var internetBefore = DiscoveryProbe.InternetEgress();
                var expected = FirstAddressOf(nic.Address);
                var change = DiscoveryBoost.Lower(nic.Index, out var target);
                if (change is null)
                {
                    discoverySteps.Add("第二招用不上：读不到这块网卡的跃点数");
                }
                else if (change.Original is int already && already <= target)
                {
                    discoverySteps.Add($"第二招不用改：这块网卡的跃点数已经是最低（{already}）");
                }
                else
                {
                    var internetAfter = DiscoveryProbe.InternetEgress();
                    if (internetBefore != expected && internetAfter == expected)
                    {
                        DiscoveryBoost.Restore(change);
                        blockedNote = $"把「{nic.Name}」的跃点数调到最低，会让上网出口也变成它（现在上网走 {DescribeDiscoveryEgress(internetBefore)}），"
                            + "所以这一招跳过了，跃点数已经帮你还原——不然上网就可能断掉。";
                        discoverySteps.Add("第二招没敢用：会把上网出口也抢过去，已经还原");
                    }
                    else
                    {
                        var originalText = change.Original is int original ? $"从 {original} 降到 {target}" : $"降到 {target}";
                        boostNote = $"把「{nic.Name}」的跃点数{originalText}（原来是{(change.WasAutomatic ? "自动获取" : "手动")}），让系统优先挑它";
                        discoverySteps.Add("第二招：" + boostNote);
                    }
                }

                after = DiscoveryProbe.Lookup();
                discoverySteps.Add(after.InterfaceIndex == nic.Index
                    ? $"复查：现在系统会从「{nic.Name}」（{Ipv4.ToText(after.Address)}）发这类“喊话”"
                    : $"复查：系统还是挑 {DescribeDiscoveryEgress(after.Address)} 发这类“喊话”");
                var rank = DiscoveryBoost.BestMulticastNic(NetworkApi.ListNics());
                discoverySteps.Add(rank.Index == nic.Index
                    ? $"按跃点数排名：现在排第一的正是「{nic.Name}」（组合跃点 {rank.Metric}）"
                    : $"按跃点数排名：排在前面的还是 {DescribeInterface(rank.Index)}（组合跃点 {rank.Metric}）");
            }

            measured = DiscoveryProbe.Measure();
            discoverySteps.Add(measured.Measured
                ? $"实测：真发了 120 个测试包，从 {DescribeDiscoveryEgress(measured.Address)} 出去"
                : $"实测：测试包在这台机器上计数没变化（虚拟网卡或驱动不报），只能按系统自己挑的算：{DescribeDiscoveryEgress(measured.Address)}");

            var identity = LocalSendShout.ReadIdentity();
            if (!identity.Ready)
            {
                shoutNote = "没替它喊话：" + (identity.Problem.Length > 0 ? identity.Problem : "读不到 LocalSend 的身份");
            }
            else
            {
                var shouted = LocalSendShout.Send(identity, nic.Index, Ipv4.ToText(FirstAddressOf(nic.Address)), 3, 400);
                shoutNote = shouted.Sent > 0
                    ? $"已经用「{identity.Alias}」的身份，从「{nic.Name}」（{shouted.From}）主动喊了 {shouted.Sent} 次"
                        + (shouted.PacketDelta >= 0 ? $"（这块网卡的组播计数 +{shouted.PacketDelta}）" : string.Empty)
                        + " —— 对面刷新后应该就能看到这台机器"
                    : $"想替它喊一声，但没成功：{shouted.Problem}";
            }
        });
        if (!ok) return;

        _inboundIndex = nic.Index;
        _inboundStamp = DateTime.UtcNow;
        _inboundStatus = await Task.Run(() => DiscoveryFirewall.Query(nic.Index, withJoins: true));
        RenderDiscoveryHint();
        UpdateButtonStates();

        var lines = new List<string> { "防火墙" };
        if (firewallSteps.Count == 0) lines.Add("· 没有要改的地方，本来就是放行的");
        foreach (var step in firewallSteps) lines.Add("· " + step);
        lines.Add(string.Empty);
        lines.Add($"发现喊话（发往 {DiscoveryRoute.GroupText}）");
        foreach (var step in discoverySteps) lines.Add("· " + step);
        lines.Add(_inboundStatus.ProfileKnown
            ? $"· 网络位置：{_inboundStatus.CategoryText}" + (_inboundStatus.NetworkName.Length > 0 ? $"（网络名：{_inboundStatus.NetworkName}）" : string.Empty)
            : "· 网络位置：没读到（不影响上面的放行设置）");
        if (_inboundStatus.Joined.Count > 0)
        {
            lines.Add(_inboundStatus.Joined.Contains(nic.Index)
                ? $"· 「{nic.Name}」已经在发现组里了，发现软件能在这块网卡上收到喊话"
                : $"· 「{nic.Name}」还没加入发现组：发现软件可能没在这块网卡上监听");
        }
        lines.Add(string.Empty);
        lines.Add("替这台机器喊了一声（不用看系统挑哪块网卡）");
        lines.Add("· " + shoutNote);
        if (blockedNote.Length > 0)
        {
            lines.Add(string.Empty);
            lines.Add("第二招没用上");
            lines.Add("· " + blockedNote);
            lines.Add("· 换个办法：LocalSend 的「设置 → 高级 → 网络接口」里只勾以太网（把 Wi-Fi 排除掉），再把它重启一次。");
        }
        if (boostNote.Length > 0)
        {
            lines.Add(string.Empty);
            lines.Add("改了系统的优先级");
            lines.Add("· " + boostNote);
            lines.Add("· 想还原：在「网卡优先级」页点「恢复自动跃点」。");
        }
        lines.Add(string.Empty);
        lines.Add("接下来");
        lines.Add("· 把 LocalSend 完全关掉再打开（它在启动时才会重新挑网卡），然后到 10.40.81.200 那台机器上刷新看看。");
        lines.Add("· 刚才已经替这台机器喊过一声：对面刷新后应该就能看到；过一会儿又消失了，就点「一直喊（每 15 秒一次）」。");
        lines.Add("· 还是搜不到：LocalSend 的「设置 → 高级 → 网络接口」里只留以太网，把 Wi-Fi 和 VMware 虚拟网卡去掉，再重启一次。");
        lines.Add("· 一直不行：把日志文件 C:\\ProgramData\\SRM\\srm.log 发过来，里面记着每一步的原话。");

        var egressOk = after?.InterfaceIndex == nic.Index || (measured?.Measured == true && measured.InterfaceIndex == nic.Index);
        var good = egressOk && _inboundStatus.BlockingRules.Count == 0 && _inboundStatus.PortAllowed;
        if (good)
        {
            SetStatus($"局域网发现已修好：入站已放行，喊话走「{nic.Name}」。");
            AppDialog.Info(this, "修好了", $"这台机器的入站通道已经打开，喊话也改成从「{nic.Name}」发出，别人应该能搜到它了。", string.Join(Environment.NewLine, lines));
        }
        else
        {
            SetStatus("修复做了一部分，还有没弄好的地方。");
            AppDialog.Warning(this, "修了一部分", "还有没弄好的地方，看下面的说明。", string.Join(Environment.NewLine, lines));
        }
    }

    // ================= 替这台机器喊一声 =================

    /// <summary>现在能不能喊：要选中一块在用、有 IPv4 地址的网卡。</summary>
    private (NicInfo Nic, uint Address)? ShoutTarget()
    {
        if (NicGrid.SelectedItem is not NicRowVm row) return null;
        var nic = row.Nic;
        if (nic.IsDisabled) return null;
        var address = FirstAddressOf(nic.Address);
        if (address == 0) return null;
        return (nic, address);
    }

    private bool TryReadShoutIdentity(out LocalSendShout.Identity identity)
    {
        identity = LocalSendShout.ReadIdentity();
        if (identity.Ready) return true;
        AppDialog.Warning(this, "还没法替它喊话",
            identity.Problem.Length > 0 ? identity.Problem : "读不到 LocalSend 的身份。",
            "先打开一次 LocalSend、随便传一次东西（让它把自己的身份建起来），再回来点这个按钮。");
        return false;
    }

    private async void ShoutDiscover_Click(object sender, RoutedEventArgs e)
    {
        if (ShoutTarget() is not { } target)
        {
            AppDialog.Warning(this, "喊不了", "先在列表里选中一块“在用、有 IPv4 地址”的网卡。",
                "被禁用的网卡、或者还没拿到 IP 的虚拟网卡，喊不出去。");
            return;
        }
        if (!TryReadShoutIdentity(out var identity)) return;

        LocalSendShout.ShoutResult? result = null;
        var ok = await RunBusyAsync($"正在从「{target.Nic.Name}」喊一声…", () =>
        {
            result = LocalSendShout.Send(identity, target.Nic.Index, Ipv4.ToText(target.Address));
        });
        if (!ok || result is null) return;

        var lines = new List<string>
        {
            $"身份：{identity.Alias} —— 从 LocalSend 的设置里读到的设备名，对面列表里看到的就是它",
            $"出口：{result.From}（网卡 #{target.Nic.Index} · {target.Nic.Name}）",
            result.PacketDelta >= 0
                ? $"实测：喊了 {result.Sent} 次，这块网卡的组播包计数 +{result.PacketDelta}、字节 +{result.ByteDelta}：包确实从它出去了"
                : $"已经喊了 {result.Sent} 次（这块网卡读不到发送计数，没法用计数复核）",
        };
        lines.Add(string.Empty);
        lines.Add("接下来");
        lines.Add("· 让对方在那台机器上打开 LocalSend 的「附近设备」：几秒内就该看到这台机器；没看到就点一下对方的刷新，或者重开一次对方的 LocalSend。");
        lines.Add("· 想一直能被搜到：点「一直喊（每 15 秒一次）」，程序开着的时候每 15 秒替你喊一声。");
        lines.Add("· 传文件仍然是 LocalSend 的活：这台机器的 LocalSend 保持开着就行。");

        SetStatus($"已经从「{target.Nic.Name}」（{result.From}）喊了 {result.Sent} 次。");
        AppDialog.Info(this, "喊过了",
            $"已经从「{target.Nic.Name}」喊了 {result.Sent} 次，对方刷新后应该就能看到这台机器。",
            string.Join(Environment.NewLine, lines));
    }

    private async void KeepShout_Click(object sender, RoutedEventArgs e)
    {
        if (_shoutTimer is not null)
        {
            StopShouting("已停止持续喊话。");
            return;
        }
        if (ShoutTarget() is not { } target)
        {
            AppDialog.Warning(this, "喊不了", "先在列表里选中一块“在用、有 IPv4 地址”的网卡。",
                "被禁用的网卡、或者还没拿到 IP 的虚拟网卡，喊不出去。");
            return;
        }
        if (!TryReadShoutIdentity(out _)) return;

        _shoutIndex = target.Nic.Index;
        _shoutAddress = Ipv4.ToText(target.Address);
        _shoutSent = 0;
        _shoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _shoutTimer.Tick += async (_, _) => await ShoutTickAsync();
        _shoutTimer.Start();
        UpdateButtonStates();
        await ShoutTickAsync();
    }

    /// <summary>计时器到点就替这台机器喊一声；喊不动了自动停。</summary>
    private async Task ShoutTickAsync()
    {
        if (_shoutTimer is null) return;

        // 中途换了选中的网卡，就跟着换到新网卡上喊
        if (ShoutTarget() is { } selected && selected.Nic.Index != _shoutIndex)
        {
            _shoutIndex = selected.Nic.Index;
            _shoutAddress = Ipv4.ToText(selected.Address);
        }

        var identity = LocalSendShout.ReadIdentity();
        if (!identity.Ready)
        {
            StopShouting("持续喊话已停止：读不到 LocalSend 的身份。");
            return;
        }

        LocalSendShout.ShoutResult result;
        try
        {
            var target = _shoutAddress;
            var index = _shoutIndex;
            result = await Task.Run(() => LocalSendShout.Send(identity, index, target, count: 1, gapMs: 0));
        }
        catch (Exception ex)
        {
            StopShouting("持续喊话已停止：" + Shorten(ex.Message));
            return;
        }

        if (result.Sent == 0)
        {
            StopShouting("持续喊话已停止：" + (result.Problem.Length > 0 ? Shorten(result.Problem) : "发不出去"));
            return;
        }

        _shoutSent++;
        SetStatus($"持续喊话中：已经喊了 {_shoutSent} 次，最近一次从 {result.From}（网卡 #{result.InterfaceIndex}）发出。");
    }

    private void StopShouting(string message)
    {
        _shoutTimer?.Stop();
        _shoutTimer = null;
        _shoutIndex = 0;
        _shoutAddress = string.Empty;
        SetStatus(message);
        UpdateButtonStates();
    }

    private async void RefreshAll_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAllAsync();
        SetStatus("列表已刷新。");
    }
}
