import Darwin
import Foundation

/// 系统命令实现的探测器：UDP 触发 ARP、读 `arp -a`、`/sbin/ping`。
public struct SystemScanProber: ScanProber {
    public static let arpTriggerPort: UInt16 = 33434
    static let arpPath = "/usr/sbin/arp"
    static let pingPath = "/sbin/ping"

    public init() {}

    public func triggerArp(target: UInt32, sourceIP: String) -> Bool {
        Self.udpSendOneByte(sourceIP: sourceIP, targetIP: IPv4.string(target), port: Self.arpTriggerPort)
    }

    public func arpTable(interface: String) -> [UInt32: String] {
        guard let result = try? ProcessRunner.run(Self.arpPath, ["-a", "-i", interface]), result.success else {
            return [:]
        }
        return Self.parseARP(result.stdout)
    }

    public func ping(target: UInt32, sourceIP: String?, timeoutMs: Int) -> Double? {
        var arguments = ["-n", "-c", "1", "-W", String(timeoutMs)]
        if let sourceIP { arguments += ["-S", sourceIP] }
        arguments.append(IPv4.string(target))
        guard let result = try? ProcessRunner.run(Self.pingPath, arguments), result.success else { return nil }
        return Self.parsePingLatency(result.stdout)
    }

    /// 从 `arp -a` 输出提取 地址 → 小写 MAC。
    public static func parseARP(_ text: String) -> [UInt32: String] {
        var table: [UInt32: String] = [:]
        guard let regex = try? NSRegularExpression(
            pattern: #"\((\d{1,3}(?:\.\d{1,3}){3})\) at ([0-9a-fA-F:]+) on (\S+)"#) else { return [:] }
        let range = NSRange(text.startIndex..., in: text)
        for match in regex.matches(in: text, range: range) {
            guard let addressRange = Range(match.range(at: 1), in: text),
                  let macRange = Range(match.range(at: 2), in: text) else { continue }
            let address = String(text[addressRange])
            let mac = String(text[macRange]).lowercased()
            guard let raw = IPv4.parse(address) else { continue }
            table[raw] = mac
        }
        return table
    }

    public static func parsePingLatency(_ text: String) -> Double? {
        guard let range = text.range(of: #"time[=<]([0-9.]+)\s*ms"#, options: .regularExpression) else { return nil }
        let matched = String(text[range])
        guard let numberRange = matched.range(of: #"[0-9.]+"#, options: .regularExpression),
              let value = Double(matched[numberRange]) else { return nil }
        return value
    }

    /// 绑定源地址后向 target:port 发 1 字节 UDP，迫使内核对在线链路目标发起 ARP。
    static func udpSendOneByte(sourceIP: String, targetIP: String, port: UInt16) -> Bool {
        let fd = socket(AF_INET, SOCK_DGRAM, 0)
        guard fd >= 0 else { return false }
        defer { close(fd) }

        var local = sockaddr_in()
        local.sin_family = sa_family_t(AF_INET)
        local.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        local.sin_port = 0
        guard inet_pton(AF_INET, sourceIP, &local.sin_addr) == 1 else { return false }
        let bindResult = withUnsafePointer(to: &local) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard bindResult == 0 else { return false }

        var remote = sockaddr_in()
        remote.sin_family = sa_family_t(AF_INET)
        remote.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        remote.sin_port = port.bigEndian
        guard inet_pton(AF_INET, targetIP, &remote.sin_addr) == 1 else { return false }
        let connectResult = withUnsafePointer(to: &remote) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                connect(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard connectResult == 0 else { return false }
        let byte = UInt8(0x01)
        let sent = withUnsafePointer(to: byte) { send(fd, $0, 1, 0) }
        return sent == 1
    }
}
