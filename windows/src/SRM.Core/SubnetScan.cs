using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using SRM.Core.Native;

namespace SRM.Core;

/// <summary>地址检测里每个地址的状态。</summary>
public enum SubnetHostState
{
    /// <summary>网络地址或广播地址，不能分给设备。</summary>
    Reserved,

    /// <summary>本机自己。</summary>
    Self,

    /// <summary>这块网卡的默认网关。</summary>
    Gateway,

    /// <summary>已被占用（ARP 有响应，或者 Ping 有回应）。</summary>
    Occupied,

    /// <summary>空闲：同网段的设备一定会有 ARP 响应，没响应就是没人用。</summary>
    Free,

    /// <summary>不在本机网段，Ping 又不通：查不出到底有没有人用。</summary>
    Unknown,
}

public sealed class SubnetHost
{
    public uint Address { get; init; }
    public SubnetHostState State { get; init; }
    public string Mac { get; init; } = string.Empty;
    public int? RttMs { get; init; }
    public string Note { get; init; } = string.Empty;

    public string AddressText => Ipv4.ToText(Address);
}

/// <summary>一次检测的计划：查哪些地址、从哪块网卡发出去。</summary>
public sealed class SubnetScanPlan
{
    public List<uint> Addresses { get; init; } = new();
    public uint NetworkAddress { get; init; }
    public uint BroadcastAddress { get; init; }
    public int Prefix { get; init; }
    public uint SourceAddress { get; init; }
    public uint Gateway { get; init; }
    public string InterfaceName { get; init; } = string.Empty;
    public int InterfaceIndex { get; init; }
    public bool OnLink { get; init; }
    public string TargetText { get; init; } = string.Empty;

    public string NetworkText => Prefix > 0 ? $"{Ipv4.ToText(NetworkAddress)}/{Prefix}" : TargetText;
}

/// <summary>网段地址检测：判断某个地址是空闲还是已经被别的设备占用。</summary>
public static class SubnetScan
{
    /// <summary>一次最多检测多少个地址，防止手滑填了 0.0.0.0/0。</summary>
    public const int MaxAddresses = 2048;

    public const int DefaultConcurrency = 64;

    /// <summary>把用户填的内容解析成检测计划。支持 192.168.15.0/24、192.168.15.66、192.168.15.1-200 三种写法。</summary>
    public static bool TryParse(string? text, out SubnetScanPlan plan, out string error)
    {
        plan = new SubnetScanPlan();
        error = string.Empty;
        var value = (text ?? string.Empty).Trim().Replace('–', '-').Replace('－', '-').Replace('—', '-');
        if (value.Length == 0)
        {
            error = "请填写要检测的网段或地址，例如 192.168.15.0/24、192.168.15.66、192.168.15.1-200。";
            return false;
        }

        uint first;
        uint last;
        var network = 0u;
        var broadcast = 0u;
        var prefix = 0;

        var dash = value.IndexOf('-');
        if (dash > 0)
        {
            var left = value[..dash].Trim();
            var right = value[(dash + 1)..].Trim();
            if (!Ipv4.TryParse(left, out first))
            {
                error = "区间起点不是合法的 IPv4 地址，例如 192.168.15.1-200。";
                return false;
            }
            if (right.Contains('.'))
            {
                if (!Ipv4.TryParse(right, out last))
                {
                    error = "区间终点不是合法的 IPv4 地址。";
                    return false;
                }
            }
            else
            {
                if (!byte.TryParse(right, out var tail))
                {
                    error = "区间写法应是 192.168.15.1-192.168.15.200 或 192.168.15.1-200。";
                    return false;
                }
                last = (first & 0xFFFFFF00) | tail;
            }
            if (last < first) (first, last) = (last, first);
        }
        else if (value.Contains('/'))
        {
            if (!Ipv4.TryParseSubnet(value, out network, out var mask))
            {
                error = "网段写法应是 192.168.15.0/24（掩码 0-32）。";
                return false;
            }
            prefix = Ipv4.PrefixFromMask(mask);
            if (prefix > 30)
            {
                error = "/31、/32 里没有可分配的地址，直接填单个地址（例如 192.168.15.66）就行。";
                return false;
            }
            first = network + 1;
            broadcast = network | ~mask;
            last = broadcast - 1;
        }
        else
        {
            if (!Ipv4.TryParse(value, out first))
            {
                error = "地址写法应是 192.168.15.66 或 192.168.15.0/24。";
                return false;
            }
            last = first;
        }

        var count = (int)(last - first + 1);
        if (count > MaxAddresses)
        {
            error = $"一次最多检测 {MaxAddresses} 个地址，这个范围太大了，请把网段收小一点。";
            return false;
        }

        var addresses = new List<uint>(count + 2);
        if (prefix > 0) addresses.Add(network);
        for (var index = 0; index < count; index++) addresses.Add(first + (uint)index);
        if (prefix > 0) addresses.Add(broadcast);

        var located = Locate(prefix > 0 ? network : first);
        plan = new SubnetScanPlan
        {
            Addresses = addresses,
            NetworkAddress = network,
            BroadcastAddress = broadcast,
            Prefix = prefix,
            TargetText = value,
            SourceAddress = located.SourceAddress,
            Gateway = located.Gateway,
            InterfaceName = located.InterfaceName,
            InterfaceIndex = located.InterfaceIndex,
            OnLink = located.OnLink,
        };
        return true;
    }
    /// <summary>看这个地址走哪块网卡出去：找到覆盖它的直连路由，取出网卡地址和网关。</summary>
    private static (bool OnLink, uint SourceAddress, uint Gateway, string InterfaceName, int InterfaceIndex) Locate(uint probe)
    {
        try
        {
            RouteRow? best = null;
            foreach (var row in RouteTable.Query())
            {
                if (row.NextHop != 0) continue;
                var mask = Ipv4.MaskFromPrefix(row.PrefixLength);
                if ((probe & mask) != (row.Destination & mask)) continue;
                if (best is null || row.PrefixLength > best.PrefixLength
                    || (row.PrefixLength == best.PrefixLength && row.Metric < best.Metric)) best = row;
            }
            if (best is null) return (false, 0, 0, string.Empty, 0);

            var adapter = Adapters.Query().FirstOrDefault(item => item.Index == best.InterfaceIndex);
            if (adapter is null) return (false, 0, 0, string.Empty, 0);
            var source = 0u;
            foreach (var address in adapter.Addresses)
            {
                if (Ipv4.TryParse(address, out source)) break;
            }
            if (source == 0) return (false, 0, 0, string.Empty, 0);
            var gateway = 0u;
            if (adapter.Gateways.Count > 0) Ipv4.TryParse(adapter.Gateways[0], out gateway);
            return (true, source, gateway, adapter.Name, adapter.Index);
        }
        catch
        {
            return (false, 0, 0, string.Empty, 0);
        }
    }

