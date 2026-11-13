import XCTest
@testable import SRMCore

final class SubnetScannerTests: XCTestCase {
    /// 可编程假探测器：按 target 行为表返回。
    final class FakeProber: ScanProber, @unchecked Sendable {
        var arpTargets: Set<UInt32> = []           // 会出现在 ARP 表中的地址
        var arpWaitsConsumed: [UInt32: Int] = [:]  // 记录每目标 ARP 轮次
        var pingable: Set<UInt32> = []
        var pingRTT: Double = 3.0

        func triggerArp(target: UInt32, sourceIP: String) -> Bool { true }

        func arpTable(interface: String) -> [UInt32: String] {
            var table: [UInt32: String] = [:]
            for target in arpTargets { table[target] = "aa:bb:cc:dd:ee:ff" }
            return table
        }

        func ping(target: UInt32, sourceIP: String?, timeoutMs: Int) -> Double? {
            pingable.contains(target) ? pingRTT : nil
        }
    }

    private func makeInterfaces(_ name: String = "en0", cidr: String = "192.168.15.10/24") -> [NetInterface] {
        [NetInterface(name: name, ipv4Addresses: [cidr], isUp: true, isRunning: true,
                      isLoopback: false, hardwareAddress: nil, displayName: nil)]
    }

    private func makeRoutes() -> [KernelRoute] {
        [
            KernelRoute(destination: "0.0.0.0/0", gateway: "192.168.15.1", flags: "UGScg", interfaceName: "en0", expire: nil),
            KernelRoute(destination: "192.168.15.0/24", gateway: "link#9", flags: "UC", interfaceName: "en0", expire: nil),
        ]
    }

    func testReservedSelfGatewayClassification() {
        let prober = FakeProber()
        prober.pingable = [IPv4.parse("192.168.15.1")!]
        let target = SubnetScanTarget.parse("192.168.15.0/30")!  // .0 .1 .2 .3
        let interfaces = makeInterfaces(cidr: "192.168.15.2/24") // 本机 = .2
        let scanner = SubnetScanner(target: target, prober: prober, routes: makeRoutes(), interfaces: interfaces)
        let rows = scanner.run(progress: { _, _ in })
        XCTAssertEqual(rows.count, 4)
        XCTAssertEqual(rows.first { $0.address == "192.168.15.0" }?.state, .reserved)
        XCTAssertEqual(rows.first { $0.address == "192.168.15.1" }?.state, .gateway)
        XCTAssertEqual(rows.first { $0.address == "192.168.15.1" }?.rttMs, 3.0)
        XCTAssertEqual(rows.first { $0.address == "192.168.15.2" }?.state, .selfHost)
        // .3 /30 广播地址 → 保留（预分类，不探测）
        XCTAssertEqual(rows.first { $0.address == "192.168.15.3" }?.state, .reserved)
        XCTAssertEqual(scanner.interfaceUsed, "en0")
    }

    func testOccupiedViaARP() {
        let prober = FakeProber()
        prober.arpTargets = [IPv4.parse("192.168.15.3")!]
        let target = SubnetScanTarget.parse("192.168.15.2-3")!
        let interfaces = makeInterfaces(cidr: "192.168.15.1/24")
        let scanner = SubnetScanner(target: target, prober: prober, routes: makeRoutes(), interfaces: interfaces)
        let rows = scanner.run(progress: { _, _ in })
        let row = rows.first { $0.address == "192.168.15.3" }
        XCTAssertEqual(row?.state, .occupied)
        XCTAssertEqual(row?.mac, "aa:bb:cc:dd:ee:ff")
    }

    func testOffLinkPingClassification() {
        let prober = FakeProber()
        prober.pingable = [IPv4.parse("10.1.2.3")!]
        let target = SubnetScanTarget.parse("10.1.2.3-4")!
        let interfaces = makeInterfaces(cidr: "192.168.15.1/24")
        // 路由表没有覆盖 10.1.2.x 的在线链路路由 → 离链路纯 ping
        let scanner = SubnetScanner(target: target, prober: prober, routes: makeRoutes(), interfaces: interfaces)
        let rows = scanner.run(progress: { _, _ in })
        XCTAssertEqual(rows.first { $0.address == "10.1.2.3" }?.state, .occupied)
        XCTAssertEqual(rows.first { $0.address == "10.1.2.3" }?.rttMs, 3.0)
        XCTAssertEqual(rows.first { $0.address == "10.1.2.4" }?.state, .unknown)
        XCTAssertNil(scanner.interfaceUsed)
    }

