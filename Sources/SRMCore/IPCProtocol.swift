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
    // 网卡管理与 IP 检测
    case nicList
    case nicSetServiceEnabled
    case nicSetOrder
    case firewallApply
    case firewallRemove
    case scanStart
    case scanStatus
    case scanResult
    case scanStop
}

public struct SRMRequest: Codable, Sendable {
    public var id: Int
    public var command: String
    public var spec: RouteSpec?
    public var enabled: Bool?
    public var serviceName: String?
    public var serviceEnabled: Bool?
    public var serviceOrder: [String]?
    public var scanTarget: String?
    public var jobId: String?

    public init(
        id: Int,
        command: SRMCommand,
        spec: RouteSpec? = nil,
        enabled: Bool? = nil,
        serviceName: String? = nil,
        serviceEnabled: Bool? = nil,
        serviceOrder: [String]? = nil,
        scanTarget: String? = nil,
        jobId: String? = nil
    ) {
        self.id = id
        self.command = command.rawValue
        self.spec = spec
        self.enabled = enabled
        self.serviceName = serviceName
        self.serviceEnabled = serviceEnabled
        self.serviceOrder = serviceOrder
        self.scanTarget = scanTarget
        self.jobId = jobId
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

/// 守护进程侧扫描任务状态。
public struct ScanJobStatus: Codable, Sendable {
    public enum Phase: String, Codable, Sendable {
        case idle
        case running
        case done
        case failed
        case stopped
    }

    public var phase: Phase
    public var done: Int
    public var total: Int
    public var error: String?
    public var rows: [ScanRow]?

    public init(phase: Phase, done: Int, total: Int, error: String?, rows: [ScanRow]?) {
        self.phase = phase
        self.done = done
        self.total = total
        self.error = error
        self.rows = rows
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
    public var nics: [NICInfo]?
    public var scanJob: ScanJobStatus?
    public var jobId: String?

    public init(id: Int, ok: Bool, error: String? = nil) {
        self.id = id
        self.ok = ok
        self.error = error
        self.nics = nil
        self.scanJob = nil
        self.jobId = nil
    }
}
