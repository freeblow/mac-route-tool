import Foundation

/// A route entry as reported by the kernel.
public struct KernelRoute: Identifiable, Hashable, Codable, Sendable {
    /// Normalised destination (`a.b.c.d/prefix`).
    public var destination: String
    /// Next hop as printed by netstat (may be `link#7`, `localhost`, ...).
    public var gateway: String
    /// Raw BSD flag letters, e.g. `UGScIg`.
    public var flags: String
    /// Outgoing interface, when known.
    public var interfaceName: String?
    /// `Expire` column, when present.
    public var expire: String?

    public var id: String { "\(destination)|\(gateway)|\(interfaceName ?? "")" }

    public var isUp: Bool { flags.contains("U") }
    public var isGateway: Bool { flags.contains("G") }
    public var isStatic: Bool { flags.contains("S") }
    public var isCloning: Bool { flags.contains("c") }
    /// RTF_IFSCOPE — the route is bound to a specific interface.
    public var isInterfaceScoped: Bool { flags.contains("I") }

    /// Does this kernel route satisfy the given spec?
    public func matches(_ spec: RouteSpec) -> Bool {
        guard destination == spec.normalizedDestination else { return false }
        if let gateway = spec.normalizedGateway {
            guard self.gateway == gateway else { return false }
        }
        if let wanted = spec.interfaceName, !wanted.isEmpty {
            guard interfaceName == wanted else { return false }
        }
        return true
    }
}

public enum RouteTableReader {
    public static let netstatPath = "/usr/sbin/netstat"

    public static func readIPv4() throws -> [KernelRoute] {
        let result = try ProcessRunner.run(netstatPath, ["-rn", "-f", "inet"])
        guard result.exitCode == 0 else {
            throw RouteTableError.netstatFailed(result.stderr)
        }
        return parse(result.stdout)
    }

    public static func parse(_ text: String) -> [KernelRoute] {
        var routes: [KernelRoute] = []
        for rawLine in text.split(separator: "\n", omittingEmptySubsequences: true) {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            guard !line.isEmpty else { continue }
            if line.hasPrefix("Routing tables") || line.hasPrefix("Internet:") || line.hasPrefix("Destination") { continue }

            let columns = line.split(whereSeparator: { $0 == " " || $0 == "\t" }).map(String.init)
            guard columns.count >= 3 else { continue }

            let destination = RouteSpec.normalizeDestination(columns[0]) ?? columns[0]
            let gateway = columns[1]
            let flags = columns[2]
            let interfaceName: String?
            var expire: String?
            if columns.count >= 4 {
                interfaceName = columns[3]
            } else {
                interfaceName = nil
            }
            if columns.count >= 5 {
                expire = columns[4]
            }
            routes.append(KernelRoute(
                destination: destination,
                gateway: gateway,
                flags: flags,
                interfaceName: interfaceName,
                expire: expire
            ))
        }
        return routes
    }
}

public enum RouteTableError: LocalizedError {
    case netstatFailed(String)

    public var errorDescription: String? {
        switch self {
        case .netstatFailed(let detail):
            return "读取路由表失败：\(detail)"
        }
    }
}
