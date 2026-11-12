import Darwin
import Foundation

/// A network interface reported by the kernel.
public struct NetInterface: Identifiable, Hashable, Codable, Sendable {
    public var name: String
    /// IPv4 addresses in CIDR form.
    public var ipv4Addresses: [String]
    public var isUp: Bool
    public var isRunning: Bool
    public var isLoopback: Bool
    public var hardwareAddress: String?
    /// Human readable name (e.g. "Wi-Fi", "Ethernet").
    public var displayName: String?
    /// IPv6 地址（CIDR 形式）。可选：旧版守护进程响应缺该字段时解码为 nil。
    public var ipv6Addresses: [String]?

    public var id: String { name }

    public var isUsable: Bool { isUp && isRunning && !ipv4Addresses.isEmpty }

    public var primaryIPv4: String? { ipv4Addresses.first }

    public var subtitle: String {
        if let address = primaryIPv4 { return "\(name) · \(address)" }
        return name
    }
}

public enum NetworkInterfaces {
    public static func all() -> [NetInterface] {
        var pointer: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&pointer) == 0, let head = pointer else { return [] }
        defer { freeifaddrs(pointer) }

        var byName: [String: NetInterface] = [:]

        var cursor: UnsafeMutablePointer<ifaddrs>? = head
        while let current = cursor {
            let entry = current.pointee
            cursor = current.pointee.ifa_next

            let name = String(cString: entry.ifa_name)
            var interface = byName[name] ?? NetInterface(
                name: name,
                ipv4Addresses: [],
                isUp: (entry.ifa_flags & UInt32(IFF_UP)) != 0,
                isRunning: (entry.ifa_flags & UInt32(IFF_RUNNING)) != 0,
                isLoopback: (entry.ifa_flags & UInt32(IFF_LOOPBACK)) != 0,
                hardwareAddress: nil,
                displayName: nil,
                ipv6Addresses: []
            )

            if let address = entry.ifa_addr, let netmask = entry.ifa_netmask {
                if address.pointee.sa_family == UInt8(AF_INET) {
                    let addr = address.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { $0.pointee }
                    let mask = netmask.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { $0.pointee }
                    let rawAddress = UInt32(bigEndian: addr.sin_addr.s_addr)
                    let rawMask = UInt32(bigEndian: mask.sin_addr.s_addr)
                    let prefix = rawMask == 0 ? 0 : rawMask.nonzeroBitCount
                    interface.ipv4Addresses.append("\(IPv4.string(rawAddress))/\(prefix)")
                }
                if address.pointee.sa_family == UInt8(AF_INET6) {
                    let addr6 = address.withMemoryRebound(to: sockaddr_in6.self, capacity: 1) { $0.pointee }
                    var prefix = 128
                    if let netmask = entry.ifa_netmask {
                        let mask6 = netmask.withMemoryRebound(to: sockaddr_in6.self, capacity: 1) { $0.pointee }
                        prefix = Self.ipv6Prefix(mask6.sin6_addr)
                    }
                    let bytes = withUnsafeBytes(of: addr6.sin6_addr.__u6_addr.__u6_addr8) { Array($0) }
                    let groups = stride(from: 0, to: 16, by: 2).map {
                        String(format: "%x", UInt16(bytes[$0]) << 8 | UInt16(bytes[$0 + 1]))
                    }
                    interface.ipv6Addresses?.append("\(groups.joined(separator: ":"))/\(prefix)")
                }
            }
            if let address = entry.ifa_addr, address.pointee.sa_family == UInt8(AF_LINK) {
                // macOS 上 AF_LINK 的 ifa_data 指向 if_data（统计信息），
                // 硬件地址在 ifa_addr（sockaddr_dl）里，按 sdl_nlen 偏移读取 sdl_alen 字节。
                let sockaddrDL = address.withMemoryRebound(to: sockaddr_dl.self, capacity: 1) { $0.pointee }
                let offset = Int(sockaddrDL.sdl_nlen)
                let length = Int(sockaddrDL.sdl_alen)
                var bytes: [String] = []
                withUnsafeBytes(of: sockaddrDL.sdl_data) { raw in
                    guard offset >= 0, length >= 6, raw.count >= offset + 6 else { return }
                    for index in 0..<6 { bytes.append(String(format: "%02x", raw[offset + index])) }
                }
                if bytes.count == 6 {
                    interface.hardwareAddress = bytes.joined(separator: ":")
                }
            }
            byName[name] = interface
        }

        var interfaces = byName.values.map { $0 }
        interfaces.sort { lhs, rhs in
            if lhs.isLoopback != rhs.isLoopback { return !lhs.isLoopback }
            if lhs.isUp != rhs.isUp { return lhs.isUp }
            return lhs.name < rhs.name
        }
        return interfaces
    }

    /// 128 位掩码中的连续前缀长度。
    static func ipv6Prefix(_ mask: in6_addr) -> Int {
        var count = 0
        let bytes = withUnsafeBytes(of: mask.__u6_addr.__u6_addr8) { Array($0) }
        for byte in bytes {
            var value = byte
            while value & 0x80 != 0 { count += 1; value <<= 1 }
            if value != 0 { break }  // 掩码不连续，到此为止
        }
        return count
    }

    /// Friendly name for an interface, derived from `networksetup -listallhardwareports`.
    public static func displayName(for interfaceName: String) -> String? {
        guard let result = try? ProcessRunner.run("/usr/sbin/networksetup", ["-listallhardwareports"]),
              result.success else { return nil }
        return parseHardwarePorts(result.stdout)[interfaceName]
    }

    /// Maps device name -> hardware port description ("Wi-Fi", "Thunderbolt Bridge", ...).
    public static func parseHardwarePorts(_ text: String) -> [String: String] {
        var mapping: [String: String] = [:]
        var port: String?
        for line in text.split(separator: "\n", omittingEmptySubsequences: true) {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if trimmed.hasPrefix("Hardware Port:") {
                port = String(trimmed.dropFirst("Hardware Port:".count)).trimmingCharacters(in: .whitespaces)
            } else if trimmed.hasPrefix("Device:"), let port {
                let device = String(trimmed.dropFirst("Device:".count)).trimmingCharacters(in: .whitespaces)
                mapping[device] = port
            }
        }
        return mapping
    }
}
