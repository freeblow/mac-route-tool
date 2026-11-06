import Foundation

public struct RouteProbeResult: Sendable {
    public let target: String
    public let isGateway: Bool
    public let reachable: Bool
    public let detail: String

    public init(target: String, isGateway: Bool, reachable: Bool, detail: String) {
        self.target = target
        self.isGateway = isGateway
        self.reachable = reachable
        self.detail = detail
    }
}

public enum RouteProbe {
    /// A network address is not a usable probe host, so test its next hop.
    public static func target(for spec: RouteSpec) -> (address: String, isGateway: Bool)? {
        if spec.prefixLength == 32 {
            return (String(spec.normalizedDestination.split(separator: "/")[0]), false)
        }
        guard let gateway = spec.normalizedGateway else { return nil }
        return (gateway, true)
    }

    public static func test(_ spec: RouteSpec) -> RouteProbeResult {
        guard let target = target(for: spec) else {
            return RouteProbeResult(target: "", isGateway: false, reachable: false,
                                    detail: "网段路由没有网关，无法选定测试主机")
        }
        do {
            let output = try ProcessRunner.run("/sbin/ping", ["-n", "-c", "1", "-W", "2000", target.address])
            if output.success {
                let latency = output.stdout.range(of: #"time[=<]([0-9.]+)\s*ms"#, options: .regularExpression)
                    .map { String(output.stdout[$0]) }
                return RouteProbeResult(target: target.address, isGateway: target.isGateway,
                                        reachable: true, detail: latency ?? "收到 ICMP 响应")
            }
            return RouteProbeResult(target: target.address, isGateway: target.isGateway,
                                    reachable: false, detail: "未收到 ICMP 响应（目标也可能禁用 Ping）")
        } catch {
            return RouteProbeResult(target: target.address, isGateway: target.isGateway,
                                    reachable: false, detail: error.localizedDescription)
        }
    }
}
