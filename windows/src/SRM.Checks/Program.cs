using System.Text;
using SRM.Core;

Console.OutputEncoding = Encoding.UTF8;
var failures = 0;

if (args.Contains("firewall-script"))
{
    var index = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 6;
    Console.WriteLine(DiscoveryFirewall.RepairScript(index));
    return 0;
}

if (args.Contains("probe"))
{
    var index = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 6;
    Console.WriteLine($"== 写入探针 idx={index}（写入与当前相同的值，不会改变系统）==");
    var settings = NetworkApi.TryGetSettings(index);
    Console.WriteLine($"读取到的设置：{(settings is null ? "（读不到）" : $"仅内网={settings.IgnoresDefaultRoutes} 跨网卡收发={settings.WeakHost}")}");
    if (settings is not null)
    {
        try { NetworkApi.SetNicIgnoreDefaultRoutes(index, settings.IgnoresDefaultRoutes); Console.WriteLine("仅内网写入：成功"); }
        catch (Exception ex) { Console.WriteLine($"仅内网写入：{ex.Message}"); }
        if (settings.WeakHostSend != settings.WeakHostReceive)
        {
            Console.WriteLine("跨网卡收发：收/发状态不一致，跳过写入测试（避免改动系统）。");
        }
        else
        {
            try { NetworkApi.SetNicWeakHost(index, settings.WeakHost); Console.WriteLine("跨网卡收发写入：成功"); }
            catch (Exception ex) { Console.WriteLine($"跨网卡收发写入：{ex.Message}"); }
        }
    }
    return 0;
}

if (args.Contains("raw"))
{
    Console.WriteLine("=== 路由表原始字节（前 320 字节） ===");
    var bytes = RouteTable.DumpRaw(320);
    for (var offset = 0; offset < bytes.Length; offset += 16)
    {
        var chunk = bytes.Skip(offset).Take(16).ToArray();
        Console.WriteLine($"  {offset,4}: {string.Join(" ", chunk.Select(b => b.ToString("X2")))}");
    }
    Console.WriteLine();
    Console.WriteLine("=== 网卡原始信息 ===");
    foreach (var adapter in Adapters.Query())
    {
        Console.WriteLine($"  idx={adapter.Index} guid={adapter.Guid} name={adapter.Name} desc={adapter.Description} speed={adapter.TransmitLinkSpeed}");
    }
    Console.WriteLine();
    Console.WriteLine("=== 设备注册表 ===");
    foreach (var device in DeviceManager.QueryNetworkDevices().Where(d => !d.IsVirtual))
    {
        Console.WriteLine($"  {device.Description} | {device.InstanceId}");
        foreach (var line in DeviceManager.DumpRegistryValues(device)) Console.WriteLine("    " + line);
    }
    return 0;
}

void Section(string title)
{
    Console.WriteLine();
    Console.WriteLine("=== " + title + " ===");
}

void Check(string label, bool condition, string detail = "")
{
    if (condition)
    {
        Console.WriteLine("  PASS  " + label);
        return;
    }
    failures++;
    Console.WriteLine("  FAIL  " + label + (detail.Length > 0 ? "  ->  " + detail : string.Empty));
}

NicInfo Nic(int index, int metric, string name = "nic") => new() { Index = index, Metric = metric, Name = name };

Section("1) 地址解析与网段规范化");
Check("10.40.85.0/24 原样保留", Ipv4.TryParsePrefix("10.40.85.0/24", out var n1, out _) && n1 == "10.40.85.0/24", n1);
Check("不带掩码按 /32 主机路由", Ipv4.TryParsePrefix("10.40.85.7", out var n2, out _) && n2 == "10.40.85.7/32", n2);
Check("default 转换为 0.0.0.0/0", Ipv4.TryParsePrefix("default", out var n3, out _) && n3 == "0.0.0.0/0", n3);
Check("主机位自动归零", Ipv4.TryParsePrefix("10.40.85.99/24", out var n4, out _) && n4 == "10.40.85.0/24", n4);
Check("掩码越界被拒绝", !Ipv4.TryParsePrefix("10.0.0.0/33", out _, out _));
Check("非法地址被拒绝", !Ipv4.TryParsePrefix("999.1.1.1", out _, out _));
Check("掩码文本正确", Ipv4.MaskText(24) == "255.255.255.0", Ipv4.MaskText(24));
Check("掩码反查前缀", Ipv4.PrefixFromMask(0xFFFFFF00) == 24);

