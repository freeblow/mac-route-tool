import SwiftUI
import AppKit
import SRMCore
import UniformTypeIdentifiers

struct ScanPaneView: View {
    enum ScanFilter: String, CaseIterable {
        case all = "全部"
        case occupied = "只看已占用"
        case free = "只看空闲"
    }

    @EnvironmentObject private var model: AppModel
    @State private var targetText = ""
    @State private var filter: ScanFilter = .all

    var body: some View {
        VStack(spacing: 0) {
            form
            Divider()
            if model.isScanning {
                progressRow
                Divider()
            }
            results
            Divider()
            summaryFooter
        }
        .onAppear {
            model.loadInterfaces()
            if targetText.isEmpty, let first = subnetOptions.first {
                targetText = first
            }
        }
        .onChange(of: model.interfaces) { _ in
            if targetText.isEmpty, let first = subnetOptions.first {
                targetText = first
            }
        }
    }

    /// 本机直连子网（接口 IPv4，前缀 ≤ /30，排除 169.254 与 loopback）。
    private var subnetOptions: [String] {
        model.interfaces
            .filter { !$0.isLoopback && $0.isUsable }
            .flatMap { $0.ipv4Addresses }
            .filter { cidr in
                guard let slash = cidr.lastIndex(of: "/"), let prefix = Int(cidr[slash...].dropFirst()) else { return false }
                guard let address = IPv4.parse(String(cidr[..<slash])) else { return false }
                return prefix <= 30 && (address >> 8) != 0xa9fe  // 排除 169.254.x.x
            }
    }