    func testCancelStopsEarly() {
        let prober = FakeProber()
        let target = SubnetScanTarget.parse("192.168.15.1-200")!
        let interfaces = makeInterfaces(cidr: "192.168.15.1/24")
        // 并发度必须远小于 GCD 线程池上限：默认 64 个探测同时 Thread.sleep 会
        // 饿死全局线程池，令 asyncAfter 的 cancel 闭包延迟到探测结束后才执行
        // （测试环境伪影，非产品缺陷——生产环境 cancel 由主线程调用）。
        let scanner = SubnetScanner(target: target, prober: prober, routes: makeRoutes(),
                                    interfaces: interfaces, concurrency: 4)
        DispatchQueue.global().asyncAfter(deadline: .now() + 0.1) { scanner.cancel() }
        let rows = scanner.run(progress: { _, _ in })
        XCTAssertFalse(rows.isEmpty)
        XCTAssertLessThan(rows.count, target.addresses.count)  // 取消后提前结束
        // 取消后不应伪造「空闲」：未完成的探测一律判 unknown（FakeProber 无 ARP
        // 响应，未取消时全为 free，故出现 free 即为取消路径伪造）。
        XCTAssertFalse(rows.contains { $0.state == .free },
                       "取消后未完成的探测不应被标记为空闲")
    }

    func testRowsAreSortedByAddress() {
        let prober = FakeProber()
        let target = SubnetScanTarget.parse("192.168.15.1-10")!
        let interfaces = makeInterfaces(cidr: "192.168.15.1/24")
        let scanner = SubnetScanner(target: target, prober: prober, routes: makeRoutes(), interfaces: interfaces)
        let rows = scanner.run(progress: { _, _ in })
        let addresses = rows.compactMap { IPv4.parse($0.address) }
        XCTAssertEqual(addresses, addresses.sorted())
    }
}

final class SystemScanProberTests: XCTestCase {
    func testParseARP() {
        let sample = """
        ? (192.168.15.1) at a4:83:e7:1a:2b:3c on en0 ifscope [ethernet]
        ? (192.168.15.66) at aa:bb:cc:dd:ee:ff on en0 ifscope [ethernet]
        ? (192.168.15.77) at (incomplete) on en0 ifscope [ethernet]
        ? (10.40.81.3) at 11-22-33-44-55-66 on en1 ifscope [ethernet]
        """
        let table = SystemScanProber.parseARP(sample)
        XCTAssertEqual(table[IPv4.parse("192.168.15.1")!], "a4:83:e7:1a:2b:3c")
        XCTAssertEqual(table[IPv4.parse("192.168.15.66")!], "aa:bb:cc:dd:ee:ff")
        XCTAssertNil(table[IPv4.parse("192.168.15.77")!])   // incomplete 不算
        XCTAssertNil(table[IPv4.parse("10.40.81.3")!])      // 连字符 MAC 格式不符合输出，跳过
    }

    /// arpTable 用 `arp -an`（全表毫秒级）+ 按接口过滤，替代 30 秒量级的 `arp -a -i`。
    func testParseARPInterfaceFilter() {
        let sample = """
        ? (192.168.15.1) at a4:83:e7:1a:2b:3c on en0 ifscope [ethernet]
        ? (10.40.81.2) at 6c:1f:f7:6:a4:e8 on en1 ifscope [ethernet]
        """
        let en0 = SystemScanProber.parseARP(sample, interface: "en0")
        XCTAssertEqual(en0.count, 1)
        XCTAssertEqual(en0[IPv4.parse("192.168.15.1")!], "a4:83:e7:1a:2b:3c")
        let en1 = SystemScanProber.parseARP(sample, interface: "en1")
        XCTAssertEqual(en1.count, 1)
        XCTAssertEqual(en1[IPv4.parse("10.40.81.2")!], "6c:1f:f7:6:a4:e8")
        XCTAssertNil(SystemScanProber.parseARP(sample, interface: "en5")[IPv4.parse("192.168.15.1")!])
    }