Section("2) 优先级调整算法");
var ordered = new List<NicInfo> { Nic(6, 10), Nic(7, 20), Nic(21, 30) };
var up = NicPriority.PlanMove(ordered, 21, -1);
Check("上移一格取更小跃点", up.Count == 1 && up[0].Index == 21 && up[0].Metric == 19, string.Join(";", up));
var down = NicPriority.PlanMove(ordered, 6, 1);
Check("下移一格取更大跃点", down.Count == 1 && down[0].Index == 6 && down[0].Metric == 21, string.Join(";", down));
var top = NicPriority.PlanMove(ordered, 6, -1);
Check("已在顶部时不动", top.Count == 0, string.Join(";", top));
var bottom = NicPriority.PlanMove(ordered, 21, 1);
Check("已在底部时不动", bottom.Count == 0, string.Join(";", bottom));
var lowPair = new List<NicInfo> { Nic(6, 1), Nic(7, 5) };
var swapLow = NicPriority.PlanMove(lowPair, 7, -1);
Check("对方已是最小跃点则互换", swapLow.Count == 2 && swapLow[0] == (6, 2) && swapLow[1] == (7, 1), string.Join(";", swapLow));
var highPair = new List<NicInfo> { Nic(6, 9998), Nic(7, 9999) };
var swapHigh = NicPriority.PlanMove(highPair, 6, 1);
Check("对方已是最大跃点则互换", swapHigh.Count == 2 && swapHigh[0] == (7, 9998) && swapHigh[1] == (6, 9999), string.Join(";", swapHigh));
var disabledRow = new List<NicInfo> { Nic(6, 10), new NicInfo { Index = 9, Name = "Ethernet 2" } };
var threw = false;
try { NicPriority.PlanMove(disabledRow, 9, -1); } catch (InvalidOperationException) { threw = true; }
Check("已禁用网卡给出提示", threw);

Section("3) 本机网卡原始数据（iphlpapi）");
var adapters = Adapters.Query();
foreach (var adapter in adapters)
{
    Console.WriteLine($"  idx={adapter.Index,-3} guid={adapter.Guid,-40} up={adapter.IsUp,-6} speed={adapter.TransmitLinkSpeed,-12} name={adapter.Name} | {adapter.Description} | {string.Join(",", adapter.Addresses)}");
}
Check("读到至少两块网卡", adapters.Count >= 2, adapters.Count.ToString());
Check("网卡都有接口序号", adapters.All(a => a.Index > 0));

Section("4) 本机设备数据（SetupAPI）");
var devices = DeviceManager.QueryNetworkDevices();
foreach (var device in devices)
{
    Console.WriteLine($"  devinst={device.DevInst,-5} idx={device.Index,-3} disabled={device.IsDisabled,-6} problem={device.HasProblem,-6} virtual={device.IsVirtual,-6} name={device.ConnectionName} | {device.Description} | {device.InstanceId} | {device.Guid}");
}
Check("读到至少两块设备", devices.Count >= 2, devices.Count.ToString());

