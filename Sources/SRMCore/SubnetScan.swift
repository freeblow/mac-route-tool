import Foundation

/// 子网扫描目标：CIDR 段 / 单地址 / 地址范围。
public struct SubnetScanTarget: Equatable, Sendable {
    public enum Kind: Equatable, Sendable {
        case subnet(prefix: Int)
        case single
        case range
    }

    public static let maxAddresses = 2048

    public let addresses: [UInt32]
    public let kind: Kind
    public let displayText: String

    public init(addresses: [UInt32], kind: Kind, displayText: String) {
        self.addresses = addresses
        self.kind = kind
        self.displayText = displayText
    }

    /// 解析失败返回 nil。接受 `192.168.15.0/24`（整段，含网络/广播地址，仅 /30 及更小）、
    /// 单地址 `192.168.15.66`、范围 `192.168.15.1-200` 或全写 `1.2.3.1-1.2.3.200`。
    /// 横线接受全角与 Unicode 破折号变体。
    public static func parse(_ raw: String) -> SubnetScanTarget? {
        let normalized = normalize(raw)
        guard !normalized.isEmpty else { return nil }

        if normalized.contains("/") {
            let parts = normalized.split(separator: "/", maxSplits: 1, omittingEmptySubsequences: false)
            guard parts.count == 2,
                  let base = IPv4.parse(String(parts[0])),
                  let prefix = Int(parts[1]), (0...30).contains(prefix) else { return nil }
            let network = IPv4.network(base, prefix: prefix)
            let size = UInt64(1) << UInt64(32 - prefix)
            guard size <= UInt64(maxAddresses) else { return nil }
            let addresses = (0..<Int(size)).map { network + UInt32($0) }
            return SubnetScanTarget(addresses: addresses, kind: .subnet(prefix: prefix), displayText: normalized)
        }

        if normalized.contains("-") {
            let parts = normalized.split(separator: "-", maxSplits: 1, omittingEmptySubsequences: false)
            guard parts.count == 2, let start = IPv4.parse(String(parts[0])) else { return nil }
            let endText = String(parts[1])
            let end: UInt32
            if endText.contains(".") {
                guard let parsed = IPv4.parse(endText) else { return nil }
                end = parsed
            } else {
                guard endText.allSatisfy({ $0.isASCII && $0.isNumber }),
                      let last = UInt32(endText), last <= 255 else { return nil }
                end = (start & 0xffffff00) | last
            }
            guard start <= end, UInt64(end) - UInt64(start) + 1 <= UInt64(maxAddresses) else { return nil }
            let addresses = (0...(end - start)).map { start + $0 }
            return SubnetScanTarget(addresses: addresses, kind: .range, displayText: normalized)
        }

        guard let address = IPv4.parse(normalized) else { return nil }
        return SubnetScanTarget(addresses: [address], kind: .single, displayText: normalized)
    }

    /// 全角/Unicode 横线归一为半角，去掉首尾空白。
    static func normalize(_ raw: String) -> String {
        raw.replacingOccurrences(of: "－", with: "-")
            .replacingOccurrences(of: "—", with: "-")
            .replacingOccurrences(of: "–", with: "-")
            .replacingOccurrences(of: "−", with: "-")
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }
}
