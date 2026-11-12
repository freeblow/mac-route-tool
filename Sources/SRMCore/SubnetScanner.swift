import Foundation

/// 扫描探测动作抽象，测试注入假实现。
public protocol ScanProber: Sendable {
    /// 向目标发一个触发 ARP 解析的数据报。返回是否已发送。
    func triggerArp(target: UInt32, sourceIP: String) -> Bool
    /// 读取系统 ARP 表（已按接口过滤），键为地址，值为 MAC。
    func arpTable(interface: String) -> [UInt32: String]
    /// ping 目标，返回 RTT（ms），无响应返回 nil。
    func ping(target: UInt32, sourceIP: String?, timeoutMs: Int) -> Double?
}

/// 子网扫描引擎：在线链路目标用「UDP 触发 ARP + 读 ARP 表」两轮判定占用，
/// 离链路目标用并发 ping；保留/本机/网关地址预先分类。
public final class SubnetScanner: @unchecked Sendable {
    /// ARP 等待轮次（毫秒），对应 Windows 版 1200/1000 两轮。
    static let arpWaits = [1200, 1000]
    static let gatewayPingTimeoutMs = 800
    static let offLinkPingTimeoutMs = 900

    public let target: SubnetScanTarget
    private let prober: ScanProber
    private let routes: [KernelRoute]
    private let interfaces: [NetInterface]
    private let concurrency: Int

    private let lock = NSLock()
    private var cancelledFlag = false
    private var interfaceUsedFlag: String?

    public var cancelled: Bool {
        lock.lock(); defer { lock.unlock() }
        return cancelledFlag
    }

    public func cancel() {
        lock.lock(); cancelledFlag = true; lock.unlock()
    }

    public var interfaceUsed: String? {
        lock.lock(); defer { lock.unlock() }
        return interfaceUsedFlag
    }

    public init(target: SubnetScanTarget, prober: ScanProber, routes: [KernelRoute],
                interfaces: [NetInterface], concurrency: Int = 64) {
        self.target = target
        self.prober = prober
        self.routes = routes
        self.interfaces = interfaces
        self.concurrency = concurrency
    }

    struct OnLinkInfo {
        let interfaceName: String
        let sourceIP: String
    }

    /// 最长前缀的在线链路路由覆盖目标 → 源地址 + 接口；找不到视为离链路。
    func locateOnLink() -> OnLinkInfo? {
        guard let probeAddress = target.addresses.first else { return nil }
        var best: (interfaceName: String, prefix: Int)?
        for route in routes where route.isUp && !route.isGateway {
            guard let name = route.interfaceName else { continue }
            let parts = route.destination.split(separator: "/")
            guard parts.count == 2,
                  let net = IPv4.parse(String(parts[0])),
                  let prefix = Int(parts[1]) else { continue }
            guard IPv4.network(probeAddress, prefix: prefix) == net else { continue }
            if best == nil || prefix > best!.prefix { best = (name, prefix) }
        }
        guard let link = best,
              let cidr = interfaces.first(where: { $0.name == link.interfaceName })?.primaryIPv4,
              let slash = cidr.lastIndex(of: "/") else { return nil }
        lock.lock()
        interfaceUsedFlag = link.interfaceName
        lock.unlock()
        return OnLinkInfo(interfaceName: link.interfaceName, sourceIP: String(cidr[..<slash]))
    }

