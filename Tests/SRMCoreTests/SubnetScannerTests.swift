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
        let scanner = SubnetScanner(target: target, prober: prober, routes: makeRoutes(), interfaces: interfaces)
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
