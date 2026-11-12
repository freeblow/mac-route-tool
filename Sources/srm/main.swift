import Foundation
import SRMCore

let toolVersion = SRMVersion.expected

let rawArguments = Array(CommandLine.arguments.dropFirst())
var socketPath = SocketPaths.socket
var arguments: [String] = []
var cursor = 0
while cursor < rawArguments.count {
    if rawArguments[cursor] == "--socket", cursor + 1 < rawArguments.count {
        socketPath = rawArguments[cursor + 1]
        cursor += 2
        continue
    }
    arguments.append(rawArguments[cursor])
    cursor += 1
}
let client = SRMClient(socketPath: socketPath)

func fail(_ message: String) -> Never {
    FileHandle.standardError.write(Data("srm: \(message)\n".utf8))
    exit(1)
}

func request(_ command: SRMCommand, spec: RouteSpec? = nil, enabled: Bool? = nil) -> SRMResponse {
    do {
        return try client.send(SRMRequest(id: Int(Date().timeIntervalSince1970 * 1000), command: command, spec: spec, enabled: enabled))
    } catch {
        fail(error.localizedDescription)
    }
}

func symbol(for state: RouteState) -> String {
    switch state {
    case .active: return "✓"
    case .pending: return "…"
    case .failed: return "✗"
    case .disabled: return "○"
    case .unmanaged: return "·"
    }
}

func pad(_ text: String, _ width: Int) -> String {
    let count = text.count
    return count >= width ? text + " " : text + String(repeating: " ", count: width - count)
}

func printRoutes(_ routes: [RouteStatus]) {
    if routes.isEmpty {
        print("（没有受管理的路由）")
        return
    }
    print(pad("", 2) + pad("目标网段", 20) + pad("网关", 18) + pad("接口", 10) + "状态")
    for route in routes {
        let interface = route.spec.interfaceName ?? "自动"
        let message = route.message.map { "（\($0)）" } ?? ""
        let stateText: String
        switch route.state {
        case .active: stateText = "生效"
        case .pending: stateText = "等待中"
        case .failed: stateText = "失败"
        case .disabled: stateText = "已停用"
        case .unmanaged: stateText = "未接管"
        }
        print(pad(symbol(for: route.state), 2) + pad(route.spec.normalizedDestination, 20) + pad(route.spec.normalizedGateway ?? "-", 18) + pad(interface, 10) + stateText + " " + message)
    }
}


guard let command = arguments.first else {
    print("""
    srm — 静态路由管理 CLI（\(toolVersion)）

    用法:
      srm list                 列出受管理的路由及其状态
      srm status               查看守护进程状态
      srm interfaces           列出网络接口
      srm kernel               打印内核路由表
      srm add <网段> <网关> [--iface en0] [--direct] [--off] [--note 文本]
      srm del <网段>           删除受管理的路由
      srm enable <网段>        启用路由
      srm disable <网段>       停用路由（并从内核移除）
      srm apply                强制重写全部路由
    """)
    exit(0)
}

switch command {
case "list":
    let response = request(.list)
    guard response.ok else { fail(response.error ?? "未知错误") }
    printRoutes(response.routes ?? [])

case "status":
    let response = request(.status)
    guard response.ok, let info = response.daemon else { fail(response.error ?? "守护进程未响应") }
    print("srmd \(info.version) pid=\(info.pid)")
    print("配置: \(info.configPath)")
    print("Socket: \(info.socketPath)")
    print("对齐周期: \(info.pollInterval)s")
    if let uid = info.authorizedUID { print("授权用户 uid: \(uid)") }
    if let last = info.lastReconcile {
        print("最近对齐: \(ISO8601DateFormatter().string(from: last))")
    }
    if let error = info.lastError { print("最近错误: \(error)") }

case "interfaces":
    let response = request(.interfaces)
    guard response.ok else { fail(response.error ?? "未知错误") }
    for item in response.interfaces ?? [] {
        let state = item.isUsable ? "up" : (item.isUp ? "no-addr" : "down")
        print("\(item.name.padding(toLength: 8, withPad: " ", startingAt: 0)) \(state.padding(toLength: 8, withPad: " ", startingAt: 0)) \(item.ipv4Addresses.joined(separator: ", "))")
    }

case "kernel":
    let response = request(.kernelRoutes)
    guard response.ok else { fail(response.error ?? "未知错误") }
    for route in response.kernelRoutes ?? [] {
        print("\(route.destination.padding(toLength: 20, withPad: " ", startingAt: 0)) \(route.gateway.padding(toLength: 16, withPad: " ", startingAt: 0)) \(route.flags.padding(toLength: 8, withPad: " ", startingAt: 0)) \(route.interfaceName ?? "-")")
    }

case "add":
    guard arguments.count >= 3 else { fail("用法: srm add <网段> <网关> [--iface en0]") }
    var spec = RouteSpec(destination: arguments[1], gateway: arguments[2])
    var index = 3
    while index < arguments.count {
        switch arguments[index] {
        case "--iface":
            index += 1
            spec.interfaceName = index < arguments.count ? arguments[index] : nil
        case "--direct":
            spec.direct = true
        case "--off":
            spec.enabled = false
        case "--note":
            index += 1
            spec.note = index < arguments.count ? arguments[index] : ""
        default:
            fail("未知参数 \(arguments[index])")
        }
        index += 1
    }
    let response = request(.add, spec: spec)
    guard response.ok else { fail(response.error ?? "未知错误") }
    printRoutes(response.routes ?? [])

case "del":
    guard arguments.count >= 2 else { fail("用法: srm del <网段>") }
    let target = arguments[1]
    let listing = request(.list)
    guard listing.ok else { fail(listing.error ?? "未知错误") }
    let normalized = RouteSpec.normalizeDestination(target)
    guard let match = (listing.routes ?? []).first(where: { $0.spec.normalizedDestination == normalized || $0.spec.destination == target }) else {
        fail("未找到受管理的路由 \(target)")
    }
    let response = request(.delete, spec: match.spec)
    guard response.ok else { fail(response.error ?? "未知错误") }
    printRoutes(response.routes ?? [])

case "enable", "disable":
    guard arguments.count >= 2 else { fail("用法: srm \(command) <网段>") }
    let target = arguments[1]
    let listing = request(.list)
    guard listing.ok else { fail(listing.error ?? "未知错误") }
    let normalized = RouteSpec.normalizeDestination(target)
    guard let match = (listing.routes ?? []).first(where: { $0.spec.normalizedDestination == normalized || $0.spec.destination == target }) else {
        fail("未找到受管理的路由 \(target)")
    }
    let response = request(.setEnabled, spec: match.spec, enabled: command == "enable")
    guard response.ok else { fail(response.error ?? "未知错误") }
    printRoutes(response.routes ?? [])

case "apply":
    let response = request(.apply)
    guard response.ok else { fail(response.error ?? "未知错误") }
    printRoutes(response.routes ?? [])

default:
    fail("未知命令 \(command)")
}
