using System.Runtime.InteropServices;

namespace SRM.Core.Native;

internal static class IpHlpApi
{
    public const ushort AfInet = 2;
    public const ushort AfInet6 = 23;

    [StructLayout(LayoutKind.Explicit, Size = 28)]
    internal struct SockAddrInet
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(4)] public uint V4Address;
    }

    [StructLayout(LayoutKind.Explicit, Size = 104)]
    internal struct MibIpForwardRow2
    {
        [FieldOffset(0)] public ulong InterfaceLuid;
        [FieldOffset(8)] public uint InterfaceIndex;
        [FieldOffset(12)] public SockAddrInet DestinationPrefix;
        [FieldOffset(40)] public byte SitePrefixLength;
        [FieldOffset(44)] public SockAddrInet NextHop;
        [FieldOffset(84)] public uint Metric;
        [FieldOffset(88)] public uint Protocol;
        [FieldOffset(100)] public uint Origin;
    }

    [StructLayout(LayoutKind.Explicit, Size = 168)]
    internal struct MibIpInterfaceRow
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(8)] public ulong InterfaceLuid;
        [FieldOffset(16)] public uint InterfaceIndex;
        [FieldOffset(42)] public byte WeakHostSend;
        [FieldOffset(43)] public byte WeakHostReceive;
        [FieldOffset(44)] public byte UseAutomaticMetric;
        [FieldOffset(144)] public uint SitePrefixLength;
        [FieldOffset(148)] public uint Metric;
        [FieldOffset(152)] public uint NlMtu;
        [FieldOffset(156)] public byte Connected;
        [FieldOffset(160)] public uint ReachableTime;
        [FieldOffset(164)] public byte TransmitOffload;
        [FieldOffset(165)] public byte ReceiveOffload;
        [FieldOffset(166)] public byte DisableDefaultRoutes;
    }

    [StructLayout(LayoutKind.Explicit, Size = 216)]
    internal struct IpAdapterAddresses
    {
        [FieldOffset(0)] public uint Length;
        [FieldOffset(4)] public uint IfIndex;
        [FieldOffset(8)] public IntPtr Next;
        [FieldOffset(16)] public IntPtr AdapterName;
        [FieldOffset(24)] public IntPtr FirstUnicastAddress;
        [FieldOffset(64)] public IntPtr Description;
        [FieldOffset(72)] public IntPtr FriendlyName;
        [FieldOffset(100)] public uint IfType;
        [FieldOffset(104)] public uint OperStatus;
        [FieldOffset(184)] public ulong TransmitLinkSpeed;
        [FieldOffset(192)] public ulong ReceiveLinkSpeed;
        [FieldOffset(208)] public IntPtr FirstGatewayAddress;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    internal struct IpAdapterUnicastAddress
    {
        [FieldOffset(0)] public uint Length;
        [FieldOffset(8)] public IntPtr Next;
        [FieldOffset(16)] public IntPtr Address;
        [FieldOffset(24)] public int AddressLength;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    internal struct SockAddr
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(4)] public uint V4Address;
        [FieldOffset(8)] public uint V6Address01;
    }

    internal const uint GaaFlagSkipAnycast = 0x0002;
    internal const uint GaaFlagSkipMulticast = 0x0004;
    internal const uint GaaFlagSkipDnsServer = 0x0008;
    internal const uint GaaFlagIncludeGateways = 0x0080;

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern uint GetAdaptersAddresses(ushort family, uint flags, IntPtr reserved, IntPtr addresses, ref uint size);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern uint GetIpForwardTable2(ushort family, out IntPtr table);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern void FreeMibTable(IntPtr memory);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern uint ConvertInterfaceIndexToLuid(uint interfaceIndex, out ulong luid);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern uint GetIpInterfaceEntry(ref MibIpInterfaceRow row);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern uint SetIpInterfaceEntry(ref MibIpInterfaceRow row);

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct MibIpNetRow
    {
        [FieldOffset(0)] public uint InterfaceIndex;
        [FieldOffset(4)] public uint PhysicalAddressLength;
        [FieldOffset(8)] public ulong PhysicalAddress;
        [FieldOffset(16)] public uint Address;
        [FieldOffset(20)] public uint Type;
    }

    /// <summary>读系统的 ARP 表：里面有 MAC 的地址就是“确实有设备在用”。</summary>
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern uint GetIpNetTable(IntPtr table, ref uint size, bool order);
}
