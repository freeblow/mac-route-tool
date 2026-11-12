import Foundation

public struct NICInfo: Codable, Sendable, Identifiable, Hashable {
    public var serviceName: String
    public var deviceName: String
    public var status: NICStatus
    public var isVirtual: Bool
    public var ipv4Addresses: [String]
    public var ipv6Addresses: [String]
    public var gateway: String?
    public var speedText: String?
    public var hardwareAddress: String?
    public var rank: Int

    public var id: String { serviceName }
}

public enum NICStatus: String, Codable, Sendable {
    case connected
    case disconnected
    case disabled
}

public struct NICServiceListing: Codable, Sendable, Equatable {
    public var order: [String]
    public var devices: [String: String]
    public var disabled: Set<String>

    public init(order: [String] = [], devices: [String: String] = [:], disabled: Set<String> = []) {
        self.order = order
        self.devices = devices
        self.disabled = disabled
    }
}

public enum NICError: LocalizedError {
    case commandFailed(String, String)
    case socketFailed(Int32)
    case badAddress(String)

    public var errorDescription: String? {
        switch self {
        case .commandFailed(let tool, let detail):
            return "\(tool) 执行失败：\(detail)"
        case .socketFailed(let errnoValue):
            return "socket 操作失败（errno \(errnoValue)）：\(String(cString: strerror(errnoValue)))"
        case .badAddress(let value):
            return "无效地址：\(value)"
        }
    }
}

public enum NICManager {
    public static let networksetupPath = "/usr/sbin/networksetup"
    public static let ifconfigPath = "/sbin/ifconfig"
    /// 视为虚拟设备的接口名前缀。
    public static let virtualPrefixes = ["bridge", "utun", "awdl", "llw", "vlan", "vmenet"]

    /// 服务顺序 + 设备映射 + 禁用集合（读系统状态，无副作用）。
    public static func listServices() -> NICServiceListing {
        var listing = NICServiceListing()
        if let result = try? ProcessRunner.run(networksetupPath, ["-listnetworkserviceorder"]), result.success {
            for (name, device) in parseServiceOrder(result.stdout) {
                listing.order.append(name)
                if let device { listing.devices[name] = device }
            }
        }
        if let result = try? ProcessRunner.run(networksetupPath, ["-listallnetworkservices"]), result.success {
            for (name, isDisabled) in parseServiceNames(result.stdout) where isDisabled {
                listing.disabled.insert(name)
            }
        }
        return listing
    }

    /// 启用/禁用网络服务（需要 root；由 srmd 调用）。
    public static func setServiceEnabled(_ service: String, enabled: Bool) throws {
        let result = try ProcessRunner.run(networksetupPath, ["-setnetworkserviceenabled", service, enabled ? "on" : "off"])
        guard result.success else { throw NICError.commandFailed("networksetup", result.trimmedError) }
    }

    /// 设置服务顺序（需要 root；由 srmd 调用）。
    public static func setServiceOrder(_ order: [String]) throws {
        let result = try ProcessRunner.run(networksetupPath, ["-ordernetworkservices"] + order)
        guard result.success else { throw NICError.commandFailed("networksetup", result.trimmedError) }
    }

    /// 组装网卡列表。rank 从 1 开始，等于服务顺序位置（即"优先级"）。
    public static func enumerate() -> [NICInfo] {
        let listing = listServices()
        let interfaces = Dictionary(uniqueKeysWithValues: NetworkInterfaces.all().map { ($0.name, $0) })
        let routes = (try? RouteTableReader.readIPv4()) ?? []
        let gateways = defaultGateways(routes)
        let speeds = mediaInfo()

        return listing.order.enumerated().map { index, service in
            let device = listing.devices[service]
            let iface = device.flatMap { interfaces[$0] }
            let connected = iface?.isUsable ?? false
            return NICInfo(
                serviceName: service,
                deviceName: device ?? "",
                status: listing.disabled.contains(service) ? .disabled : (connected ? .connected : .disconnected),
                isVirtual: device.map { isVirtualDeviceName($0) } ?? false,
                ipv4Addresses: iface?.ipv4Addresses ?? [],
                ipv6Addresses: iface?.ipv6Addresses ?? [],
                gateway: device.flatMap { gateways[$0] },
                speedText: device.flatMap { speeds[$0] },
                hardwareAddress: iface?.hardwareAddress,
                rank: index + 1
            )
        }
    }

