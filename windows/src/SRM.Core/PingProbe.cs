using System.Net.NetworkInformation;
using SRM.Core.Localization;

namespace SRM.Core;

public static class PingProbe
{
    public static (string Address, string Label) GetTarget(RouteEntry route)
    {
        if (route.Destination.EndsWith("/32", StringComparison.Ordinal) && Ipv4.TryParse(route.Destination.Split('/')[0], out var host))
        {
            return (Ipv4.ToText(host), Loc.T("Core.Ping.Target"));
        }
        return (route.Gateway, Loc.T("Core.Ping.Gateway"));
    }

    public static async Task<string> ProbeAsync(RouteEntry route, int timeoutMs = 2000)
    {
        var (address, label) = GetTarget(route);
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, timeoutMs).ConfigureAwait(false);
            return reply.Status == IPStatus.Success
                ? Loc.F("Core.Ping.Reachable", label, address, reply.RoundtripTime)
                : Loc.F("Core.Ping.NoResponse", label, address, reply.Status);
        }
        catch (Exception ex)
        {
            return Loc.F("Core.Ping.Failed", label, address, ex.Message);
        }
    }

    /// <summary>简单 ping 一下：通了返回往返毫秒数，没通返回 null。</summary>
    public static int? Send(uint address, int timeoutMs = 1000)
    {
        try
        {
            using var ping = new Ping();
            var reply = ping.Send(Ipv4.ToText(address), timeoutMs);
            return reply.Status == IPStatus.Success ? (int)reply.RoundtripTime : null;
        }
        catch
        {
            return null;
        }
    }
}