    func testParsePingLatency() {
        XCTAssertEqual(SystemScanProber.parsePingLatency("64 bytes from 10.0.0.1: icmp_seq=0 ttl=64 time=12.345 ms"), 12.345)
        XCTAssertNil(SystemScanProber.parsePingLatency("Request timeout for icmp_seq 0"))
    }
}

final class NICManagerTests: XCTestCase {
    let serviceOrderSample = """
    An asterisk (*) denotes that a network service is disabled.
    (1) Wi-Fi
    (Hardware Port: Wi-Fi, Device: en0)
    (2) Ethernet
    (Hardware Port: Ethernet, Device: en5)
    (3) Thunderbolt Bridge
    (Hardware Port: Thunderbolt Bridge, Device: bridge0)
    """

    let serviceNamesSample = """
    An asterisk (*) denotes that a network service is disabled.
    Wi-Fi
    Ethernet
    *USB 10/100 LAN
    Tailscale
    """

    let ifconfigSample = """
    en0: flags=8863<UP,BROADCAST,SMART,RUNNING,SIMPLEX,MULTICAST> mtu 1500
        media: autoselect (1000baseT <full-duplex,energy-efficient>)
    en5: flags=8822<BROADCAST,SMART,SIMPLEX,MULTICAST> mtu 1500
        media: autoselect (<unknown subtype>)
    bridge0: flags=8863<UP,BROADCAST,SMART,RUNNING,SIMPLEX,MULTICAST> mtu 1500
        media: <unknown type>
    utun3: flags=8050<RUNNING,POINTOPOINT,MULTICAST> mtu 1380
    """

    func testParseServiceOrder() {
        let parsed = NICManager.parseServiceOrder(serviceOrderSample)
        XCTAssertEqual(parsed.count, 3)
        XCTAssertEqual(parsed[0].name, "Wi-Fi")
        XCTAssertEqual(parsed[0].device, "en0")
        XCTAssertEqual(parsed[2].name, "Thunderbolt Bridge")
        XCTAssertEqual(parsed[2].device, "bridge0")
    }

    func testParseServiceNames() {
        let parsed = NICManager.parseServiceNames(serviceNamesSample)
        XCTAssertEqual(parsed.count, 4)
        XCTAssertEqual(parsed[0].name, "Wi-Fi")
        XCTAssertFalse(parsed[0].disabled)
        XCTAssertEqual(parsed[2].name, "USB 10/100 LAN")
        XCTAssertTrue(parsed[2].disabled)
    }

    func testParseIfconfigMedia() {
        let parsed = NICManager.parseIfconfigMedia(ifconfigSample)
        XCTAssertEqual(parsed["en0"], "1 Gbps")
        XCTAssertNil(parsed["en5"])     // unknown subtype
        XCTAssertNil(parsed["bridge0"]) // unknown type
        XCTAssertNil(parsed["utun3"])   // 没有 media 行
    }

    func testSpeedText() {
        XCTAssertEqual(NICManager.speedText(fromMediaSubtype: "10baseT"), "10 Mbps")
        XCTAssertEqual(NICManager.speedText(fromMediaSubtype: "100baseTX"), "100 Mbps")
        XCTAssertEqual(NICManager.speedText(fromMediaSubtype: "1000baseT"), "1 Gbps")
        XCTAssertEqual(NICManager.speedText(fromMediaSubtype: "2500baseT"), "2.5 Gbps")
        XCTAssertEqual(NICManager.speedText(fromMediaSubtype: "10GbaseT"), "10 Gbps")
        XCTAssertNil(NICManager.speedText(fromMediaSubtype: "<unknown>"))
    }

    func testVirtualDeviceNames() {
        XCTAssertTrue(NICManager.isVirtualDeviceName("bridge0"))
        XCTAssertTrue(NICManager.isVirtualDeviceName("utun3"))
        XCTAssertTrue(NICManager.isVirtualDeviceName("awdl0"))
        XCTAssertTrue(NICManager.isVirtualDeviceName("llw0"))
        XCTAssertTrue(NICManager.isVirtualDeviceName("vlan0"))
        XCTAssertFalse(NICManager.isVirtualDeviceName("en0"))
        XCTAssertFalse(NICManager.isVirtualDeviceName("en5"))
    }

