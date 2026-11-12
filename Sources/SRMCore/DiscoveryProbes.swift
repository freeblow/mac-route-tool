import Darwin
import Foundation

public enum DiscoveryConstants {
    /// LocalSend 使用的组播组与端口。
    public static let multicastGroup = "224.0.0.167"
    public static let discoveryPort: UInt16 = 53317
}

/// 向组播组发一条 LocalSend 宣告（喊一声）。
public enum DiscoveryShout {
    /// 最小合法宣告：LocalSend 靠 `alias` 显示名字。
    public static func announcePayload(alias: String, port: UInt16 = DiscoveryConstants.discoveryPort) -> Data {
        let payload: [String: Any] = [
            "alias": alias,
            "deviceModel": "",
            "deviceType": "pc",
            "fingerprint": UUID().uuidString,
            "download": false,
            "port": Int(port),
            "protocol": "http",
        ]
        return (try? JSONSerialization.data(withJSONObject: payload)) ?? Data()
    }

    /// 从指定本机 IPv4 向组播组发送宣告。
    public static func send(payload: Data, from sourceIP: String, port: UInt16 = DiscoveryConstants.discoveryPort) throws {
        let fd = socket(AF_INET, SOCK_DGRAM, 0)
        guard fd >= 0 else { throw NICError.socketFailed(errno) }
        defer { close(fd) }

        var ttl: UInt8 = 2
        setsockopt(fd, IPPROTO_IP, IP_MULTICAST_TTL, &ttl, socklen_t(MemoryLayout<UInt8>.size))
        var loop: UInt8 = 0
        setsockopt(fd, IPPROTO_IP, IP_MULTICAST_LOOP, &loop, socklen_t(MemoryLayout<UInt8>.size))

        var local = sockaddr_in()
        local.sin_family = sa_family_t(AF_INET)
        local.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        local.sin_port = 0
        guard inet_pton(AF_INET, sourceIP, &local.sin_addr) == 1 else {
            throw NICError.badAddress(sourceIP)
        }
        let bindResult = withUnsafePointer(to: &local) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard bindResult == 0 else { throw NICError.socketFailed(errno) }

        var remote = sockaddr_in()
        remote.sin_family = sa_family_t(AF_INET)
        remote.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        remote.sin_port = port.bigEndian
        guard inet_pton(AF_INET, DiscoveryConstants.multicastGroup, &remote.sin_addr) == 1 else {
            throw NICError.badAddress(DiscoveryConstants.multicastGroup)
        }
        let connectResult = withUnsafePointer(to: &remote) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                connect(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard connectResult == 0 else { throw NICError.socketFailed(errno) }

        let sent = payload.withUnsafeBytes { raw in
            Darwin.send(fd, raw.baseAddress, raw.count, 0)
        }
        guard sent == payload.count else { throw NICError.socketFailed(errno) }
    }

    public static func parseAlias(_ data: Data) -> String? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let alias = object["alias"] as? String, !alias.isEmpty else { return nil }
        return alias
    }
}

public struct DiscoveryWatchResult: Codable, Sendable {
    public var aliasCounts: [String: Int]
    public var packetCount: Int
    public var elapsedSeconds: Int

    public init(aliasCounts: [String: Int], packetCount: Int, elapsedSeconds: Int) {
        self.aliasCounts = aliasCounts
        self.packetCount = packetCount
        self.elapsedSeconds = elapsedSeconds
    }
}

/// 监听组播若干秒（听听看诊断）：加入组播组，统计听到的 LocalSend 设备别名。
public enum DiscoveryWatcher {
    public static func watch(seconds: Int = 10, port: UInt16 = DiscoveryConstants.discoveryPort) throws -> DiscoveryWatchResult {
        let fd = socket(AF_INET, SOCK_DGRAM, 0)
        guard fd >= 0 else { throw NICError.socketFailed(errno) }
        defer { close(fd) }

        var reuse: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &reuse, socklen_t(MemoryLayout<Int32>.size))

        var local = sockaddr_in()
        local.sin_family = sa_family_t(AF_INET)
        local.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        local.sin_port = port.bigEndian
        local.sin_addr = in_addr(s_addr: INADDR_ANY)
        let bindResult = withUnsafePointer(to: &local) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard bindResult == 0 else { throw NICError.socketFailed(errno) }

        // 在每个本机 IPv4 地址上加入组播组（对应 Windows 版 JoinMulticastGroup 全网卡加入）。
        let addresses = NetworkInterfaces.all()
            .filter { !$0.isLoopback }
            .compactMap { $0.primaryIPv4?.split(separator: "/").first.map(String.init) }
        var membership = ip_mreq()
        guard inet_pton(AF_INET, DiscoveryConstants.multicastGroup, &membership.imr_multiaddr) == 1 else {
            throw NICError.badAddress(DiscoveryConstants.multicastGroup)
        }
        for address in addresses {
            if inet_pton(AF_INET, address, &membership.imr_interface) == 1 {
                setsockopt(fd, IPPROTO_IP, IP_ADD_MEMBERSHIP, &membership,
                           socklen_t(MemoryLayout<ip_mreq>.size))
            }
        }

        let deadline = Date().addingTimeInterval(TimeInterval(seconds))
        var counts: [String: Int] = [:]
        var packetCount = 0
        var buffer = [UInt8](repeating: 0, count: 65536)
        while Date() < deadline {
            var fds = pollfd(fd: fd, events: Int16(POLLIN), revents: 0)
            let ready = poll(&fds, 1, 300)
            guard ready > 0 else { continue }
            let received = recv(fd, &buffer, buffer.count, 0)
            guard received > 0 else { continue }
            packetCount += 1
            if let alias = DiscoveryShout.parseAlias(Data(buffer[0..<received])) {
                counts[alias, default: 0] += 1
            }
        }
        return DiscoveryWatchResult(aliasCounts: counts, packetCount: packetCount, elapsedSeconds: seconds)
    }
}
