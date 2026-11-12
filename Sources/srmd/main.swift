import Darwin
import Foundation
import SRMCore

let toolVersion = "0.1.0"

struct Options {
    var configPath = ConfigStore.defaultConfigPath
    var socketPath = SocketPaths.socket
    var pollInterval: Double? = nil
    var dryRun = false
    var once = false
    var showVersion = false
}

func parseArguments() -> Options {
    var options = Options()
    let arguments = Array(CommandLine.arguments.dropFirst())
    var index = 0
    while index < arguments.count {
        let argument = arguments[index]
        switch argument {
        case "--config":
            index += 1
            if index < arguments.count { options.configPath = arguments[index] }
        case "--socket":
            index += 1
            if index < arguments.count { options.socketPath = arguments[index] }
        case "--interval":
            index += 1
            if index < arguments.count { options.pollInterval = Double(arguments[index]) }
        case "--dry-run":
            options.dryRun = true
        case "--once":
            options.once = true
        case "--version", "-v":
            options.showVersion = true
        case "--help", "-h":
            print("""
            srmd — 静态路由守护进程（SRM）

            用法: srmd [选项]

              --config <path>    配置文件路径（默认 \(ConfigStore.defaultConfigPath)）
              --socket <path>    unix socket 路径（默认 \(SocketPaths.socket)）
              --interval <sec>   对齐周期，秒
              --dry-run          只报告，不真正修改路由表
              --once             执行一次对齐后退出
              --version          打印版本
            """)
            exit(0)
        default:
            break
        }
        index += 1
    }
    return options
}

final class Daemon {
    private let options: Options
    private let manager: RouteManager
    private var server: SocketServer?
    private let queue = DispatchQueue(label: "srm.daemon")
    private var timer: DispatchSourceTimer?
    private var startedAt = Date()
    private var lastError: String?
    private let scanJobs = ScanJobManager()

    init(options: Options) throws {
        self.options = options
        self.manager = try RouteManager(options: RouteManager.Options(
            configPath: options.configPath,
            dryRun: options.dryRun,
            stateDirectory: URL(fileURLWithPath: options.socketPath).deletingLastPathComponent().path
        ))
        let server = SocketServer(socketPath: options.socketPath) { [weak self] request, peerUID in
            guard let self else { return SRMResponse(id: request.id, ok: false, error: "守护进程已停止") }
            return self.handle(request, peerUID: peerUID)
        }
        self.server = server
    }

    func run() throws {
        let runtimeDirectory = URL(fileURLWithPath: options.socketPath).deletingLastPathComponent().path
        try FileManager.default.createDirectory(atPath: runtimeDirectory, withIntermediateDirectories: true, attributes: [
            .posixPermissions: 0o755,
        ])
        try server?.start()
        restrictSocketOwnership()

        signal(SIGTERM, SIG_IGN)
        signal(SIGINT, SIG_IGN)
        let signalSource = DispatchSource.makeSignalSource(signal: SIGTERM, queue: queue)
        signalSource.setEventHandler { [weak self] in
            self?.shutdown()
        }
        signalSource.resume()
        let interruptSource = DispatchSource.makeSignalSource(signal: SIGINT, queue: queue)
        interruptSource.setEventHandler { [weak self] in
            self?.shutdown()
        }
        interruptSource.resume()

        if options.once {
            let statuses = manager.reconcile()
            report(statuses)
            server?.stop()
            exit(0)
        }

        let interval = options.pollInterval ?? manager.config.pollInterval
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now(), repeating: max(1.0, interval))
        timer.setEventHandler { [weak self] in
            self?.tick()
        }
        timer.resume()
        self.timer = timer