    private var form: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 6) {
                Menu {
                    ForEach(subnetOptions, id: \.self) { option in
                        Button(option) { targetText = option }
                    }
                } label: {
                    Label("直连子网", systemImage: "network")
                        .font(.caption)
                }
                .fixedSize()

                Text("扫描位置")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Picker("", selection: $model.scanUsesDaemon) {
                    Text("本机").tag(false)
                    Text("守护进程").tag(true)
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .controlSize(.small)
                .fixedSize()
                .help("守护进程方式由 srmd(root) 执行；诊断操作始终在本机执行")
            }

            HStack(spacing: 6) {
                TextField("192.168.15.0/24 · 单地址 · 192.168.15.1-200", text: $targetText)
                    .textFieldStyle(.roundedBorder)
                    .font(.system(size: 12, design: .monospaced))

                if model.isScanning {
                    Button {
                        model.stopScan()
                    } label: {
                        Label("停止", systemImage: "stop.fill")
                    }
                    .buttonStyle(.bordered)
                } else {
                    Button {
                        model.startScan(target: targetText)
                    } label: {
                        Label("开始检测", systemImage: "play.fill")
                    }
                    .buttonStyle(.borderedProminent)
                    .disabled(targetText.trimmingCharacters(in: .whitespaces).isEmpty)
                }
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
    }

    private var progressRow: some View {
        VStack(spacing: 4) {
            ProgressView(value: Double(model.scanDone), total: Double(max(model.scanTotal, 1)))
            HStack {
                Text("\(model.scanDone)/\(model.scanTotal)")
                    .font(.caption2)
                    .foregroundStyle(.secondary)
                Spacer()
                Text(model.scanUsesDaemon ? "由守护进程执行" : "本机执行")
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
    }

    private var filteredRows: [ScanRow] {
        switch filter {
        case .all: return model.scanRows
        case .occupied: return model.scanRows.filter { $0.state == .occupied || $0.state == .gateway }
        case .free: return model.scanRows.filter { $0.state == .free }
        }
    }

    @ViewBuilder
    private var results: some View {
        if model.scanRows.isEmpty {
            VStack(spacing: 8) {
                Image(systemName: "magnifyingglass")
                    .font(.system(size: 28))
                    .foregroundStyle(.tertiary)
                Text("选择子网或输入目标后点击「开始检测」")
                    .foregroundStyle(.secondary)
                    .font(.caption)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .padding(.vertical, 30)
        } else {
            HStack(spacing: 4) {
                Picker("", selection: $filter) {
                    ForEach(ScanFilter.allCases, id: \.self) { value in
                        Text(value.rawValue).tag(value)
                    }
                }
                .pickerStyle(.segmented)
                .controlSize(.small)
                .fixedSize()
                Spacer()
                Text("\(filteredRows.count) 行")
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 4)

            ScrollView {
                LazyVStack(spacing: 0) {
                    ForEach(filteredRows) { row in
                        ScanRowView(row: row)
                            .contextMenu {
                                Button("复制地址") { Self.copy(row.address) }
                                Button("复制整行") { Self.copy("\(row.address) \(row.state.displayText) \(row.mac ?? "") \(row.rttMs.map { "\($0) ms" } ?? "") \(row.note ?? "")") }
                            }
                            .onTapGesture(count: 2) { Self.copy(row.address) }
                        Divider().padding(.leading, 12)
                    }
                }
            }
        }
    }

    private var summaryFooter: some View {
        HStack(spacing: 8) {
            Text(summaryText)
                .font(.caption)
                .foregroundStyle(.secondary)
                .lineLimit(2)
            Spacer()
            Button {
                exportCSV()
            } label: {
                Label("导出 CSV", systemImage: "square.and.arrow.down")
            }
            .buttonStyle(.bordered)
            .controlSize(.small)
            .disabled(model.scanRows.isEmpty)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
    }

    private var summaryText: String {
        guard let summary = model.scanSummary else {
            return model.isScanning ? "扫描中…" : ""
        }
        var parts = [
            "空闲 \(summary.freeCount)",
            "占用 \(summary.occupiedCount)",
            "网关/本机 \(summary.gatewaySelfCount)",
            "保留 \(summary.reservedCount)",
            "查不到 \(summary.unknownCount)",
        ]
        if let used = summary.interfaceUsed { parts.insert("出口 \(used)", at: 0) }
        if summary.elapsedSeconds > 0 { parts.append("用时 \(summary.elapsedSeconds)s") }
        return parts.joined(separator: " · ")
    }

    private func exportCSV() {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.commaSeparatedText]
        let stamp = {
            let formatter = DateFormatter()
            formatter.dateFormat = "yyyyMMdd-HHmmss"
            return formatter.string(from: Date())
        }()
        panel.nameFieldStringValue = "ip-scan-\(model.lastScanTarget ?? "result")-\(stamp).csv"
        let rows = model.scanRows
        panel.begin { response in
            guard response == .OK, let url = panel.url else { return }
            var data = Data([0xEF, 0xBB, 0xBF])  // UTF-8 BOM，Excel 友好
            data.append(Data(ScanCSV.export(rows).utf8))
            try? data.write(to: url)
        }
    }

    static func copy(_ text: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
    }
}

struct ScanRowView: View {
    let row: ScanRow

    var body: some View {
        HStack(spacing: 10) {
            Text(row.address)
                .font(.system(size: 12, design: .monospaced))
                .frame(width: 110, alignment: .leading)

            Text(row.state.displayText)
                .font(.caption)
                .foregroundStyle(stateColor)
                .frame(width: 64, alignment: .leading)

            Text(row.mac ?? "—")
                .font(.caption)
                .fontDesign(.monospaced)
                .foregroundStyle(.secondary)
                .frame(width: 120, alignment: .leading)

            Text(row.rttMs.map { String(format: "%.1f ms", $0) } ?? "—")
                .font(.caption)
                .foregroundStyle(.secondary)
                .frame(width: 64, alignment: .leading)

            Text(row.note ?? "")
                .font(.caption2)
                .foregroundStyle(.tertiary)
                .lineLimit(1)

            Spacer(minLength: 0)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 5)
    }

    private var stateColor: Color {
        switch row.state.displayColor {
        case "green": return .green
        case "orange": return .orange
        case "blue": return .blue
        case "red": return .red
        default: return .secondary
        }
    }
}