    /// <summary>扫描这些地址：同网段用 ARP 判定（快，而且不响应 Ping 的设备也查得出来），跨网段只能靠 Ping。</summary>
    public static async Task<List<SubnetHost>> ScanAsync(SubnetScanPlan plan, IProgress<int>? progress = null,
        CancellationToken token = default, int concurrency = DefaultConcurrency)
    {
        var results = new SubnetHost?[plan.Addresses.Count];
        var pending = new List<int>();
        var done = 0;

        for (var index = 0; index < plan.Addresses.Count; index++)
        {
            var address = plan.Addresses[index];
            // 本机自己先判：扫的那一段的广播地址正好是本机地址时（比如本机是 /24 的 .63、
            // 却按 /28 去扫 .48 那一段），它仍然是本机在用的地址，不能说成“广播地址”。
            if (address == plan.SourceAddress)
            {
                results[index] = new SubnetHost
                {
                    Address = address,
                    State = SubnetHostState.Self,
                    Note = plan.InterfaceName.Length > 0 ? $"本机（{plan.InterfaceName}）" : "本机",
                };
            }
            else if (plan.Prefix > 0 && address == plan.NetworkAddress)
            {
                results[index] = new SubnetHost { Address = address, State = SubnetHostState.Reserved, Note = "网络地址，不能分给设备。" };
            }
            else if (plan.Prefix > 0 && address == plan.BroadcastAddress)
            {
                results[index] = new SubnetHost { Address = address, State = SubnetHostState.Reserved, Note = "广播地址，不能分给设备。" };
            }
            else
            {
                pending.Add(index);
            }
        }
        done += plan.Addresses.Count - pending.Count;
        progress?.Report(done);

        var found = new Dictionary<uint, string>();
        var rtts = new Dictionary<uint, int>();
        if (pending.Count > 0)
        {
            var targets = pending.Select(index => plan.Addresses[index]).ToList();
            if (plan.OnLink && plan.SourceAddress != 0)
            {
                found = await ProbeArpAsync(plan, targets, progress, done, token).ConfigureAwait(false);
                done += targets.Count;
                progress?.Report(done);

                var alive = targets.Where(address => found.ContainsKey(address)).ToList();
                if (plan.Gateway != 0 && !alive.Contains(plan.Gateway)) alive.Add(plan.Gateway);
                rtts = await PingManyAsync(alive, 800, concurrency, token).ConfigureAwait(false);
            }
            else
            {
                rtts = await PingManyAsync(targets, 900, concurrency, token).ConfigureAwait(false);
                done += targets.Count;
                progress?.Report(done);
            }
        }

        var list = new List<SubnetHost>(plan.Addresses.Count);
        for (var index = 0; index < plan.Addresses.Count; index++)
        {
            if (results[index] is { } ready)
            {
                list.Add(ready);
                continue;
            }

            var address = plan.Addresses[index];
            var mac = found.TryGetValue(address, out var macText) ? macText : string.Empty;
            var hasRtt = rtts.TryGetValue(address, out var rtt);
            var occupied = plan.OnLink && plan.SourceAddress != 0 ? mac.Length > 0 : hasRtt;

            if (plan.Gateway != 0 && address == plan.Gateway)
            {
                list.Add(new SubnetHost
                {
                    Address = address,
                    State = SubnetHostState.Gateway,
                    Mac = mac,
                    RttMs = hasRtt ? rtt : null,
                    Note = hasRtt ? "网关" : "网关（不响应 Ping）",
                });
                continue;
            }
            if (occupied)
            {
                list.Add(new SubnetHost
                {
                    Address = address,
                    State = SubnetHostState.Occupied,
                    Mac = mac,
                    RttMs = hasRtt ? rtt : null,
                    Note = mac.Length > 0
                        ? hasRtt ? "已被占用" : "已被占用（不响应 Ping，可能是防火墙）"
                        : "已被占用（这个网段不在本机网卡上，只能按 Ping 判断）",
                });
                continue;
            }
            list.Add(new SubnetHost
            {
                Address = address,
                State = plan.OnLink ? SubnetHostState.Free : SubnetHostState.Unknown,
                Note = plan.OnLink ? "空闲，没人在用。" : "不在本机网段，Ping 不通，无法确定。",
            });
        }
        return list;
    }

