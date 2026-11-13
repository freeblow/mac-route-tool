using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace SRM.Core;

/// <summary>
/// 「喊话到底从哪块网卡出去、对面能不能听见」——用真实测量说话，不再只看路由表猜。
/// 三件事：
///   1) Lookup：问系统一句“发往发现组播时源地址是谁”（不发包，瞬间出结果）；
///   2) Measure：真发一批测试包，看哪块网卡的发送计数涨了（最硬的证据）；
///   3) Watch：在发现端口上听一阵子，看谁在喊话、本机自己的喊话是从哪个地址出去的。
/// 三件事都不需要管理员权限，也不会改动系统设置。
/// </summary>
public static class DiscoveryProbe
{
    /// <summary>专供测量用的端口：和发现软件的 53317 错开，免得打扰它。</summary>
    public const int ProbePort = 53318;

    /// <summary>一次出口判定的结果。</summary>
    public sealed class Egress
    {
        public uint Address { get; init; }

        /// <summary>知道是系统里哪块网卡时，这里是网卡序号；不知道就是 0。</summary>
        public int InterfaceIndex { get; init; }

        /// <summary>“实测”= 真发包看计数；“系统选的”= 问系统的源地址；“估算”= 只看路由表；空 = 测不出来。</summary>
        public string Method { get; init; } = string.Empty;

        public bool Measured => Method.StartsWith("实测", StringComparison.Ordinal);
        public bool Known => Address != 0;
    }

    /// <summary>听到的一条喊话。</summary>
    public sealed class Shout
    {
        public string Source { get; init; } = string.Empty;
        public string Alias { get; init; } = string.Empty;
        public int Bytes { get; init; }
        public bool Mine { get; init; }
        public bool Announcement { get; init; }
    }

    /// <summary>监听结果。</summary>
    public sealed class WatchResult
    {
        public bool Listening { get; init; }
        public string Problem { get; init; } = string.Empty;
        public int Seconds { get; init; }
        public List<Shout> Shouts { get; init; } = new();

        public List<Shout> Mine => Shouts.Where(item => item.Mine).ToList();
        public List<Shout> Others => Shouts.Where(item => !item.Mine).ToList();
    }

    /// <summary>问系统：发往发现组播的包会带哪个源地址（不发任何包，随时可以调）。</summary>
    public static Egress Lookup()
    {
        var address = DiscoveryRoute.ProbeEgressAddress();
        return new Egress
        {
            Address = address,
            InterfaceIndex = address == 0 ? 0 : IndexOfAddress(address),
            Method = address == 0 ? string.Empty : "系统选的",
        };
    }

    /// <summary>问系统：现在上网（发往公网地址）会从哪个本机地址出去；测不出来返回 0。</summary>
    public static uint InternetEgress() => ProbeSourceAddress("8.8.8.8", 53);