Section("5) 界面用的合并网卡列表");
var nics = NetworkApi.ListNics();
var position = 0;
foreach (var nic in nics)
{
    position++;
    Console.WriteLine($"  {position} | idx={nic.Index,-3} {nic.Name,-30} {nic.Status,-6} {nic.SpeedText,-10} metric={nic.MetricText,-10} gw={nic.Gateway,-15} ip={nic.Address,-22} {(nic.IsVirtual ? "虚拟" : "物理")} {nic.Description}");
    Console.WriteLine($"        配置网关={(nic.ConfiguredGateway.Length > 0 ? nic.ConfiguredGateway : "—")} 默认路由度量={(nic.DefaultRouteMetric?.ToString() ?? "—")} 仅内网={(nic.IgnoresDefaultRoutes ? "是" : "否")} 跨网卡收发={(nic.WeakHostSend && nic.WeakHostReceive ? "已开启" : nic.WeakHostSend || nic.WeakHostReceive ? "部分" : "未开启")}");
}
Check("列表按跃点升序（空跃点最后）", IsSorted(nics), string.Join(",", nics.Select(n => n.Metric)));
Check("没有回环接口", nics.All(n => n.Index != 1 && !n.Name.StartsWith("Loopback", StringComparison.OrdinalIgnoreCase)));
Check("至少有一块网卡带默认网关", nics.Any(n => n.Gateway.Length > 0), "无");
Check("读到网卡自身配置的默认网关", nics.Any(n => n.ConfiguredGateway.Length > 0), "无");
Check("在用网卡能读到共存设置", nics.Any(n => n.Metric is not null && NetworkApi.TryGetSettings(n.Index) is not null), "无法读取");
Console.WriteLine($"  推荐上网出口：{(NicCoexist.PickExit(nics) is { } picked ? $"{picked.Index} · {picked.Name}" : "（无）")}；当前抢上网出口的网卡：{(nics.Any(n => n.JoinsExitRace) ? string.Join("、", nics.Where(n => n.JoinsExitRace).Select(n => n.Name)) : "（无）")}");

Section("6) 路由表");
var routes = RouteTable.Query();
foreach (var route in routes.Take(12))
{
    Console.WriteLine($"  {route.DestinationText,-20} via {Ipv4.ToText(route.NextHop),-16} if={route.InterfaceIndex,-3} metric={route.Metric}");
}
Check("读到路由表", routes.Count > 0, routes.Count.ToString());
Check("包含默认路由 0.0.0.0/0", routes.Any(r => r.Destination == 0 && r.PrefixLength == 0), "无默认路由");
Check("默认路由网关合法", routes.Where(r => r.PrefixLength == 0).All(r => r.NextHop != 0), "网关为 0");
Check("接口序号均有效", routes.All(r => r.InterfaceIndex > 0), "存在 0");
foreach (var nic in nics.Where(item => item.HasDefaultRoute))
{
    var fromTable = routes.Where(r => r.PrefixLength == 0 && r.NextHop != 0 && r.InterfaceIndex == nic.Index).Select(r => (int)r.Metric).DefaultIfEmpty(-1).Min();
    Check($"idx={nic.Index} 默认路由度量与路由表一致", fromTable == nic.DefaultRouteMetric, $"路由表={fromTable} 读取={nic.DefaultRouteMetric}");
}

Section("7) 持久路由（注册表）");
var persistent = PersistentRoutes.Query();
Console.WriteLine(persistent.Count == 0 ? "  （当前没有持久路由）" : "  " + string.Join("\n  ", persistent));
Check("持久路由条目格式正确", persistent.All(item => item.Count(c => c == ',') >= 3), string.Join(" | ", persistent));

Section("8) 跃点数读取（IPv4 / IPv6）");
foreach (var nic in nics.Where(n => n.Index > 1))
{
    var v4 = NetworkApi.TryGetMetric(nic.Index, 2);
    var v6 = NetworkApi.TryGetMetric(nic.Index, 23);
    Console.WriteLine($"  idx={nic.Index,-3} {nic.Name,-30} IPv4={(v4 is null ? "—" : $"{v4.Value.Metric}{(v4.Value.Automatic ? " 自动" : " 手动")}"),-12} IPv6={(v6 is null ? "—" : $"{v6.Value.Metric}{(v6.Value.Automatic ? " 自动" : " 手动")}")}");
}
Check("至少读到一块网卡的 IPv4 跃点数", nics.Any(n => n.Metric is not null));

Section("9) 运行环境");
Console.WriteLine($"  当前进程管理员权限：{NetworkApi.IsElevated}");
Console.WriteLine($"  配置目录：{AppInfo.ConfigDirectory}");
Console.WriteLine($"  配置文件存在：{File.Exists(AppInfo.ConfigPath)}");
Console.WriteLine($"  已有配置条目：{RouteConfigStore.Load().Routes.Count}");

