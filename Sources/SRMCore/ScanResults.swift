import Foundation

/// 子网扫描结果行。
public struct ScanRow: Codable, Sendable, Identifiable, Hashable {
    public enum State: String, Codable, Sendable, CaseIterable {
        case reserved
        case selfHost = "self"
        case gateway
        case occupied
        case free
        case unknown

        public var displayText: String {
            switch self {
            case .reserved: return "保留地址"
            case .selfHost: return "本机"
            case .gateway: return "网关"
            case .occupied: return "已被占用"
            case .free: return "空闲可用"
            case .unknown: return "查不到"
            }
        }

        /// GUI 状态色的语义名（Task 13/14 映射到 SwiftUI Color）。
        public var displayColor: String {
            switch self {
            case .reserved, .selfHost: return "secondary"
            case .gateway: return "blue"
            case .occupied: return "orange"
            case .free: return "green"
            case .unknown: return "secondary"
            }
        }
    }

    public var address: String
    public var state: State
    public var mac: String?
    public var rttMs: Double?
    public var note: String?

    public var id: String { address }

    public init(address: String, state: State, mac: String? = nil, rttMs: Double? = nil, note: String? = nil) {
        self.address = address
        self.state = state
        self.mac = mac
        self.rttMs = rttMs
        self.note = note
    }
}

/// 扫描汇总。
public struct ScanSummary: Codable, Sendable {
    public var freeCount: Int
    public var occupiedCount: Int
    public var gatewaySelfCount: Int
    public var reservedCount: Int
    public var unknownCount: Int
    public var interfaceUsed: String?
    public var elapsedSeconds: Double

    public init(freeCount: Int, occupiedCount: Int, gatewaySelfCount: Int,
                reservedCount: Int, unknownCount: Int, interfaceUsed: String?, elapsedSeconds: Double) {
        self.freeCount = freeCount
        self.occupiedCount = occupiedCount
        self.gatewaySelfCount = gatewaySelfCount
        self.reservedCount = reservedCount
        self.unknownCount = unknownCount
        self.interfaceUsed = interfaceUsed
        self.elapsedSeconds = elapsedSeconds
    }
}

/// 扫描结果统计。
public enum SubnetScanner {
    /// 按状态汇总各计数，耗时四舍五入到 0.1 秒。
    public static func summarize(rows: [ScanRow], interfaceUsed: String?, elapsed: Double) -> ScanSummary {
        var freeCount = 0
        var occupiedCount = 0
        var gatewaySelfCount = 0
        var reservedCount = 0
        var unknownCount = 0
        for row in rows {
            switch row.state {
            case .free: freeCount += 1
            case .occupied: occupiedCount += 1
            case .gateway, .selfHost: gatewaySelfCount += 1
            case .reserved: reservedCount += 1
            case .unknown: unknownCount += 1
            }
        }
        return ScanSummary(
            freeCount: freeCount,
            occupiedCount: occupiedCount,
            gatewaySelfCount: gatewaySelfCount,
            reservedCount: reservedCount,
            unknownCount: unknownCount,
            interfaceUsed: interfaceUsed,
            elapsedSeconds: (elapsed * 10).rounded() / 10
        )
    }
}

/// 结果导出为 CSV（UTF-8 BOM 由写文件方添加）。
public enum ScanCSV {
    public static let header = "地址,状态,MAC 地址,响应(ms),说明"

    public static func export(_ rows: [ScanRow]) -> String {
        var lines = [header]
        for row in rows {
            let columns = [
                row.address,
                row.state.displayText,
                row.mac ?? "",
                row.rttMs.map { String(format: "%.1f", $0) } ?? "",
                row.note ?? "",
            ]
            lines.append(columns.map { escape($0) }.joined(separator: ","))
        }
        return lines.joined(separator: "\n") + "\n"
    }

    static func escape(_ value: String) -> String {
        if value.contains(",") || value.contains("\"") || value.contains("\n") {
            return "\"" + value.replacingOccurrences(of: "\"", with: "\"\"") + "\""
        }
        return value
    }
}
