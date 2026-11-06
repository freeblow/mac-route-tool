import Foundation

public enum RouteApplier {
    public static let routePath = "/sbin/route"

    public struct Outcome: Sendable {
        public var success: Bool
        public var message: String
        public var arguments: [String]

        public static func ok(_ message: String = "ok", arguments: [String] = []) -> Outcome {
            Outcome(success: true, message: message, arguments: arguments)
        }

        public static func failure(_ message: String, arguments: [String] = []) -> Outcome {
            Outcome(success: false, message: message, arguments: arguments)
        }
    }

    /// Gateway routes stay unscoped so ordinary traffic can select them.
    /// The requested interface is checked against the gateway's route by RouteManager.
    public static func addArguments(for spec: RouteSpec) -> [String] {
        var arguments = ["-n", "add", "-net"]
        if spec.direct, let name = spec.interfaceName, !name.isEmpty {
            arguments += ["-interface", name]
        }
        arguments.append(spec.normalizedDestination)
        if let gateway = spec.normalizedGateway {
            arguments.append(gateway)
        }
        return arguments
    }

    public static func deleteArguments(for spec: RouteSpec) -> [String] {
        var arguments = ["-n", "delete", "-net"]
        arguments.append(spec.normalizedDestination)
        if let gateway = spec.normalizedGateway {
            arguments.append(gateway)
        }
        return arguments
    }

    /// Delete using the exact fields reported by the kernel.
    public static func deleteArguments(for route: KernelRoute) -> [String] {
        var arguments = ["-n", "delete", "-net"]
        if route.isInterfaceScoped, let name = route.interfaceName {
            arguments += ["-ifscope", name]
        }
        arguments.append(route.destination)
        if IPv4.isValidAddress(route.gateway) {
            arguments.append(route.gateway)
        }
        return arguments
    }

    @discardableResult
    public static func add(_ spec: RouteSpec) -> Outcome {
        guard spec.normalizedGateway != nil else {
            return .failure("缺少网关地址，无法写入路由表")
        }
        let arguments = addArguments(for: spec)
        let result = execute(arguments)
        if result.success {
            return .ok("已写入", arguments: arguments)
        }
        // macOS 的 route(8) 对修饰符位置在不同版本上表现不一致：
        // 修饰符放在目标地址之前是文档写法，若解析失败则改用「目标在前」的写法再试一次。
        if looksLikeSyntaxError(result.trimmedError), let alternative = alternativeArguments(for: spec) {
            let retry = execute(alternative)
            if retry.success {
                return .ok("已写入", arguments: alternative)
            }
            return .failure(retry.trimmedError.isEmpty ? "route add 失败" : retry.trimmedError, arguments: alternative)
        }
        return .failure(result.trimmedError.isEmpty ? "route add 失败" : result.trimmedError, arguments: arguments)
    }

    private static func execute(_ arguments: [String]) -> ProcessResult {
        (try? ProcessRunner.run(routePath, arguments)) ?? ProcessResult(exitCode: -1, stdout: "", stderr: "无法执行 \(routePath)")
    }

    private static func looksLikeSyntaxError(_ message: String) -> Bool {
        let lowered = message.lowercased()
        return ["usage", "expected", "illegal", "unknown option", "bad address", "not supported"]
            .contains { lowered.contains($0) }
    }

    /// 「目标在前、修饰符在后」的备选参数顺序。
    private static func alternativeArguments(for spec: RouteSpec) -> [String]? {
        guard spec.direct, let name = spec.interfaceName, !name.isEmpty else { return nil }
        var arguments = ["-n", "add", "-net", spec.normalizedDestination]
        arguments += ["-interface", name]
        if let gateway = spec.normalizedGateway {
            arguments.append(gateway)
        }
        return arguments
    }

    @discardableResult
    public static func remove(_ spec: RouteSpec) -> Outcome {
        let arguments = deleteArguments(for: spec)
        do {
            let result = try ProcessRunner.run(routePath, arguments)
            if result.success {
                return .ok("已删除", arguments: arguments)
            }
            return .failure(result.trimmedError.isEmpty ? "route delete 失败" : result.trimmedError, arguments: arguments)
        } catch {
            return .failure(error.localizedDescription, arguments: arguments)
        }
    }

    @discardableResult
    public static func remove(_ route: KernelRoute) -> Outcome {
        let arguments = deleteArguments(for: route)
        do {
            let result = try ProcessRunner.run(routePath, arguments)
            if result.success {
                return .ok("已删除", arguments: arguments)
            }
            return .failure(result.trimmedError.isEmpty ? "route delete 失败" : result.trimmedError, arguments: arguments)
        } catch {
            return .failure(error.localizedDescription, arguments: arguments)
        }
    }

    /// Ask the kernel which interface it would use to reach `destination`.
    public static func resolveInterface(for destination: String) -> String? {
        guard let result = try? ProcessRunner.run(routePath, ["-n", "get", destination]), result.success else {
            return nil
        }
        for line in result.stdout.split(separator: "\n") {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if trimmed.hasPrefix("interface:") {
                let name = trimmed.dropFirst("interface:".count).trimmingCharacters(in: .whitespaces)
                return name.isEmpty ? nil : name
            }
        }
        return nil
    }
}
