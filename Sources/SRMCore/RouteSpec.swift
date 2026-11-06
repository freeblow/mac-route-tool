import Foundation

public enum RouteValidationError: LocalizedError, Equatable {
    case badDestination(String)
    case badGateway(String)
    case badInterface(String)

    public var errorDescription: String? {
        switch self {
        case .badDestination(let value):
            return "目标网段无效：\(value)（请使用 CIDR，例如 10.40.85.0/24，或 default）"
        case .badGateway(let value):
            return "网关地址无效：\(value)"
        case .badInterface(let value):
            return "接口名无效：\(value)"
        }
    }
}

/// A single static route managed by the tool.
public struct RouteSpec: Codable, Identifiable, Hashable, Sendable {
    /// Stable identity of the record in the configuration file.
    public var id: UUID
    /// Destination in CIDR form, or the literal "default".
    public var destination: String
    /// Next hop address. `nil` means an on-link route resolved through the interface.
    public var gateway: String?
    /// Expected interface used to reach the gateway.
    public var interfaceName: String?
    /// On-link route: use `-interface` instead of a gateway route.
    public var direct: Bool
    /// Disabled routes stay in the config but are not enforced.
    public var enabled: Bool
    /// Free-form note shown in the UI.
    public var note: String

    public init(
        id: UUID = UUID(),
        destination: String,
        gateway: String? = nil,
        interfaceName: String? = nil,
        direct: Bool = false,
        enabled: Bool = true,
        note: String = ""
    ) {
        self.id = id
        self.destination = destination
        self.gateway = gateway
        self.interfaceName = interfaceName
        self.direct = direct
        self.enabled = enabled
        self.note = note
    }

    // MARK: - Normalisation

    /// Destination normalised to `a.b.c.d/prefix` (or `0.0.0.0/0` for default).
    public var normalizedDestination: String {
        RouteSpec.normalizeDestination(destination) ?? destination
    }

    public var normalizedGateway: String? {
        guard let gateway, !gateway.isEmpty else { return nil }
        return IPv4.parse(gateway).map(IPv4.string) ?? gateway
    }

    public var isDefaultRoute: Bool {
        normalizedDestination == "0.0.0.0/0"
    }

    public var prefixLength: Int {
        Int(normalizedDestination.split(separator: "/").last ?? "32") ?? 32
    }

    /// Identity used to match a spec against a kernel route.
    public var matchKey: String {
        "\(normalizedDestination)|\(normalizedGateway ?? "")|\(interfaceName ?? "")"
    }

    public static func normalizeDestination(_ raw: String) -> String? {
        let value = raw.trimmingCharacters(in: .whitespaces)
        guard !value.isEmpty else { return nil }
        if value.lowercased() == "default" { return "0.0.0.0/0" }

        if let slash = value.lastIndex(of: "/") {
            let addressPart = String(value[value.startIndex..<slash])
            guard let prefix = Int(value[value.index(after: slash)...]), (0...32).contains(prefix) else { return nil }
            if let address = IPv4.parse(addressPart) {
                return "\(IPv4.string(IPv4.network(address, prefix: prefix)))/\(prefix)"
            }
            if let parsed = IPv4.inetAton(addressPart) {
                return "\(IPv4.string(IPv4.network(parsed.address, prefix: prefix)))/\(prefix)"
            }
            return nil
        }

        if let address = IPv4.parse(value) {
            return "\(IPv4.string(address))/32"
        }
        if let parsed = IPv4.inetAton(value) {
            return "\(IPv4.string(IPv4.network(parsed.address, prefix: parsed.prefix)))/\(parsed.prefix)"
        }
        return nil
    }

    // MARK: - Validation

    public func validate() throws {
        guard RouteSpec.normalizeDestination(destination) != nil else {
            throw RouteValidationError.badDestination(destination)
        }
        if let gateway, !gateway.isEmpty, !IPv4.isValidAddress(gateway) {
            throw RouteValidationError.badGateway(gateway)
        }
        if let interfaceName, !interfaceName.isEmpty {
            guard interfaceName.allSatisfy({ $0.isLetter || $0.isNumber || $0 == "-" || $0 == "_" }) else {
                throw RouteValidationError.badInterface(interfaceName)
            }
        }
    }

    /// A sanitised copy used when persisting or applying.
    public func sanitized() -> RouteSpec {
        var copy = self
        copy.destination = normalizedDestination
        copy.gateway = normalizedGateway
        if let interfaceName, interfaceName.isEmpty { copy.interfaceName = nil }
        return copy
    }
}