    public static func isVirtualDeviceName(_ name: String) -> Bool {
        virtualPrefixes.contains { name.lowercased().hasPrefix($0) }
    }

    /// 设备名 → 默认网关 IP（仅取 0.0.0.0/0 且下一跳为 IP 的路由）。
    public static func defaultGateways(_ routes: [KernelRoute]) -> [String: String] {
        var map: [String: String] = [:]
        for route in routes where route.destination == "0.0.0.0/0" && route.isGateway {
            if let device = route.interfaceName, IPv4.isValidAddress(route.gateway) {
                map[device] = route.gateway
            }
        }
        return map
    }

    /// 设备名 → 链路速率文本（`ifconfig -a` 的 media 行）。
    public static func mediaInfo() -> [String: String] {
        guard let result = try? ProcessRunner.run(ifconfigPath, ["-a"]), result.success else { return [:] }
        return parseIfconfigMedia(result.stdout)
    }

    /// 解析 `ifconfig -a` 输出：设备头行 + 缩进的 `media:` 行。
    public static func parseIfconfigMedia(_ text: String) -> [String: String] {
        var result: [String: String] = [:]
        var device: String?
        for rawLine in text.split(separator: "\n", omittingEmptySubsequences: true) {
            let line = String(rawLine)
            if !line.hasPrefix(" ") && !line.hasPrefix("\t") {
                if let colon = line.firstIndex(of: ":") {
                    let name = String(line[..<colon])
                    if !name.isEmpty, name.allSatisfy({ $0.isLetter || $0.isNumber }) {
                        device = name
                        continue
                    }
                }
            }
            guard let device, let mediaRange = line.range(of: "media: ") else { continue }
            let after = line[mediaRange.upperBound...]
            guard let open = after.firstIndex(of: "("), let close = after.firstIndex(of: ")"), open < close else { continue }
            let inner = after[after.index(after: open)..<close]
            let subtype = inner.split(separator: " ").first.map(String.init) ?? ""
            if let speed = speedText(fromMediaSubtype: subtype) {
                result[device] = speed
            }
        }
        return result
    }

    /// `1000baseT` → "1 Gbps"，`2500baseT` → "2.5 Gbps"，`100baseTX` → "100 Mbps"。
    public static func speedText(fromMediaSubtype: String) -> String? {
        let lowered = fromMediaSubtype.lowercased()
        guard let range = lowered.range(of: #"\d+"#, options: .regularExpression),
              let number = Double(lowered[range]) else { return nil }
        if lowered.contains("g") || number >= 1000 {
            let gbps = number >= 1000 ? number / 1000 : number
            return gbps == gbps.rounded() ? "\(Int(gbps)) Gbps" : "\(gbps) Gbps"
        }
        return "\(Int(number)) Mbps"
    }

    /// 解析 `-listnetworkserviceorder`：`(N) 服务名` + 下一行 `(Hardware Port: ..., Device: ...)`。
    public static func parseServiceOrder(_ text: String) -> [(name: String, device: String?)] {
        var result: [(name: String, device: String?)] = []
        var pendingName: String?
        for rawLine in text.split(separator: "\n", omittingEmptySubsequences: true) {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            if line.hasPrefix("("), !line.contains("Hardware Port:"),
               let close = line.firstIndex(of: ")") {
                pendingName = String(line[line.index(after: close)...]).trimmingCharacters(in: .whitespaces)
            } else if let serviceName = pendingName, line.contains("Device:") {
                let device = line.split(separator: ",").first { $0.contains("Device:") }
                    .map { String($0.split(separator: ":").last ?? "")
                        .trimmingCharacters(in: .whitespaces)
                        .trimmingCharacters(in: CharacterSet(charactersIn: ")")) }
                result.append((name: serviceName, device: device))
                pendingName = nil
            }
        }
        return result
    }

    /// 解析 `-listallnetworkservices`：跳过说明行，`*` 前缀 = 已禁用。
    public static func parseServiceNames(_ text: String) -> [(name: String, disabled: Bool)] {
        text.split(separator: "\n", omittingEmptySubsequences: true)
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty && !$0.hasPrefix("An asterisk") }
            .map { line -> (name: String, disabled: Bool) in
                if line.hasPrefix("*") {
                    return (name: String(line.dropFirst()).trimmingCharacters(in: .whitespaces), disabled: true)
                }
                return (name: line, disabled: false)
            }
    }
}
