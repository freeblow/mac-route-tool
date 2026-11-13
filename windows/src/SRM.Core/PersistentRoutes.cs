using Microsoft.Win32;

namespace SRM.Core;

/// <summary>
/// 系统里的持久路由。它们其实有两个存放处：老式 route -p add 写在注册表的
/// Tcpip\Parameters\PersistentRoutes 里；PowerShell 的 New-NetRoute -PolicyStore PersistentStore
/// （旧版 SRM.ps1 就是这么加的）写在 NetTCPIP 持久存储里，注册表那一份是空的。
/// 所以这里同时读注册表和 route print 的输出，取并集，才不会漏。
/// </summary>
public static class PersistentRoutes
{
    private const string KeyPath = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\PersistentRoutes";
    private static readonly object Gate = new();
    private static List<string> _cache = new();
    private static DateTime _cacheStamp = DateTime.MinValue;

    /// <summary>返回形如“目标,掩码,网关,跃点”的条目（与注册表里存的格式一致）。结果缓存 4 秒，避免界面刷新时反复启动 route.exe。</summary>
    public static List<string> Query()
    {
        lock (Gate)
        {
            if (DateTime.UtcNow - _cacheStamp < TimeSpan.FromSeconds(4)) return new List<string>(_cache);
            var list = new List<string>();
            Merge(list, QueryRegistry());
            Merge(list, QueryRoutePrint());
            _cache = list;
            _cacheStamp = DateTime.UtcNow;
            return new List<string>(list);
        }
    }

    public static void Invalidate()
    {
        lock (Gate) _cacheStamp = DateTime.MinValue;
    }

    public static bool Contains(uint destination, uint mask, uint nextHop)
    {
        var wanted = $"{Ipv4.ToText(destination)},{Ipv4.ToText(mask)},{Ipv4.ToText(nextHop)},";
        return Query().Any(item => item.StartsWith(wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>删掉注册表里的那一份持久路由记录（另一个存放处由 SystemRoutes.Remove 用 Remove-NetRoute 处理）。</summary>
    public static void Remove(uint destination, uint mask, uint nextHop)
    {
        var wanted = $"{Ipv4.ToText(destination)},{Ipv4.ToText(mask)},{Ipv4.ToText(nextHop)},";
        try
        {
            var items = QueryRegistry();
            var remaining = items.Where(item => !item.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (remaining.Length != items.Count)
            {
                using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
                if (key is not null)
                {
                    if (remaining.Length == 0) key.DeleteValue("PersistentRoutes", throwOnMissingValue: false);
                    else key.SetValue("PersistentRoutes", remaining, RegistryValueKind.MultiString);
                }
            }
        }
        catch
        {
            // 注册表这份删不掉不影响其它方式，交给 route delete / Remove-NetRoute。
        }
        Invalidate();
    }

    private static List<string> QueryRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
            return key?.GetValue("PersistentRoutes") is string[] value
                ? value.Where(item => !string.IsNullOrWhiteSpace(item)).ToList()
                : new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>解析 route print -4 里的持久路由段：正好 4 列、且前三列都是 IPv4 的行。</summary>
    private static List<string> QueryRoutePrint()
    {
        var list = new List<string>();
        try
        {
            var (code, output) = RouteService.RunProcess("route.exe", new[] { "print", "-4" });
            if (code != 0) return list;
            foreach (var raw in output.Split('\n'))
            {
                var tokens = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length != 4) continue;
                if (!Ipv4.TryParse(tokens[0], out _) || !Ipv4.TryParse(tokens[1], out _) || !Ipv4.TryParse(tokens[2], out _)) continue;
                list.Add($"{tokens[0]},{tokens[1]},{tokens[2]},{tokens[3]}");
            }
        }
        catch
        {
            // 解析失败就当作没有，不影响别的功能。
        }
        return list;
    }

    private static void Merge(List<string> target, List<string> items)
    {
        foreach (var item in items)
        {
            if (!target.Contains(item, StringComparer.OrdinalIgnoreCase)) target.Add(item);
        }
    }
}
