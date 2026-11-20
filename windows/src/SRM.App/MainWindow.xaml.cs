using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;
using SRM.App.Dialogs;
using SRM.Core;
using SRM.Core.Localization;

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
    /// <summary>上一次检测的耗时（切语言重拼摘要要用）；没跑完或没跑过时为空。</summary>
    private TimeSpan? _scanElapsed;
    /// <summary>检测被停/失败时摘要行显示的那句话的键（成功时为空）。</summary>
    private string? _scanSummaryKey;
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
    private bool _languageApplying;
    private string _statusMessage = Loc.T("Ui.Status.ReadingSystem");
    private DateTime _lastRefresh;

    public MainWindow()
    {
        LocResources.Install();
        InitializeComponent();
        SetupUi();
        Loc.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => Loc.LanguageChanged -= OnLanguageChanged;
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
        Dispatcher.InvokeAsync(() => _ = RefreshAllAsync());
    }

    /// <summary>窗口第一次显示、以及每次重新解析界面之后，都要把代码里设的东西再装回去。</summary>
    private void SetupUi()
    {
        RouteGrid.ItemsSource = _routeRows;
        NicGrid.ItemsSource = _nicRows;
        SystemRouteGrid.ItemsSource = _systemRows;
        ScanGrid.ItemsSource = _scanRows;
        ElevationBanner.Visibility = NetworkApi.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        ElevationBannerText.Text = Loc.T("Ui.ElevationBanner");
        EnvText.Text = Loc.F("Ui.Status.Env", AppInfo.ConfigPath);
        HeaderInfoText.Text = $"v{AppInfo.Version}";
        ApplyLanguage();
    }

    // ================= 语言 =================

    /// <summary>把右上角的语言按钮对齐到当前语言；初始化期间不触发切换。</summary>
    private void ApplyLanguage()
    {
        _languageApplying = true;
        try
        {
            LangZhButton.IsChecked = Loc.Language == Loc.ChineseSimplified;
            LangEnButton.IsChecked = Loc.Language == Loc.English;
        }
        finally
        {
            _languageApplying = false;
        }
        Language = XmlLanguage.GetLanguage(Loc.Culture.IetfLanguageTag);
    }

    private void Language_Checked(object sender, RoutedEventArgs e)
    {
        if (_languageApplying) return;
        if ((sender as FrameworkElement)?.Tag is not string language || language == Loc.Language) return;
        Loc.SetLanguage(language);
    }

    /// <summary>切了语言：列表里的说法是建行的时候定下来的，重铺一遍最省心。</summary>
    private async void OnLanguageChanged()
    {
        SetupUi();
        UpdateButtonStates();
        UpdateStatusBar();
        RefreshRouteRows();
        RefreshRouteStatuses();
        ApplyScanFilter();
        await RefreshAllAsync();
    }

    // ================= 刷新 =================

    private async Task RefreshAllAsync()
    {
        if (_busy) return;
        _statusMessage = Loc.T("Ui.Status.ReadingSystem");
        UpdateStatusBar();
        try
        {
            ReloadConfigIfChanged(force: true);
            RefreshRouteRows();
            RefreshRouteStatuses();
            await RefreshSystemRoutesAsync();
            await RefreshNicRowsAsync();
            _lastRefresh = DateTime.Now;
            _statusMessage = Loc.T("Ui.Status.Ready");
        }
        catch (Exception ex)
        {
            _statusMessage = Loc.F("Ui.Status.RefreshFailed", Shorten(ex.Message));
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
            _statusMessage = Loc.F("Ui.Status.RefreshFailed", ex.Message);
            UpdateStatusBar();
        }
    }

    private async Task AutoSyncAsync()
    {
        if (!NetworkApi.IsElevated) return;
        if (!_routeRows.Any(row => row.NeedsSync)) return;
        await RunBusyAsync(Loc.T("Ui.Route.AutoSyncing"), () => _service.Sync());
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
        RouteSummaryText.Text = entries.Count == 0 ? string.Empty : Loc.F("Ui.Route.Summary", entries.Count, enabled);
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
                row.ApplyStatus(new RouteStatus { State = RouteState.Disabled, Message = Loc.T("Ui.Route.Detail.DisabledByConfig") });
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
                row.ApplyStatus(new RouteStatus { State = RouteState.Failed, Message = Loc.T("Ui.Route.Detail.BadFormat") });
                continue;
            }
            var prefix = Ipv4.PrefixFromMask(mask);
            var active = table.Any(item => item.Destination == network && item.PrefixLength == prefix && item.NextHop == gateway
                                           && (route.InterfaceIndex is not > 0 || item.InterfaceIndex == route.InterfaceIndex));
            var wanted = $"{Ipv4.ToText(network)},{Ipv4.ToText(mask)},{Ipv4.ToText(gateway)},";
            var persisted = persistent.Any(item => item.StartsWith(wanted, StringComparison.OrdinalIgnoreCase));
            row.NeedsSync = !active || !persisted;
            row.ApplyStatus(active
                ? new RouteStatus { State = RouteState.Active, Message = Loc.T(persisted ? "Ui.Route.Detail.Persisted" : "Ui.Route.Detail.NotPersisted") }
                 : new RouteStatus { State = RouteState.Pending, Message = Loc.T("Ui.Route.Detail.NotWritten") });
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
            ? Loc.F("Ui.SysRoute.SummaryAllManual", _systemRows.Count)
            : Loc.F("Ui.SysRoute.Summary", _systemRows.Count, deletable);
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
            var names = string.Join(Loc.T("Core.List.Separator"), racers.Select(row => row.NameText));
            CoexistWarnText.Text = Loc.F("Ui.Coexist.Warning", racers.Count, names);
            CoexistWarnBar.Visibility = Visibility.Visible;
        }
        else
        {
            CoexistWarnBar.Visibility = Visibility.Collapsed;
        }
    }

    private string DescribeInterface(int? index)
    {
        if (index is not int value || value <= 0) return Loc.T("Ui.Route.InterfaceAuto");
        var nic = _lastNics.FirstOrDefault(item => item.Index == value);
        return nic is null ? $"#{value}" : $"{value} · {nic.Name}";
    }

    private void UpdateStatusBar()
    {
        StatusText.Text = _lastRefresh == default
            ? _statusMessage
            : Loc.F("Ui.Status.LastRefresh", _statusMessage, _lastRefresh.ToString("HH:mm:ss"));
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
        WeakHostButton.Content = Loc.T(nic is not null && nic.WeakHostSend && nic.WeakHostReceive ? "Ui.Nic.WeakHostOff" : "Ui.Nic.WeakHostOn");

        AddRouteButton.IsEnabled = idle;
        SyncRouteButton.IsEnabled = idle;
        HeaderRefreshButton.IsEnabled = idle;
        EditRouteButton.IsEnabled = idle && route is not null;
        DeleteRouteButton.IsEnabled = idle && route is not null;
        ToggleRouteButton.IsEnabled = idle && route is not null;
        TestRouteButton.IsEnabled = idle && route is not null;
        ToggleRouteButton.Content = Loc.T(route is not null && route.Entry.Enabled ? "Ui.Route.Disable" : "Ui.Route.Enable");
        ScanStartButton.IsEnabled = idle;
        ScanStopButton.IsEnabled = _scanning;
        ScanExportButton.IsEnabled = !_scanning && _scanResults.Count > 0;
        DiscoveryButton.IsEnabled = idle && nic is not null && !nic.IsDisabled;
        DiscoveryButton.Content = Loc.T(nic is not null && _discoveryIndex == nic.Index ? "Ui.Nic.DiscoveryAuto" : "Ui.Nic.Discovery");
        FixDiscoverButton.IsEnabled = idle && nic is not null && !nic.IsDisabled;
        FixDiscoverButton.Content = Loc.T(_inboundStatus is { NeedsFix: true } ? "Ui.Nic.FixDiscoveryShort" : "Ui.Nic.FixDiscovery");
        ListenDiscoverButton.IsEnabled = idle;
        ShoutDiscoverButton.IsEnabled = idle && nic is not null && !nic.IsDisabled;
        KeepShoutButton.Content = Loc.T(_shoutTimer is null ? "Ui.Nic.KeepShout" : "Ui.Nic.StopShout");
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
            SetStatus(Loc.T("Ui.Status.ActionFailed"));
            AppDialog.Error(this, Loc.T("Ui.Dialog.ActionFailed"), ex.Message);
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
        SetStatus(Loc.F("Ui.Msg.RouteAdded", editor.Result.Destination));
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
        SetStatus(Loc.F("Ui.Msg.RouteSaved", row.Entry.Destination));
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
        SetStatus(Loc.F(row.Entry.Enabled ? "Ui.Msg.RouteEnabled" : "Ui.Msg.RouteDisabled", row.Entry.Destination));
    }

    private async void DeleteRoute_Click(object sender, RoutedEventArgs e)
    {
        if (RouteGrid.SelectedItem is not RouteRowVm row) return;
        var confirmed = AppDialog.Confirm(this, Loc.T("Ui.Msg.RouteDeleteTitle"),
            Loc.F("Ui.Msg.RouteDeleteHeadline", row.Entry.Destination),
            Loc.T(row.Entry.Enabled ? "Ui.Msg.RouteDeleteDetailOn" : "Ui.Msg.RouteDeleteDetailOff"),
            Loc.T("Ui.Delete"), danger: true);
        if (!confirmed) return;
        await RemoveSystemRouteAsync(row.Entry);
        _service.Config.Routes.Remove(row.Entry);
        _service.Errors.Remove(row.Entry.Id);
        SaveConfig();
        RefreshRouteRows();
        RefreshRouteStatuses();
        SetStatus(Loc.F("Ui.Msg.RouteDeleted", row.Entry.Destination));
    }

    private async void SyncRoutes_Click(object sender, RoutedEventArgs e)
    {
        if (_service.Config.Routes.Count == 0)
        {
            SetStatus(Loc.T("Ui.Msg.RouteNoneToSync"));
            return;
        }
        await RunBusyAsync(Loc.T("Ui.Route.AutoSyncing"), () => _service.Sync());
        RefreshRouteStatuses();
        var failed = _service.Config.Routes.Count(item => _service.Errors.ContainsKey(item.Id));
        SetStatus(failed == 0 ? Loc.T("Ui.Msg.RouteSyncDone") : Loc.F("Ui.Msg.RouteSyncFailed", failed));
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
        row.TestText = Loc.T("Ui.Msg.RouteTesting");
        var text = await PingProbe.ProbeAsync(row.Entry);
        row.TestText = text;
    }

    private async Task SyncRouteAsync(RouteEntry entry)
    {
        await RunBusyAsync(Loc.F("Ui.Msg.RouteWriting", entry.Destination), () =>
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
        if (_service.Errors.TryGetValue(entry.Id, out var error)) SetStatus(Loc.F("Ui.Msg.RouteWriteFailedStatus", Shorten(error)));
    }

    private async Task RemoveSystemRouteAsync(RouteEntry entry)
    {
        await RunBusyAsync(Loc.F("Ui.Msg.RouteRemoving", entry.Destination), () =>
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
            AppDialog.Info(this, Loc.T("Ui.Msg.SysRouteManualTitle"), entry.Detail);
            return;
        }
        var detail = Loc.F("Ui.Msg.SysRouteTargetGateway", entry.Destination, entry.Gateway) + Environment.NewLine + Environment.NewLine + entry.Detail;
        if (!AppDialog.Confirm(this, Loc.T("Ui.Msg.SysRouteDeleteTitle"), Loc.T("Ui.Msg.SysRouteDeleteHeadline"), detail, Loc.T("Ui.Delete"), danger: true)) return;
        var ok = await RunBusyAsync(Loc.F("Ui.Msg.RouteRemoving", entry.Destination), () => SystemRoutes.Remove(entry));
        if (!ok) return;
        RefreshRouteStatuses();
        await RefreshSystemRoutesAsync();
        SetStatus(Loc.F("Ui.Msg.SysRouteDeleted", entry.Destination, entry.Gateway));
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
            AppDialog.Warning(this, Loc.T("Ui.Msg.ScanBadTargetTitle"), Loc.T("Ui.Msg.ScanBadTarget"), error);
            return;
        }

        _scanPlan = plan;
        _scanResults = new List<SubnetHost>();
        _scanElapsed = null;
        _scanSummaryKey = null;
        _scanRows.Clear();
        ScanEmptyHint.Visibility = Visibility.Collapsed;
        _scanToken = new CancellationTokenSource();
        _scanning = true;
        UpdateButtonStates();
        ScanProgressBar.Value = 0;
        ScanProgressBar.Maximum = Math.Max(1, plan.Addresses.Count);
        ScanSummaryText.Text = Loc.F("Ui.Msg.ScanRunning", plan.NetworkText, plan.Addresses.Count);
        SetStatus(Loc.F("Ui.Msg.ScanRunningStatus", plan.NetworkText));

        var progress = new Progress<int>(done =>
        {
            var current = Math.Min(done, plan.Addresses.Count);
            ScanProgressBar.Value = Math.Min(done, ScanProgressBar.Maximum);
            ScanSummaryText.Text = Loc.F("Ui.Msg.ScanProgress", plan.NetworkText, current, plan.Addresses.Count);
        });

        var watch = Stopwatch.StartNew();
        try
        {
            var hosts = await SubnetScan.ScanAsync(plan, progress, _scanToken.Token);
            watch.Stop();
            _scanResults = hosts;
            _scanElapsed = watch.Elapsed;
            ApplyScanFilter();
            SetStatus(ScanSummaryText.Text);
        }
        catch (OperationCanceledException)
        {
            watch.Stop();
            _scanSummaryKey = "Ui.Msg.ScanStoppedDetail";
            ApplyScanFilter();
            SetStatus(Loc.T("Ui.Msg.ScanStopped"));
        }
        catch (Exception ex)
        {
            watch.Stop();
            _scanSummaryKey = "Ui.Msg.ScanFailedDetail";
            ApplyScanFilter();
            SetStatus(Loc.F("Ui.Msg.ScanFailedStatus", Shorten(ex.Message)));
            AppDialog.Error(this, Loc.T("Ui.Msg.ScanFailedTitle"), ex.Message);
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
        SetStatus(Loc.T("Ui.Msg.ScanStopping"));
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
            Loc.F("Ui.Msg.ScanSummaryHosts", plan.NetworkText, hosts.Count),
            Loc.F("Ui.Msg.ScanSummaryFree", free),
            Loc.F("Ui.Msg.ScanSummaryOccupied", occupied),
        };
        if (mine > 0) parts.Add(Loc.F("Ui.Msg.ScanSummaryMine", mine));
        if (reserved > 0) parts.Add(Loc.F("Ui.Msg.ScanSummaryReserved", reserved));
        if (unknown > 0) parts.Add(Loc.F("Ui.Msg.ScanSummaryUnknown", unknown));
        if (plan.InterfaceName.Length > 0) parts.Add(Loc.F("Ui.Msg.ScanSummaryVia", plan.InterfaceName));
        if (!plan.OnLink && unknown > 0) parts.Add(Loc.T("Ui.Msg.ScanSummaryPingOnly"));
        parts.Add(Loc.F("Ui.Msg.ScanSummaryElapsed", elapsed.TotalSeconds.ToString("0.#")));
        return string.Join(" · ", parts);
    }

    private void ApplyScanFilter()
    {
        if (ScanEmptyHint is null || ScanEmptyTitle is null || ScanEmptyDetail is null) return;
        RefreshScanNotes();
        RefreshScanSummary();
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
        ScanEmptyTitle.Text = Loc.T(_scanResults.Count == 0 ? "Ui.Scan.EmptyTitle" : "Ui.Msg.ScanEmptyFiltered");
        ScanEmptyDetail.Text = Loc.T(_scanResults.Count == 0 ? "Ui.Scan.EmptyDetail" : "Ui.Msg.ScanEmptyFilteredDetail");
    }

    /// <summary>结果的说明文字是扫描时写进去的；切了语言就按当前语言重算一遍（其它字段不动）。</summary>
    private void RefreshScanNotes()
    {
        if (_scanPlan is null || _scanResults.Count == 0) return;
        for (var i = 0; i < _scanResults.Count; i++)
        {
            var host = _scanResults[i];
            var note = SubnetScan.Describe(_scanPlan, host);
            if (string.Equals(note, host.Note, StringComparison.Ordinal)) continue;
            _scanResults[i] = new SubnetHost { Address = host.Address, State = host.State, Mac = host.Mac, RttMs = host.RttMs, Note = note };
        }
    }

    /// <summary>摘要行也是扫描结束时拼好的；切了语言就按当前语言重拼一遍。</summary>
    private void RefreshScanSummary()
    {
        if (ScanSummaryText is null || _scanPlan is null) return;
        if (_scanSummaryKey is not null)
        {
            ScanSummaryText.Text = Loc.T(_scanSummaryKey);
            return;
        }
        if (_scanElapsed is TimeSpan elapsed && _scanResults.Count > 0)
            ScanSummaryText.Text = BuildScanSummary(_scanPlan, _scanResults, elapsed);
    }

    private void ScanFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyScanFilter();

    private void ScanExport_Click(object sender, RoutedEventArgs e)
    {
        if (_scanResults.Count == 0) return;
        var target = (_scanPlan?.NetworkText ?? "ip").Replace('/', '_').Replace(':', '_');
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("Ui.Msg.ScanExportTitle"),
            Filter = Loc.T("Ui.Msg.ScanExportFilter"),
            DefaultExt = "csv",
            FileName = $"ip-scan-{target}-{DateTime.Now:yyyyMMdd-HHmm}.csv",
        };
        if (dialog.ShowDialog(this) != true) return;
        var lines = new List<string>
        {
            string.Join(",", Loc.T("Ui.Scan.ColAddress"), Loc.T("Ui.Col.Status"), Loc.T("Ui.Scan.ColMac"), Loc.T("Ui.Scan.ColRtt"), Loc.T("Ui.Col.Detail")),
        };
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
            AppDialog.Error(this, Loc.T("Ui.Msg.ScanExportFailedTitle"), ex.Message);
            return;
        }
        SetStatus(Loc.F("Ui.Msg.ScanExported", _scanResults.Count, Path.GetFileName(dialog.FileName)));
        AppDialog.Info(this, Loc.T("Ui.Msg.ScanExportDoneTitle"), Loc.T("Ui.Msg.ScanExportDoneBody"), dialog.FileName);
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
        if (TryCopyToClipboard(row.AddressText)) SetStatus(Loc.F("Ui.Msg.CopyAddressDone", row.AddressText));
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
            SetStatus(Loc.T("Ui.Msg.CopyPickRow"));
            return;
        }
        if (!TryCopyToClipboard(CopyText.Lines(rows.Select(row => row.AddressText)))) return;
        SetStatus(rows.Count == 1 ? Loc.F("Ui.Msg.CopyAddressDone", rows[0].AddressText) : Loc.F("Ui.Msg.CopyAddressesDone", rows.Count));
    }

    private void ScanCopyRow_Click(object sender, RoutedEventArgs e)
    {
        var rows = ScanCopyTargets();
        if (rows.Count == 0)
        {
            SetStatus(Loc.T("Ui.Msg.CopyPickRow"));
            return;
        }
        if (!TryCopyToClipboard(CopyText.Lines(rows.Select(row => CopyText.SubnetRow(row.Host))))) return;
        SetStatus(rows.Count == 1
            ? Loc.F("Ui.Msg.CopyScanRowDone", rows[0].AddressText)
            : Loc.F("Ui.Msg.CopyRowsDone", rows.Count));
    }

    private void ScanCopyAll_Click(object sender, RoutedEventArgs e)
    {
        var hosts = _scanRows.Select(row => row.Host).ToList();
        if (hosts.Count == 0)
        {
            SetStatus(Loc.T("Ui.Msg.CopyNothingInList"));
            return;
        }
        if (!TryCopyToClipboard(CopyText.SubnetTable(hosts))) return;
        SetStatus(Loc.F("Ui.Msg.CopyAllDone", hosts.Count));
    }

    private void NicCopyRow_Click(object sender, RoutedEventArgs e)
    {
        if (NicGrid.SelectedItem is not NicRowVm row)
        {
            SetStatus(Loc.T("Ui.Msg.CopyPickNic"));
            return;
        }
        if (!TryCopyToClipboard(CopyText.NicRow(row))) return;
        SetStatus(Loc.F("Ui.Msg.CopyNicInfoDone", row.NameText));
    }

    private void NicCopyAddress_Click(object sender, RoutedEventArgs e)
    {
        if (NicGrid.SelectedItem is not NicRowVm row)
        {
            SetStatus(Loc.T("Ui.Msg.CopyPickNic"));
            return;
        }
        if (!TryCopyToClipboard(row.AddressText)) return;
        SetStatus(Loc.F("Ui.Msg.CopyNicAddressDone", row.NameText, row.AddressText));
    }

    private void RouteCopyRow_Click(object sender, RoutedEventArgs e)
    {
        if (RouteGrid.SelectedItem is not RouteRowVm row)
        {
            SetStatus(Loc.T("Ui.Msg.CopyPickRoute"));
            return;
        }
        if (!TryCopyToClipboard(CopyText.RouteRow(row))) return;
        SetStatus(Loc.F("Ui.Msg.CopyRouteDone", row.DestinationText));
    }

    private void RouteCopyTarget_Click(object sender, RoutedEventArgs e)
    {
        if (RouteGrid.SelectedItem is not RouteRowVm row)
        {
            SetStatus(Loc.T("Ui.Msg.CopyPickRoute"));
            return;
        }
        if (!TryCopyToClipboard(row.DestinationText)) return;
        SetStatus(Loc.F("Ui.Msg.CopySubnetDone", row.DestinationText));
    }

    private void SystemRouteCopyRow_Click(object sender, RoutedEventArgs e)
    {
        if (SystemRouteGrid.SelectedItem is not SystemRouteRowVm row)
        {
            SetStatus(Loc.T("Ui.Msg.CopyPickSystemRow"));
            return;
        }
        if (!TryCopyToClipboard(CopyText.SystemRouteRow(row))) return;
        SetStatus(Loc.F("Ui.Msg.CopySystemRowDone", row.DestinationText));
    }

    /// <summary>点到的是哪一行：从鼠标点到的控件往上找 DataGridRow。</summary>
    private static DataGridRow? RowOf(DataGrid grid, object? source) =>
        source is DependencyObject node ? ItemsControl.ContainerFromElement(grid, node) as DataGridRow : null;

    /// <summary>写剪贴板：别的程序可能正占着剪贴板，失败就等一下再试两次。</summary>
    private bool TryCopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            SetStatus(Loc.T("Ui.Msg.CopyRowEmpty"));
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
                SetStatus(Loc.F("Ui.Msg.CopyFailed", Shorten(ex.Message)));
                return false;
            }
        }
        SetStatus(Loc.T("Ui.Msg.CopyClipboardBusy"));
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
            AppDialog.Warning(this, Loc.T("Ui.Msg.PriorityFailedTitle"), ex.Message);
            return;
        }
        if (changes.Count == 0)
        {
            SetStatus(Loc.T(step < 0 ? "Ui.Msg.PriorityTop" : "Ui.Msg.PriorityBottom"));
            return;
        }
        var name = row.Nic.Name;
        var ok = await RunBusyAsync(Loc.F("Ui.Msg.PriorityAdjusting", name), () =>
        {
            foreach (var (index, metric) in changes) NetworkApi.SetNicMetric(index, metric);
        });
        if (!ok) return;
        await RefreshNicRowsAsync();
        SetStatus(Loc.F("Ui.Msg.PriorityUpdated", name));
    }

    private async void AutoMetric_Click(object sender, RoutedEventArgs e)
    {
        if (NicGrid.SelectedItem is not NicRowVm row) return;
        if (row.Nic.Metric is null)
        {
            AppDialog.Warning(this, Loc.T("Ui.Msg.NicNoConfigTitle"), Loc.T("Ui.Msg.NicNoConfig"));
            return;
        }
        if (!row.IsAutomatic)
        {
            var confirmed = AppDialog.Confirm(this, Loc.T("Ui.Msg.AutoMetricTitle"),
                Loc.F("Ui.Msg.AutoMetricHeadline", row.Nic.Name),
                Loc.T("Ui.Msg.AutoMetricDetail"),
                Loc.T("Ui.Msg.AutoMetricConfirm"));
            if (!confirmed) return;
        }
        var name = row.Nic.Name;
        var ok = await RunBusyAsync(Loc.F("Ui.Msg.AutoMetricRunning", name), () => NetworkApi.ResetNicMetric(row.Nic.Index));
        if (!ok) return;
        await RefreshNicRowsAsync();
        SetStatus(Loc.F("Ui.Msg.AutoMetricDone", name));
    }

    private async void DisableNic_Click(object sender, RoutedEventArgs e)
    {
        if (NicGrid.SelectedItem is not NicRowVm row) return;
        var confirmed = AppDialog.Confirm(this, Loc.T("Ui.Nic.Disable"),
            Loc.F("Ui.Msg.DisableNicHeadline", row.Nic.Name),
            Loc.T("Ui.Msg.DisableNicDetail"),
            Loc.T("Ui.Nic.Disable"), danger: true);
        if (!confirmed) return;
        var name = row.Nic.Name;
        var ok = await RunBusyAsync(Loc.F("Ui.Msg.DisablingNic", name), () => NetworkApi.SetNicEnabled(row.Nic, false));
        if (!ok) return;
        await Task.Delay(800);
        await RefreshNicRowsAsync();
        SetStatus(Loc.F("Ui.Msg.NicDisabled", name));
    }

    private async void EnableNic_Click(object sender, RoutedEventArgs e)
    {
        if (NicGrid.SelectedItem is not NicRowVm row) return;
        var name = row.Nic.Name;
        var ok = await RunBusyAsync(Loc.F("Ui.Msg.EnablingNic", name), () => NetworkApi.SetNicEnabled(row.Nic, true));
        if (!ok) return;
        await Task.Delay(800);
        await RefreshNicRowsAsync();
        SetStatus(Loc.F("Ui.Msg.NicEnabled", name));
    }

    // ================= 多网卡共存 =================

    private async void Coexist_Click(object sender, RoutedEventArgs e)
    {
        var nics = _lastNics;
        if (nics.Count == 0)
        {
            AppDialog.Warning(this, Loc.T("Ui.Msg.NoNicsTitle"), Loc.T("Ui.Msg.NoNics"));
            return;
        }
        var exit = NicCoexist.PickExit(nics);
        var steps = NicCoexist.Plan(nics, exit?.Index);
        if (steps.Count == 0)
        {
            AppDialog.Info(this, Loc.T("Ui.Msg.CoexistAlreadyTitle"), Loc.T("Ui.Msg.CoexistAlready"),
                Loc.T("Ui.Msg.CoexistAlreadyDetail"));
            return;
        }
        var byIndex = nics.ToDictionary(item => item.Index);
        var headline = exit is null
            ? Loc.T("Ui.Msg.CoexistHeadlineAll")
            : Loc.F("Ui.Msg.CoexistHeadlineKeep", exit.Name);
        var detail = string.Join(Environment.NewLine, steps.Select(step => Loc.F("Ui.Msg.CoexistStep", step.Name, step.Summary)))
            + Environment.NewLine + Environment.NewLine
            + Loc.T("Ui.Msg.CoexistDetail");
        if (!AppDialog.Confirm(this, Loc.T("Ui.Msg.CoexistTitle"), headline, detail, Loc.T("Ui.Msg.CoexistConfirm"))) return;

        var count = steps.Count;
        var ok = await RunBusyAsync(Loc.T("Ui.Msg.CoexistRunning"), () =>
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
        SetStatus(Loc.F("Ui.Msg.CoexistDone", count));
    }

    private async void SetExit_Click(object sender, RoutedEventArgs e)
    {
        if (NicGrid.SelectedItem is not NicRowVm row) return;
        var nic = row.Nic;
        if (nic.ConfiguredGateway.Length == 0 && !nic.HasDefaultRoute)
        {
            AppDialog.Warning(this, Loc.T("Ui.Msg.SetExitNoGatewayTitle"), Loc.F("Ui.Msg.SetExitNoGateway", nic.Name),
                Loc.T("Ui.Msg.SetExitNoGatewayDetail"));
            return;
        }
        var others = _lastNics
            .Where(item => NicCoexist.ShouldGoInternal(item, nic.Index) && !item.IgnoresDefaultRoutes)
            .ToList();
        var detail = others.Count == 0
            ? Loc.T("Ui.Msg.SetExitDetailNone")
            : string.Join(Environment.NewLine, others.Select(item => Loc.F("Ui.Msg.SetExitStep", item.Name)))
                + Environment.NewLine + Environment.NewLine
                + Loc.T("Ui.Msg.SetExitDetail");
        if (!AppDialog.Confirm(this, Loc.T("Ui.Nic.SetExit"), Loc.F("Ui.Msg.SetExitHeadline", nic.Name), detail, Loc.T("Ui.Nic.SetExit"))) return;

        var name = nic.Name;
        var ok = await RunBusyAsync(Loc.F("Ui.Msg.SetExitRunning", name), () =>
        {
            NetworkApi.SetNicIgnoreDefaultRoutes(nic.Index, false);
            foreach (var item in others) NetworkApi.SetNicIgnoreDefaultRoutes(item.Index, true);
        });
        if (!ok) return;
        await RefreshNicRowsAsync();
        SetStatus(others.Count == 0 ? Loc.F("Ui.Msg.SetExitDone", name) : Loc.F("Ui.Msg.SetExitDoneOthers", name));
    }

    private async void SetInternal_Click(object sender, RoutedEventArgs e)
    {
        if (NicGrid.SelectedItem is not NicRowVm row) return;
        var nic = row.Nic;
        if (nic.IgnoresDefaultRoutes)
        {
            SetStatus(Loc.F("Ui.Msg.AlreadyInternal", nic.Name));
            return;
        }
        if (nic.Metric is null)
        {
            AppDialog.Warning(this, Loc.T("Ui.Msg.NicNoConfigTitle"), Loc.T("Ui.Msg.NicNoConfig"));
            return;
        }
        var lastExit = nic.JoinsExitRace && !_lastNics.Any(item => item.Index != nic.Index && item.JoinsExitRace);
        var detail = lastExit
            ? Loc.F("Ui.Msg.SetInternalLastExit", nic.Name)
            : Loc.T("Ui.Msg.SetInternalDetail");
        if (!AppDialog.Confirm(this, Loc.T("Ui.Nic.SetInternal"), Loc.F("Ui.Msg.SetInternalHeadline", nic.Name), detail, Loc.T("Ui.Nic.SetInternal"), danger: lastExit)) return;

        var name = nic.Name;
        var ok = await RunBusyAsync(Loc.F("Ui.Msg.SetInternalRunning", name), () => NetworkApi.SetNicIgnoreDefaultRoutes(nic.Index, true));
        if (!ok) return;
        await RefreshNicRowsAsync();
        SetStatus(Loc.F("Ui.Msg.SetInternalDone", name));
    }

    private async void WeakHost_Click(object sender, RoutedEventArgs e)
    {
        if (NicGrid.SelectedItem is not NicRowVm row) return;
        var nic = row.Nic;
        if (nic.Metric is null)
        {
            AppDialog.Warning(this, Loc.T("Ui.Msg.NicNoConfigTitle"), Loc.T("Ui.Msg.NicNoConfig"));
            return;
        }
        var enabled = !(nic.WeakHostSend && nic.WeakHostReceive);
        var headline = Loc.F(enabled ? "Ui.Msg.WeakHostEnableHeadline" : "Ui.Msg.WeakHostDisableHeadline", nic.Name);
        var detail = Loc.T(enabled ? "Ui.Msg.WeakHostEnableDetail" : "Ui.Msg.WeakHostDisableDetail");
        if (!AppDialog.Confirm(this, Loc.T("Ui.Msg.WeakHostTitle"), headline, detail, Loc.T(enabled ? "Ui.Msg.WeakHostAllow" : "Ui.Msg.WeakHostDeny"))) return;

        var name = nic.Name;
        var ok = await RunBusyAsync(Loc.F(enabled ? "Ui.Msg.WeakHostEnabling" : "Ui.Msg.WeakHostDisabling", name),
            () => NetworkApi.SetNicWeakHost(nic.Index, enabled));
        if (!ok) return;
        await RefreshNicRowsAsync();
        SetStatus(Loc.F(enabled ? "Ui.Msg.WeakHostEnabled" : "Ui.Msg.WeakHostDisabled", name));
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
            ? Loc.F("Ui.Msg.EgressAuto", where)
            : egress.InterfaceIndex == forced.InterfaceIndex
                ? Loc.F("Ui.Msg.EgressForced", where)
                : Loc.F("Ui.Msg.EgressForcedMismatch", forcedName, where);
        DiscoveryHintText.Text = _inboundStatus is null ? head : Loc.F("Ui.Msg.DiscoveryHintWithInbound", head, _inboundStatus.ShortText());
        var tooltip = new List<string> { Loc.F("Ui.Msg.DiscoveryTipEgress", DiscoveryRoute.GroupText, where) };
        tooltip.Add(Loc.T("Ui.Msg.DiscoveryTipMetric"));
        tooltip.Add(Loc.T("Ui.Msg.DiscoveryTipShout"));
        tooltip.Add(_inboundStatus is null ? Loc.T("Ui.Msg.DiscoveryTipInboundLoading") : Loc.F("Ui.Msg.DiscoveryTipInbound", _inboundStatus.Describe()));
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
        if (egress == 0) return Loc.T("Ui.Msg.EgressUnknown");
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
            AppDialog.Warning(this, Loc.T("Ui.Msg.DiscoveryNoAddressTitle"), Loc.T("Ui.Msg.DiscoveryNoAddress"), Loc.T("Ui.Msg.DiscoveryNoAddressDetail"));
            return;
        }

        var forcedHere = _discoveryIndex == nic.Index;
        if (!forcedHere)
        {
            var detail = Loc.F("Ui.Msg.DiscoveryApplyDetail1", DiscoveryRoute.GroupText, nic.Name)
                + Loc.T("Ui.Msg.DiscoveryApplyDetail2") + Environment.NewLine + Environment.NewLine
                + Loc.F("Ui.Msg.DiscoveryApplyDetail3", nic.Name, Ipv4.ToText(FirstAddressOf(nic.Address))) + Environment.NewLine
                + Loc.T("Ui.Msg.DiscoveryApplyDetail4") + Environment.NewLine
                + Loc.T("Ui.Msg.DiscoveryApplyDetail5");
            if (!AppDialog.Confirm(this, Loc.T("Ui.Msg.DiscoveryTitle"), Loc.F("Ui.Msg.DiscoveryApplyHeadline", nic.Name), detail, Loc.T("Ui.Msg.DiscoveryApplyConfirm"))) return;
            if (!await RunBusyAsync(Loc.F("Ui.Msg.DiscoveryApplying", nic.Name), () => DiscoveryRoute.Apply(nic.Index))) return;
        }
        else
        {
            if (!AppDialog.Confirm(this, Loc.T("Ui.Msg.DiscoveryTitle"), Loc.T("Ui.Msg.DiscoveryRestoreHeadline"),
                Loc.F("Ui.Msg.DiscoveryRestoreDetail", nic.Name), Loc.T("Ui.Msg.DiscoveryRestoreConfirm"))) return;
            if (!await RunBusyAsync(Loc.T("Ui.Msg.DiscoveryRestoring"), () => DiscoveryRoute.Remove(nic.Index))) return;
        }

        RefreshDiscoveryState(force: true);
        UpdateButtonStates();

        var egress = DiscoveryProbe.Lookup();
        var expected = FirstAddressOf(nic.Address);
        var matched = egress.InterfaceIndex == nic.Index || (egress.InterfaceIndex == 0 && egress.Address == expected);
        if (!forcedHere && matched)
        {
            SetStatus(Loc.F("Ui.Msg.DiscoveryApplied", nic.Name, Ipv4.ToText(egress.Address)));
            AppDialog.Info(this, Loc.T("Ui.Msg.DiscoveryAppliedTitle"), Loc.F("Ui.Msg.DiscoveryAppliedBody", nic.Name, Ipv4.ToText(egress.Address)),
                Loc.T("Ui.Msg.DiscoveryAppliedHint") + Environment.NewLine + Environment.NewLine
                + Loc.T("Ui.Msg.DiscoveryAppliedHint2"));
        }
        else if (!forcedHere)
        {
            SetStatus(Loc.F("Ui.Msg.DiscoveryMismatch", DescribeDiscoveryEgress(egress.Address)));
            AppDialog.Warning(this, Loc.T("Ui.Msg.DiscoveryMismatchTitle"), Loc.F("Ui.Msg.DiscoveryMismatchBody", DescribeDiscoveryEgress(egress.Address)),
                Loc.T("Ui.Msg.DiscoveryMismatchWhy")
                + Environment.NewLine + Environment.NewLine
                + Loc.T("Ui.Msg.DiscoveryMismatchFix"));
        }
        else
        {
            SetStatus(Loc.T("Ui.Msg.DiscoveryRestored"));
        }
    }

    /// <summary>「听听看（诊断）」：在发现端口上听一阵子，看谁在喊话、本机自己的喊话是从哪个地址出去的。</summary>
    private async void ListenDiscover_Click(object sender, RoutedEventArgs e)
    {
        const int seconds = 10;
        DiscoveryProbe.WatchResult? heard = null;
        var ok = await RunBusyAsync(Loc.F("Ui.Msg.ListenRunning", seconds), () => heard = DiscoveryProbe.Watch(seconds));
        if (!ok || heard is null) return;

        if (!heard.Listening)
        {
            SetStatus(Loc.T("Ui.Msg.ListenPortBusy"));
            AppDialog.Warning(this, Loc.T("Ui.Msg.ListenPortBusyTitle"), heard.Problem, Loc.T("Ui.Msg.ListenPortBusyDetail"));
            return;
        }

        var lines = new List<string>();
        if (heard.Mine.Count == 0)
        {
            lines.Add(Loc.F("Ui.Msg.ListenNoneMine", seconds));
        }
        else
        {
            lines.Add(Loc.F("Ui.Msg.ListenMineCount", heard.Mine.Count));
            foreach (var shout in heard.Mine.Take(3))
            {
                lines.Add(Loc.F("Ui.Msg.ListenMineFrom", DescribeDiscoveryEgress(AddressOfText(shout.Source)), shout.Bytes));
            }
        }
        lines.Add(string.Empty);
        if (heard.Others.Count == 0)
        {
            lines.Add(Loc.T("Ui.Msg.ListenNoneOthers"));
        }
        else
        {
            var groups = heard.Others.GroupBy(item => item.Source).OrderByDescending(item => item.Count()).ToList();
            lines.Add(Loc.F("Ui.Msg.ListenOthersCount", heard.Others.Count, groups.Count));
            foreach (var group in groups.Take(8))
            {
                var alias = group.Select(item => item.Alias).FirstOrDefault(text => text.Length > 0);
                lines.Add(Loc.F("Ui.Msg.ListenOtherItem", DescribeDiscoveryEgress(AddressOfText(group.Key)), group.Count())
                    + (alias is null ? string.Empty : Loc.F("Ui.Msg.ListenOtherAlias", alias)));
            }
        }

        var mine = heard.Mine.Select(item => AddressOfText(item.Source)).Where(value => value != 0).Distinct().ToList();
        var target = (NicGrid.SelectedItem as NicRowVm)?.Nic;
        lines.Add(string.Empty);
        lines.Add(Loc.T("Ui.Msg.ListenHowTo"));
        if (mine.Count == 0)
        {
            lines.Add(Loc.T("Ui.Msg.ListenNoMine1"));
            lines.Add(Loc.T("Ui.Msg.ListenNoMine2"));
        }
        else
        {
            lines.Add(Loc.F("Ui.Msg.ListenMineEgress", string.Join(Loc.T("Core.List.Separator"), mine.Select(value => DescribeDiscoveryEgress(value)))));
            lines.Add(Loc.T("Ui.Msg.ListenMineEgressHint"));
        }
        if (target is not null && mine.Count > 0 && mine.All(value => value != FirstAddressOf(target.Address)))
        {
            lines.Add(Loc.F("Ui.Msg.ListenNotSelected", target.Name));
        }

        SetStatus(Loc.F("Ui.Msg.ListenDone", heard.Others.Count, heard.Mine.Count));
        AppDialog.Info(this, Loc.T("Ui.Msg.ListenTitle"), Loc.F("Ui.Msg.ListenBody", seconds, heard.Others.Count, heard.Mine.Count),
            string.Join(Environment.NewLine, lines));
    }

    private async void FixDiscover_Click(object sender, RoutedEventArgs e)
    {
        if (NicGrid.SelectedItem is not NicRowVm row) return;
        var nic = row.Nic;
        if (nic.IsDisabled || FirstAddressOf(nic.Address) == 0)
        {
            AppDialog.Warning(this, Loc.T("Ui.Msg.FixNoAddressTitle"), Loc.T("Ui.Msg.DiscoveryNoAddress"), Loc.T("Ui.Msg.FixNoAddressDetail"));
            return;
        }
        if (!NetworkApi.IsElevated)
        {
            AppDialog.Warning(this, Loc.T("Ui.Msg.FixNeedsAdminTitle"), Loc.T("Ui.Msg.FixNeedsAdmin"), Loc.T("Ui.Msg.FixNeedsAdminDetail"));
            return;
        }

        var status = _inboundStatus ?? DiscoveryFirewall.Query(nic.Index);
        var blocks = status.BlockingRules.Count == 0 ? Loc.T("Ui.Msg.FixBlocksNone") : Loc.F("Ui.Msg.FixBlocksCount", status.BlockingRules.Count);
        var detail = new List<string>
        {
            Loc.F("Ui.Msg.FixStepBlocks", blocks),
            Loc.F("Ui.Msg.FixStepAllow", DiscoveryRoute.Port),
            Loc.F("Ui.Msg.FixStepProfile", nic.Name, status.CategoryText),
            Loc.F("Ui.Msg.FixStepEgress", nic.Name, Ipv4.ToText(FirstAddressOf(nic.Address))),
            Loc.F("Ui.Msg.FixStepMetric", nic.Name),
            Loc.F("Ui.Msg.FixStepShout", nic.Name),
        };
        detail.Add(string.Empty);
        detail.Add(Loc.T("Ui.Msg.FixDetailFooter"));
        if (!AppDialog.Confirm(this, Loc.T("Ui.Msg.FixTitle"), Loc.F("Ui.Msg.FixHeadline", nic.Name),
                string.Join(Environment.NewLine, detail), Loc.T("Ui.Msg.FixConfirm"))) return;

        var firewallSteps = new List<string>();
        var discoverySteps = new List<string>();
        var measured = (DiscoveryProbe.Egress?)null;
        var after = (DiscoveryProbe.Egress?)null;
        var boostNote = string.Empty;
        var blockedNote = string.Empty;
        var shoutNote = string.Empty;
        var ok = await RunBusyAsync(Loc.T("Ui.Msg.FixRunning"), () =>
        {
            firewallSteps.AddRange(DiscoveryFirewall.Repair(nic.Index));
            DiscoveryRoute.Apply(nic.Index);

            after = DiscoveryProbe.Lookup();
            if (after.InterfaceIndex == nic.Index)
            {
                discoverySteps.Add(Loc.F("Ui.Msg.FixEgressOk", nic.Name));
            }
            else
            {
                discoverySteps.Add(Loc.F("Ui.Msg.FixEgressStill", DescribeDiscoveryEgress(after.Address)));
                var internetBefore = DiscoveryProbe.InternetEgress();
                var expected = FirstAddressOf(nic.Address);
                var change = DiscoveryBoost.Lower(nic.Index, out var target);
                if (change is null)
                {
                    discoverySteps.Add(Loc.T("Ui.Msg.FixNoMetric"));
                }
                else if (change.Original is int already && already <= target)
                {
                    discoverySteps.Add(Loc.F("Ui.Msg.FixMetricLowest", already));
                }
                else
                {
                    var internetAfter = DiscoveryProbe.InternetEgress();
                    if (internetBefore != expected && internetAfter == expected)
                    {
                        DiscoveryBoost.Restore(change);
                        blockedNote = Loc.F("Ui.Msg.FixStealNote", nic.Name, DescribeDiscoveryEgress(internetBefore))
                            + Loc.T("Ui.Msg.FixStealNote2");
                        discoverySteps.Add(Loc.T("Ui.Msg.FixStealStep"));
                    }
                    else
                    {
                        var originalText = change.Original is int original ? Loc.F("Ui.Msg.FixBoostFrom", original, target) : Loc.F("Ui.Msg.FixBoostTo", target);
                        boostNote = Loc.F("Ui.Msg.FixBoostNote", nic.Name, originalText, Loc.T(change.WasAutomatic ? "Ui.Msg.FixBoostAuto" : "Ui.Msg.FixBoostManual"));
                        discoverySteps.Add(Loc.F("Ui.Msg.FixBoostStep", boostNote));
                    }
                }

                after = DiscoveryProbe.Lookup();
                discoverySteps.Add(after.InterfaceIndex == nic.Index
                    ? Loc.F("Ui.Msg.FixRecheckOk", nic.Name, Ipv4.ToText(after.Address))
                    : Loc.F("Ui.Msg.FixRecheckStill", DescribeDiscoveryEgress(after.Address)));
                var rank = DiscoveryBoost.BestMulticastNic(NetworkApi.ListNics());
                discoverySteps.Add(rank.Index == nic.Index
                    ? Loc.F("Ui.Msg.FixRankFirst", nic.Name, rank.Metric)
                    : Loc.F("Ui.Msg.FixRankStill", DescribeInterface(rank.Index), rank.Metric));
            }

            measured = DiscoveryProbe.Measure();
            discoverySteps.Add(measured.Measured
                ? Loc.F("Ui.Msg.FixMeasured", DescribeDiscoveryEgress(measured.Address))
                : Loc.F("Ui.Msg.FixMeasureFallback", DescribeDiscoveryEgress(measured.Address)));

            var identity = LocalSendShout.ReadIdentity();
            if (!identity.Ready)
            {
                shoutNote = Loc.F("Ui.Msg.FixNoShout", identity.Problem.Length > 0 ? identity.Problem : Loc.T("Ui.Msg.FixNoIdentity"));
            }
            else
            {
                var shouted = LocalSendShout.Send(identity, nic.Index, Ipv4.ToText(FirstAddressOf(nic.Address)), 3, 400);
                shoutNote = shouted.Sent > 0
                    ? Loc.F("Ui.Msg.FixShouted", identity.Alias, nic.Name, shouted.From, shouted.Sent)
                        + (shouted.PacketDelta >= 0 ? Loc.F("Ui.Msg.FixShoutedDelta", shouted.PacketDelta) : string.Empty)
                        + Loc.T("Ui.Msg.FixShoutedTail")
                    : Loc.F("Ui.Msg.FixShoutFailed", shouted.Problem);
            }
        });
        if (!ok) return;

        _inboundIndex = nic.Index;
        _inboundStamp = DateTime.UtcNow;
        _inboundStatus = await Task.Run(() => DiscoveryFirewall.Query(nic.Index, withJoins: true));
        RenderDiscoveryHint();
        UpdateButtonStates();

        var lines = new List<string> { Loc.T("Ui.Msg.FixSectionFirewall") };
        if (firewallSteps.Count == 0) lines.Add(Loc.T("Ui.Msg.FixFirewallNothing"));
        foreach (var step in firewallSteps) lines.Add("· " + step);
        lines.Add(string.Empty);
        lines.Add(Loc.F("Ui.Msg.FixSectionDiscovery", DiscoveryRoute.GroupText));
        foreach (var step in discoverySteps) lines.Add("· " + step);
        lines.Add(_inboundStatus.ProfileKnown
            ? Loc.F("Ui.Msg.FixProfile", _inboundStatus.CategoryText) + (_inboundStatus.NetworkName.Length > 0 ? Loc.F("Ui.Msg.FixProfileName", _inboundStatus.NetworkName) : string.Empty)
            : Loc.T("Ui.Msg.FixProfileUnknown"));
        if (_inboundStatus.Joined.Count > 0)
        {
            lines.Add(_inboundStatus.Joined.Contains(nic.Index)
                ? Loc.F("Ui.Msg.FixJoined", nic.Name)
                : Loc.F("Ui.Msg.FixNotJoined", nic.Name));
        }
        lines.Add(string.Empty);
        lines.Add(Loc.T("Ui.Msg.FixSectionShout"));
        lines.Add("· " + shoutNote);
        if (blockedNote.Length > 0)
        {
            lines.Add(string.Empty);
            lines.Add(Loc.T("Ui.Msg.FixSectionSkipped"));
            lines.Add("· " + blockedNote);
            lines.Add(Loc.T("Ui.Msg.FixSkippedAlt"));
        }
        if (boostNote.Length > 0)
        {
            lines.Add(string.Empty);
            lines.Add(Loc.T("Ui.Msg.FixSectionBoost"));
            lines.Add("· " + boostNote);
            lines.Add(Loc.T("Ui.Msg.FixBoostRevert"));
        }
        lines.Add(string.Empty);
        lines.Add(Loc.T("Ui.Msg.FixSectionNext"));
        lines.Add(Loc.T("Ui.Msg.FixNext1"));
        lines.Add(Loc.T("Ui.Msg.FixNext2"));
        lines.Add(Loc.T("Ui.Msg.FixNext3"));
        lines.Add(Loc.T("Ui.Msg.FixNext4"));

        var egressOk = after?.InterfaceIndex == nic.Index || (measured?.Measured == true && measured.InterfaceIndex == nic.Index);
        var good = egressOk && _inboundStatus.BlockingRules.Count == 0 && _inboundStatus.PortAllowed;
        if (good)
        {
            SetStatus(Loc.F("Ui.Msg.FixDone", nic.Name));
            AppDialog.Info(this, Loc.T("Ui.Msg.FixDoneTitle"), Loc.F("Ui.Msg.FixDoneBody", nic.Name), string.Join(Environment.NewLine, lines));
        }
        else
        {
            SetStatus(Loc.T("Ui.Msg.FixPartial"));
            AppDialog.Warning(this, Loc.T("Ui.Msg.FixPartialTitle"), Loc.T("Ui.Msg.FixPartialBody"), string.Join(Environment.NewLine, lines));
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
        AppDialog.Warning(this, Loc.T("Ui.Msg.ShoutNoIdentityTitle"),
            identity.Problem.Length > 0 ? identity.Problem : Loc.T("Ui.Msg.FixNoIdentity"),
            Loc.T("Ui.Msg.ShoutNoIdentity"));
        return false;
    }

    private async void ShoutDiscover_Click(object sender, RoutedEventArgs e)
    {
        if (ShoutTarget() is not { } target)
        {
            AppDialog.Warning(this, Loc.T("Ui.Msg.ShoutNoTargetTitle"), Loc.T("Ui.Msg.ShoutNoTarget"),
                Loc.T("Ui.Msg.ShoutNoTargetDetail"));
            return;
        }
        if (!TryReadShoutIdentity(out var identity)) return;

        LocalSendShout.ShoutResult? result = null;
        var ok = await RunBusyAsync(Loc.F("Ui.Msg.ShoutSending", target.Nic.Name), () =>
        {
            result = LocalSendShout.Send(identity, target.Nic.Index, Ipv4.ToText(target.Address));
        });
        if (!ok || result is null) return;

        var lines = new List<string>
        {
            Loc.F("Ui.Msg.ShoutIdentityLine", identity.Alias),
            Loc.F("Ui.Msg.ShoutEgressLine", result.From, target.Nic.Index, target.Nic.Name),
            result.PacketDelta >= 0
                ? Loc.F("Ui.Msg.ShoutMeasured", result.Sent, result.PacketDelta, result.ByteDelta)
                : Loc.F("Ui.Msg.ShoutSentNoCounter", result.Sent),
        };
        lines.Add(string.Empty);
        lines.Add(Loc.T("Ui.Msg.FixSectionNext"));
        lines.Add(Loc.T("Ui.Msg.ShoutNext1"));
        lines.Add(Loc.T("Ui.Msg.ShoutNext2"));
        lines.Add(Loc.T("Ui.Msg.ShoutNext3"));

        SetStatus(Loc.F("Ui.Msg.ShoutDone", target.Nic.Name, result.From, result.Sent));
        AppDialog.Info(this, Loc.T("Ui.Msg.ShoutDoneTitle"),
            Loc.F("Ui.Msg.ShoutDoneBody", target.Nic.Name, result.Sent),
            string.Join(Environment.NewLine, lines));
    }

    private async void KeepShout_Click(object sender, RoutedEventArgs e)
    {
        if (_shoutTimer is not null)
        {
            StopShouting(Loc.T("Ui.Msg.ShoutStopped"));
            return;
        }
        if (ShoutTarget() is not { } target)
        {
            AppDialog.Warning(this, Loc.T("Ui.Msg.ShoutNoTargetTitle"), Loc.T("Ui.Msg.ShoutNoTarget"),
                Loc.T("Ui.Msg.ShoutNoTargetDetail"));
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
            StopShouting(Loc.T("Ui.Msg.ShoutStoppedNoIdentity"));
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
            StopShouting(Loc.F("Ui.Msg.ShoutStoppedError", Shorten(ex.Message)));
            return;
        }

        if (result.Sent == 0)
        {
            StopShouting(Loc.F("Ui.Msg.ShoutStoppedError", result.Problem.Length > 0 ? Shorten(result.Problem) : Loc.T("Ui.Msg.ShoutStoppedSend")));
            return;
        }

        _shoutSent++;
        SetStatus(Loc.F("Ui.Msg.ShoutTicking", _shoutSent, result.From, result.InterfaceIndex));
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
        SetStatus(Loc.T("Ui.Msg.ListRefreshed"));
    }
}
