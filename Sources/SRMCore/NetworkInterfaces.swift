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
                displayName: nil
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
            }
            if let data = entry.ifa_data, entry.ifa_addr.pointee.sa_family == UInt8(AF_LINK) {
                let sockaddrDL = UnsafeRawPointer(data).assumingMemoryBound(to: sockaddr_dl.self).pointee
                let length = Int(sockaddrDL.sdl_alen)
                if length >= 6 {
                    var bytes: [String] = []
                    withUnsafeBytes(of: sockaddrDL.sdl_data) { raw in
                        for index in 0..<6 { bytes.append(String(format: "%02x", raw[index])) }
                    }
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
