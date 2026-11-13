import Foundation

/// Owns the configuration and keeps the kernel routing table in sync with it.
///
/// The manager never deletes kernel routes it did not create itself, so DHCP
/// and VPN supplied entries stay untouched.
///
/// 线程安全：守护进程里本类的公开接口会被两个线程并发调用——
/// SocketServer 的 accept 线程处理 IPC 请求，定时器队列周期执行
/// reconcile。所有公开状态（config/managed/lastError/lastReconcile）
/// 都由 `lock` 保护；内部方法假定持锁调用，禁止在持锁期间再调公开方法
/// （NSLock 不可重入，applyAll 用 reconcileLocked 而非 reconcile）。
public final class RouteManager {
    public struct Options: Sendable {
        public var configPath: String
        public var dryRun: Bool
        /// Where the list of self-managed kernel routes is persisted.
        public var stateDirectory: String

        public init(
            configPath: String = ConfigStore.defaultConfigPath,
            dryRun: Bool = false,
            stateDirectory: String = SocketPaths.directory
        ) {
            self.configPath = configPath
            self.dryRun = dryRun
            self.stateDirectory = stateDirectory
        }
    }

    private let lock = NSLock()
    private var configStorage: SRMConfig
    private var lastErrorStorage: String?
    private var lastReconcileStorage: Date?
    private var managed: [String: RouteSpec] = [:]

    public var config: SRMConfig {
        lock.lock(); defer { lock.unlock() }
        return configStorage
    }

    public var lastError: String? {
        lock.lock(); defer { lock.unlock() }
        return lastErrorStorage
    }

    public var lastReconcile: Date? {
        lock.lock(); defer { lock.unlock() }
        return lastReconcileStorage
    }

    private let store: ConfigStore
    private let options: Options
    private let statePath: String

    public init(options: Options = Options()) throws {
        self.options = options
        self.store = ConfigStore(path: options.configPath)
        self.statePath = "\(options.stateDirectory)/managed.json"
        self.configStorage = try store.load()
        loadManagedState()
    }

    // MARK: - Configuration

    public func saveConfig() throws {
        lock.lock(); defer { lock.unlock() }
        try store.save(configStorage)
    }

    @discardableResult
    public func add(_ spec: RouteSpec) throws -> RouteSpec {
        lock.lock(); defer { lock.unlock() }
        let sanitized = spec.sanitized()
        try sanitized.validate()
        if sanitized.direct {
            // 无网关直连路由（如 224.0.0.167/32 -interface en0）不需要网关，但必须绑定出接口。
            if sanitized.normalizedGateway == nil {
                guard let interfaceName = sanitized.interfaceName, !interfaceName.isEmpty else {
                    throw RouteValidationError.badInterface("直连路由必须指定出接口")
                }
            }
        } else {
            guard sanitized.normalizedGateway != nil else {
                throw RouteValidationError.badGateway("")
            }
        }
        if let index = configStorage.routes.firstIndex(where: { $0.id == sanitized.id }) {
            configStorage.routes[index] = sanitized
        } else {
            configStorage.routes.append(sanitized)
        }
        try store.save(configStorage)
        return sanitized
    }

    @discardableResult
    public func update(_ spec: RouteSpec) throws -> RouteSpec {
        lock.lock(); defer { lock.unlock() }
        guard let index = configStorage.routes.firstIndex(where: { $0.id == spec.id }) else {
            throw ManagerError.unknownRoute(spec.id)
        }
        let sanitized = spec.sanitized()
        try sanitized.validate()
        configStorage.routes[index] = sanitized
        try store.save(configStorage)
        return sanitized
    }

    public func delete(id: UUID) throws {
        lock.lock(); defer { lock.unlock() }
        guard let index = configStorage.routes.firstIndex(where: { $0.id == id }) else {
            throw ManagerError.unknownRoute(id)
        }
        let removed = configStorage.routes.remove(at: index)
        try store.save(configStorage)
        // Drop the kernel entry if we own it.
        if managed[removed.matchKey] != nil {
            managed[removed.matchKey] = nil
            saveManagedState()
            if !options.dryRun {
                for route in (try? RouteTableReader.readIPv4()) ?? [] where route.matches(removed) {
                    _ = RouteApplier.remove(route)
                }
            }
        }
    }

