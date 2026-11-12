import Foundation

/// 应用与守护进程的版本契约：GUI 用 `expected` 与守护进程上报的版本比对，
/// 不一致即在界面提示「安装或修复守护进程」，避免旧守护进程收到新命令时
/// 一路报「未知命令」。
public enum SRMVersion {
    /// 随本版本分发的工具版本号（srmd / srm / GUI 三端一致）。
    public static let expected = "0.2.1"

    /// 解析「主.次.修订」版本号，缺省分量按 0 处理；无法解析返回 nil。
    public static func parse(_ value: String) -> (major: Int, minor: Int, patch: Int)? {
        let parts = value.split(separator: ".", omittingEmptySubsequences: false)
        guard (1...3).contains(parts.count) else { return nil }
        var numbers: [Int] = []
        for part in parts {
            guard let number = Int(part) else { return nil }
            numbers.append(number)
        }
        while numbers.count < 3 { numbers.append(0) }
        return (numbers[0], numbers[1], numbers[2])
    }

    /// 守护进程上报的版本是否落后于 `expected`。
    /// `running` 为 nil（无守护进程信息）不算过旧——那是连接问题，另有提示。
    public static func isOutdated(running: String?, expected: String = SRMVersion.expected) -> Bool {
        guard let running, let actual = parse(running), let wanted = parse(expected) else {
            return running != nil
        }
        if actual.major != wanted.major { return actual.major < wanted.major }
        if actual.minor != wanted.minor { return actual.minor < wanted.minor }
        return actual.patch < wanted.patch
    }
}
