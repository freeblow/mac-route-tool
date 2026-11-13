namespace SRM.Core;

public enum SystemRouteKind
{
    /// <summary>系统里的持久路由：route -p add 或 New-NetRoute -PolicyStore PersistentStore 加的。</summary>
    PersistentRoute,

    /// <summary>网卡属性里静态填的默认网关（SRM 删不掉，要在网卡属性里改）。</summary>
    AdapterStaticGateway,
}

/// <summary>系统里已经存在、但不属于 SRM 配置的路由/网关：用来提醒“这条是谁留下的、要不要清掉”。</summary>
public sealed class SystemRouteEntry
{
    public SystemRouteKind Kind { get; init; }

    /// <summary>规范化后的网段，例如 0.0.0.0/0。</summary>
    public string Destination { get; init; } = string.Empty;

    /// <summary>目标地址（不含掩码），供 route delete 使用。</summary>
    public string Address { get; init; } = string.Empty;

    public string Mask { get; init; } = string.Empty;
    public string Gateway { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;

    /// <summary>能否由 SRM 直接删掉（网卡属性里的静态网关不能）。</summary>
    public bool Removable => Kind == SystemRouteKind.PersistentRoute;
}

public static class SystemRoutes
{
    /// <summary>找出系统里已经存在、但不在 SRM 配置里的持久路由和网卡静态网关。</summary>
    public static List<SystemRouteEntry> Query(IEnumerable<RouteEntry> managed)
    {
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in managed)
        {
            if (Ipv4.TryParseSubnet(route.Destination, out var network, out var mask) && Ipv4.TryParse(route.Gateway, out var gateway))
            {
                owned.Add(Signature(Ipv4.ToText(network), Ipv4.ToText(mask), Ipv4.ToText(gateway)));
            }
        }

        var list = new List<SystemRouteEntry>();
        foreach (var item in PersistentRoutes.Query())
        {
            var parts = item.Split(',');
            if (parts.Length < 3) continue;
            if (!Ipv4.TryParse(parts[0], out var destination) || !Ipv4.TryParse(parts[1], out var mask) || !Ipv4.TryParse(parts[2], out var gateway)) continue;
            var maskText = Ipv4.ToText(mask);
            var gatewayText = Ipv4.ToText(gateway);
            if (owned.Contains(Signature(Ipv4.ToText(destination), maskText, gatewayText))) continue;
            var discovery = destination == DiscoveryRoute.Group && mask == uint.MaxValue;
            list.Add(new SystemRouteEntry
            {
                Kind = SystemRouteKind.PersistentRoute,
                Destination = $"{Ipv4.ToText(destination)}/{Ipv4.PrefixFromMask(mask)}",
                Address = Ipv4.ToText(destination),
                Mask = maskText,
                Gateway = gatewayText,
                Source = discovery ? "局域网发现（SRM 设置）" : "持久路由",
                Detail = discovery
                    ? "SRM 为了让局域网发现（LocalSend 等）从指定网卡发出去而添加；想关掉可以在「网卡优先级」页点「让局域网发现走这块网卡」按钮。"
                    : "由其它程序或旧版本添加，重启后依然存在。确定没用了可以删掉。",
            });
        }

        foreach (var adapter in AdapterStaticConfigs.Query())
        {
            if (adapter.Gateway.Length == 0) continue;
            // 只有当 SRM 自己已经管着“走这个网关的默认路由”时才算重复，别的路由用同一网关不影响这里。
            if (owned.Contains(Signature("0.0.0.0", "0.0.0.0", adapter.Gateway))) continue;
            list.Add(new SystemRouteEntry
            {
                Kind = SystemRouteKind.AdapterStaticGateway,
                Destination = "0.0.0.0/0",
                Address = "0.0.0.0",
                Mask = "0.0.0.0",
                Gateway = adapter.Gateway,
                Source = adapter.Legacy ? $"网卡旧配置残留（{adapter.Name}）"
                    : adapter.EnableDhcp ? $"网卡残留网关（{adapter.Name}）"
                    : $"网卡静态网关（{adapter.Name}）",
                Detail = adapter.Legacy
                    ? $"注册表老位置里还留着 {adapter.Address} / 网关 {adapter.Gateway}，现在系统用的是新位置那份配置，所以暂时不生效；想彻底清掉，在「网络连接」里把这块网卡改成自动获取 IP 再保存一次。"
                    : adapter.EnableDhcp
                        ? $"「{adapter.Name}」现在是自动获取 IP，属性里还留着旧的静态网关 {adapter.Gateway}（静态地址 {adapter.Address}）。"
                        : $"「{adapter.Name}」的 IP 是手动设的静态地址 {adapter.Address}，网关 {adapter.Gateway}；要改请在网卡属性里改。",
            });
        }

        return list
            .OrderBy(item => item.Destination, StringComparer.Ordinal)
            .ThenBy(item => item.Gateway, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>删除一条系统持久路由：路由表、注册表、NetTCPIP 持久存储三处一起清。</summary>
    public static void Remove(SystemRouteEntry entry)
    {
        if (!entry.Removable) throw new InvalidOperationException(entry.Detail);
        try
        {
            RouteService.RunProcess("route.exe", new[] { "delete", entry.Address, "mask", entry.Mask, entry.Gateway });
        }
        catch
        {
            // route.exe 删不掉不影响后面的持久存储清理。
        }
        if (Ipv4.TryParse(entry.Address, out var destination) && Ipv4.TryParse(entry.Mask, out var mask) && Ipv4.TryParse(entry.Gateway, out var gateway))
        {
            PersistentRoutes.Remove(destination, mask, gateway);
        }
        RemoveFromPolicyStore(entry);
        PersistentRoutes.Invalidate();
    }

    private static void RemoveFromPolicyStore(SystemRouteEntry entry)
    {
        var script = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; "
            + $"Remove-NetRoute -DestinationPrefix {entry.Destination} -NextHop {entry.Gateway} -PolicyStore PersistentStore -Confirm:$false -ErrorAction SilentlyContinue";
        try
        {
            RouteService.RunProcess("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script });
        }
        catch
        {
            // 旧版写入的条目可能已经不存在，忽略即可。
        }
    }

    private static string Signature(string destination, string mask, string gateway) => $"{destination},{mask},{gateway}";
}
