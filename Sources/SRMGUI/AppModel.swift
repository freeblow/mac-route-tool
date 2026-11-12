import Foundation
import SwiftUI
import SRMCore

@MainActor
final class AppModel: ObservableObject {
    enum EditorTarget: Identifiable {
        case new
        case edit(RouteSpec)

        var id: String {
            switch self {
            case .new: return "new"
            case .edit(let spec): return spec.id.uuidString
            }
        }
    }

    @Published var routes: [RouteStatus] = []
    @Published var interfaces: [NetInterface] = []
    @Published var daemon: DaemonInfo?
    /// 守护进程版本落后于应用：新命令在旧守护进程上只会报「未知命令」，
    /// 检测到后横幅提示一键修复，而不是让用户面对一堆原始报错。
    @Published var daemonOutdated = false
    @Published var errorMessage: String?
    @Published var connectionError: String?
    @Published var isRefreshing = false
    @Published var isInstallingService = false
    @Published var editorTarget: EditorTarget?
    @Published var probeResults: [UUID: RouteProbeResult] = [:]
    @Published var probingRoutes: Set<UUID> = []

    // 网卡管理
    @Published var nics: [NICInfo] = []
    @Published var originalServiceOrder: [String] = []
    @Published var kernelRoutes: [KernelRoute] = []
    @Published var firewallApplied = false

    // 子网扫描
    @Published var scanRows: [ScanRow] = []
    @Published var scanPhase: ScanJobStatus.Phase = .idle
    @Published var scanDone = 0
    @Published var scanTotal = 0
    @Published var scanSummary: ScanSummary?
    @Published var scanUsesDaemon = false
    @Published var isScanning = false
    @Published var lastScanTarget: String?

    // 局域网发现
    @Published var watchResult: DiscoveryWatchResult?
    @Published var isWatching = false
    @Published var isShoutRepeat = false

    private var timer: Timer?
    private var requestID = 0
    private var activeScanner: SubnetScanner?
    private var scanPollTimer: Timer?
    private var shoutTimer: Timer?
    private var fetchScanResult = false
    private var scanResultRequested = false