Section("10) 多网卡共存策略");
var sample = new List<NicInfo>
{
    new() { Index = 6, Name = "以太网", Status = "已连接", IsUp = true, Metric = 100, ConfiguredGateway = "10.40.81.254", DefaultRouteMetric = 100 },
    new() { Index = 8, Name = "VMnet8", Status = "已连接", IsUp = true, Metric = 35, ConfiguredGateway = "192.168.10.1", DefaultRouteMetric = 35, IsVirtual = true },
    new() { Index = 7, Name = "WLAN", Status = "已断开", IsUp = false, Metric = 10, ConfiguredGateway = "192.168.1.1" },
    new() { Index = 12, Name = "蓝牙网络连接", Status = "已断开", IsUp = false, Metric = 65 },
};
var sampleExit = NicCoexist.PickExit(sample);
Check("上网出口选默认路由度量最小的网卡", sampleExit?.Index == 8, sampleExit?.Name ?? "null");
var samplePlan = NicCoexist.Plan(sample, sampleExit?.Index);
Check("出口保留，其它抢出口的网卡改为仅内网、在用网卡开启跨网卡收发", samplePlan.Count == 3
    && samplePlan.Any(s => s.Index == 8 && !s.IgnoreDefaultRoutes && s.EnableWeakHost)
    && samplePlan.Any(s => s.Index == 6 && s.IgnoreDefaultRoutes && s.EnableWeakHost)
    && samplePlan.Any(s => s.Index == 7 && !s.IgnoreDefaultRoutes)
    && samplePlan.All(s => s.Index != 12), string.Join(" | ", samplePlan.Select(s => $"{s.Name}：{s.Summary}")));
var noExitPlan = NicCoexist.Plan(sample, null);
Check("没有出口时只开跨网卡收发，不动默认路由", noExitPlan.Count > 0 && noExitPlan.All(s => !s.IgnoreDefaultRoutes), string.Join(" | ", noExitPlan.Select(s => s.Name)));
var settled = sample.Select(item => new NicInfo
{
    Index = item.Index,
    Name = item.Name,
    Status = item.Status,
    IsUp = item.IsUp,
    Metric = item.Metric,
    ConfiguredGateway = item.ConfiguredGateway,
    DefaultRouteMetric = item.DefaultRouteMetric,
    IsVirtual = item.IsVirtual,
    IgnoresDefaultRoutes = NicCoexist.ShouldGoInternal(item, 8),
    WeakHostSend = true,
    WeakHostReceive = true,
}).ToList();
Check("已经共存时不再产生调整", NicCoexist.Plan(settled, 8).Count == 0, string.Join(" | ", NicCoexist.Plan(settled, 8).Select(s => s.Name)));
Check("已禁用的网卡不参与共存调整", NicCoexist.Plan(sample.Append(new NicInfo { Index = 15, Name = "TAP", IsDisabled = true, Metric = 65, ConfiguredGateway = "10.0.0.1" }).ToList(), 8).All(s => s.Index != 15));

Section("11) 系统里已有的持久路由 / 网卡静态网关");
var (routeCode, routeText) = RouteService.RunProcess("route.exe", new[] { "print", "-4" });
Check("route print 能读取", routeCode == 0, $"exit={routeCode}");
var parsedPersistent = PersistentRoutes.Query();
Console.WriteLine($"  解析到 {parsedPersistent.Count} 条持久路由：{(parsedPersistent.Count == 0 ? "（无）" : string.Join(" | ", parsedPersistent))}");
var fromCommand = routeText.Split('\n')
    .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
    .Count(tokens => tokens.Length == 4 && Ipv4.TryParse(tokens[0], out _) && Ipv4.TryParse(tokens[1], out _) && Ipv4.TryParse(tokens[2], out _));
Check("持久路由条数与 route print 一致", parsedPersistent.Count == fromCommand, $"route print={fromCommand} 解析={parsedPersistent.Count}");
Check("解析出的持久路由都能在 route print 原文里找到", parsedPersistent.All(item => { var parts = item.Split(','); return routeText.Contains(parts[0]) && routeText.Contains(parts[2]); }), string.Join(" | ", parsedPersistent));