    func testDefaultGateways() {
        let routes = [
            KernelRoute(destination: "0.0.0.0/0", gateway: "192.168.15.1", flags: "UGScg", interfaceName: "en0", expire: nil),
            KernelRoute(destination: "0.0.0.0/0", gateway: "link#12", flags: "UGScIg", interfaceName: "en5", expire: nil),
            KernelRoute(destination: "192.168.15.0/24", gateway: "link#9", flags: "UC", interfaceName: "en0", expire: nil),
        ]
        let gateways = NICManager.defaultGateways(routes)
        XCTAssertEqual(gateways["en0"], "192.168.15.1")
        XCTAssertNil(gateways["en5"])  // link#12 不是 IP
        XCTAssertEqual(gateways.count, 1)
    }

    /// 兼容性：旧版守护进程载荷缺少 ipv6Addresses 字段时，解码必须得到 nil 而不是失败。
    func testNetInterfaceDecodesOldPayloadWithoutIPv6() throws {
        let oldPayload = """
        {"name":"en0","ipv4Addresses":["192.168.15.5/24"],"isUp":true,"isRunning":true,"isLoopback":false,"hardwareAddress":null,"displayName":null}
        """
        let decoded = try JSONDecoder().decode(NetInterface.self, from: Data(oldPayload.utf8))
        XCTAssertEqual(decoded.name, "en0")
        XCTAssertNil(decoded.ipv6Addresses)
    }
}

final class DiscoveryFirewallTests: XCTestCase {
    let sampleConf = """
    scrub-anchor "com.apple/*"
    nat-anchor "com.apple/*"
    anchor "com.apple/*"
    load anchor "com.apple" from "/etc/pf.anchors/com.apple"
    """

    func testAnchorRulesContainPortAndProtocols() {
        let rules = DiscoveryFirewall.anchorRules()
        XCTAssertTrue(rules.contains("53317"))
        XCTAssertTrue(rules.contains("proto tcp"))
        XCTAssertTrue(rules.contains("proto udp"))
        XCTAssertTrue(rules.contains("pass in"))
    }

    /// pfctl 要求规则文件以换行结尾：Swift 多行字面量不带结尾换行，缺了它
    /// pfctl 报 syntax error（行号还错位到 pf.conf 上，曾让「放行防火墙」失败）。
    func testAnchorRulesEndsWithNewline() {
        XCTAssertTrue(DiscoveryFirewall.anchorRules().hasSuffix("\n"))
    }

    func testPfConfWithAnchor() {
        let updated = DiscoveryFirewall.pfConfWithAnchor(sampleConf)
        XCTAssertNotNil(updated)
        XCTAssertTrue(updated!.contains("anchor \"com.srm.localsend\""))
        XCTAssertTrue(updated!.contains("load anchor \"com.srm.localsend\""))
        XCTAssertTrue(updated!.hasPrefix("scrub-anchor"))  // 原有内容保留在最前
    }

    func testPfConfWithAnchorIsIdempotent() {
        let once = DiscoveryFirewall.pfConfWithAnchor(sampleConf)!
        XCTAssertNil(DiscoveryFirewall.pfConfWithAnchor(once))
    }

    func testPfConfWithoutAnchor() {
        let withAnchor = DiscoveryFirewall.pfConfWithAnchor(sampleConf)!
        let removed = DiscoveryFirewall.pfConfWithoutAnchor(withAnchor)
        XCTAssertFalse(removed.contains("com.srm.localsend"))
        XCTAssertTrue(removed.contains("com.apple/*"))  // 原有内容不动
        let removedAgain = DiscoveryFirewall.pfConfWithoutAnchor(sampleConf)
        XCTAssertEqual(removedAgain, sampleConf)  // 没有锚点时原样返回
    }
}

final class DiscoveryProbesTests: XCTestCase {
    func testParseAlias() {
        let json = Data(#"{"alias": "张三的 MacBook", "port": 53317}"#.utf8)
        XCTAssertEqual(DiscoveryShout.parseAlias(json), "张三的 MacBook")
        XCTAssertNil(DiscoveryShout.parseAlias(Data("not json".utf8)))
        XCTAssertNil(DiscoveryShout.parseAlias(Data(#"{"noalias": 1}"#.utf8)))
        XCTAssertNil(DiscoveryShout.parseAlias(Data(#"{"alias": ""}"#.utf8)))
    }

    func testAnnouncePayloadRoundTrip() {
        let payload = DiscoveryShout.announcePayload(alias: "测试机")
        XCTAssertEqual(DiscoveryShout.parseAlias(payload), "测试机")
    }
}
