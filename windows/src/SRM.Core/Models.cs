using System.Text.Json.Serialization;
using SRM.Core.Localization;

namespace SRM.Core;

public sealed class RouteEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("destination")]
    public string Destination { get; set; } = string.Empty;

    [JsonPropertyName("gateway")]
    public string Gateway { get; set; } = string.Empty;

    [JsonPropertyName("interfaceIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? InterfaceIndex { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("note")]
    public string Note { get; set; } = string.Empty;
}

public sealed class RouteConfig
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("routes")]
    public List<RouteEntry> Routes { get; set; } = new();
}

public enum RouteState
{
    Disabled,
    Active,
    Failed,
    Pending,
}

public sealed class RouteStatus
{
    public RouteState State { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class NicInfo
{
    public int Index { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public bool IsUp { get; init; }
    public bool IsDisabled { get; init; }
    public bool IsVirtual { get; init; }
    public long LinkSpeedBps { get; init; }
    public string Address { get; init; } = string.Empty;
    public string Gateway { get; init; } = string.Empty;
    public int? Metric { get; init; }
    public bool? AutomaticMetric { get; init; }
    public string AdapterGuid { get; init; } = string.Empty;
    public string DeviceId { get; init; } = string.Empty;

    /// <summary>网卡自身配置的默认网关（即使当前未连接也会读出来）。</summary>
    public string ConfiguredGateway { get; init; } = string.Empty;

    /// <summary>当前生效默认路由（0.0.0.0/0）的度量值；为空表示这块网卡目前没有默认路由。</summary>
    public int? DefaultRouteMetric { get; init; }

    /// <summary>是否已设为“仅内网”：忽略该网卡的默认路由，不参与上网出口竞争。</summary>
    public bool IgnoresDefaultRoutes { get; init; }

    public bool WeakHostSend { get; init; }
    public bool WeakHostReceive { get; init; }

    public bool HasDefaultRoute => DefaultRouteMetric is not null;
    public bool JoinsExitRace => HasDefaultRoute && !IgnoresDefaultRoutes;

    /// <summary>这块网卡是否配了默认网关（不管当前通不通）。</summary>
    public bool HasGateway => ConfiguredGateway.Length > 0 || HasDefaultRoute;

    public string SpeedText
    {
        get
        {
            if (LinkSpeedBps >= 1_000_000_000) return $"{LinkSpeedBps / 1_000_000_000.0:0.#} Gbps";
            if (LinkSpeedBps > 0) return $"{LinkSpeedBps / 1_000_000.0:0.#} Mbps";
            return string.Empty;
        }
    }

    public string MetricText => Metric is null ? "—" : Loc.F(AutomaticMetric == true ? "Core.Nic.MetricAuto" : "Core.Nic.MetricManual", Metric);
    public bool CanChangePriority => Metric is not null;
}

/// <summary>一块网卡的多网卡共存设置（IPv4）。</summary>
public sealed record NicSettings(bool IgnoresDefaultRoutes, bool WeakHostSend, bool WeakHostReceive)
{
    /// <summary>“跨网卡收发”是否已经完整开启。</summary>
    public bool WeakHost => WeakHostSend && WeakHostReceive;
}

public sealed class RouteRow
{
    public uint Destination { get; init; }
    public int PrefixLength { get; init; }
    public uint NextHop { get; init; }
    public int InterfaceIndex { get; init; }
    public uint Metric { get; init; }
    public string DestinationText => $"{Ipv4.ToText(Destination)}/{PrefixLength}";
}
