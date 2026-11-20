using System.Net;
using System.Runtime.InteropServices;
using SRM.Core.Native;
using SRM.Core.Localization;

namespace SRM.Core;

public sealed class AdapterRecord
{
    public int Index { get; init; }
    public string Guid { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool IsUp { get; init; }
    public ulong TransmitLinkSpeed { get; init; }
    public List<string> Addresses { get; } = new();
    public List<string> Gateways { get; } = new();
}

public static class Adapters
{
    public static List<AdapterRecord> Query()
    {
        const uint flags = IpHlpApi.GaaFlagSkipAnycast | IpHlpApi.GaaFlagSkipMulticast
            | IpHlpApi.GaaFlagSkipDnsServer | IpHlpApi.GaaFlagIncludeGateways;
        var size = 16 * 1024u;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            var result = IpHlpApi.GetAdaptersAddresses(IpHlpApi.AfInet, flags, IntPtr.Zero, buffer, ref size);
            if (result == 111)
            {
                Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal((int)size);
                result = IpHlpApi.GetAdaptersAddresses(IpHlpApi.AfInet, flags, IntPtr.Zero, buffer, ref size);
            }
            if (result != 0) throw new InvalidOperationException(Loc.F("Core.Adapters.ReadFailed", result));

            var list = new List<AdapterRecord>();
            var pointer = buffer;
            while (pointer != IntPtr.Zero)
            {
                var adapter = Marshal.PtrToStructure<IpHlpApi.IpAdapterAddresses>(pointer);
                var record = new AdapterRecord
                {
                    Index = (int)adapter.IfIndex,
                    Guid = ParseGuid(Marshal.PtrToStringAnsi(adapter.AdapterName)),
                    Name = Marshal.PtrToStringUni(adapter.FriendlyName) ?? string.Empty,
                    Description = Marshal.PtrToStringUni(adapter.Description) ?? string.Empty,
                    IsUp = adapter.OperStatus == 1,
                    TransmitLinkSpeed = adapter.TransmitLinkSpeed,
                };
                ReadV4(adapter.FirstUnicastAddress, record.Addresses);
                ReadV4(adapter.FirstGatewayAddress, record.Gateways);
                list.Add(record);
                pointer = adapter.Next;
            }
            return list;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string ParseGuid(string? adapterName)
    {
        if (string.IsNullOrEmpty(adapterName)) return string.Empty;
        var start = adapterName.IndexOf('{');
        if (start < 0) return string.Empty;
        var end = adapterName.IndexOf('}', start);
        return end < 0 ? string.Empty : adapterName[start..(end + 1)].ToUpperInvariant();
    }

    private static void ReadV4(IntPtr head, List<string> target)
    {
        var pointer = head;
        while (pointer != IntPtr.Zero)
        {
            var entry = Marshal.PtrToStructure<IpHlpApi.IpAdapterUnicastAddress>(pointer);
            if (entry.Address != IntPtr.Zero)
            {
                var address = Marshal.PtrToStructure<IpHlpApi.SockAddr>(entry.Address);
                if (address.Family == IpHlpApi.AfInet)
                {
                    var text = new IPAddress(BitConverter.GetBytes(address.V4Address)).ToString();
                    if (!text.StartsWith("169.254.", StringComparison.Ordinal) && text != "0.0.0.0") target.Add(text);
                }
            }
            pointer = entry.Next;
        }
    }
}
