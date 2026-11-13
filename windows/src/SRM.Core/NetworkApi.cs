using System.Diagnostics;
using System.Security.Principal;
using SRM.Core.Native;

namespace SRM.Core;

public static class NetworkApi
{
    public static bool IsElevated { get; } = DetectElevation();

    private static bool DetectElevation()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static List<NicInfo> ListNics()
    {
        var adapters = Adapters.Query();
        var devices = DeviceManager.QueryNetworkDevices();
        var routes = RouteTable.Query();

        var gateways = new Dictionary<int, string>();
        var defaultMetrics = new Dictionary<int, int>();
        foreach (var route in routes)
        {
            if (route.PrefixLength != 0) continue;
            if (!gateways.ContainsKey(route.InterfaceIndex)) gateways[route.InterfaceIndex] = Ipv4.ToText(route.NextHop);
            if (route.NextHop == 0) continue;
            if (!defaultMetrics.TryGetValue(route.InterfaceIndex, out var current) || route.Metric < current)
            {
                defaultMetrics[route.InterfaceIndex] = (int)route.Metric;
            }
        }

        var list = new List<NicInfo>();
        var matchedDevices = new HashSet<uint>();
        foreach (var adapter in adapters)
        {
            if (adapter.Index <= 1 || adapter.Name.StartsWith("Loopback", StringComparison.OrdinalIgnoreCase)) continue;
            var device = devices.FirstOrDefault(item => item.Guid.Length > 0 && string.Equals(item.Guid, adapter.Guid, StringComparison.OrdinalIgnoreCase));
            if (device is not null) matchedDevices.Add(device.DevInst);
            var metric = TryGetMetric(adapter.Index, IpHlpApi.AfInet);
            var settings = TryGetSettings(adapter.Index);
            var hasDefaultRoute = defaultMetrics.TryGetValue(adapter.Index, out var defaultMetric);
            list.Add(new NicInfo
            {
                Index = adapter.Index,
                Name = adapter.Name,
                Description = device?.Description.Length > 0 ? device.Description : adapter.Description,
                Status = adapter.IsUp ? "已连接" : "已断开",
                IsUp = adapter.IsUp,
                IsDisabled = false,
                IsVirtual = device?.IsVirtual ?? false,
                LinkSpeedBps = (long)adapter.TransmitLinkSpeed,
                Address = string.Join(" / ", adapter.Addresses),
                Gateway = gateways.TryGetValue(adapter.Index, out var gateway) ? gateway : string.Empty,
                Metric = metric?.Metric,
                AutomaticMetric = metric?.Automatic,
                AdapterGuid = adapter.Guid,
                DeviceId = device?.InstanceId ?? string.Empty,
                ConfiguredGateway = adapter.Gateways.FirstOrDefault() ?? string.Empty,
                DefaultRouteMetric = hasDefaultRoute ? defaultMetric : null,
                IgnoresDefaultRoutes = settings?.IgnoresDefaultRoutes ?? false,
                WeakHostSend = settings?.WeakHostSend ?? false,
                WeakHostReceive = settings?.WeakHostReceive ?? false,
            });
        }

        foreach (var device in devices)
        {
            if (matchedDevices.Contains(device.DevInst)) continue;
            if (device.ConnectionName.Length == 0) continue;
            if (device.IsHidden || device.Index == 1) continue;
            var fallbackMetric = device.Index > 1 ? TryGetMetric(device.Index, IpHlpApi.AfInet) : null;
            var fallbackSettings = device.Index > 1 ? TryGetSettings(device.Index) : null;
            var status = device.IsDisabled ? "已禁用" : device.HasProblem ? "未就绪" : "已断开";
            list.Add(new NicInfo
            {
                Index = device.Index,
                Name = device.ConnectionName,
                Description = device.Description,
                Status = status,
                IsUp = false,
                IsDisabled = device.IsDisabled,
                IsVirtual = device.IsVirtual,
                Metric = fallbackMetric?.Metric,
                AutomaticMetric = fallbackMetric?.Automatic,
                AdapterGuid = device.Guid,
                DeviceId = device.InstanceId,
                IgnoresDefaultRoutes = fallbackSettings?.IgnoresDefaultRoutes ?? false,
                WeakHostSend = fallbackSettings?.WeakHostSend ?? false,
                WeakHostReceive = fallbackSettings?.WeakHostReceive ?? false,
            });
        }

        return list
            .OrderBy(nic => nic.Metric ?? int.MaxValue)
            .ThenBy(nic => nic.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    public static void SetNicMetric(int index, int metric)
    {
        if (metric < NicPriority.MinMetric || metric > NicPriority.MaxMetric)
        {
            throw new ArgumentOutOfRangeException(nameof(metric), $"跃点数应在 {NicPriority.MinMetric} 到 {NicPriority.MaxMetric} 之间。");
        }
        ApplyMetric(index, IpHlpApi.AfInet, metric, automatic: false, required: true);
        ApplyMetric(index, IpHlpApi.AfInet6, metric, automatic: false, required: false);
    }

    public static void ResetNicMetric(int index)
    {
        ApplyMetric(index, IpHlpApi.AfInet, null, automatic: true, required: true);
        ApplyMetric(index, IpHlpApi.AfInet6, null, automatic: true, required: false);
    }

    public static void SetNicEnabled(NicInfo nic, bool enabled)
    {
        var devices = DeviceManager.QueryNetworkDevices();
        NetworkDevice? target = null;
        if (nic.AdapterGuid.Length > 0)
        {
            target = devices.FirstOrDefault(item => string.Equals(item.Guid, nic.AdapterGuid, StringComparison.OrdinalIgnoreCase));
        }
        target ??= devices.FirstOrDefault(item => nic.Index > 0 && item.Index == nic.Index);
        target ??= devices.FirstOrDefault(item => item.ConnectionName.Length > 0 && string.Equals(item.ConnectionName, nic.Name, StringComparison.CurrentCultureIgnoreCase));
        if (target is null) throw new InvalidOperationException($"找不到网卡“{nic.Name}”对应的设备，可能已被移除。");

        try
        {
            DeviceManager.SetEnabled(target, enabled);
        }
        catch (Exception ex)
        {
            if (!TrySetEnabledByNetsh(nic.Name, enabled, out var error))
            {
                throw new InvalidOperationException($"{ex.Message}（备用方式也失败：{error}）");
            }
        }
    }

    public static (int Metric, bool Automatic)? TryGetMetric(int index, ushort family)
    {
        try
        {
            var row = ReadInterfaceRow(index, family);
            return ((int)row.Metric, row.UseAutomaticMetric != 0);
        }
        catch
        {
            return null;
        }
    }

    private static void ApplyMetric(int index, ushort family, int? metric, bool automatic, bool required)
    {
        var error = ApplyRowChange(index, family, (ref IpHlpApi.MibIpInterfaceRow row) =>
        {
            row.UseAutomaticMetric = automatic ? (byte)1 : (byte)0;
            if (!automatic && metric is int value) row.Metric = (uint)value;
        });
        if (error is null) return;
        var fallbackError = string.Empty;
        if (family == IpHlpApi.AfInet && TrySetMetricByPowerShell(index, metric, automatic, out fallbackError)) return;
        if (!required) return;
        throw new InvalidOperationException(CombineError(error, fallbackError));
    }

    /// <summary>读取网卡的多网卡共存设置：是否仅内网、是否允许跨网卡收发。</summary>
    public static NicSettings? TryGetSettings(int index)
    {
        try
        {
            var row = ReadInterfaceRow(index, IpHlpApi.AfInet);
            return new NicSettings(row.DisableDefaultRoutes != 0, row.WeakHostSend != 0, row.WeakHostReceive != 0);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>开关“跨网卡收发”（弱主机模型）：让这块网卡能收发本机其它网卡的流量，多网卡同时工作时不再互相丢包。</summary>
    public static void SetNicWeakHost(int index, bool enabled)
    {
        var error = ApplyRowChange(index, IpHlpApi.AfInet, (ref IpHlpApi.MibIpInterfaceRow row) =>
        {
            row.WeakHostSend = (byte)(enabled ? 1 : 0);
            row.WeakHostReceive = (byte)(enabled ? 1 : 0);
        });
        if (error is not null && !TrySetWeakHostByPowerShell(index, enabled, out var fallbackError))
        {
            throw new InvalidOperationException(CombineError(error, fallbackError));
        }
        // IPv6 尽力而为：失败不影响 IPv4 的结果。
        ApplyRowChange(index, IpHlpApi.AfInet6, (ref IpHlpApi.MibIpInterfaceRow row) =>
        {
            row.WeakHostSend = (byte)(enabled ? 1 : 0);
            row.WeakHostReceive = (byte)(enabled ? 1 : 0);
        });
    }

    /// <summary>开关“仅内网”：开启后这块网卡不再参与上网出口（默认路由）竞争，但本网段的设备照常访问。</summary>
    public static void SetNicIgnoreDefaultRoutes(int index, bool ignore)
    {
        var error = ApplyRowChange(index, IpHlpApi.AfInet, (ref IpHlpApi.MibIpInterfaceRow row) =>
        {
            row.DisableDefaultRoutes = (byte)(ignore ? 1 : 0);
        });
        if (error is not null && !TrySetIgnoreDefaultRoutesByPowerShell(index, ignore, out var fallbackError))
        {
            throw new InvalidOperationException(CombineError(error, fallbackError));
        }
        ApplyRowChange(index, IpHlpApi.AfInet6, (ref IpHlpApi.MibIpInterfaceRow row) =>
        {
            row.DisableDefaultRoutes = (byte)(ignore ? 1 : 0);
        });
    }

    private delegate void RowMutator(ref IpHlpApi.MibIpInterfaceRow row);

    /// <summary>读取 → 修改 → 写回一条网卡接口配置；成功返回 null，失败返回原因。</summary>
    private static string? ApplyRowChange(int index, ushort family, RowMutator mutate)
    {
        try
        {
            var row = ReadInterfaceRow(index, family);
            mutate(ref row);
            if (family == IpHlpApi.AfInet6) row.SitePrefixLength = 0;
            var result = IpHlpApi.SetIpInterfaceEntry(ref row);
            return result == 0 ? null : DescribeInterfaceError(result);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string CombineError(string error, string fallbackError) =>
        fallbackError.Length > 0 ? $"{error}（备用方式也失败：{fallbackError}）" : error;

    private static bool TrySetMetricByPowerShell(int index, int? metric, bool automatic, out string error)
    {
        var command = automatic
            ? $"Set-NetIPInterface -InterfaceIndex {index} -AddressFamily IPv4 -AutomaticMetric Enabled -ErrorAction Stop"
            : $"Set-NetIPInterface -InterfaceIndex {index} -AddressFamily IPv4 -InterfaceMetric {metric ?? 1} -ErrorAction Stop";
        return TryRunPowerShell(command, out error);
    }

    private static bool TrySetWeakHostByPowerShell(int index, bool enabled, out string error)
    {
        var state = enabled ? "Enabled" : "Disabled";
        return TryRunPowerShell($"Set-NetIPInterface -InterfaceIndex {index} -AddressFamily IPv4 -WeakHostSend {state} -WeakHostReceive {state} -ErrorAction Stop", out error);
    }

    private static bool TrySetIgnoreDefaultRoutesByPowerShell(int index, bool ignore, out string error)
    {
        var state = ignore ? "Enabled" : "Disabled";
        return TryRunPowerShell($"Set-NetIPInterface -InterfaceIndex {index} -AddressFamily IPv4 -IgnoreDefaultRoutes {state} -ErrorAction Stop", out error);
    }

    private static bool TryRunPowerShell(string command, out string error)
    {
        error = string.Empty;
        try
        {
            // 先让子进程统一用 UTF-8 输出，否则中文报错在界面上会变成乱码。
            var script = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; " + command;
            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script })
            {
                startInfo.ArgumentList.Add(argument);
            }
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                error = "无法启动 PowerShell。";
                return false;
            }
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(30000);
            if (process.ExitCode != 0)
            {
                error = output.Trim();
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static IpHlpApi.MibIpInterfaceRow ReadInterfaceRow(int index, ushort family)
    {
        if (index <= 0) throw new InvalidOperationException("接口序号无效。");
        if (IpHlpApi.ConvertInterfaceIndexToLuid((uint)index, out var luid) != 0)
        {
            throw new InvalidOperationException($"接口序号 {index} 无效或该网卡已被移除。");
        }
        var row = new IpHlpApi.MibIpInterfaceRow { Family = family, InterfaceLuid = luid, InterfaceIndex = (uint)index };
        var result = IpHlpApi.GetIpInterfaceEntry(ref row);
        if (result != 0) throw new InvalidOperationException($"读取该网卡的 IP 配置失败（错误码 {result}）。");
        return row;
    }

    private static string DescribeInterfaceError(uint result) => result switch
    {
        5 => "设置失败：需要管理员权限。",
        87 => "设置失败：网卡或跃点数不被系统接受。",
        _ => $"设置失败（错误码 {result}）。",
    };

    private static bool TrySetEnabledByNetsh(string connectionName, bool enabled, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(connectionName))
        {
            error = "缺少网卡名称。";
            return false;
        }
        try
        {
            var startInfo = new ProcessStartInfo("netsh.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("interface");
            startInfo.ArgumentList.Add("set");
            startInfo.ArgumentList.Add("interface");
            startInfo.ArgumentList.Add($"name={connectionName}");
            startInfo.ArgumentList.Add(enabled ? "admin=enable" : "admin=disable");
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                error = "无法启动 netsh。";
                return false;
            }
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(20000);
            if (process.ExitCode != 0)
            {
                error = output.Trim();
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

}