    /// <summary>同网段探测：先往这些地址发个 UDP，逼系统自己发 ARP 请求，然后读 ARP 表；两轮还没出现的就是空闲。</summary>
    private static async Task<Dictionary<uint, string>> ProbeArpAsync(SubnetScanPlan plan, List<uint> targets,
        IProgress<int>? progress, int baseCount, CancellationToken token)
    {
        var found = new Dictionary<uint, string>();
        foreach (var wait in new[] { 1200, 1000 })
        {
            token.ThrowIfCancellationRequested();
            var remaining = targets.Where(address => !found.ContainsKey(address)).ToList();
            if (remaining.Count == 0) break;
            TriggerArp(plan.SourceAddress, remaining);
            await Task.Delay(wait, token).ConfigureAwait(false);
            foreach (var pair in ReadArpTable(plan.InterfaceIndex))
            {
                if (!found.ContainsKey(pair.Address)) found[pair.Address] = pair.Mac;
            }
            progress?.Report(baseCount + targets.Count);
        }
        return found;
    }

    /// <summary>发 UDP 逼出 ARP 请求（端口没人监听无所谓，目的只是让系统去问一句“谁在用这个地址”）。</summary>
    private static void TriggerArp(uint source, List<uint> targets)
    {
        try
        {
            using var client = new UdpClient(new IPEndPoint(IPAddress.Parse(Ipv4.ToText(source)), 0));
            var payload = new byte[1];
            foreach (var address in targets)
            {
                try
                {
                    client.Send(payload, payload.Length, new IPEndPoint(IPAddress.Parse(Ipv4.ToText(address)), 33434));
                }
                catch
                {
                    // 个别地址发不出去不影响别的
                }
            }
        }
        catch
        {
            // 绑不上本机地址就跳过这一轮，后面的结果会当成“空闲”处理。
        }
    }

    private static List<(uint Address, string Mac)> ReadArpTable(int interfaceIndex)
    {
        var list = new List<(uint, string)>();
        try
        {
            var size = 0u;
            var probe = IpHlpApi.GetIpNetTable(IntPtr.Zero, ref size, false);
            if (probe != 122 || size == 0) return list;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (IpHlpApi.GetIpNetTable(buffer, ref size, false) != 0) return list;
                var count = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf<IpHlpApi.MibIpNetRow>();
                for (var index = 0; index < count; index++)
                {
                    var pointer = IntPtr.Add(buffer, 4 + (index * rowSize));
                    var row = Marshal.PtrToStructure<IpHlpApi.MibIpNetRow>(pointer);
                    if (interfaceIndex > 0 && row.InterfaceIndex != (uint)interfaceIndex) continue;
                    if (row.Type is not (3 or 4)) continue;
                    if (row.PhysicalAddressLength != 6) continue;
                    var mac = BitConverter.GetBytes(row.PhysicalAddress);
                    list.Add((Ipv4.FromNetworkOrder(row.Address), string.Join("-", mac.Take(6).Select(item => item.ToString("X2")))));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            // 读不到 ARP 表就当作没找到任何设备
        }
        return list;
    }

    private static async Task<Dictionary<uint, int>> PingManyAsync(List<uint> targets, int timeoutMs, int concurrency, CancellationToken token)
    {
        var result = new Dictionary<uint, int>();
        if (targets.Count == 0) return result;
        using var gate = new SemaphoreSlim(Math.Max(1, concurrency));
        var tasks = new List<Task>(targets.Count);
        foreach (var address in targets)
        {
            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var rtt = PingProbe.Send(address, timeoutMs);
                    if (rtt is not null)
                    {
                        lock (result) result[address] = rtt.Value;
                    }
                }
                finally
                {
                    gate.Release();
                }
            }, token));
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return result;
    }
}