var managedRoutes = RouteConfigStore.Load().Routes;
foreach (var raw in AdapterStaticConfigs.Query())
{
    Console.WriteLine($"  [原始网卡配置] {raw.Name,-28} {raw.Guid} dhcp={raw.EnableDhcp} ip={(raw.Address.Length > 0 ? raw.Address : "—")} gw={(raw.Gateway.Length > 0 ? raw.Gateway : "—")} legacy={raw.Legacy}");
}
var systemRoutes = SystemRoutes.Query(managedRoutes);
foreach (var item in systemRoutes)
{
    Console.WriteLine($"  [{(item.Removable ? "可删除" : "网卡属性里改")}] {item.Destination,-16} → {item.Gateway,-16} {item.Source} | {item.Detail}");
}
Check("系统路由条目字段完整", systemRoutes.All(item => item.Destination.Length > 0 && item.Gateway.Length > 0 && item.Source.Length > 0 && item.Detail.Length > 0));
var managedSignatures = managedRoutes.Select(Signature).Where(text => text.Length > 0).ToList();
Check("SRM 管着的路由不会在“系统其它路由”里重复出现", systemRoutes.All(item => !managedSignatures.Contains($"{item.Destination}→{item.Gateway}")), string.Join(" | ", systemRoutes.Select(item => $"{item.Destination}→{item.Gateway}")));

Section("12) 网段地址检测（IP 是不是被占用了）");
Check("网段写法 192.168.15.0/24 能解析", SubnetScan.TryParse("192.168.15.0/24", out var subnetPlan, out _) && subnetPlan.Addresses.Count == 256);
Check("解析出的网段带上网络地址和广播地址", subnetPlan.Addresses[0] == 0xC0A80F00u && subnetPlan.Addresses[^1] == 0xC0A80FFFu);
Check("单地址写法只查一个地址", SubnetScan.TryParse("10.0.0.5", out var singlePlan, out _) && singlePlan.Addresses.Count == 1 && singlePlan.Addresses[0] == 0x0A000005u);
Check("区间写法 192.168.15.1-200 能解析", SubnetScan.TryParse("192.168.15.1-200", out var rangePlan, out _) && rangePlan.Addresses.Count == 200);
Check("区间写反了会自动纠正", SubnetScan.TryParse("192.168.15.200-192.168.15.1", out var reversed, out _) && reversed.Addresses[0] == 0xC0A80F01u);
Check("非法写法会被拒绝", !SubnetScan.TryParse("999.1.1.1/24", out _, out var badError) && badError.Length > 0);
Check("太大的网段会被拒绝（10.0.0.0/8）", !SubnetScan.TryParse("10.0.0.0/8", out _, out _));
Check("主机路由 /32 会提示直接填单个地址", !SubnetScan.TryParse("10.0.0.5/32", out _, out _));