        NSLog("srmd \(toolVersion) 已启动：socket=\(options.socketPath) 周期=\(interval)s 配置=\(options.configPath)")
        dispatchMain()
    }

    private func restrictSocketOwnership() {
        guard let uid = manager.config.authorizedUID, uid > 0 else { return }
        let result = chown(options.socketPath, uid_t(uid), 0)
        if result != 0 {
            NSLog("srmd 无法设置 socket 属主：\(String(cString: strerror(errno)))")
        }
    }

    private func tick() {
        let statuses = manager.reconcile()
        lastError = statuses.compactMap { $0.state == .failed ? $0.message : nil }.first
    }

    private func shutdown() {
        timer?.cancel()
        timer = nil
        server?.stop()
        NSLog("srmd 退出")
        exit(0)
    }

    private func report(_ statuses: [RouteStatus]) {
        for status in statuses {
            let symbol: String
            switch status.state {
            case .active: symbol = "OK    "
            case .pending: symbol = "PENDING"
            case .failed: symbol = "FAILED"
            case .disabled: symbol = "OFF   "
            case .unmanaged: symbol = "SKIP  "
            }
            let gateway = status.spec.normalizedGateway ?? "-"
            let interface = status.spec.interfaceName ?? "-"
            print("\(symbol) \(status.spec.normalizedDestination) via \(gateway) dev \(interface)\(status.message.map { " — \($0)" } ?? "")")
        }
    }

    // MARK: - IPC

    private func isAuthorized(peerUID: Int) -> Bool {
        if peerUID == 0 { return true }
        guard let allowed = manager.config.authorizedUID else { return false }
        return Int(peerUID) == allowed
    }

    private func handle(_ request: SRMRequest, peerUID: Int) -> SRMResponse {
        guard isAuthorized(peerUID: peerUID) else {
            return SRMResponse(id: request.id, ok: false, error: "未授权的调用方（uid \(peerUID)）")
        }

        guard let command = SRMCommand(rawValue: request.command) else {
            return SRMResponse(id: request.id, ok: false, error: "未知命令 \(request.command)")
        }

        switch command {
        case .status:
            var response = SRMResponse(id: request.id, ok: true)
            response.daemon = daemonInfo()
            return response

        case .list:
            let statuses = manager.reconcile()
            var response = SRMResponse(id: request.id, ok: true)
            response.daemon = daemonInfo()
            response.routes = statuses
            response.kernelRoutes = try? RouteTableReader.readIPv4()
            return response

        case .interfaces:
            var response = SRMResponse(id: request.id, ok: true)
            response.interfaces = NetworkInterfaces.all()
            return response

        case .kernelRoutes:
            var response = SRMResponse(id: request.id, ok: true)
            response.kernelRoutes = try? RouteTableReader.readIPv4()
            return response

        case .config:
            var response = SRMResponse(id: request.id, ok: true)
            response.routes = manager.config.routes.map {
                RouteStatus(spec: $0, state: $0.enabled ? .active : .disabled, message: nil, resolvedInterface: nil, managed: false)
            }
            response.daemon = daemonInfo()
            return response

        // 网卡/防火墙变更不允许在 dry-run 下执行
        case .nicList:
            var response = SRMResponse(id: request.id, ok: true)
            response.nics = NICManager.enumerate()
            return response

        case .nicSetServiceEnabled, .nicSetOrder, .firewallApply, .firewallRemove:
            if options.dryRun {
                return SRMResponse(id: request.id, ok: false, error: "守护进程处于 dry-run 模式，不执行系统变更")
            }
            return handleNICCommand(command, request)

        case .scanStart, .scanStatus, .scanResult, .scanStop:
            if options.dryRun {
                return SRMResponse(id: request.id, ok: false, error: "守护进程处于 dry-run 模式，不执行扫描")
            }
            return handleScanCommand(command, request)

        case .add, .update:
            guard var spec = request.spec else {
                return SRMResponse(id: request.id, ok: false, error: "缺少路由定义")
            }
            do {
                if command == .add {
                    spec = try manager.add(spec)
                } else {
                    spec = try manager.update(spec)
                }
                let statuses = manager.reconcile()
                var response = SRMResponse(id: request.id, ok: true)
                response.routes = statuses
                response.daemon = daemonInfo()
                return response
            } catch {
                return SRMResponse(id: request.id, ok: false, error: error.localizedDescription)
            }

        case .delete:
            guard let spec = request.spec else {
                return SRMResponse(id: request.id, ok: false, error: "缺少路由定义")
            }
            do {
                try manager.delete(id: spec.id)
                let statuses = manager.reconcile()
                var response = SRMResponse(id: request.id, ok: true)
                response.routes = statuses
                response.daemon = daemonInfo()
                return response
            } catch {
                return SRMResponse(id: request.id, ok: false, error: error.localizedDescription)
            }

        case .setEnabled:
            guard let spec = request.spec, let enabled = request.enabled else {
                return SRMResponse(id: request.id, ok: false, error: "缺少参数")
            }
            do {
                try manager.setEnabled(id: spec.id, enabled: enabled)
                let statuses = manager.reconcile()
                var response = SRMResponse(id: request.id, ok: true)
                response.routes = statuses
                response.daemon = daemonInfo()
                return response
            } catch {
                return SRMResponse(id: request.id, ok: false, error: error.localizedDescription)
            }

        case .apply:
            let statuses = manager.applyAll()
            var response = SRMResponse(id: request.id, ok: true)
            response.routes = statuses
            response.daemon = daemonInfo()
            return response
        }
    }

    private func handleNICCommand(_ command: SRMCommand, _ request: SRMRequest) -> SRMResponse {
        func response(nics: Bool = false) -> SRMResponse {
            var response = SRMResponse(id: request.id, ok: true)
            if nics { response.nics = NICManager.enumerate() }
            return response
        }
        func failure(_ error: Error) -> SRMResponse {
            SRMResponse(id: request.id, ok: false, error: error.localizedDescription)
        }

        do {
            switch command {
            case .nicSetServiceEnabled:
                guard let service = request.serviceName, let enabled = request.serviceEnabled else {
                    return SRMResponse(id: request.id, ok: false, error: "缺少服务名或启停参数")
                }
                try NICManager.setServiceEnabled(service, enabled: enabled)
                NSLog("srmd 网络服务已\(enabled ? "启用" : "禁用")：\(service)")
                return response(nics: true)

            case .nicSetOrder:
                guard let order = request.serviceOrder, !order.isEmpty else {
                    return SRMResponse(id: request.id, ok: false, error: "缺少服务顺序")
                }
                try NICManager.setServiceOrder(order)
                NSLog("srmd 服务顺序已更新：\(order.joined(separator: " → "))")
                return response(nics: true)

            case .firewallApply:
                try DiscoveryFirewall.apply()
                NSLog("srmd 已放行局域网发现端口 53317（PF 锚点 \(DiscoveryFirewall.anchorName)）")
                return response()

            case .firewallRemove:
                try DiscoveryFirewall.remove()
                NSLog("srmd 已移除局域网发现放行规则")
                return response()

            default:
                return SRMResponse(id: request.id, ok: false, error: "未支持的网卡命令 \(command.rawValue)")
            }
        } catch {
            return failure(error)
        }
    }

    private func handleScanCommand(_ command: SRMCommand, _ request: SRMRequest) -> SRMResponse {
        switch command {
        case .scanStart:
            guard let target = request.scanTarget else {
                return SRMResponse(id: request.id, ok: false, error: "缺少扫描目标")
            }
            switch scanJobs.start(target: target) {
            case .started(let id):
                var response = SRMResponse(id: request.id, ok: true)
                response.jobId = id
                response.scanJob = scanJobs.status()
                return response
            case .busy:
                return SRMResponse(id: request.id, ok: false, error: "已有扫描任务在执行")
            case .invalidTarget:
                return SRMResponse(id: request.id, ok: false, error: "扫描目标格式无效")
            }

        case .scanStatus:
            var response = SRMResponse(id: request.id, ok: true)
            response.scanJob = scanJobs.status()
            return response

        case .scanResult:
            var response = SRMResponse(id: request.id, ok: true)
            response.scanJob = scanJobs.result()
            return response

        case .scanStop:
            scanJobs.stop()
            var response = SRMResponse(id: request.id, ok: true)
            response.scanJob = scanJobs.status()
            return response

        default:
            return SRMResponse(id: request.id, ok: false, error: "未支持的扫描命令 \(command.rawValue)")
        }
    }

    private func daemonInfo() -> DaemonInfo {
        DaemonInfo(
            version: toolVersion,
            pid: Int(ProcessInfo.processInfo.processIdentifier),
            startedAt: startedAt,
            configPath: options.configPath,
            socketPath: options.socketPath,
            authorizedUID: manager.config.authorizedUID,
            pollInterval: options.pollInterval ?? manager.config.pollInterval,
            lastReconcile: manager.lastReconcile,
            lastError: lastError ?? manager.lastError,
            isDryRun: options.dryRun
        )
    }
}

let options = parseArguments()
if options.showVersion {
    print("srmd \(toolVersion)")
    exit(0)
}

do {
    let daemon = try Daemon(options: options)
    try daemon.run()
} catch {
    FileHandle.standardError.write(Data("srmd 启动失败：\(error.localizedDescription)\n".utf8))
    exit(1)
}
