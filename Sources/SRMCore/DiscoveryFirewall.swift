import Foundation

/// LocalSend 局域网发现端口（TCP/UDP 53317 入站）的 PF 放行规则管理。
/// 变更函数（apply/remove）需要 root，由 srmd 调用；isApplied 可无权限调用。
public enum DiscoveryFirewall {
    public static let anchorName = "com.srm.localsend"
    public static let anchorFilePath = "/etc/pf.anchors/com.srm.localsend"
    public static let pfConfPath = "/etc/pf.conf"
    static let marker = "# SRM 局域网发现放行"

    /// 锚点规则文件内容。
    public static func anchorRules() -> String {
        """
        \(marker)
        pass in quick proto tcp from any to any port 53317 keep state label "SRM LocalSend"
        pass in quick proto udp from any to any port 53317 keep state label "SRM LocalSend"
        """
    }

    /// 在 pf.conf 末尾追加锚点引用；已存在时返回 nil（幂等）。
    public static func pfConfWithAnchor(_ existing: String) -> String? {
        guard !existing.contains(marker) && !existing.contains("anchor \"\(anchorName)\"") else { return nil }
        let trimmed = existing.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed
            + "\n\n\(marker)\n"
            + "anchor \"\(anchorName)\"\n"
            + "load anchor \"\(anchorName)\" from \"\(anchorFilePath)\"\n"
    }

    /// 移除锚点相关行；没有锚点时原样返回。
    public static func pfConfWithoutAnchor(_ existing: String) -> String {
        let lines = existing.split(separator: "\n", omittingEmptySubsequences: false)
        let filtered = lines.filter { !$0.contains(anchorName) && !$0.contains(marker) }
        return filtered.joined(separator: "\n")
    }

    /// GUI 可无权限调用（/etc/pf.conf 全局可读）。
    public static func isApplied() -> Bool {
        guard let conf = try? String(contentsOfFile: pfConfPath, encoding: .utf8) else { return false }
        return conf.contains(marker)
    }

    /// 由 srmd（root）调用：写锚点文件 + 更新 pf.conf + 重载 pf。
    public static func apply() throws {
        let directory = (anchorFilePath as NSString).deletingLastPathComponent
        try FileManager.default.createDirectory(atPath: directory, withIntermediateDirectories: true)
        try anchorRules().write(toFile: anchorFilePath, atomically: true, encoding: .utf8)

        let existing = (try? String(contentsOfFile: pfConfPath, encoding: .utf8)) ?? ""
        if let updated = pfConfWithAnchor(existing) {
            try updated.write(toFile: pfConfPath, atomically: true, encoding: .utf8)
        }
        try runPFCTL(["-f", pfConfPath])
    }

    /// 由 srmd（root）调用：从 pf.conf 移除引用 + 删除锚点文件 + 重载 pf。
    public static func remove() throws {
        let existing = (try? String(contentsOfFile: pfConfPath, encoding: .utf8)) ?? ""
        if existing.contains(marker) || existing.contains("anchor \"\(anchorName)\"") {
            try pfConfWithoutAnchor(existing).write(toFile: pfConfPath, atomically: true, encoding: .utf8)
        }
        try? FileManager.default.removeItem(atPath: anchorFilePath)
        try runPFCTL(["-f", pfConfPath])
    }

    static func runPFCTL(_ arguments: [String]) throws {
        let result = try ProcessRunner.run("/sbin/pfctl", arguments)
        guard result.success else { throw NICError.commandFailed("pfctl", result.trimmedError) }
    }
}
