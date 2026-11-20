using System.Globalization;
using System.Net;
using System.Net.Sockets;
using SRM.Core.Localization;

namespace SRM.Core;

/// <summary>
/// 让「局域网发现」的喊话从指定的网卡出去。
/// LocalSend 这类工具靠组播向 224.0.0.167 喊“我在这里”；多网卡机器上系统只会挑一块网卡发，
/// 这里给这个组播地址加一条指向指定网卡的主机路由，并在写入前把以前留下的、指向别的网卡
/// （尤其是指向回环的那条）全部清掉——那些残留会让系统选错网卡，甚至让包根本发不出去。
/// </summary>
public static class DiscoveryRoute
{
    /// <summary>LocalSend 使用的组播组。</summary>
    public const string GroupText = "224.0.0.167";
    public const uint Group = 0xE00000A7;
    public const int Port = 53317;

    /// <summary>当前系统会把发往这个组播组的包从哪个本机地址发出去；测不出来返回 0。</summary>
    public static uint ProbeEgressAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(new IPAddress(new byte[] { 224, 0, 0, 167 }), Port));
            if (socket.LocalEndPoint is IPEndPoint endpoint)
            {
                var bytes = endpoint.Address.GetAddressBytes();
                return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
            }
        }
        catch
        {
            // 探测失败就当测不出来
        }
        return 0;
    }

    /// <summary>这条网卡上有没有「局域网发现走它」的设置。</summary>
    public static bool IsForcedTo(int interfaceIndex) => Find(interfaceIndex) is not null;

    public static RouteRow? Find(int interfaceIndex)
    {
        foreach (var row in Query())
        {
            if (row.Destination != Group || row.PrefixLength != 32) continue;
            if (interfaceIndex > 0 && row.InterfaceIndex != interfaceIndex) continue;
            return row;
        }
        return null;
    }

    /// <summary>路由表里所有指向这个组播组的条目（正常情况下只该有一条）。</summary>
    public static List<RouteRow> Query()
    {
        try
        {
            return RouteTable.Query().Where(row => row.Destination == Group && row.PrefixLength == 32).ToList();
        }
        catch
        {
            // 读不到路由表就当作没设置
            return new List<RouteRow>();
        }
    }

    public static void Apply(int interfaceIndex)
    {
        Cleanup(interfaceIndex);
        if (Find(interfaceIndex) is not null)
        {
            OperationLog.Write(Loc.F("Core.Discovery.RouteExistsLog", interfaceIndex));
            return;
        }
        var arguments = new List<string> { "-p", "add", GroupText, "mask", "255.255.255.255", "0.0.0.0" };
        if (interfaceIndex > 0) arguments.AddRange(new[] { "IF", interfaceIndex.ToString(CultureInfo.InvariantCulture) });
        var (code, output) = RouteService.RunProcess("route.exe", arguments);
        PersistentRoutes.Invalidate();
        var text = (output ?? string.Empty).Trim();
        var command = "route " + string.Join(' ', arguments);
        OperationLog.Write(Loc.F("Core.Discovery.RunLog", command, code, text.Length > 0 ? text : Loc.T("Core.Empty")));
        if (code != 0 && !AlreadyThere(text))
        {
            throw new InvalidOperationException(Loc.F("Core.Discovery.CommandFailed", command, code,
                text.Length > 0 ? text : Loc.T("Core.Discovery.NoOutput")));
        }

        var rows = Query();
        OperationLog.Write(rows.Count == 1
            ? Loc.F("Core.Discovery.VerifySingle", rows[0].InterfaceIndex)
            : Loc.F("Core.Discovery.VerifyMultiple", rows.Count,
                string.Join(Loc.T("Core.List.Separator"), rows.Select(row => "#" + row.InterfaceIndex))));
    }

    /// <summary>把设置撤掉：删掉路由表里的条目，并把持久化副本一起清掉。</summary>
    public static void Remove(int interfaceIndex)
    {
        var arguments = new List<string> { "delete", GroupText };
        if (interfaceIndex > 0) arguments.AddRange(new[] { "IF", interfaceIndex.ToString(CultureInfo.InvariantCulture) });
        var (code, output) = RouteService.RunProcess("route.exe", arguments);
        PersistentRoutes.Remove(Group, uint.MaxValue, 0);
        PersistentRoutes.Invalidate();
        OperationLog.Write(Loc.F("Core.Discovery.RunLog", "route " + string.Join(' ', arguments), code, (output ?? string.Empty).Trim()));
    }

    /// <summary>写入前先把以前留下的条目清干净：别的网卡上的、指向回环的、以及各种持久化副本。</summary>
    private static void Cleanup(int keepInterface)
    {
        var stale = Query();
        var others = stale.Where(row => row.InterfaceIndex != keepInterface).ToList();
        if (others.Count == 0) return;

        var before = string.Join(Loc.T("Core.List.Separator"),
            others.Select(row => Loc.F("Core.Discovery.NicMetric", row.InterfaceIndex, row.Metric)));
        // 不带 IF 的 route delete 会把指向这个目标的所有条目一次删掉，连指向回环的残留一起清。
        var (code, output) = RouteService.RunProcess("route.exe", new[] { "delete", GroupText });
        PersistentRoutes.Remove(Group, uint.MaxValue, 0);
        RemoveFromPolicyStore();
        PersistentRoutes.Invalidate();
        OperationLog.Write(Loc.F("Core.Discovery.CleanupLog", others.Count, before, code, (output ?? string.Empty).Trim()));
    }

    /// <summary>旧版 SRM.ps1 用 PowerShell 写过持久路由，那一份存在 NetTCPIP 存储里，得单独清。</summary>
    private static void RemoveFromPolicyStore()
    {
        var script = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; "
            + $"Remove-NetRoute -DestinationPrefix {GroupText}/32 -PolicyStore PersistentStore -Confirm:$false -ErrorAction SilentlyContinue";
        try
        {
            RouteService.RunProcess("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script });
        }
        catch
        {
            // 清理失败不影响主流程
        }
    }

    /// <summary>有些系统对“同一条路由再加一次”会报“已存在”，这不算失败。</summary>
    private static bool AlreadyThere(string text) =>
        text.Contains("already exists", StringComparison.OrdinalIgnoreCase) || text.Contains("已存在", StringComparison.Ordinal);
}
