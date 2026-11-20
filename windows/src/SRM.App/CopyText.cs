using SRM.Core;
using SRM.Core.Localization;

namespace SRM.App;

/// <summary>
/// 表格里的一行怎么变成文本：列与列之间用制表符（Tab）隔开，粘到 Excel 里正好一列一格，
/// 粘到聊天窗口、记事本里也能看清；列名和表格上显示的一模一样，方便对照。
/// </summary>
public static class CopyText
{
    public const char Separator = '\t';

    public static string SubnetHeader => Row(
        Loc.T("Ui.Scan.ColAddress"), Loc.T("Ui.Col.Status"), Loc.T("Ui.Scan.ColMac"), Loc.T("Ui.Scan.ColRtt"), Loc.T("Ui.Col.Detail"));

    /// <summary>「IP 检测」里的一行：地址、状态、MAC、响应、说明。</summary>
    public static string SubnetRow(SubnetHost host) => Row(
        host.AddressText,
        ScanRowVm.StateTextOf(host.State),
        host.Mac.Length > 0 ? host.Mac : "—",
        host.RttMs is null ? "—" : $"{host.RttMs} ms",
        host.Note);

    public static string SubnetTable(IEnumerable<SubnetHost> hosts) => Table(SubnetHeader, hosts.Select(SubnetRow));

    public static string NicHeader => Row(
        Loc.T("Ui.Col.Priority"), Loc.T("Ui.Col.Nic"), Loc.T("Ui.Col.Index"), Loc.T("Ui.Col.Status"), Loc.T("Ui.Col.Kind"), Loc.T("Ui.Col.Speed"),
        Loc.T("Ui.Col.Address"), Loc.T("Ui.Col.DefaultGateway"), Loc.T("Ui.Col.Exit"), Loc.T("Ui.Col.Metric"), Loc.T("Ui.Col.Coexist"), Loc.T("Ui.Col.Model"));

    /// <summary>「网卡优先级」里的一行。</summary>
    public static string NicRow(NicRowVm row) => Row(
        row.RankText,
        row.NameText,
        row.IndexText,
        row.StatusText,
        row.KindText,
        row.SpeedText,
        row.AddressText,
        row.GatewayText,
        row.ExitText,
        row.MetricText,
        row.WeakHostText,
        row.DescriptionText);

    public static string RouteHeader => Row(
        Loc.T("Ui.Col.Status"), Loc.T("Ui.Col.Destination"), Loc.T("Ui.Col.Gateway"), Loc.T("Ui.Col.Interface"), Loc.T("Ui.Col.Note"), Loc.T("Ui.Col.Connectivity"));

    /// <summary>「静态路由」里的一行。</summary>
    public static string RouteRow(RouteRowVm row) => Row(
        row.StateText,
        row.DestinationText,
        row.GatewayText,
        row.InterfaceText,
        row.NoteText,
        row.TestText.Length > 0 ? row.TestText : "—");

    public static string SystemRouteHeader => Row(
        Loc.T("Ui.Col.Destination"), Loc.T("Ui.Col.Gateway"), Loc.T("Ui.Col.Source"), Loc.T("Ui.Col.Detail"));

    /// <summary>「系统里其它路由」里的一行。</summary>
    public static string SystemRouteRow(SystemRouteRowVm row) => Row(
        row.DestinationText,
        row.GatewayText,
        row.SourceText,
        row.DetailText);

    /// <summary>一行文本：把各列用制表符连起来。</summary>
    public static string Row(params string[] cells) => string.Join(Separator, cells);

    /// <summary>多行文本：每行一条结果。</summary>
    public static string Lines(IEnumerable<string> lines) => string.Join(Environment.NewLine, lines);

    /// <summary>表头 + 数据行。</summary>
    public static string Table(string header, IEnumerable<string> rows) =>
        Lines(new[] { header }.Concat(rows));
}
