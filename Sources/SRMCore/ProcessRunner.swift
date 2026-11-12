import Foundation

public struct ProcessResult: Sendable {
    public var exitCode: Int32
    public var stdout: String
    public var stderr: String

    public var success: Bool { exitCode == 0 }

    public var trimmedError: String {
        let text = stderr.trimmingCharacters(in: .whitespacesAndNewlines)
        return text.isEmpty ? stdout.trimmingCharacters(in: .whitespacesAndNewlines) : text
    }
}

public enum ProcessRunner {
    @discardableResult
    public static func run(_ launchPath: String, _ arguments: [String]) throws -> ProcessResult {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: launchPath)
        process.arguments = arguments

        let outPipe = Pipe()
        let errPipe = Pipe()
        defer {
            try? outPipe.fileHandleForReading.close()
            try? outPipe.fileHandleForWriting.close()
            try? errPipe.fileHandleForReading.close()
            try? errPipe.fileHandleForWriting.close()
        }
        process.standardOutput = outPipe
        process.standardError = errPipe
        process.standardInput = FileHandle.nullDevice

        do {
            try process.run()
        } catch {
            throw ProcessError.launchFailed(launchPath, error.localizedDescription)
        }

        // 双管道并发读取，避免管道缓冲区写满导致子进程与读取端互相等死。
        // 读取用独立线程而非 GCD：扫描引擎 64 并发探测会同时调用本函数，
        // 若读取工作项挤不进已被阻塞线程占满的 GCD 池，调用方在 group 等待上
        // 永久饿死（曾导致应用启动扫描即挂死）。每个读取线程持有自己的局部
        // Data，交回时加锁。
        var outData = Data()
        var errData = Data()
        let lock = NSLock()
        let readersDone = DispatchSemaphore(value: 0)

        Thread.detachNewThread {
            let data = outPipe.fileHandleForReading.readDataToEndOfFile()
            lock.lock(); outData = data; lock.unlock()
            readersDone.signal()
        }
        Thread.detachNewThread {
            let data = errPipe.fileHandleForReading.readDataToEndOfFile()
            lock.lock(); errData = data; lock.unlock()
            readersDone.signal()
        }
        readersDone.wait()
        readersDone.wait()
        process.waitUntilExit()

        lock.lock()
        defer { lock.unlock() }
        return ProcessResult(
            exitCode: process.terminationStatus,
            stdout: String(decoding: outData, as: UTF8.self),
            stderr: String(decoding: errData, as: UTF8.self)
        )
    }
}

public enum ProcessError: LocalizedError {
    case launchFailed(String, String)

    public var errorDescription: String? {
        switch self {
        case .launchFailed(let path, let detail):
            return "无法执行 \(path)：\(detail)"
        }
    }
}