var scanNic = Adapters.Query().FirstOrDefault(item => item.Addresses.Count > 0);
if (scanNic is null || !Ipv4.TryParse(scanNic.Addresses[0], out var scanHost))
{
    Console.WriteLine("  没有可用的 IPv4 网卡，跳过实测。");
}
else
{
    var selfBlock = $"{Ipv4.ToText(scanHost & 0xFFFFFFF0u)}/28";
    if (SubnetScan.TryParse(selfBlock, out var selfPlan, out var selfError))
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var hosts = SubnetScan.ScanAsync(selfPlan, null, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
        watch.Stop();
        foreach (var host in hosts)
        {
            var rtt = host.RttMs is null ? "—" : $"{host.RttMs} ms";
            Console.WriteLine($"  {host.AddressText,-16} {host.State,-10} {host.Mac,-18} {rtt,-9} {host.Note}");
        }
        Check("实测：结果条数和地址数一致", hosts.Count == selfPlan.Addresses.Count);
        Check("实测：能找到本机自己", hosts.Any(item => item.State == SubnetHostState.Self));
        var networkHost = hosts.FirstOrDefault(item => item.Address == selfPlan.NetworkAddress);
        var broadcastHost = hosts.FirstOrDefault(item => item.Address == selfPlan.BroadcastAddress);
        Check("实测：网络地址标成保留", networkHost?.State == SubnetHostState.Reserved, networkHost?.Note ?? string.Empty);
        Check("实测：广播地址标成保留（本机正好是这一段的广播时标成“本机”）",
            broadcastHost?.State is SubnetHostState.Reserved or SubnetHostState.Self, broadcastHost?.Note ?? string.Empty);
        Check("实测：网段被识别成直连（可以走 ARP）", selfPlan.OnLink, selfPlan.InterfaceName);
        Console.WriteLine($"  扫 {selfBlock}（{selfPlan.Addresses.Count} 个地址，走 {selfPlan.InterfaceName}）用了 {watch.ElapsedMilliseconds} ms");
    }
    else
    {
        Check("实测：本机所在 /28 能解析", false, selfError);
    }

    var gatewayText = scanNic.Gateways.FirstOrDefault();
    if (gatewayText is not null && Ipv4.TryParse(gatewayText, out var gatewayHost))
    {
        var gatewayBlock = $"{Ipv4.ToText(gatewayHost & 0xFFFFFFF0u)}/28";
        if (SubnetScan.TryParse(gatewayBlock, out var gatewayPlan, out var gatewayError))
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var hosts = SubnetScan.ScanAsync(gatewayPlan, null, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
            watch.Stop();
            foreach (var host in hosts)
            {
                var rtt = host.RttMs is null ? "—" : $"{host.RttMs} ms";
                Console.WriteLine($"  {host.AddressText,-16} {host.State,-10} {host.Mac,-18} {rtt,-9} {host.Note}");
            }
            var gatewayRow = hosts.FirstOrDefault(item => item.Address == gatewayHost);
            Check("实测：网关被识别出来", gatewayRow?.State == SubnetHostState.Gateway, $"{gatewayText} → {gatewayRow?.State}");
            Check("实测：ARP 探得出被占用的地址", hosts.Any(item => item.State == SubnetHostState.Occupied), string.Join(" | ", hosts.Where(item => item.State == SubnetHostState.Occupied).Select(item => item.AddressText)));
            Console.WriteLine($"  扫 {gatewayBlock}（含网关 {gatewayText}）用了 {watch.ElapsedMilliseconds} ms");
        }
        else
        {
            Check("实测：网关所在 /28 能解析", false, gatewayError);
        }
    }
}

Console.WriteLine();
Console.WriteLine("=== 13) 局域网发现（组播出口） ===");
var discoveryEgress = DiscoveryRoute.ProbeEgressAddress();
var discoveryNic = discoveryEgress == 0
    ? null
    : Adapters.Query().FirstOrDefault(item => item.Addresses.Any(text => Ipv4.TryParse(text, out var parsed) && parsed == discoveryEgress));
Console.WriteLine($"  发往 {DiscoveryRoute.GroupText}（LocalSend 发现用）时系统选择的出口地址：{(discoveryEgress == 0 ? "测不出来" : Ipv4.ToText(discoveryEgress))}（{(discoveryNic?.Name ?? "未知")}）");
Check("探测得到组播出口地址", discoveryEgress != 0, discoveryEgress == 0 ? "网络全断时才会这样" : Ipv4.ToText(discoveryEgress));
var discoveryRow = DiscoveryRoute.Find(0);
Console.WriteLine(discoveryRow is null
    ? "  当前没有指定：由系统按跃点数自动选网卡"
    : $"  已指定：走网卡 #{discoveryRow.InterfaceIndex}（{Adapters.Query().FirstOrDefault(item => item.Index == discoveryRow.InterfaceIndex)?.Name}）");
Check("“局域网发现走哪块网卡”的开关状态可读", true);

