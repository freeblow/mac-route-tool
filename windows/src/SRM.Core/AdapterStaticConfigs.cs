using Microsoft.Win32;
using SRM.Core.Localization;

namespace SRM.Core;

/// <summary>网卡属性里手动填的 IPv4 地址/网关。Legacy=true 表示这条来自注册表的老位置（当前系统用的是新位置那份配置）。</summary>
public sealed record AdapterStaticConfig(string Guid, string Name, bool EnableDhcp, string Address, string Gateway, bool Legacy);

public static class AdapterStaticConfigs
{
    public static List<AdapterStaticConfig> Query()
    {
        var list = new List<AdapterStaticConfig>();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var adapter in Adapters.Query())
            {
                if (adapter.Guid.Length > 0) names[adapter.Guid] = adapter.Name;
            }
        }
        catch
        {
            // 拿不到名字也不影响后面的判断。
        }

        try
        {
            // Adapters.Query() 只列“还启用着”的网卡；已禁用的设备（例如停用的 TAP）要另找一遍，
            // 否则它们留在注册表里的旧配置就成了看不见的幽灵配置。
            foreach (var device in DeviceManager.QueryNetworkDevices())
            {
                if (device.Guid.Length == 0 || names.ContainsKey(device.Guid)) continue;
                var name = device.ConnectionName.Length > 0 ? device.ConnectionName : device.Description;
                names[device.Guid] = device.IsDisabled ? Loc.F("Core.Adapter.DisabledName", name) : name;
            }
        }
        catch
        {
            // 设备列表读不到就只统计启用中的网卡。
        }

        foreach (var (guid, name) in names)
        {
            var current = Read(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + guid);
            var legacy = Read(@"SYSTEM\CurrentControlSet\Services\" + guid + @"\Parameters\Tcpip");
            var picked = current;
            var isLegacy = false;
            if (picked is not null && picked.Value.Gateway.Length == 0 && legacy?.Gateway.Length > 0)
            {
                picked = legacy;
                isLegacy = true;
            }
            if (picked is not { } value) continue;
            if (value.Gateway.Length == 0 && value.Address.Length == 0) continue;
            list.Add(new AdapterStaticConfig(guid, name, value.EnableDhcp, value.Address, value.Gateway, isLegacy));
        }
        return list;
    }

    private static (bool EnableDhcp, string Address, string Gateway)? Read(string path)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            if (key is null) return null;
            return (key.GetValue("EnableDHCP") is int flag && flag != 0,
                FirstText(key.GetValue("IPAddress") as string[]),
                FirstText(key.GetValue("DefaultGateway") as string[]));
        }
        catch
        {
            return null;
        }
    }

    private static string FirstText(string[]? values) =>
        values is null ? string.Empty : values.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)) ?? string.Empty;
}
