import XCTest
@testable import SRMCore

/// ProcessRunner 在并发探测下被大量同时调用（扫描引擎 64 并发 × arp/ping），
/// 双管道读取必须无数据竞争——TSan 下跑并发压力锁死回归。
final class ProcessRunnerConcurrencyTests: XCTestCase {
    func testConcurrentRunsDoNotRaceOrDeadlock() throws {
        let total = 32
        let group = DispatchGroup()
        let queue = DispatchQueue(label: "srm.test.pr", attributes: .concurrent)
        let lock = NSLock()
        var failures = 0
        for _ in 0..<total {
            group.enter()
            queue.async {
                defer { group.leave() }
                do {
                    let result = try ProcessRunner.run("/bin/echo", ["process-runner-stress"])
                    if !result.success || !result.stdout.contains("process-runner-stress") {
                        lock.lock(); failures += 1; lock.unlock()
                    }
                } catch {
                    lock.lock(); failures += 1; lock.unlock()
                }
            }
        }
        let waitResult = group.wait(timeout: .now() + 30)
        XCTAssertEqual(waitResult, .success, "并发 ProcessRunner 死锁")
        XCTAssertEqual(failures, 0, "\(failures) 次并发调用失败")
    }
}
