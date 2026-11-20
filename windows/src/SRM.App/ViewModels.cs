using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using SRM.Core;
using SRM.Core.Localization;

namespace SRM.App;

internal static class Palette
{
    public static readonly Brush Accent = Freeze("#2563EB");
    public static readonly Brush AccentSoft = Freeze("#E8F0FE");
    public static readonly Brush Success = Freeze("#15803D");
    public static readonly Brush SuccessSoft = Freeze("#E7F7EE");
    public static readonly Brush Warning = Freeze("#B45309");
    public static readonly Brush WarningSoft = Freeze("#FDF5E6");
    public static readonly Brush Danger = Freeze("#DC2626");
    public static readonly Brush DangerSoft = Freeze("#FDECEC");
    public static readonly Brush Neutral = Freeze("#5B6472");
    public static readonly Brush NeutralSoft = Freeze("#EDEFF2");

    private static Brush Freeze(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string name = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public sealed class RouteRowVm : Observable
{
    private string _stateText = Loc.T("Ui.Route.State.Pending");
    private string _stateDetail = string.Empty;
    private Brush _stateBrush = Palette.Neutral;
    private Brush _stateBackground = Palette.NeutralSoft;
    private string _testText = string.Empty;
    private string _interfaceText = Loc.T("Ui.Route.InterfaceAuto");

    public RouteRowVm(RouteEntry entry) => Entry = entry;

    public RouteEntry Entry { get; }

    public string DestinationText => Entry.Destination;
    public string GatewayText => Entry.Gateway;
    public string NoteText => string.IsNullOrWhiteSpace(Entry.Note) ? "—" : Entry.Note;

    public string InterfaceText
    {
        get => _interfaceText;
        set => Set(ref _interfaceText, value);
    }

    public string StateText
    {
        get => _stateText;
        private set => Set(ref _stateText, value);
    }

    public string StateDetail
    {
        get => _stateDetail;
        private set => Set(ref _stateDetail, value);
    }

    public Brush StateBrush
    {
        get => _stateBrush;
        private set => Set(ref _stateBrush, value);
    }

    public Brush StateBackground
    {
        get => _stateBackground;
        private set => Set(ref _stateBackground, value);
    }

    public string TestText
    {
        get => _testText;
        set => Set(ref _testText, value);
    }

    /// <summary>运行时标记：该路由还需要写入系统路由表（供自动同步判断）。</summary>
    public bool NeedsSync { get; set; }

    public void ApplyStatus(RouteStatus status)
    {
        switch (status.State)
        {
            case RouteState.Active:
                StateText = Loc.T("Ui.Route.State.Active");
                StateBrush = Palette.Success;
                StateBackground = Palette.SuccessSoft;
                break;
            case RouteState.Disabled:
                StateText = Loc.T("Ui.Route.State.Disabled");
                StateBrush = Palette.Neutral;
                StateBackground = Palette.NeutralSoft;
                break;
            case RouteState.Failed:
                StateText = Loc.T("Ui.Route.State.Failed");
                StateBrush = Palette.Danger;
                StateBackground = Palette.DangerSoft;
                break;
            default:
                StateText = Loc.T("Ui.Route.State.Waiting");
                StateBrush = Palette.Warning;
                StateBackground = Palette.WarningSoft;
                break;
        }
        StateDetail = status.Message;
    }

    public void ApplyEntryEdits()
    {
        Raise(nameof(DestinationText));
        Raise(nameof(GatewayText));
        Raise(nameof(NoteText));
    }
}

/// <summary>「系统里其它路由」列表的一行：系统里存在、但不归 SRM 管的持久路由或网卡静态网关。</summary>
public sealed class SystemRouteRowVm : Observable
{
    public SystemRouteRowVm(SystemRouteEntry entry) => Entry = entry;

    public SystemRouteEntry Entry { get; }

    public string Key => KeyOf(Entry);

    public string DestinationText => Entry.Destination;
    public string GatewayText => Entry.Gateway;
    public string SourceText => Entry.Source;
    public string DetailText => Entry.Detail;

    /// <summary>持久路由可以直接删；网卡属性里的静态网关只能去「网络连接」改，这里只提示。</summary>
    public Visibility DeleteVisibility => Entry.Removable ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HintVisibility => Entry.Removable ? Visibility.Collapsed : Visibility.Visible;

    public Brush SourceBrush => Entry.Removable ? Palette.Warning : Palette.Neutral;
    public Brush SourceBackground => Entry.Removable ? Palette.WarningSoft : Palette.NeutralSoft;

    public static string KeyOf(SystemRouteEntry entry) =>
        $"{entry.Kind}|{entry.Destination}|{entry.Gateway}|{entry.Source}";
}

/// <summary>「IP 检测」列表里的一行：这个地址是空闲还是被占用。</summary>
public sealed class ScanRowVm : Observable
{
    public ScanRowVm(SubnetHost host) => Host = host;

    public SubnetHost Host { get; }

    public string AddressText => Host.AddressText;
    public string MacText => Host.Mac.Length > 0 ? Host.Mac : "—";
    public string RttText => Host.RttMs is null ? "—" : $"{Host.RttMs} ms";
    public string NoteText => Host.Note;

    public bool IsOccupied => Host.State is SubnetHostState.Occupied or SubnetHostState.Gateway or SubnetHostState.Self;
    public bool IsFree => Host.State == SubnetHostState.Free;

    public string StateText => StateTextOf(Host.State);

    public Brush StateBrush => Host.State switch
    {
        SubnetHostState.Free => Palette.Success,
        SubnetHostState.Occupied => Palette.Warning,
        SubnetHostState.Gateway => Palette.Accent,
        SubnetHostState.Self => Palette.Accent,
        _ => Palette.Neutral,
    };

    public Brush StateBackground => Host.State switch
    {
        SubnetHostState.Free => Palette.SuccessSoft,
        SubnetHostState.Occupied => Palette.WarningSoft,
        SubnetHostState.Gateway => Palette.AccentSoft,
        SubnetHostState.Self => Palette.AccentSoft,
        _ => Palette.NeutralSoft,
    };

    public static string StateTextOf(SubnetHostState state) => state switch
    {
        SubnetHostState.Reserved => Loc.T("Ui.Scan.State.Reserved"),
        SubnetHostState.Self => Loc.T("Ui.Scan.State.Self"),
        SubnetHostState.Gateway => Loc.T("Ui.Scan.State.Gateway"),
        SubnetHostState.Occupied => Loc.T("Ui.Scan.State.Occupied"),
        SubnetHostState.Free => Loc.T("Ui.Scan.State.Free"),
        _ => Loc.T("Ui.Scan.State.Unknown"),
    };
}

/// <summary>网卡在多网卡共存里的角色（只用于界面显示）。</summary>
public enum NicExitRole
{
    None,
    Exit,
    Backup,
    Internal,
}

public sealed class NicRowVm : Observable
{
    private int _rank;
    private NicInfo _nic;
    private NicExitRole _exitRole;

    public NicRowVm(NicInfo nic)
    {
        _nic = nic;
        Key = nic.AdapterGuid.Length > 0 ? nic.AdapterGuid : nic.Name;
    }

    public string Key { get; }

    public NicInfo Nic => _nic;

    public int Rank
    {
        get => _rank;
        set
        {
            if (Set(ref _rank, value)) Raise(nameof(RankText));
        }
    }

    public string RankText => _rank > 0 ? _rank.ToString() : "—";
    public string NameText => _nic.Name;
    public string IndexText => _nic.Index > 0 ? _nic.Index.ToString() : "—";
    public string StatusText => _nic.Status;
    public string KindText => Loc.T(_nic.IsVirtual ? "Ui.Nic.Kind.Virtual" : "Ui.Nic.Kind.Physical");
    public string SpeedText => _nic.SpeedText.Length > 0 ? _nic.SpeedText : "—";
    public string AddressText => _nic.Address.Length > 0 ? _nic.Address : "—";
    public string GatewayText
    {
        get
        {
            var text = _nic.ConfiguredGateway.Length > 0 ? _nic.ConfiguredGateway : _nic.Gateway;
            return text.Length > 0 ? text : "—";
        }
    }

    public string MetricText => _nic.MetricText;
    public string DescriptionText => _nic.Description;
    public bool IsDisabled => _nic.IsDisabled;
    public bool HasMetric => _nic.Metric is not null;
    public bool IsAutomatic => _nic.AutomaticMetric == true;
    public bool IsInternal => _nic.IgnoresDefaultRoutes;
    public bool WeakHostOn => _nic.WeakHostSend && _nic.WeakHostReceive;
    public bool HasGateway => _nic.HasGateway;

    public NicExitRole ExitRole
    {
        get => _exitRole;
        set
        {
            if (!Set(ref _exitRole, value)) return;
            Raise(nameof(ExitText));
            Raise(nameof(ExitBrush));
            Raise(nameof(ExitBackground));
        }
    }

    public string ExitText => _exitRole switch
    {
        NicExitRole.Exit => Loc.T("Ui.Nic.Exit.Exit"),
        NicExitRole.Backup => Loc.T("Ui.Nic.Exit.Backup"),
        NicExitRole.Internal => Loc.T("Ui.Nic.Exit.Internal"),
        _ => "—",
    };

    public Brush ExitBrush => _exitRole == NicExitRole.Exit ? Palette.Success
        : _exitRole == NicExitRole.Backup ? Palette.Warning
        : Palette.Neutral;

    public Brush ExitBackground => _exitRole == NicExitRole.Exit ? Palette.SuccessSoft
        : _exitRole == NicExitRole.Backup ? Palette.WarningSoft
        : Palette.NeutralSoft;

    public string WeakHostText => Loc.T(WeakHostOn ? "Ui.Nic.WeakHost.On"
        : _nic.WeakHostSend || _nic.WeakHostReceive ? "Ui.Nic.WeakHost.Partial"
        : "Ui.Nic.WeakHost.Off");
    public Brush WeakHostBrush => WeakHostOn ? Palette.Accent : Palette.Neutral;
    public Brush WeakHostBackground => WeakHostOn ? Palette.AccentSoft : Palette.NeutralSoft;

    public Brush StatusBrush => _nic.Status == Loc.T("Core.Nic.State.Up") ? Palette.Success
        : _nic.Status == Loc.T("Core.Nic.State.Disabled") ? Palette.Danger
        : _nic.Status == Loc.T("Core.Nic.State.NotReady") ? Palette.Warning
        : Palette.Neutral;

    public Brush StatusBackground => _nic.Status == Loc.T("Core.Nic.State.Up") ? Palette.SuccessSoft
        : _nic.Status == Loc.T("Core.Nic.State.Disabled") ? Palette.DangerSoft
        : _nic.Status == Loc.T("Core.Nic.State.NotReady") ? Palette.WarningSoft
        : Palette.NeutralSoft;

    public void Apply(NicInfo nic)
    {
        _nic = nic;
        Raise(string.Empty);
    }
}
