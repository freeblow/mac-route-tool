using SRM.Core;

namespace SRM.App;

/// <summary>
/// 表格里的一行怎么变成文本：列与列之间用制表符（Tab）隔开，粘到 Excel 里正好一列一格，
/// 粘到聊天窗口、记事本里也能看清；列名和表格上显示的一模一样，方便对照。
/// </summary>
public static class CopyText
{
    public const char Separator = '\t';

    public static string SubnetHeader => Row("地址", "状态", "MAC 地址", "响应", "说明");

    /// <summary>「IP 检测」里的一行：地址、状态、MAC、响应、说明。</summary>
    public static string SubnetRow(SubnetHost host) => Row(
        host.AddressText,
        ScanRowVm.StateTextOf(host.State),
        host.Mac.Length > 0 ? host.Mac : "—",
        host.RttMs is null ? "—" : $"{host.RttMs} ms",
        host.Note);

    public static string SubnetTable(IEnumerable<SubnetHost> hosts) => Table(SubnetHeader, hosts.Select(SubnetRow));

    public static string NicHeader => Row("优先级", "网卡", "序号", "状态", "类型", "速率", "IPv4 地址", "默认网关", "上网出口", "跃点数", "共存", "型号");

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

    public static string RouteHeader => Row("状态", "目标网段", "网关", "出口接口", "备注", "连通性");

    /// <summary>「静态路由」里的一行。</summary>
    public static string RouteRow(RouteRowVm row) => Row(
        row.StateText,
        row.DestinationText,
        row.GatewayText,
        row.InterfaceText,
        row.NoteText,
        row.TestText.Length > 0 ? row.TestText : "—");

    public static string SystemRouteHeader => Row("目标网段", "网关", "来源", "说明");

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
