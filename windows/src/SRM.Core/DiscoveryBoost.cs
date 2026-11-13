namespace SRM.Core;

/// <summary>
/// 「喊话走错网卡」的第二招：把选中网卡的跃点数降到最低。
/// 系统在挑组播出口时是按“这块网卡的跃点数 + 组播路由自带的 256”来比的，谁小挑谁；
/// 而给某个组播地址单独加一条路由并不会改这个选择。所以这一招才是真正能改出口的地方。
/// 这块网卡如果已经是「仅内网」，降它的跃点数不会把上网出口抢过来。
/// </summary>
public static class DiscoveryBoost
{
    public sealed record MetricChange(int Index, int? Original, bool WasAutomatic);

    /// <summary>组播出口比的是这个数：224.0.0.0/4 那条路由的跃点 + 网卡自己的跃点。</summary>
    public static long MulticastEgressMetric(int interfaceIndex)
    {
        var metric = NetworkApi.TryGetMetric(interfaceIndex, Native.IpHlpApi.AfInet);
        if (metric is null) return long.MaxValue;
        try
        {
            var row = RouteTable.Query().FirstOrDefault(item =>
                item.Destination == 0xE0000000u && item.PrefixLength == 4 && item.InterfaceIndex == interfaceIndex);
            if (row is null) return long.MaxValue;
            return row.Metric + metric.Value.Metric;
        }
        catch
        {
            return long.MaxValue;
        }
    }

    /// <summary>按跃点数算出来“系统最可能挑哪块网卡发组播”。</summary>
    public static (int Index, long Metric) BestMulticastNic(IEnumerable<NicInfo> nics)
    {
        var bestIndex = 0;
        var bestMetric = long.MaxValue;
        foreach (var nic in nics)
        {
            if (nic.IsDisabled || nic.Metric is null) continue;
            var value = MulticastEgressMetric(nic.Index);
            if (value >= bestMetric) continue;
            bestMetric = value;
            bestIndex = nic.Index;
        }
        return (bestIndex, bestMetric);
    }

    /// <summary>现在默认路由（上网出口）由哪块网卡承担：按“路由跃点 + 网卡跃点”最小的算；读不到返回 0。</summary>
    public static int DefaultRouteWinner()
    {
        try
        {
            var winner = 0;
            long best = long.MaxValue;
            foreach (var row in RouteTable.Query())
            {
                if (row.PrefixLength != 0 || row.NextHop == 0) continue;
                var metric = NetworkApi.TryGetMetric(row.InterfaceIndex, Native.IpHlpApi.AfInet);
                var combined = row.Metric + (metric?.Metric ?? 0);
                if (combined >= best) continue;
                best = combined;
                winner = row.InterfaceIndex;
            }
            return winner;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>把这块网卡的跃点数降到比其它网卡都低；已经是最高优先时原样返回。</summary>
    public static MetricChange? Lower(int interfaceIndex, out int target)
    {
        target = 0;
        var current = NetworkApi.TryGetMetric(interfaceIndex, Native.IpHlpApi.AfInet);
        if (current is null) return null;

        var others = NetworkApi.ListNics()
            .Where(nic => nic.Index != interfaceIndex && !nic.IsDisabled && nic.Metric is not null)
            .Select(nic => nic.Metric!.Value)
            .ToList();
        target = others.Count == 0 ? NicPriority.MinMetric : Math.Max(NicPriority.MinMetric, others.Min() - 1);
        var change = new MetricChange(interfaceIndex, current.Value.Metric, current.Value.Automatic);
        if (current.Value.Metric <= target) return change;
        NetworkApi.SetNicMetric(interfaceIndex, target);
        OperationLog.Write($"为了抢组播出口：把网卡 #{interfaceIndex} 的跃点数从 {current.Value.Metric} 改成 {target}");
        return change;
    }

    /// <summary>还原跃点数（原来若是自动获取，就还原成自动）。</summary>
    public static void Restore(MetricChange change)
    {
        if (change.Original is not int original) return;
        if (change.WasAutomatic)
        {
            NetworkApi.ResetNicMetric(change.Index);
            OperationLog.Write($"还原网卡 #{change.Index} 的跃点数为自动获取");
            return;
        }
        NetworkApi.SetNicMetric(change.Index, original);
        OperationLog.Write($"还原网卡 #{change.Index} 的跃点数为 {original}");
    }
}