    public func setEnabled(id: UUID, enabled: Bool) throws {
        lock.lock(); defer { lock.unlock() }
        guard let index = configStorage.routes.firstIndex(where: { $0.id == id }) else {
            throw ManagerError.unknownRoute(id)
        }
        configStorage.routes[index].enabled = enabled
        try store.save(configStorage)
        if !enabled, managed[configStorage.routes[index].matchKey] != nil {
            let spec = configStorage.routes[index]
            managed[spec.matchKey] = nil
            saveManagedState()
            if !options.dryRun {
                for route in (try? RouteTableReader.readIPv4()) ?? [] where route.matches(spec) {
                    _ = RouteApplier.remove(route)
                }
            }
        }
    }

    // MARK: - Reconciliation

    /// Compares the configuration with the kernel table and repairs the difference.
    @discardableResult
    public func reconcile() -> [RouteStatus] {
        lock.lock(); defer { lock.unlock() }
        return reconcileLocked()
    }

    /// 持锁版本的 reconcile：供 applyAll 等已在锁内的路径复用。
    private func reconcileLocked() -> [RouteStatus] {
        defer { lastReconcileStorage = Date() }

        let kernelRoutes: [KernelRoute]
        do {
            kernelRoutes = try RouteTableReader.readIPv4()
        } catch {
            let message = error.localizedDescription
            lastErrorStorage = message
            return configStorage.routes.map { spec in
                RouteStatus(
                    spec: spec,
                    state: spec.enabled ? .failed : .disabled,
                    message: spec.enabled ? message : "已停用",
                    resolvedInterface: nil,
                    managed: false
                )
            }
        }
        let interfaces = NetworkInterfaces.all()
        var statuses: [RouteStatus] = []

        for spec in configStorage.routes {
            if !spec.enabled {
                statuses.append(RouteStatus(spec: spec, state: .disabled, message: "已停用", resolvedInterface: nil, managed: false))
                continue
            }

            // Older versions used -ifscope for gateway routes. Such a route is
            // present in the table but ordinary sockets may still use another
            // interface's default route, so replace only entries we recorded.
            if !spec.direct,
               let scoped = kernelRoutes.first(where: { $0.matches(spec) && $0.isInterfaceScoped }),
               managed[spec.matchKey] != nil,
               !options.dryRun {
                let removal = RouteApplier.remove(scoped)
                if !removal.success {
                    statuses.append(RouteStatus(
                        spec: spec, state: .failed,
                        message: "无法迁移旧路由：\(removal.message)",
                        resolvedInterface: scoped.interfaceName, managed: true
                    ))
                    continue
                }
            }

            if let match = kernelRoutes.first(where: { $0.matches(spec) && (spec.direct || !$0.isInterfaceScoped) }) {
                managed[spec.matchKey] = spec
                saveManagedState()
                statuses.append(RouteStatus(
                    spec: spec,
                    state: .active,
                    message: nil,
                    resolvedInterface: match.interfaceName,
                    managed: true
                ))
                continue
            }

            // A stale entry for the same destination but a different interface
            // has to be removed before the route can be written again.
            if let stale = kernelRoutes.first(where: {
                $0.destination == spec.normalizedDestination &&
                (spec.normalizedGateway == nil || $0.gateway == spec.normalizedGateway) &&
                ($0.interfaceName != spec.interfaceName)
            }) {
                if !options.dryRun {
                    _ = RouteApplier.remove(stale)
                }
            }

            statuses.append(apply(spec, interfaces: interfaces))
        }

        pruneOrphans(kernelRoutes: kernelRoutes)
        return statuses
    }