    /// 执行扫描，返回按地址升序排列的结果行。
    public func run(progress: @escaping @Sendable (Int, Int) -> Void) -> [ScanRow] {
        let link = locateOnLink()

        let selfAddrs: Set<UInt32> = Set(interfaces.flatMap { interface in
            interface.ipv4Addresses.compactMap { cidr -> UInt32? in
                guard let slash = cidr.lastIndex(of: "/") else { return nil }
                return IPv4.parse(String(cidr[..<slash]))
            }
        })
        let gatewayAddrs: Set<UInt32> = Set(routes
            .filter { $0.destination == "0.0.0.0/0" && $0.isGateway }
            .compactMap { IPv4.parse($0.gateway) })
        // 网络/广播地址预先标记为保留（不探测）。
        var reserved: Set<UInt32> = []
        if case .subnet(let prefix) = target.kind, let base = target.addresses.first {
            let size = UInt64(1) << UInt64(32 - prefix)
            reserved = [base, base + UInt32(size) - 1]
        }

        // 需要探测的地址 = 全部 − 保留 − 本机
        let probeAddresses = target.addresses.filter { !reserved.contains($0) && !selfAddrs.contains($0) }

        var results: [UInt32: ScanRow] = [:]
        for address in target.addresses where reserved.contains(address) {
            results[address] = ScanRow(address: IPv4.string(address), state: .reserved, note: "网络/广播地址")
        }
        for address in target.addresses where selfAddrs.contains(address) {
            results[address] = ScanRow(address: IPv4.string(address), state: .selfHost, note: "本机")
        }

        let total = target.addresses.count
        var done = results.count
        let queue = DispatchQueue(label: "srm.scan", attributes: .concurrent)
        let semaphore = DispatchSemaphore(value: max(1, concurrency))
        let group = DispatchGroup()

        for address in probeAddresses {
            if cancelled { break }
            group.enter()
            semaphore.wait()
            queue.async { [self] in
                defer { semaphore.signal(); group.leave() }
                let row = self.probe(address, link: link, gatewayAddrs: gatewayAddrs)
                self.lock.lock()
                results[address] = row
                done += 1
                let (currentDone, currentTotal) = (done, total)
                self.lock.unlock()
                progress(currentDone, currentTotal)
            }
        }
        group.wait()

        return target.addresses.compactMap { results[$0] }
            .sorted { (IPv4.parse($0.address) ?? 0) < (IPv4.parse($1.address) ?? 0) }
    }

    func probe(_ address: UInt32, link: OnLinkInfo?, gatewayAddrs: Set<UInt32>) -> ScanRow {
        let text = IPv4.string(address)
        let isGateway = gatewayAddrs.contains(address)

        if let link {
            var mac: String?
            for waitMs in Self.arpWaits {
                if cancelled { break }
                _ = prober.triggerArp(target: address, sourceIP: link.sourceIP)
                Thread.sleep(forTimeInterval: Double(waitMs) / 1000.0)
                if let found = prober.arpTable(interface: link.interfaceName)[address] { mac = found; break }
            }
            if isGateway {
                let rtt = cancelled ? nil : prober.ping(target: address, sourceIP: link.sourceIP, timeoutMs: Self.gatewayPingTimeoutMs)
                return ScanRow(address: text, state: .gateway, mac: mac, rttMs: rtt,
                               note: rtt == nil ? "网关（不响应 Ping）" : nil)
            }
            if let mac {
                return ScanRow(address: text, state: .occupied, mac: mac, note: "ARP 响应")
            }
            return ScanRow(address: text, state: .free, note: "两轮 ARP 无响应")
        }

        if let rtt = prober.ping(target: address, sourceIP: nil, timeoutMs: Self.offLinkPingTimeoutMs) {
            return ScanRow(address: text, state: isGateway ? .gateway : .occupied, rttMs: rtt,
                           note: isGateway ? nil : "Ping 响应")
        }
        return ScanRow(address: text, state: isGateway ? .gateway : .unknown,
                       note: isGateway ? "网关（不响应 Ping）" : "无法判定（离链路且无 Ping 响应）")
    }

    public static func summarize(rows: [ScanRow], interfaceUsed: String?, elapsed: TimeInterval) -> ScanSummary {
        func count(_ state: ScanRow.State) -> Int { rows.filter { $0.state == state }.count }
        let rounded = (elapsed * 10).rounded() / 10
        return ScanSummary(
            freeCount: count(.free),
            occupiedCount: count(.occupied),
            gatewaySelfCount: count(.gateway) + count(.selfHost),
            reservedCount: count(.reserved),
            unknownCount: count(.unknown),
            interfaceUsed: interfaceUsed,
            elapsedSeconds: rounded
        )
    }
}