Console.WriteLine();
Console.WriteLine("=== 14) 局域网发现（入站防火墙） ===");
DiscoveryFirewall.Status? inbound = null;
try
{
    inbound = DiscoveryFirewall.Query(0, withJoins: true);
}
catch (Exception ex)
{
    Console.WriteLine("  读取失败：" + ex.Message);
}
Check("防火墙规则能读到（只看注册表，不需要管理员）", inbound is { Readable: true });
if (inbound is { Readable: true })
{
    Console.WriteLine($"  发现端口 {DiscoveryRoute.Port} 有没有放行：{(inbound.PortAllowed ? "有" : "没有")}（SRM 自己建的放行规则：{(inbound.OwnRuleReady ? "在" : "不在")}）");
    Console.WriteLine(inbound.BlockingRules.Count == 0
        ? "  会挡住发现包的「阻止」入站规则：没有"
        : $"  会挡住发现包的「阻止」入站规则：{inbound.BlockingRules.Count} 条（挡住 {inbound.BlockedProfilesText} 网络）");
    foreach (var rule in inbound.BlockingRules)
    {
        Console.WriteLine($"    {rule.ProtocolText} · {rule.ProfileText} · {(rule.Program.Length > 0 ? rule.Program : rule.Name)}");
    }
    Console.WriteLine($"  这块机器当前网络位置：{(inbound.ProfileKnown ? inbound.CategoryText : "读不到（自检不带管理员权限，属正常）")}");
    Console.WriteLine(inbound.Joined.Count == 0
        ? "  已经加入发现组的网卡：没有（发现软件没在跑时就是这样）"
        : $"  已经加入发现组的网卡：{string.Join("、", inbound.Joined)}");
    Console.WriteLine("  界面提示：" + inbound.ShortText());
    Check("“要不要修”的判断和读到的东西一致", inbound.NeedsFix == (inbound.BlockingRules.Count > 0 || !inbound.PortAllowed));
    Check("状态能给出说明文字", inbound.ShortText().Length > 0 && inbound.Describe().Length > 0);
}

Console.WriteLine();
Console.WriteLine("=== 15) 局域网发现（出口到底是哪块网卡） ===");
var lookup = DiscoveryProbe.Lookup();
var lookupNic = Adapters.Query().FirstOrDefault(item => item.Addresses.Any(text => Ipv4.TryParse(text, out var parsed) && parsed == lookup.Address));
Console.WriteLine($"  问系统“喊话从哪个地址出去”：{(lookup.Known ? $"{Ipv4.ToText(lookup.Address)}（{lookupNic?.Name ?? "未知"}）" : "测不出来")}");
var bestMulticast = DiscoveryBoost.BestMulticastNic(NetworkApi.ListNics());
Console.WriteLine(bestMulticast.Index == 0
    ? "  按跃点数算“系统最可能挑哪块网卡”：没有可用网卡"
    : $"  按跃点数算“系统最可能挑哪块网卡”：#{bestMulticast.Index}（{Adapters.Query().FirstOrDefault(item => item.Index == bestMulticast.Index)?.Name}，组合跃点 {bestMulticast.Metric}）");
var routeWinner = DiscoveryBoost.DefaultRouteWinner();
Console.WriteLine(routeWinner == 0
    ? "  上网出口（默认路由）：没读到"
    : $"  上网出口（默认路由）：#{routeWinner}（{Adapters.Query().FirstOrDefault(item => item.Index == routeWinner)?.Name}）");
var measuredEgress = DiscoveryProbe.Measure();
Console.WriteLine($"  真发 120 个测试包实测：{(measuredEgress.Known ? $"{Ipv4.ToText(measuredEgress.Address)}（{measuredEgress.Method}）" : "测不出来")}");
Check("出口判定至少给出一个结果", lookup.Known || measuredEgress.Known, "网络全断时才会这样");
Check("“组播出口要按跃点数挑”的算法能算出结果", bestMulticast.Index != 0 || NetworkApi.ListNics().All(item => item.IsDisabled || item.Metric is null));
var groupRoutes = DiscoveryRoute.Query();
Console.WriteLine(groupRoutes.Count == 0
    ? "  当前有没有指定“走哪块网卡”：没有（系统自己挑）"
    : "  " + string.Join("；", groupRoutes.Select(row => $"224.0.0.167/32 走 #{row.InterfaceIndex}（跃点 {row.Metric}）")));
Check("同一个组播地址最多只有一条路由（旧的会被清掉）", groupRoutes.Count <= 1, $"现在有 {groupRoutes.Count} 条");

