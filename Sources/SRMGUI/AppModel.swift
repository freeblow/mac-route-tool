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
    @Published var errorMessage: String?
    @Published var connectionError: String?
    @Published var isRefreshing = false
    @Published var isInstallingService = false
    @Published var editorTarget: EditorTarget?
    @Published var probeResults: [UUID: RouteProbeResult] = [:]
    @Published var probingRoutes: Set<UUID> = []

    private var timer: Timer?
    private var requestID = 0

    func start() {
        refresh()
        timer = Timer.scheduledTimer(withTimeInterval: 4, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.refresh() }
        }
    }

    func stop() {
        timer?.invalidate()
        timer = nil
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
        silent: Bool = false,
        completion: @escaping @MainActor (SRMResponse) -> Void
    ) {
        let request = SRMRequest(id: nextID(), command: command, spec: spec, enabled: enabled)
        requestAsync(request) { [weak self] response in
            guard let self else { return }
            if response.ok {
                self.errorMessage = nil
                self.connectionError = nil
                completion(response)
            } else {
                self.isRefreshing = false
                if silent {
                    // 后台刷新失败：只在状态区提示，不弹窗打断。
                    self.daemon = nil
                    self.connectionError = response.error
                } else {
                    self.errorMessage = response.error ?? "操作失败"
                }
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
        } else {
            errorMessage = response.error
        }
    }

    private func nextID() -> Int {
        requestID += 1
        return requestID
    }
}