    /// <summary>问系统：发往这个地址时会带哪个本机源地址（只做路由查询，不发包）。</summary>
    public static uint ProbeSourceAddress(string address, int port)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(IPAddress.Parse(address), port));
            if (socket.LocalEndPoint is IPEndPoint endpoint) return ToValue(endpoint.Address);
        }
        catch
        {
            // 探测失败就当测不出来
        }
        return 0;
    }

    /// <summary>真发一批测试包，看哪块网卡的发送计数涨了；涨得最多的那块就是出口。测不出来时退回其它办法。</summary>
    public static Egress Measure(int packets = 120)
    {
        var measured = MeasureByCounters(packets);
        if (measured is not null) return measured;
        var loopback = MeasureByLoopback();
        if (loopback != 0)
        {
            return new Egress { Address = loopback, InterfaceIndex = IndexOfAddress(loopback), Method = "实测" };
        }
        var estimate = DiscoveryRoute.ProbeEgressAddress();
        return new Egress
        {
            Address = estimate,
            InterfaceIndex = estimate == 0 ? 0 : IndexOfAddress(estimate),
            Method = estimate != 0 ? "估算" : string.Empty,
        };
    }

    /// <summary>在发现端口上听一阵子，收集喊话（含本机自己的，能看出出口地址）。</summary>
    public static WatchResult Watch(int seconds, CancellationToken token = default)
    {
        var group = new IPAddress(DiscoveryRoute.Group);
        var locals = LocalAddresses();
        Socket socket;
        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, DiscoveryRoute.Port));
        }
        catch (Exception ex)
        {
            return new WatchResult
            {
                Listening = false,
                Seconds = seconds,
                Problem = $"端口 {DiscoveryRoute.Port} 现在被别的程序占着，听不到喊话：{ex.Message}",
            };
        }

        try
        {
            foreach (var text in locals)
            {
                if (!IPAddress.TryParse(text, out var local)) continue;
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(group, local));
                }
                catch
                {
                    // 某块网卡加不进组就跳过，其它网卡照听
                }
            }

            var result = new WatchResult { Listening = true, Seconds = seconds };
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            var buffer = new byte[8192];
            var from = (EndPoint)new IPEndPoint(IPAddress.Any, 0);
            while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
            {
                if (!socket.Poll(300_000, SelectMode.SelectRead)) continue;
                try
                {
                    var read = socket.ReceiveFrom(buffer, ref from);
                    if (read <= 0) continue;
                    var source = from is IPEndPoint endpoint ? endpoint.Address.ToString() : string.Empty;
                    var text = Encoding.UTF8.GetString(buffer, 0, Math.Min(read, 4096));
                    result.Shouts.Add(new Shout
                    {
                        Source = source,
                        Alias = ReadAlias(text),
                        Bytes = read,
                        Mine = locals.Contains(source),
                        Announcement = text.Contains("\"announce\"", StringComparison.OrdinalIgnoreCase),
                    });
                }
                catch (SocketException)
                {
                    // 单包读失败就继续听
                }
            }
            return result;
        }
        finally
        {
            socket.Dispose();
        }
    }

    /// <summary>本机所有可用的 IPv4 地址（用来判断“这条喊话是不是自己发的”）。</summary>
    public static List<string> LocalAddresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var text = address.Address.ToString();
                    if (!list.Contains(text)) list.Add(text);
                }
            }
        }
        catch
        {
            // 读不到就当作没有本机地址
        }
        return list;
    }

    /// <summary>这个本机地址在哪块网卡上（网卡序号）；找不到返回 0。</summary>
    public static int IndexOfAddress(uint address)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var properties = nic.GetIPProperties();
                var matches = properties.UnicastAddresses
                    .Any(item => item.Address.AddressFamily == AddressFamily.InterNetwork && ToValue(item.Address) == address);
                if (!matches) continue;
                try
                {
                    return properties.GetIPv4Properties().Index;
                }
                catch
                {
                    // 个别虚拟网卡读不到序号，继续找别的
                }
            }
        }
        catch
        {
            // 读不到就算了
        }
        return 0;
    }

    /// <summary>把地址文本换成界面上的网卡名字，例如 10.40.81.63 → 以太网。</summary>
    public static string DescribeAddress(uint address, IEnumerable<NicInfo> nics)
    {
        if (address == 0) return "测不出来";
        var text = Ipv4.ToText(address);
        var nic = nics.FirstOrDefault(item => item.Address.Split(" / ", StringSplitOptions.RemoveEmptyEntries)
            .Any(part => Ipv4.TryParse(part, out var parsed) && parsed == address));
        return nic is null ? text : $"{nic.Name}（{text}）";
    }

    // ================= 内部实现 =================

    private sealed class Candidate
    {
        public int Index { get; init; }
        public string Name { get; init; } = string.Empty;
        public uint Address { get; init; }
        public NetworkInterface Nic { get; init; } = null!;
        public long NonUnicast { get; init; }
        public long Bytes { get; init; }
    }

    /// <summary>真发一批包，看哪块网卡的发送计数涨最多：那就是“喊话”实际出去的网卡。</summary>
    private static Egress? MeasureByCounters(int packets)
    {
        var candidates = new List<Candidate>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            try
            {
                var properties = nic.GetIPProperties();
                var index = properties.GetIPv4Properties().Index;
                if (index <= 1) continue;
                var address = properties.UnicastAddresses
                    .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(item => ToValue(item.Address))
                    .FirstOrDefault(value => value != 0);
                if (address == 0) continue;
                var stats = nic.GetIPv4Statistics();
                candidates.Add(new Candidate
                {
                    Index = index,
                    Name = nic.Name,
                    Address = address,
                    Nic = nic,
                    NonUnicast = stats.NonUnicastPacketsSent,
                    Bytes = stats.BytesSent,
                });
            }
            catch
            {
                // 读不到计数的网卡不参与
            }
        }
        if (candidates.Count == 0) return null;

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, false);
            var payload = new byte[1000];
            var target = new IPEndPoint(new IPAddress(new byte[] { 224, 0, 0, 167 }), DiscoveryRoute.Port);
            for (var i = 0; i < packets; i++) socket.SendTo(payload, target);
        }
        catch
        {
            return null;
        }

        Thread.Sleep(800);

        Candidate? best = null;
        long bestPackets = 0;
        long bestBytes = 0;
        foreach (var item in candidates)
        {
            long packetDelta;
            long byteDelta;
            try
            {
                var stats = item.Nic.GetIPv4Statistics();
                packetDelta = stats.NonUnicastPacketsSent - item.NonUnicast;
                byteDelta = stats.BytesSent - item.Bytes;
            }
            catch
            {
                continue;
            }
            if (packetDelta > bestPackets || (packetDelta == bestPackets && byteDelta > bestBytes))
            {
                bestPackets = packetDelta;
                bestBytes = byteDelta;
                best = item;
            }
        }
        if (best is null) return null;
        var enough = bestPackets >= packets / 2 || bestBytes >= (long)packets * 500;
        var text = string.Join("；", candidates.Select(item =>
        {
            try
            {
                var stats = item.Nic.GetIPv4Statistics();
                return $"{item.Name} 组播包 +{stats.NonUnicastPacketsSent - item.NonUnicast} / 字节 +{stats.BytesSent - item.Bytes}";
            }
            catch
            {
                return $"{item.Name} 读不到计数";
            }
        }));
        OperationLog.Write($"实测发现出口：发了 {packets} 个测试包 → {(enough ? DescribeAddress(best.Address, NetworkApi.ListNics()) : "计数没有明显变化")}（{text}）");
        if (!enough) return null;
        return new Egress { Address = best.Address, InterfaceIndex = best.Index, Method = "实测" };
    }

    /// <summary>发几个测试包到组播地址，收自己那份回环副本，看它带着哪个源地址回来。</summary>
    private static uint MeasureByLoopback()
    {
        var group = new IPAddress(DiscoveryRoute.Group);
        Socket? receiver = null;
        Socket? sender = null;
        try
        {
            var locals = LocalAddresses();
            if (locals.Count == 0) return 0;

            receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            receiver.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            receiver.Bind(new IPEndPoint(IPAddress.Any, ProbePort));
            foreach (var text in locals)
            {
                if (!IPAddress.TryParse(text, out var local)) continue;
                try
                {
                    receiver.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(group, local));
                }
                catch
                {
                    // 加不进组就不管，继续用其它网卡听
                }
            }

            sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sender.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            sender.Bind(new IPEndPoint(IPAddress.Any, 0));
            sender.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
            sender.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);

            var payload = Encoding.ASCII.GetBytes("SRM-egress-probe");
            for (var i = 0; i < 3; i++) sender.SendTo(payload, new IPEndPoint(group, ProbePort));

            var buffer = new byte[128];
            var from = (EndPoint)new IPEndPoint(IPAddress.Any, 0);
            var deadline = DateTime.UtcNow.AddMilliseconds(1200);
            while (DateTime.UtcNow < deadline)
            {
                if (!receiver.Poll(200_000, SelectMode.SelectRead)) continue;
                int read;
                try
                {
                    read = receiver.ReceiveFrom(buffer, ref from);
                }
                catch (SocketException)
                {
                    break;
                }
                if (read <= 0 || from is not IPEndPoint endpoint) continue;
                var value = ToValue(endpoint.Address);
                if (value != 0) return value;
            }
        }
        catch
        {
            // 测量失败就返回 0，由调用方退回其它办法
        }
        finally
        {
            receiver?.Dispose();
            sender?.Dispose();
        }
        return 0;
    }

    private static uint ToValue(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return 0;
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    /// <summary>从喊话内容里抠出设备名（alias），抠不到就算了。</summary>
    private static string ReadAlias(string json)
    {
        const string key = "\"alias\"";
        var index = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return string.Empty;
        index = json.IndexOf(':', index + key.Length);
        if (index < 0) return string.Empty;
        index = json.IndexOf('"', index + 1);
        if (index < 0) return string.Empty;
        var builder = new StringBuilder();
        for (var i = index + 1; i < json.Length; i++)
        {
            var ch = json[i];
            if (ch == '\\' && i + 1 < json.Length)
            {
                var next = json[++i];
                builder.Append(next switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => next,
                });
                continue;
            }
            if (ch == '"') break;
            builder.Append(ch);
        }
        return builder.ToString();
    }
}