    private func apply(_ spec: RouteSpec, interfaces: [NetInterface]) -> RouteStatus {
        var resolvedInterface: String?

        if spec.direct, spec.normalizedGateway == nil {
            // 无网关直连路由：出接口就是路由本身，校验接口存在且可用（不经过网关解析）。
            guard let wanted = spec.interfaceName, !wanted.isEmpty else {
                return RouteStatus(spec: spec, state: .failed, message: "直连路由未指定出接口", resolvedInterface: nil, managed: false)
            }
            guard let info = interfaces.first(where: { $0.name == wanted }) else {
                return RouteStatus(spec: spec, state: .pending, message: "接口 \(wanted) 不存在", resolvedInterface: nil, managed: false)
            }
            guard info.isUsable else {
                return RouteStatus(spec: spec, state: .pending, message: "接口 \(wanted) 尚未就绪", resolvedInterface: wanted, managed: false)
            }
            resolvedInterface = wanted
        } else {
            guard let gateway = spec.normalizedGateway else {
                return RouteStatus(spec: spec, state: .failed, message: "缺少网关地址", resolvedInterface: nil, managed: false)
            }
            let resolved = RouteApplier.resolveInterface(for: gateway)
            if resolved == nil {
                return RouteStatus(spec: spec, state: .pending, message: "网关 \(gateway) 暂时不可达", resolvedInterface: nil, managed: false)
            }
            resolvedInterface = resolved
        }

        if let wanted = spec.interfaceName, !wanted.isEmpty {
            if wanted != resolvedInterface {
                return RouteStatus(
                    spec: spec,
                    state: .failed,
                    message: "网关 \(spec.normalizedGateway ?? "") 实际经由 \(resolvedInterface ?? "未知接口")，与绑定接口 \(wanted) 不一致",
                    resolvedInterface: resolvedInterface,
                    managed: false
                )
            }
            if let info = interfaces.first(where: { $0.name == wanted }), !info.isUsable {
                return RouteStatus(
                    spec: spec,
                    state: .pending,
                    message: "接口 \(wanted) 尚未就绪",
                    resolvedInterface: resolvedInterface,
                    managed: false
                )
            }
        }

        if options.dryRun {
            return RouteStatus(
                spec: spec,
                state: .pending,
                message: "dry-run：将执行 route \(RouteApplier.addArguments(for: spec).joined(separator: " "))",
                resolvedInterface: resolvedInterface,
                managed: false
            )
        }

        let outcome = RouteApplier.add(spec)
        if outcome.success {
            managed[spec.matchKey] = spec
            saveManagedState()
            return RouteStatus(spec: spec, state: .active, message: nil, resolvedInterface: resolvedInterface, managed: true)
        }
        lastErrorStorage = outcome.message
        return RouteStatus(spec: spec, state: .failed, message: outcome.message, resolvedInterface: resolvedInterface, managed: false)
    }

    /// Removes kernel routes that we created earlier but that are no longer configured.
    private func pruneOrphans(kernelRoutes: [KernelRoute]) {
        let configuredKeys = Set(configStorage.routes.filter { $0.enabled }.map(\.matchKey))
        for (key, spec) in managed where !configuredKeys.contains(key) {
            if !options.dryRun {
                for route in kernelRoutes where route.matches(spec) {
                    _ = RouteApplier.remove(route)
                }
            }
            managed[key] = nil
        }
        saveManagedState()
    }

    /// Force a full re-write of every enabled route.
    @discardableResult
    public func applyAll() -> [RouteStatus] {
        lock.lock(); defer { lock.unlock() }
        guard !options.dryRun else { return reconcileLocked() }
        for spec in configStorage.routes where spec.enabled {
            for route in (try? RouteTableReader.readIPv4()) ?? [] where route.matches(spec) {
                _ = RouteApplier.remove(route)
            }
        }
        managed.removeAll()
        saveManagedState()
        return reconcileLocked()
    }

    // MARK: - Managed state persistence

    /// 只在 init（单线程阶段）或持锁时调用。
    private func loadManagedState() {
        guard let data = FileManager.default.contents(atPath: statePath),
              let specs = try? JSONDecoder().decode([RouteSpec].self, from: data) else { return }
        for spec in specs {
            managed[spec.matchKey] = spec
        }
    }

    /// 持锁时调用（dry-run 下跳过写盘）。
    private func saveManagedState() {
        guard !options.dryRun else { return }
        try? FileManager.default.createDirectory(atPath: options.stateDirectory, withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let data = try? encoder.encode(Array(managed.values)) else { return }
        try? data.write(to: URL(fileURLWithPath: statePath), options: .atomic)
    }
}

public enum ManagerError: LocalizedError {
    case unknownRoute(UUID)

    public var errorDescription: String? {
        switch self {
        case .unknownRoute(let id):
            return "找不到路由记录 \(id.uuidString)"
        }
    }
}
