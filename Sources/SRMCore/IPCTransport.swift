import Darwin
import Foundation

public enum SocketPaths {
    /// 允许通过环境变量覆盖，便于测试与非标准部署。
    public static var directory: String {
        ProcessInfo.processInfo.environment["SRM_SOCKET_DIR"] ?? "/var/run/srm"
    }

    public static var socket: String {
        ProcessInfo.processInfo.environment["SRM_SOCKET"] ?? "\(directory)/srmd.sock"
    }
}

public enum IPCError: LocalizedError {
    case notRunning(String)
    case connectionFailed(String, String)
    case encodingFailed
    case decodingFailed(String)
    case timedOut
    case ioFailed(String)

    public var errorDescription: String? {
        switch self {
        case .notRunning(let path):
            return "无法连接守护进程（\(path)）：请确认已安装并启动 srmd"
        case .connectionFailed(let path, let detail):
            return "无法连接守护进程（\(path)）：\(detail)"
        case .encodingFailed:
            return "请求编码失败"
        case .decodingFailed(let detail):
            return "响应解析失败：\(detail)"
        case .timedOut:
            return "守护进程响应超时"
        case .ioFailed(let detail):
            return "通信失败：\(detail)"
        }
    }
}

/// Blocking line-delimited JSON client for the privileged daemon.
public final class SRMClient: @unchecked Sendable {
    public let socketPath: String

    public init(socketPath: String = SocketPaths.socket) {
        self.socketPath = socketPath
    }

    /// Shared client used by the GUI and the CLI.
    public static let shared = SRMClient()

    public func send(_ request: SRMRequest, timeout: TimeInterval = 15) throws -> SRMResponse {
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else {
            throw IPCError.connectionFailed(socketPath, String(cString: strerror(errno)))
        }
        defer { close(fd) }

        var address = sockaddr_un()
        address.sun_family = sa_family_t(AF_UNIX)
        let pathBytes = Array(socketPath.utf8)
        let capacity = MemoryLayout.size(ofValue: address.sun_path)
        guard pathBytes.count < capacity else {
            throw IPCError.connectionFailed(socketPath, "socket 路径过长")
        }
        withUnsafeMutableBytes(of: &address.sun_path) { raw in
            raw.copyBytes(from: pathBytes)
            raw[pathBytes.count] = 0
        }

        var tv = timeval(tv_sec: Int(timeout), tv_usec: 0)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
        setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))

        let connected = withUnsafePointer(to: &address) { pointer -> Int32 in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockaddrPointer in
                Darwin.connect(fd, sockaddrPointer, socklen_t(MemoryLayout<sockaddr_un>.size))
            }
        }
        guard connected == 0 else {
            let detail = String(cString: strerror(errno))
            if detail.contains("No such file") || detail.contains("Connection refused") {
                throw IPCError.notRunning(socketPath)
            }
            throw IPCError.connectionFailed(socketPath, detail)
        }

        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        var payload = try encoder.encode(request)
        payload.append(0x0A)

        try writeAll(fd, payload)

        guard let line = try readLine(fd) else {
            throw IPCError.ioFailed("连接被关闭")
        }

        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        guard let response = try? decoder.decode(SRMResponse.self, from: line) else {
            throw IPCError.decodingFailed(String(decoding: line, as: UTF8.self))
        }
        return response
    }

    private func writeAll(_ fd: Int32, _ data: Data) throws {
        try data.withUnsafeBytes { raw in
            guard let base = raw.baseAddress else { return }
            var offset = 0
            while offset < data.count {
                let written = Darwin.write(fd, base.advanced(by: offset), data.count - offset)
                if written <= 0 {
                    if errno == EINTR { continue }
                    throw IPCError.ioFailed(String(cString: strerror(errno)))
                }
                offset += written
            }
        }
    }

    private func readLine(_ fd: Int32) throws -> Data? {
        var buffer = Data()
        var byte: UInt8 = 0
        while true {
            let count = Darwin.read(fd, &byte, 1)
            if count == 0 { return buffer.isEmpty ? nil : buffer }
            if count < 0 {
                if errno == EINTR { continue }
                if errno == EAGAIN || errno == EWOULDBLOCK { throw IPCError.timedOut }
                throw IPCError.ioFailed(String(cString: strerror(errno)))
            }
            if byte == 0x0A { return buffer }
            buffer.append(byte)
        }
    }
}