Console.WriteLine();
Console.WriteLine("=== 16) 替这台机器喊话（用 LocalSend 的身份，不靠系统挑网卡） ===");
var shoutIdentity = LocalSendShout.ReadIdentity();
Console.WriteLine(shoutIdentity.Found
    ? $"  从 LocalSend 的设置里读到身份：设备名「{shoutIdentity.Alias}」· 端口 {shoutIdentity.Port} · 证书指纹 "
        + (shoutIdentity.Fingerprint.Length > 0
            ? shoutIdentity.Fingerprint[..Math.Min(16, shoutIdentity.Fingerprint.Length)] + "…"
            : "（没有）")
    : $"  没读到 LocalSend 的设置：{shoutIdentity.Problem}");
if (shoutIdentity.Found)
{
    Check("读得到 LocalSend 的设备名", shoutIdentity.Alias.Length > 0);
    Check("读得到 LocalSend 的证书指纹（对面才认得出来）", shoutIdentity.Fingerprint.Length > 0, shoutIdentity.Problem);
}

var shoutPayload = LocalSendShout.BuildPayload(new LocalSendShout.Identity
{
    Found = true,
    Alias = "自检用设备",
    Fingerprint = new string('A', 64),
    Port = DiscoveryRoute.Port,
});
var shoutFormatOk = false;
try
{
    using var shoutDoc = System.Text.Json.JsonDocument.Parse(shoutPayload);
    var shoutRoot = shoutDoc.RootElement;
    shoutFormatOk = shoutRoot.TryGetProperty("alias", out var shoutAlias) && shoutAlias.GetString() == "自检用设备"
        && shoutRoot.TryGetProperty("version", out _)
        && shoutRoot.TryGetProperty("deviceModel", out _)
        && shoutRoot.TryGetProperty("deviceType", out _)
        && shoutRoot.TryGetProperty("fingerprint", out _)
        && shoutRoot.TryGetProperty("port", out var shoutPort) && shoutPort.GetInt32() == DiscoveryRoute.Port
        && shoutRoot.TryGetProperty("protocol", out _)
        && shoutRoot.TryGetProperty("download", out _)
        && shoutRoot.TryGetProperty("announce", out var shoutAnnounce) && shoutAnnounce.GetBoolean();
}
catch
{
    shoutFormatOk = false;
}
Check("喊话内容符合发现软件的格式（alias/version/deviceModel/deviceType/fingerprint/port/protocol/download/announce）", shoutFormatOk, shoutPayload);

var shoutNic = NetworkApi.ListNics().FirstOrDefault(item => !item.IsDisabled && item.Index > 1 && item.Address.Length > 0);
if (shoutNic is not null)
{
    var shoutAddress = shoutNic.Address
        .Split(" / ", StringSplitOptions.RemoveEmptyEntries)
        .Select(text => text.Trim())
        .FirstOrDefault(text => Ipv4.TryParse(text, out _)) ?? string.Empty;
    var shoutMine = shoutIdentity.Ready
        ? shoutIdentity
        : new LocalSendShout.Identity { Found = true, Alias = "SRM 自检", Fingerprint = new string('A', 64), Port = DiscoveryRoute.Port };
    var shouted = LocalSendShout.Send(shoutMine, shoutNic.Index, shoutAddress, 2, 100);
    Console.WriteLine($"  从 {shoutAddress}（网卡 #{shoutNic.Index} · {shoutNic.Name}）喊了 {shouted.Sent} 次；"
        + (shouted.PacketDelta >= 0 ? $"这块网卡的组播包计数 +{shouted.PacketDelta}" : "读不到计数"));
    Check("喊话能发出去（不报错）", shouted.Sent == 2, shouted.Problem);
    Check("喊的是“我在这里”，不是别的消息", shouted.Payload.Contains("\"announce\":true", StringComparison.Ordinal), shouted.Payload);
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "全部通过" : $"失败 {failures} 项");
return failures == 0 ? 0 : 1;

static bool IsSorted(List<NicInfo> list)
{
    var last = int.MinValue;
    foreach (var item in list)
    {
        var value = item.Metric ?? int.MaxValue;
        if (value < last) return false;
        last = value;
    }
    return true;
}

static string Signature(RouteEntry route) =>
    Ipv4.TryParseSubnet(route.Destination, out var network, out var mask)
        ? $"{Ipv4.ToText(network)}/{Ipv4.PrefixFromMask(mask)}→{route.Gateway}"
        : string.Empty;
