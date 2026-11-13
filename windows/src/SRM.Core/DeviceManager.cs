using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using SRM.Core.Native;

namespace SRM.Core;

public sealed class NetworkDevice
{
    public string InstanceId { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string ConnectionName { get; init; } = string.Empty;
    public string Guid { get; init; } = string.Empty;
    public int Index { get; init; }
    public bool IsDisabled { get; init; }
    public bool HasProblem { get; init; }
    public bool IsVirtual { get; init; }
    public bool IsHidden { get; init; }
    public uint DevInst { get; init; }
}

public static class DeviceManager
{
    public static List<NetworkDevice> QueryNetworkDevices()
    {
        var result = new List<NetworkDevice>();
        var classGuid = SetupApi.NetworkClassGuid;
        var deviceInfoSet = SetupApi.SetupDiGetClassDevs(ref classGuid, IntPtr.Zero, IntPtr.Zero, SetupApi.DigcfPresent);
        if (deviceInfoSet == new IntPtr(-1)) return result;
        try
        {
            var index = 0u;
            while (true)
            {
                var deviceInfo = new SetupApi.SpDevInfoData { cbSize = (uint)Marshal.SizeOf<SetupApi.SpDevInfoData>() };
                if (!SetupApi.SetupDiEnumDeviceInfo(deviceInfoSet, index, ref deviceInfo)) break;
                index++;
                result.Add(ReadDevice(deviceInfoSet, deviceInfo));
            }
        }
        finally
        {
            SetupApi.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
        return result;
    }

    public static void SetEnabled(NetworkDevice device, bool enabled)
    {
        var classGuid = SetupApi.NetworkClassGuid;
        var deviceInfoSet = SetupApi.SetupDiGetClassDevs(ref classGuid, IntPtr.Zero, IntPtr.Zero, SetupApi.DigcfPresent);
        if (deviceInfoSet == new IntPtr(-1)) throw new InvalidOperationException("无法打开设备列表。");
        try
        {
            var deviceInfo = new SetupApi.SpDevInfoData { cbSize = (uint)Marshal.SizeOf<SetupApi.SpDevInfoData>() };
            var ok = false;
            var memberIndex = 0u;
            while (SetupApi.SetupDiEnumDeviceInfo(deviceInfoSet, memberIndex, ref deviceInfo))
            {
                memberIndex++;
                if (deviceInfo.DevInst != device.DevInst) continue;
                var state = enabled ? SetupApi.DicsEnable : SetupApi.DicsDisable;
                if (!SetupApi.SetupDiChangeState(deviceInfoSet, ref deviceInfo, state))
                {
                    var problem = SetupApi.CM_Get_DevNode_Status(out _, out _, deviceInfo.DevInst, 0);
                    throw new InvalidOperationException(DescribeChangeError(enabled, problem));
                }
                ok = true;
                break;
            }
            if (!ok) throw new InvalidOperationException("未找到该网卡设备，可能已被移除。");
        }
        finally
        {
            SetupApi.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    public static string TryGetConnectionName(string adapterGuid)
    {
        if (string.IsNullOrEmpty(adapterGuid)) return string.Empty;
        var path = $@"SYSTEM\CurrentControlSet\Control\Network\{{{SetupApi.NetworkClassGuid.ToString("D").ToUpperInvariant()}}}\{adapterGuid}\Connection";
        using var key = Registry.LocalMachine.OpenSubKey(path);
        return key?.GetValue("Name") as string ?? string.Empty;
    }

    public static List<string> DumpRegistryValues(NetworkDevice device, string fakePropertyHint = "")
    {
        var lines = new List<string>();
        var classGuid = SetupApi.NetworkClassGuid;
        var deviceInfoSet = SetupApi.SetupDiGetClassDevs(ref classGuid, IntPtr.Zero, IntPtr.Zero, SetupApi.DigcfPresent);
        if (deviceInfoSet == new IntPtr(-1)) return lines;
        try
        {
            var deviceInfo = new SetupApi.SpDevInfoData { cbSize = (uint)Marshal.SizeOf<SetupApi.SpDevInfoData>() };
            var memberIndex = 0u;
            while (SetupApi.SetupDiEnumDeviceInfo(deviceInfoSet, memberIndex, ref deviceInfo))
            {
                memberIndex++;
                if (deviceInfo.DevInst != device.DevInst) continue;
                foreach (var (label, keyType) in new[] { ("DIREG_DEV", 0x00000001u), ("DIREG_DRV", 0x00000002u) })
                {
                    var handle = SetupApi.SetupDiOpenDevRegKey(deviceInfoSet, ref deviceInfo, SetupApi.DicsFlagGlobal, 0, keyType, SetupApi.KeyRead);
                    if (handle == new IntPtr(-1) || handle == IntPtr.Zero)
                    {
                        lines.Add($"{label}: 打不开");
                        continue;
                    }
                    using var safeHandle = new SafeRegistryHandle(handle, true);
                    using var key = RegistryKey.FromHandle(safeHandle);
                    var names = key.GetValueNames();
                    lines.Add($"{label} ({key.Name}) -> " + string.Join(", ", names.Select(n => n + "=" + (key.GetValue(n) as string ?? "?"))));
                }
                break;
            }
        }
        finally
        {
            SetupDiDestroy(deviceInfoSet);
        }
        return lines;
    }

    private static void SetupDiDestroy(IntPtr deviceInfoSet) => SetupApi.SetupDiDestroyDeviceInfoList(deviceInfoSet);

    private static NetworkDevice ReadDevice(IntPtr deviceInfoSet, SetupApi.SpDevInfoData deviceInfo)
    {
        var friendlyName = GetProperty(deviceInfoSet, deviceInfo, SetupApi.SpdrpFriendlyName);
        var deviceDesc = GetProperty(deviceInfoSet, deviceInfo, SetupApi.SpdrpDeviceDesc);
        var instanceId = GetInstanceId(deviceInfoSet, deviceInfo);
        var (adapterGuid, characteristics) = GetDriverInfo(deviceInfoSet, deviceInfo);
        var index = GuidToInterfaceIndex(adapterGuid);
        var status = SetupApi.CM_Get_DevNode_Status(out _, out var problem, deviceInfo.DevInst, 0);
        return new NetworkDevice
        {
            InstanceId = instanceId,
            Description = friendlyName.Length > 0 ? friendlyName : deviceDesc,
            ConnectionName = TryGetConnectionName(adapterGuid),
            Guid = adapterGuid,
            Index = index,
            IsDisabled = status == 0 && problem == SetupApi.CmProbDisabled,
            HasProblem = status == 0 && problem != 0,
            IsVirtual = IsVirtualDevice(instanceId, friendlyName, deviceDesc),
            IsHidden = (characteristics & SetupApi.NcfHidden) != 0,
            DevInst = deviceInfo.DevInst,
        };
    }

    private static bool IsVirtualDevice(string instanceId, string friendlyName, string deviceDesc)
    {
        if (instanceId.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase)) return true;
        if (instanceId.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase)) return true;
        var text = friendlyName + " " + deviceDesc;
        foreach (var keyword in new[] { "Virtual", "VMware", "VirtualBox", "Hyper-V", "TAP-Windows", "Wi-Fi Direct", "Npcap" })
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static int GuidToInterfaceIndex(string adapterGuid)
    {
        if (adapterGuid.Length == 0 || !Guid.TryParse(adapterGuid, out var guid)) return 0;
        if (SetupApi.ConvertInterfaceGuidToLuid(ref guid, out var luid) != 0) return 0;
        if (SetupApi.ConvertInterfaceLuidToIndex(ref luid, out var index) != 0) return 0;
        return (int)index;
    }

    private static (string Guid, uint Characteristics) GetDriverInfo(IntPtr deviceInfoSet, SetupApi.SpDevInfoData deviceInfo)
    {
        var handle = SetupApi.SetupDiOpenDevRegKey(deviceInfoSet, ref deviceInfo, SetupApi.DicsFlagGlobal, 0, SetupApi.DiregDrv, SetupApi.KeyRead);
        if (handle == new IntPtr(-1) || handle == IntPtr.Zero) return (string.Empty, 0);
        try
        {
            using var safeHandle = new SafeRegistryHandle(handle, true);
            using var key = RegistryKey.FromHandle(safeHandle);
            var guid = (key.GetValue("NetCfgInstanceId") as string ?? string.Empty).ToUpperInvariant();
            var characteristics = key.GetValue("Characteristics") switch
            {
                int value => (uint)value,
                long value => (uint)value,
                _ => 0u,
            };
            return (guid, characteristics);
        }
        catch
        {
            return (string.Empty, 0);
        }
    }

    private static string GetProperty(IntPtr deviceInfoSet, SetupApi.SpDevInfoData deviceInfo, uint property)
    {
        var buffer = new byte[1024];
        if (!SetupApi.SetupDiGetDeviceRegistryProperty(deviceInfoSet, ref deviceInfo, property, out _, buffer, (uint)buffer.Length, out var required) || required == 0)
        {
            return string.Empty;
        }
        var text = Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(required, (uint)buffer.Length)).TrimEnd('\0');
        return text;
    }

    private static string GetInstanceId(IntPtr deviceInfoSet, SetupApi.SpDevInfoData deviceInfo)
    {
        var builder = new StringBuilder(512);
        return SetupApi.SetupDiGetDeviceInstanceId(deviceInfoSet, ref deviceInfo, builder, (uint)builder.Capacity, out _)
            ? builder.ToString()
            : string.Empty;
    }

    private static string DescribeChangeError(bool enabling, uint result) => result switch
    {
        0 when enabling => "启用失败：系统拒绝了该操作（驱动或固件不支持）。",
        0 => "禁用失败：系统拒绝了该操作（驱动或固件不支持）。",
        5 when enabling => "启用失败：需要管理员权限。",
        5 => "禁用失败：需要管理员权限。",
        _ => enabling ? $"启用失败（错误码 {result}）。" : $"禁用失败（错误码 {result}）。",
    };
}
