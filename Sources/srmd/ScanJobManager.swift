import Foundation
import SRMCore

/// 守护进程侧的扫描任务管理器。同一时间只允许一个扫描任务。
final class ScanJobManager: @unchecked Sendable {
    enum StartOutcome {
        case started(id: String)
        case busy
        case invalidTarget
    }

    private let lock = NSLock()
    private var scanner: SubnetScanner?
    private var job = ScanJobStatus(phase: .idle, done: 0, total: 0, error: nil, rows: nil)

    func start(target: String) -> StartOutcome {
        lock.lock(); defer { lock.unlock() }
        guard job.phase != .running else { return .busy }
        guard let parsed = SubnetScanTarget.parse(target) else { return .invalidTarget }

        let scanner = SubnetScanner(
            target: parsed,
            prober: SystemScanProber(),
            routes: (try? RouteTableReader.readIPv4()) ?? [],
            interfaces: NetworkInterfaces.all()
        )
        job = ScanJobStatus(phase: .running, done: 0, total: parsed.addresses.count, error: nil, rows: nil)
        self.scanner = scanner

        let manager = self
        DispatchQueue.global(qos: .utility).async {
            let rows = scanner.run(progress: { done, total in
                manager.lock.lock()
                manager.job.done = done
                manager.job.total = total
                manager.lock.unlock()
            })
            manager.lock.lock()
            manager.job.phase = scanner.cancelled ? .stopped : .done
            manager.job.rows = rows
            manager.lock.unlock()
        }
        return .started(id: UUID().uuidString)
    }

    /// 进度查询（不含结果行）。
    func status() -> ScanJobStatus {
        lock.lock(); defer { lock.unlock() }
        var copy = job
        copy.rows = nil
        return copy
    }

    /// 完整结果（仅在 done/failed/stopped 后有 rows）。
    func result() -> ScanJobStatus {
        lock.lock(); defer { lock.unlock() }
        return job
    }

    func stop() {
        lock.lock(); defer { lock.unlock() }
        if job.phase == .running { scanner?.cancel() }
    }
}
