using System.Runtime.InteropServices;
using SRM.Core.Native;

namespace SRM.Core;

public static class RouteTable
{
    public static byte[] DumpRaw(int maxBytes)
    {
        var result = IpHlpApi.GetIpForwardTable2(IpHlpApi.AfInet, out var table);
        if (result != 0) return Array.Empty<byte>();
        try
        {
            var bytes = new byte[maxBytes];
            Marshal.Copy(table, bytes, 0, maxBytes);
            return bytes;
        }
        finally
        {
            IpHlpApi.FreeMibTable(table);
        }
    }

    public static List<RouteRow> Query()
    {
        var result = IpHlpApi.GetIpForwardTable2(IpHlpApi.AfInet, out var table);
        if (result != 0) throw new InvalidOperationException($"读取路由表失败（错误码 {result}）。");
        try
        {
            var count = (uint)Marshal.ReadInt32(table);
            var rowSize = Marshal.SizeOf<IpHlpApi.MibIpForwardRow2>();
            var rowsOffset = IntPtr.Size == 8 ? 8 : 4;
            var rows = new List<RouteRow>((int)count);
            for (var i = 0; i < count; i++)
            {
                var pointer = IntPtr.Add(table, rowsOffset + (int)(i * (uint)rowSize));
                var row = Marshal.PtrToStructure<IpHlpApi.MibIpForwardRow2>(pointer);
                if (row.DestinationPrefix.Family != IpHlpApi.AfInet) continue;
                rows.Add(new RouteRow
                {
                    Destination = Ipv4.FromNetworkOrder(row.DestinationPrefix.V4Address),
                    PrefixLength = row.SitePrefixLength,
                    NextHop = Ipv4.FromNetworkOrder(row.NextHop.V4Address),
                    InterfaceIndex = (int)row.InterfaceIndex,
                    Metric = row.Metric,
                });
            }
            return rows;
        }
        finally
        {
            IpHlpApi.FreeMibTable(table);
        }
    }
}
