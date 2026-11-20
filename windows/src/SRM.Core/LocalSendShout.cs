using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SRM.Core.Localization;

namespace SRM.Core;

/// <summary>
/// 替这台机器“喊一声”：照发现软件（LocalSend 用的那套）的说法，从选中的网卡发一条“我在这里”。
/// 这一步和“系统挑哪块网卡”没关系——发送用的套接字直接绑在这块网卡的地址上，
/// 包只能从它出去，所以系统把 Wi-Fi 排在前面也照样能喊到。
/// 身份沿用这台机器上 LocalSend 自己的（设备名 + 证书指纹），对面看到的就是它，
/// 找到之后照样用 LocalSend 传文件，不会凭空多出一个连不上的“假设备”。
/// </summary>
public static class LocalSendShout
{
    /// <summary>喊话用的身份，从 LocalSend 自己的设置文件里读出来。</summary>
    public sealed class Identity
    {
        public bool Found { get; init; }
        public string Path { get; init; } = string.Empty;
        public string Alias { get; init; } = string.Empty;
        public string Fingerprint { get; init; } = string.Empty;
        public int Port { get; init; } = DiscoveryRoute.Port;
        public string Protocol { get; init; } = "https";
        public string Problem { get; init; } = string.Empty;

        /// <summary>够不够用：设备名和证书指纹都读到了，对面才认得出来。</summary>
        public bool Ready => Found && Alias.Length > 0 && Fingerprint.Length > 0;
    }

    /// <summary>一次喊话的结果。</summary>
    public sealed class ShoutResult
    {
        public bool Ok { get; init; }
        public int Asked { get; init; }
        public int Sent { get; init; }
        public string From { get; init; } = string.Empty;
        public int InterfaceIndex { get; init; }

        /// <summary>这块网卡发出的组播包 / 字节数变化；读不到计数时是 -1。</summary>
        public long PacketDelta { get; init; } = -1;
        public long ByteDelta { get; init; } = -1;

        public string Payload { get; init; } = string.Empty;
        public string Problem { get; init; } = string.Empty;
    }

    private static readonly JsonSerializerOptions PayloadOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>LocalSend 的设置文件在哪。</summary>
    public static string SettingsPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LocalSend", "settings.json");

    /// <summary>读 LocalSend 的身份；读不到时 Problem 里写着为什么。</summary>
    public static Identity ReadIdentity()
    {
        var path = SettingsPath();
        if (!File.Exists(path))
        {
            return new Identity
            {
                Path = path,
                Problem = Loc.F("Core.Shout.NoSettings", path),
            };
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var alias = Text(root, "flutter.ls_alias");
            var port = Number(root, "flutter.ls_port") ?? DiscoveryRoute.Port;
            var secure = Flag(root, "flutter.ls_https") ?? true;

            var fingerprint = string.Empty;
            var context = Text(root, "flutter.ls_security_context");
            if (context.Length > 0)
            {
                using var inner = JsonDocument.Parse(context);
                fingerprint = Text(inner.RootElement, "certificateHash").Trim();
            }

            return new Identity
            {
                Found = true,
                Path = path,
                Alias = alias.Length > 0 ? alias : Environment.MachineName,
                Fingerprint = fingerprint,
                Port = port > 0 ? port : DiscoveryRoute.Port,
                Protocol = secure ? "https" : "http",
                Problem = fingerprint.Length > 0 ? string.Empty : Loc.T("Core.Shout.NoFingerprint"),
            };
        }
        catch (Exception ex)
        {
            return new Identity { Path = path, Problem = Loc.F("Core.Shout.SettingsReadFailed", ex.Message) };
        }
    }

    /// <summary>按发现软件认的格式拼出喊话内容。</summary>
    public static string BuildPayload(Identity identity)
    {
        var body = new Dictionary<string, object?>
        {
            ["alias"] = identity.Alias,
            ["version"] = "2.0",
            ["deviceModel"] = "Windows",
            ["deviceType"] = "desktop",
            ["fingerprint"] = identity.Fingerprint,
            ["port"] = identity.Port,
            ["protocol"] = identity.Protocol,
            ["download"] = false,
            ["announce"] = true,
        };
        return JsonSerializer.Serialize(body, PayloadOptions);
    }

    /// <summary>从指定网卡喊话：套接字绑在这块网卡的地址上，包只能从它出去。</summary>
    public static ShoutResult Send(Identity identity, int interfaceIndex, string localAddress, int count = 3, int gapMs = 700)
    {
        if (!identity.Ready)
        {
            return new ShoutResult
            {
                Asked = count,
                From = localAddress,
                InterfaceIndex = interfaceIndex,
                Problem = identity.Problem.Length > 0 ? identity.Problem : Loc.T("Core.Shout.NoIdentity"),
            };
        }

        if (!IPAddress.TryParse(localAddress, out var local) || local.AddressFamily != AddressFamily.InterNetwork)
        {
            return new ShoutResult
            {
                Asked = count,
                From = localAddress,
                InterfaceIndex = interfaceIndex,
                Problem = Loc.F("Core.Shout.NotLocalAddress", localAddress),
            };
        }

        var payload = BuildPayload(identity);
        var bytes = Encoding.UTF8.GetBytes(payload);
        var before = Counters(interfaceIndex);
        var sent = 0;
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(local, 0));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);

            var target = new IPEndPoint(new IPAddress(new byte[] { 224, 0, 0, 167 }), identity.Port);
            for (var i = 0; i < count; i++)
            {
                socket.SendTo(bytes, target);
                sent++;
                if (i + 1 < count && gapMs > 0) Thread.Sleep(gapMs);
            }
        }
        catch (Exception ex)
        {
            OperationLog.Write(Loc.F("Core.Shout.SendFailedLog", localAddress, interfaceIndex, sent, ex.Message));
            return new ShoutResult
            {
                Asked = count,
                Sent = sent,
                From = localAddress,
                InterfaceIndex = interfaceIndex,
                Payload = payload,
                Problem = ex.Message,
            };
        }

        Thread.Sleep(600);
        var after = Counters(interfaceIndex);
        var packetDelta = before.Packets >= 0 && after.Packets >= 0 ? after.Packets - before.Packets : -1;
        var byteDelta = before.Bytes >= 0 && after.Bytes >= 0 ? after.Bytes - before.Bytes : -1;
        OperationLog.Write(Loc.F("Core.Shout.SendLog", identity.Alias, localAddress, interfaceIndex, sent, packetDelta, byteDelta, payload));

        return new ShoutResult
        {
            Ok = sent == count,
            Asked = count,
            Sent = sent,
            From = localAddress,
            InterfaceIndex = interfaceIndex,
            PacketDelta = packetDelta,
            ByteDelta = byteDelta,
            Payload = payload,
        };
    }

    /// <summary>这块网卡一共发出去多少组播/广播包、多少字节；读不到返回 -1。</summary>
    private static (long Packets, long Bytes) Counters(int interfaceIndex)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                var properties = nic.GetIPProperties();
                var ipv4 = properties.GetIPv4Properties();
                if (ipv4 is null || ipv4.Index != interfaceIndex) continue;
                var stats = nic.GetIPv4Statistics();
                return (stats.NonUnicastPacketsSent, stats.BytesSent);
            }
        }
        catch
        {
            // 读不到计数就算了，调用方按 -1 处理
        }
        return (-1, -1);
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool? Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
}
