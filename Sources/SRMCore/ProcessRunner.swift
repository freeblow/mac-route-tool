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

        // Read both pipes concurrently to avoid dead-locking on a full pipe buffer.
        var outData = Data()
        var errData = Data()
        let group = DispatchGroup()
        let queue = DispatchQueue(label: "srm.process.io", attributes: .concurrent)

        queue.async(group: group) { outData = outPipe.fileHandleForReading.readDataToEndOfFile() }
        queue.async(group: group) { errData = errPipe.fileHandleForReading.readDataToEndOfFile() }
        group.wait()
        process.waitUntilExit()

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
