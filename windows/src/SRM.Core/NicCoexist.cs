namespace SRM.Core;

/// <summary>一键共存时对某块网卡要做的一步调整。</summary>
public sealed record CoexistStep(int Index, string Name, bool IgnoreDefaultRoutes, bool EnableWeakHost, string Summary);

/// <summary>多网卡共存策略：只留一块网卡当上网出口，其余带网关的网卡改成仅内网，并让在用网卡允许跨网卡收发。</summary>
public static class NicCoexist
{
    /// <summary>选出上网出口：优先当前正承担默认路由、度量最小的网卡。</summary>
    public static NicInfo? PickExit(IReadOnlyList<NicInfo> nics)
    {
        var live = nics.Where(nic => nic.JoinsExitRace && nic.IsUp).ToList();
        if (live.Count > 0)
        {
            return live.OrderBy(nic => nic.DefaultRouteMetric ?? int.MaxValue)
                .ThenBy(nic => nic.Metric ?? int.MaxValue)
                .First();
        }
        var standby = nics
            .Where(nic => nic.ConfiguredGateway.Length > 0 && nic.Metric is not null && !nic.IsDisabled && !nic.IgnoresDefaultRoutes)
            .ToList();
        if (standby.Count > 0)
        {
            return standby.OrderBy(nic => nic.IsVirtual ? 1 : 0)
                .ThenBy(nic => nic.Metric ?? int.MaxValue)
                .First();
        }
        return nics
            .Where(nic => nic.IsUp && nic.Metric is not null && !nic.IsDisabled)
            .OrderBy(nic => nic.IsVirtual ? 1 : 0)
            .ThenBy(nic => nic.Metric ?? int.MaxValue)
            .FirstOrDefault();
    }

    /// <summary>这块网卡是否需要改成“仅内网”：它有自己的网关或默认路由，却不是选定的上网出口，会跟出口抢默认路由。</summary>
    public static bool ShouldGoInternal(NicInfo nic, int? exitIndex) =>
        exitIndex is not null
        && nic.Index != exitIndex
        && !nic.IsDisabled
        && nic.Metric is not null
        && (nic.HasDefaultRoute || (nic.IsUp && nic.IsVirtual && nic.ConfiguredGateway.Length > 0));

    /// <summary>生成共存调整计划：只列出真正需要变化的网卡。</summary>
    public static List<CoexistStep> Plan(IReadOnlyList<NicInfo> nics, int? exitIndex)
    {
        var steps = new List<CoexistStep>();
        foreach (var nic in nics)
        {
            if (nic.IsDisabled || nic.Metric is null) continue;
            var ignore = ShouldGoInternal(nic, exitIndex);
            // 只给真正参与网络的网卡开跨网卡收发：断开且没有网关的网卡（蓝牙、直连虚拟网卡等）不用动。
            var participates = nic.IsUp || nic.HasDefaultRoute || nic.ConfiguredGateway.Length > 0;
            var weakHost = participates && !(nic.WeakHostSend && nic.WeakHostReceive);
            var ignoreChanged = ignore != nic.IgnoresDefaultRoutes;
            if (!ignoreChanged && !weakHost) continue;
            var parts = new List<string>();
            if (ignoreChanged) parts.Add(ignore ? "改为仅内网（不抢上网出口）" : "恢复为上网出口");
            if (weakHost) parts.Add("开启跨网卡收发");
            steps.Add(new CoexistStep(nic.Index, nic.Name, ignore, weakHost, string.Join("，", parts)));
        }
        return steps;
    }
}