/// Accepts connections on a unix domain socket and dispatches JSON requests.
public final class SocketServer {
    public typealias Handler = (SRMRequest, Int) -> SRMResponse

    private let socketPath: String
    private let handler: Handler
    private var listenFD: Int32 = -1
    private var running = false
    private let lock = NSLock()

    public init(socketPath: String = SocketPaths.socket, handler: @escaping Handler) {
        self.socketPath = socketPath
        self.handler = handler
    }

    public func start() throws {
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else {
            throw IPCError.ioFailed(String(cString: strerror(errno)))
        }

        var address = sockaddr_un()
        address.sun_family = sa_family_t(AF_UNIX)
        let pathBytes = Array(socketPath.utf8)
        let capacity = MemoryLayout.size(ofValue: address.sun_path)
        guard pathBytes.count < capacity else {
            close(fd)
            throw IPCError.ioFailed("socket 路径过长")
        }
        withUnsafeMutableBytes(of: &address.sun_path) { raw in
            raw.copyBytes(from: pathBytes)
            raw[pathBytes.count] = 0
        }

        unlink(socketPath)
        let bindResult = withUnsafePointer(to: &address) { pointer -> Int32 in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockaddrPointer in
                Darwin.bind(fd, sockaddrPointer, socklen_t(MemoryLayout<sockaddr_un>.size))
            }
        }
        guard bindResult == 0 else {
            let detail = String(cString: strerror(errno))
            close(fd)
            throw IPCError.ioFailed("bind \(socketPath) 失败：\(detail)")
        }
        // Owner-only: the daemon additionally validates the peer uid.
        chmod(socketPath, 0o600)

        guard Darwin.listen(fd, 16) == 0 else {
            let detail = String(cString: strerror(errno))
            close(fd)
            unlink(socketPath)
            throw IPCError.ioFailed("listen 失败：\(detail)")
        }

        listenFD = fd
        running = true
        Thread.detachNewThread { [weak self] in self?.acceptLoop() }
    }

    public func stop() {
        lock.lock()
        running = false
        let fd = listenFD
        listenFD = -1
        lock.unlock()
        if fd >= 0 {
            close(fd)
            shutdown(fd, SHUT_RDWR)
        }
        unlink(socketPath)
    }

    private func acceptLoop() {
        while true {
            lock.lock()
            let fd = listenFD
            let active = running
            lock.unlock()
            guard active, fd >= 0 else { break }

            let client = accept(fd, nil, nil)
            if client < 0 {
                if errno == EINTR { continue }
                break
            }
            handle(client)
            close(client)
        }
    }

    private func handle(_ client: Int32) {
        var uid: uid_t = 0
        var gid: gid_t = 0
        let peerUID = getpeereid(client, &uid, &gid) == 0 ? Int(uid) : -1

        guard let line = readLine(client) else { return }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        guard let request = try? decoder.decode(SRMRequest.self, from: line) else {
            let response = SRMResponse(id: -1, ok: false, error: "无法解析请求")
            write(response, to: client)
            return
        }

        let response = handler(request, peerUID)
        write(response, to: client)
    }

    private func write(_ response: SRMResponse, to fd: Int32) {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        guard var data = try? encoder.encode(response) else { return }
        data.append(0x0A)
        data.withUnsafeBytes { raw in
            guard let base = raw.baseAddress else { return }
            var offset = 0
            while offset < data.count {
                let written = Darwin.write(fd, base.advanced(by: offset), data.count - offset)
                if written <= 0 {
                    if errno == EINTR { continue }
                    return
                }
                offset += written
            }
        }
    }

    private func readLine(_ fd: Int32) -> Data? {
        var buffer = Data()
        var byte: UInt8 = 0
        while true {
            let count = Darwin.read(fd, &byte, 1)
            if count == 0 { return buffer.isEmpty ? nil : buffer }
            if count < 0 {
                if errno == EINTR { continue }
                return nil
            }
            if byte == 0x0A { return buffer }
            buffer.append(byte)
        }
    }
}
