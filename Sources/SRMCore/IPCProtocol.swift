import Foundation

public enum SRMCommand: String, Codable, Sendable {
    case status
    case list
    case add
    case update
    case delete
    case setEnabled
    case apply
    case interfaces
    case kernelRoutes
    case config
}

public struct SRMRequest: Codable, Sendable {
    public var id: Int
    public var command: String
    public var spec: RouteSpec?
    public var enabled: Bool?

    public init(id: Int, command: SRMCommand, spec: RouteSpec? = nil, enabled: Bool? = nil) {
        self.id = id
        self.command = command.rawValue
        self.spec = spec
        self.enabled = enabled
    }
}

public struct DaemonInfo: Codable, Sendable {
    public var version: String
    public var pid: Int
    public var startedAt: Date
    public var configPath: String
    public var socketPath: String
    public var authorizedUID: Int?
    public var pollInterval: Double
    public var lastReconcile: Date?
    public var lastError: String?
    public var isDryRun: Bool

    public init(
        version: String,
        pid: Int,
        startedAt: Date,
        configPath: String,
        socketPath: String,
        authorizedUID: Int?,
        pollInterval: Double,
        lastReconcile: Date?,
        lastError: String?,
        isDryRun: Bool
    ) {
        self.version = version
        self.pid = pid
        self.startedAt = startedAt
        self.configPath = configPath
        self.socketPath = socketPath
        self.authorizedUID = authorizedUID
        self.pollInterval = pollInterval
        self.lastReconcile = lastReconcile
        self.lastError = lastError
        self.isDryRun = isDryRun
    }
}

public enum RouteState: String, Codable, Sendable {
    /// Present in the kernel routing table.
    case active
    /// Waiting for the interface / gateway to become reachable.
    case pending
    /// The route could not be written.
    case failed
    /// Present in the configuration but switched off.
    case disabled
    /// The route is missing and the daemon is not allowed to touch it.
    case unmanaged
}

public struct RouteStatus: Codable, Sendable, Identifiable {
    public var spec: RouteSpec
    public var state: RouteState
    public var message: String?
    public var resolvedInterface: String?
    /// True when the daemon created this entry itself and may remove it again.
    public var managed: Bool

    public var id: UUID { spec.id }

    public init(spec: RouteSpec, state: RouteState, message: String? = nil, resolvedInterface: String? = nil, managed: Bool = false) {
        self.spec = spec
        self.state = state
        self.message = message
        self.resolvedInterface = resolvedInterface
        self.managed = managed
    }
}

public struct SRMResponse: Codable, Sendable {
    public var id: Int
    public var ok: Bool
    public var error: String?
    public var daemon: DaemonInfo?
    public var routes: [RouteStatus]?
    public var interfaces: [NetInterface]?
    public var kernelRoutes: [KernelRoute]?

    public init(id: Int, ok: Bool, error: String? = nil) {
        self.id = id
        self.ok = ok
        self.error = error
    }
}