    func start() {
        refresh()
        timer = Timer.scheduledTimer(withTimeInterval: 4, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.refresh() }
        }
    }

    func stop() {
        timer?.invalidate()
        timer = nil
        scanPollTimer?.invalidate()
        scanPollTimer = nil
        stopShoutRepeat()
    }

    func refresh() {
        isRefreshing = true
        perform(.list, silent: true) { [weak self] response in
            self?.apply(response)
        }
    }

    func applyNow() {
        perform(.apply) { [weak self] response in
            self?.apply(response)
        }
    }

    func setEnabled(_ route: RouteStatus, enabled: Bool) {
        perform(.setEnabled, spec: route.spec, enabled: enabled) { [weak self] response in
            self?.apply(response)
        }
    }

    func delete(_ route: RouteStatus) {
        perform(.delete, spec: route.spec) { [weak self] response in
            self?.apply(response)
        }
    }

    func save(_ spec: RouteSpec, isNew: Bool) {
        probeResults.removeValue(forKey: spec.id)
        perform(isNew ? .add : .update, spec: spec) { [weak self] response in
            self?.apply(response)
        }
    }

    func testConnectivity(_ route: RouteStatus) {
        guard !probingRoutes.contains(route.id) else { return }
        probingRoutes.insert(route.id)
        probeResults.removeValue(forKey: route.id)
        let id = route.id
        let spec = route.spec
        DispatchQueue.global(qos: .userInitiated).async {
            let result = RouteProbe.test(spec)
            DispatchQueue.main.async { [weak self] in
                MainActor.assumeIsolated {
                    guard let self else { return }
                    self.probingRoutes.remove(id)
                    if self.routes.contains(where: { $0.id == id && $0.spec == spec }) {
                        self.probeResults[id] = result
                    }
                }
            }
        }
    }

    func loadInterfaces() {
        perform(.interfaces) { [weak self] response in
            guard let self else { return }
            if response.ok { self.interfaces = response.interfaces ?? [] }
        }
    }

    // MARK: - 网卡管理

    func loadNICs() {
        perform(.nicList) { [weak self] response in
            guard let self, response.ok else { return }
            self.nics = response.nics ?? []
            if self.originalServiceOrder.isEmpty {
                self.originalServiceOrder = self.nics.map(\.serviceName)
            }
        }
        refreshFirewallState()
    }

    /// PF 放行状态读 /etc/pf.conf（全局可读），GUI 本地判断即可。
    func refreshFirewallState() {
        firewallApplied = DiscoveryFirewall.isApplied()
    }

    func moveNIC(_ nic: NICInfo, up: Bool) {
        var order = nics.map(\.serviceName)
        guard let index = order.firstIndex(of: nic.serviceName) else { return }
        let target = up ? index - 1 : index + 1
        guard order.indices.contains(target) else { return }
        order.swapAt(index, target)
        perform(.nicSetOrder, serviceOrder: order) { [weak self] response in
            guard let self, response.ok else { return }
            self.nics = response.nics ?? []
        }
    }

    func restoreServiceOrder() {
        guard !originalServiceOrder.isEmpty else { return }
        perform(.nicSetOrder, serviceOrder: originalServiceOrder) { [weak self] response in
            guard let self, response.ok else { return }
            self.nics = response.nics ?? []
        }
    }

    func setServiceEnabled(_ nic: NICInfo, enabled: Bool) {
        perform(.nicSetServiceEnabled, serviceName: nic.serviceName, serviceEnabled: enabled) { [weak self] response in
            guard let self, response.ok else { return }
            self.nics = response.nics ?? []
        }
    }

    func applyFirewall() {
        perform(.firewallApply) { [weak self] response in
            guard let self else { return }
            if response.ok { self.refreshFirewallState() }
        }
    }

    func removeFirewall() {
        perform(.firewallRemove) { [weak self] response in
            guard let self else { return }
            if response.ok { self.refreshFirewallState() }
        }
    }

    // MARK: - 局域网发现

    func addDiscoveryRoute(for nic: NICInfo) {
        guard !nic.deviceName.isEmpty else {
            errorMessage = "该服务没有对应的网络设备"
            return
        }
        let spec = RouteSpec(
            destination: "224.0.0.167/32",
            gateway: nil,
            interfaceName: nic.deviceName,
            direct: true,
            enabled: true,
            note: "局域网发现组播"
        )
        perform(.add, spec: spec) { [weak self] response in
            self?.apply(response)
        }
    }

    func removeDiscoveryRoute(for nic: NICInfo) {
        guard let route = routes.first(where: {
            $0.spec.destination == "224.0.0.167/32" && $0.spec.interfaceName == nic.deviceName
        }) else {
            errorMessage = "没有找到该网卡的发现路由"
            return
        }
        perform(.delete, spec: route.spec) { [weak self] response in
            self?.apply(response)
        }
    }

    func hasDiscoveryRoute(for nic: NICInfo) -> Bool {
        routes.contains {
            $0.spec.destination == "224.0.0.167/32" && $0.spec.interfaceName == nic.deviceName
        }
    }

    private var localizedName: String {
        Host.current().localizedName ?? ProcessInfo.processInfo.hostName
    }

    func shoutOnce(from nic: NICInfo) {
        guard let cidr = nic.ipv4Addresses.first,
              let sourceIP = cidr.split(separator: "/").first.map(String.init) else {
            errorMessage = "该网卡没有 IPv4 地址，无法发送宣告"
            return
        }
        let payload = DiscoveryShout.announcePayload(alias: localizedName)
        DispatchQueue.global(qos: .userInitiated).async {
            let failure: String?
            do {
                try DiscoveryShout.send(payload: payload, from: sourceIP)
                failure = nil
            } catch {
                failure = "发送宣告失败（源地址 \(sourceIP)）"
            }
            DispatchQueue.main.async { [weak self] in
                MainActor.assumeIsolated {
                    if let failure { self?.errorMessage = failure }
                }
            }
        }
    }

    func toggleShoutRepeat(_ nic: NICInfo) {
        if isShoutRepeat {
            stopShoutRepeat()
        } else {
            shoutOnce(from: nic)
            // timer 只记 serviceName：拍发时从当前 nics 重查，DHCP 换址后按新地址发送。
            shoutServiceName = nic.serviceName
            shoutTimer = Timer.scheduledTimer(withTimeInterval: 15, repeats: true) { [weak self] _ in
                MainActor.assumeIsolated { self?.repeatShout() }
            }
            isShoutRepeat = true
        }
    }

    private var shoutServiceName: String?

    private func repeatShout() {
        guard let serviceName = shoutServiceName,
              let nic = nics.first(where: { $0.serviceName == serviceName }) else {
            // 网卡已被移除：自动停止重复宣告。
            stopShoutRepeat()
            return
        }
        shoutOnce(from: nic)
    }

    private func stopShoutRepeat() {
        shoutTimer?.invalidate()
        shoutTimer = nil
        shoutServiceName = nil
        isShoutRepeat = false
    }

    func watchDiscovery() {
        guard !isWatching else { return }
        isWatching = true
        watchResult = nil
        DispatchQueue.global(qos: .userInitiated).async {
            let result = Result { try DiscoveryWatcher.watch(seconds: 10) }
            DispatchQueue.main.async { [weak self] in
                MainActor.assumeIsolated {
                    guard let self else { return }
                    self.isWatching = false
                    switch result {
                    case .success(let value):
                        self.watchResult = value
                    case .failure(let error):
                        self.errorMessage = "听听看失败：\(error.localizedDescription)"
                    }
                }
            }
        }
    }

    // MARK: - 子网扫描

    func startScan(target: String) {
        guard !isScanning else { return }
        guard let parsed = SubnetScanTarget.parse(target) else {
            errorMessage = "目标格式无效：请输入 CIDR（192.168.15.0/24）、单地址或范围（192.168.15.1-200）"
            return
        }
        scanRows = []
        scanSummary = nil
        scanPhase = .running
        scanDone = 0
        scanTotal = parsed.addresses.count
        lastScanTarget = target
        isScanning = true

        if scanUsesDaemon {
            perform(
                .scanStart, scanTarget: target,
                onFailure: { [weak self] response in
                    // 启动失败：复位扫描状态，避免界面卡在扫描中。
                    guard let self else { return }
                    self.isScanning = false
                    self.scanPhase = .idle
                    self.scanPollTimer?.invalidate()
                    self.scanPollTimer = nil
                    self.errorMessage = response.error ?? "启动扫描失败"
                },
                completion: { [weak self] response in
                    guard let self else { return }
                    self.applyScanJob(response.scanJob)
                    self.startScanPolling()
                }
            )
        } else {
            let scanner = SubnetScanner(
                target: parsed,
                prober: SystemScanProber(),
                routes: kernelRoutes,
                interfaces: interfaces
            )
            activeScanner = scanner
            let startedAt = Date()
            DispatchQueue.global(qos: .userInitiated).async {
                let rows = scanner.run(progress: { done, total in
                    DispatchQueue.main.async { [weak self] in
                        MainActor.assumeIsolated {
                            guard let self, self.activeScanner === scanner else { return }
                            self.scanDone = done
                            self.scanTotal = total
                        }
                    }
                })
                DispatchQueue.main.async { [weak self] in
                    MainActor.assumeIsolated {
                        guard let self, self.activeScanner === scanner else { return }
                        self.scanRows = rows
                        self.scanSummary = SubnetScanner.summarize(
                            rows: rows,
                            interfaceUsed: scanner.interfaceUsed,
                            elapsed: Date().timeIntervalSince(startedAt)
                        )
                        self.scanPhase = scanner.cancelled ? .stopped : .done
                        self.isScanning = false
                        self.activeScanner = nil
                    }
                }
            }
        }
    }

    func stopScan() {
        if scanUsesDaemon {
            perform(
                .scanStop, silent: true,
                onFailure: { [weak self] _ in
                    // 守护进程已失联：复位扫描状态，让「停止」按钮也能救回界面。
                    self?.resetScanTracking()
                },
                completion: { [weak self] response in
                    self?.applyScanJob(response.scanJob)
                }
            )
        } else {
            activeScanner?.cancel()
        }
    }

    /// 连接层失败（守护进程死亡等）时复位扫描状态，避免轮询永不停止。
    /// 只处理失败路径，不与正常终态（fetchScanResult/scanResultRequested）冲突。
    private func resetScanTracking() {
        isScanning = false
        scanPhase = .idle
        scanPollTimer?.invalidate()
        scanPollTimer = nil
        fetchScanResult = false
        scanResultRequested = false
    }

    private func applyScanJob(_ job: ScanJobStatus?) {
        guard let job else { return }
        scanPhase = job.phase
        scanDone = job.done
        scanTotal = job.total
        if job.phase == .running { return }
        if !fetchScanResult {
            // 守护进程模式下 .scanStatus 不携带明细：终态先记下，保持轮询，
            // 等下一拍单独拉取一次 .scanResult 再停止。
            fetchScanResult = true
            return
        }
        isScanning = false
        scanPollTimer?.invalidate()
        scanPollTimer = nil
        if let rows = job.rows {
            scanRows = rows
            scanSummary = SubnetScanner.summarize(rows: rows, interfaceUsed: nil, elapsed: 0)
        }
    }

    private func startScanPolling() {
        fetchScanResult = false
        scanResultRequested = false
        scanPollTimer?.invalidate()
        scanPollTimer = Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self, self.isScanning else { return }
                if self.scanPhase == .running {
                    self.perform(
                        .scanStatus, silent: true,
                        onFailure: { [weak self] _ in
                            // 守护进程中途死亡：复位扫描状态，轮询随之停止。
                            self?.resetScanTracking()
                        },
                        completion: { [weak self] response in
                            self?.applyScanJob(response.scanJob)
                        }
                    )
                } else if !self.scanResultRequested {
                    // 结果只取一次：即使拉取失败也不再重试，避免轮询永不停止。
                    self.scanResultRequested = true
                    self.perform(
                        .scanResult, silent: true,
                        onFailure: { [weak self] _ in
                            guard let self else { return }
                            self.isScanning = false
                            self.scanPollTimer?.invalidate()
                            self.scanPollTimer = nil
                        },
                        completion: { [weak self] response in
                            self?.applyScanJob(response.scanJob)
                        }
                    )
                }
            }
        }
    }

    func installService() {
        guard !isInstallingService else { return }
        guard let resourceURL = Bundle.main.resourceURL else {
            errorMessage = "无法找到应用资源目录"
            return
        }
        let script = resourceURL.appendingPathComponent("Service/install-service.sh")
        guard FileManager.default.isExecutableFile(atPath: script.path) else {
            errorMessage = "应用包中缺少服务安装程序，请重新安装 SRM.app"
            return
        }

        isInstallingService = true
        let command = "/bin/bash \(Self.shellQuote(script.path)) \(Self.shellQuote(NSUserName()))"
        let appleScript = "do shell script \(Self.appleScriptQuote(command)) with administrator privileges"
        let model = self
        DispatchQueue.global(qos: .userInitiated).async {
            let process = Process()
            process.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
            process.arguments = ["-e", appleScript]
            let output = Pipe()
            process.standardOutput = output
            process.standardError = output
            let failure: String?
            do {
                try process.run()
                process.waitUntilExit()
                let message = String(decoding: output.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
                    .trimmingCharacters(in: .whitespacesAndNewlines)
                failure = process.terminationStatus == 0 ? nil : message
            } catch {
                failure = error.localizedDescription
            }
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    model.isInstallingService = false
                    if let failure, !failure.contains("(-128)") {
                        model.errorMessage = "安装守护进程失败：\(failure)"
                    } else {
                        model.refresh()
                    }
                }
            }
        }
    }

    private static func shellQuote(_ value: String) -> String {
        "'" + value.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }

    private static func appleScriptQuote(_ value: String) -> String {
        "\"" + value.replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\"", with: "\\\"") + "\""
    }

    // MARK: - Internals

    private func perform(
        _ command: SRMCommand,
        spec: RouteSpec? = nil,
        enabled: Bool? = nil,
        serviceName: String? = nil,
        serviceEnabled: Bool? = nil,
        serviceOrder: [String]? = nil,
        scanTarget: String? = nil,
        silent: Bool = false,
        onFailure: (@MainActor (SRMResponse) -> Void)? = nil,
        completion: @escaping @MainActor (SRMResponse) -> Void
    ) {
        let request = SRMRequest(
            id: nextID(), command: command, spec: spec, enabled: enabled,
            serviceName: serviceName, serviceEnabled: serviceEnabled,
            serviceOrder: serviceOrder, scanTarget: scanTarget)
        requestAsync(request) { [weak self] response in
            guard let self else { return }
            if response.ok {
                self.errorMessage = nil
                self.connectionError = nil
                completion(response)
            } else {
                self.isRefreshing = false
                if response.error?.contains("未知命令") == true {
                    // 旧守护进程不认识新命令：按版本过旧处理，横幅比逐条报错有用。
                    self.daemonOutdated = true
                }
                if silent {
                    // 后台刷新失败：只在状态区提示，不弹窗打断。
                    self.daemon = nil
                    self.connectionError = response.error
                } else {
                    self.errorMessage = response.error ?? "操作失败"
                }
                onFailure?(response)
            }
        }
    }

    /// Nonisolated so the blocking socket call can happen off the main thread.
    private nonisolated func requestAsync(
        _ request: SRMRequest,
        completion: @escaping @MainActor (SRMResponse) -> Void
    ) {
        DispatchQueue.global(qos: .utility).async {
            let result = Result { try SRMClient.shared.send(request) }
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    switch result {
                    case .success(let response):
                        completion(response)
                    case .failure(let error):
                        completion(SRMResponse(id: request.id, ok: false, error: error.localizedDescription))
                    }
                }
            }
        }
    }

    private func apply(_ response: SRMResponse) {
        isRefreshing = false
        if response.ok {
            errorMessage = nil
            connectionError = nil
            if let routes = response.routes { self.routes = routes }
            if let daemon = response.daemon { self.daemon = daemon }
            if let kernelRoutes = response.kernelRoutes { self.kernelRoutes = kernelRoutes }
            daemonOutdated = SRMVersion.isOutdated(running: daemon?.version)
        } else {
            errorMessage = response.error
        }
    }

    private func nextID() -> Int {
        requestID += 1
        return requestID
    }
}
