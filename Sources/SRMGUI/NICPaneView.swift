import SwiftUI
import SRMCore

struct NICPaneView: View {
    @EnvironmentObject private var model: AppModel
    @State private var pendingDisable: NICInfo?

    var body: some View {
        VStack(spacing: 0) {
            toolbar
            Divider()
            nicList
            Divider()
            discoveryBar
        }
        .onAppear {
            model.loadNICs()
        }
        .confirmationDialog(
            "禁用网络服务？",
            isPresented: Binding(
                get: { pendingDisable != nil },
                set: { if !$0 { pendingDisable = nil } }
            ),
            titleVisibility: .visible,
            presenting: pendingDisable
        ) { nic in
            Button("禁用「\(nic.serviceName)」", role: .destructive) {
                model.setServiceEnabled(nic, enabled: false)
            }
            Button("取消", role: .cancel) {}
        } message: { nic in
            Text("禁用后该网络服务将断开连接。可随时再启用。")
        }
    }

    private var toolbar: some View {
        HStack(spacing: 8) {
            Text("顺序即优先级（第 1 位优先）")
                .font(.caption)
                .foregroundStyle(.secondary)
            Spacer()
            Button {
                model.restoreServiceOrder()
            } label: {
                Label("恢复默认顺序", systemImage: "arrow.uturn.backward")
            }
            .buttonStyle(.bordered)
            .controlSize(.small)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
    }

    @ViewBuilder
    private var nicList: some View {
        if model.nics.isEmpty {
            VStack(spacing: 8) {
                if model.connectionError != nil || model.daemon == nil {
                    Text("网卡列表需要守护进程（srmd）提供，请先修复守护进程")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                } else {
                    ProgressView().controlSize(.small)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .padding(.vertical, 30)
        } else {
            ScrollView {
                LazyVStack(spacing: 0) {
                    ForEach(model.nics) { nic in
                        NICRow(nic: nic, pendingDisable: $pendingDisable)
                        Divider().padding(.leading, 12)
                    }
                }
            }
        }
    }

    private var discoveryBar: some View {
        HStack(spacing: 8) {
            Button {
                if model.firewallApplied {
                    model.removeFirewall()
                } else {
                    model.applyFirewall()
                }
            } label: {
                Label(
                    model.firewallApplied ? "移除防火墙放行（53317）" : "放行防火墙（局域网发现）",
                    systemImage: model.firewallApplied ? "shield.lefthalf.filled.slash" : "shield.lefthalf.filled"
                )
            }
            .buttonStyle(.bordered)
            .controlSize(.small)

            Button {
                model.watchDiscovery()
            } label: {
                if model.isWatching {
                    HStack(spacing: 4) {
                        ProgressView().controlSize(.mini)
                        Text("监听中（10 秒）…")
                    }
                } else {
                    Label("听听看（诊断）", systemImage: "ear")
                }
            }
            .buttonStyle(.bordered)
            .controlSize(.small)
            .disabled(model.isWatching)

            Spacer()

            if model.isShoutRepeat {
                Text("正在每 15 秒喊一次")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
        .alert(
            "听听看结果（\(model.watchResult?.elapsedSeconds ?? 0) 秒 · \(model.watchResult?.packetCount ?? 0) 个包）",
            isPresented: Binding(
                get: { model.watchResult != nil },
                set: { if !$0 { model.watchResult = nil } }
            ),
            actions: { Button("好") { model.watchResult = nil } },
            message: { Text(watchSummary) }
        )
    }

    private var watchSummary: String {
        guard let result = model.watchResult else { return "" }
        if result.aliasCounts.isEmpty {
            return "没有听到任何局域网设备的宣告。局域网发现靠组播（224.0.0.167:53317）互相宣告，\n使用同一组播协议的应用（如 LocalSend）都会在这里被听到；\n一个都没听到，说明本机收不到该组播——这就是发现不通的方向。"
        }
        let lines = result.aliasCounts
            .sorted { $0.value > $1.value }
            .map { "\($0.key)（\($0.value) 次）" }
        return "听到的设备：\n" + lines.joined(separator: "\n")
    }
}

struct NICRow: View {
    @EnvironmentObject private var model: AppModel
    let nic: NICInfo
    @Binding var pendingDisable: NICInfo?
    @State private var showIPv6 = false

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Text("第 \(nic.rank) 位")
                .font(.caption2)
                .foregroundStyle(.secondary)
                .frame(width: 44, alignment: .leading)
                .padding(.top, 3)

            Image(systemName: statusIcon)
                .foregroundStyle(statusColor)
                .font(.system(size: 13))
                .padding(.top, 2)

            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(nic.serviceName)
                        .font(.system(size: 12, weight: .medium))
                    if nic.isVirtual {
                        Text("虚拟")
                            .font(.caption2)
                            .padding(.horizontal, 4)
                            .padding(.vertical, 1)
                            .background(Color.secondary.opacity(0.15))
                            .cornerRadius(3)
                    }
                    if !nic.deviceName.isEmpty {
                        Text(nic.deviceName)
                            .font(.caption)
                            .foregroundStyle(.tertiary)
                    }
                }
                if !nic.ipv4Addresses.isEmpty {
                    Text(nic.ipv4Addresses.joined(separator: " / "))
                        .font(.caption)
                        .fontDesign(.monospaced)
                        .foregroundStyle(.secondary)
                }
                if let gateway = nic.gateway {
                    Text("网关 \(gateway)")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                HStack(spacing: 8) {
                    if let speed = nic.speedText {
                        Text(speed)
                    }
                    if let mac = nic.hardwareAddress {
                        Text(mac)
                    }
                }
                .font(.caption2)
                .foregroundStyle(.tertiary)

                if showIPv6, !nic.ipv6Addresses.isEmpty {
                    Text(nic.ipv6Addresses.joined(separator: " / "))
                        .font(.caption2)
                        .fontDesign(.monospaced)
                        .foregroundStyle(.tertiary)
                }
            }

            Spacer(minLength: 8)

            VStack(alignment: .trailing, spacing: 4) {
                HStack(spacing: 2) {
                    Button {
                        model.moveNIC(nic, up: true)
                    } label: {
                        Image(systemName: "arrow.up")
                    }
                    .buttonStyle(.borderless)
                    .disabled(nic.rank <= 1)
                    .help("提高优先级")

                    Button {
                        model.moveNIC(nic, up: false)
                    } label: {
                        Image(systemName: "arrow.down")
                    }
                    .buttonStyle(.borderless)
                    .disabled(nic.rank >= model.nics.count)
                    .help("降低优先级")
                }
                .font(.caption)

                Menu {
                    if nic.status == .disabled {
                        Button("启用服务") { model.setServiceEnabled(nic, enabled: true) }
                    } else {
                        Button("禁用服务…", role: .destructive) { pendingDisable = nic }
                    }
                    Divider()
                    if model.hasDiscoveryRoute(for: nic) {
                        Button("移除「发现走这块网卡」") { model.removeDiscoveryRoute(for: nic) }
                    } else {
                        Button("让局域网发现走这块网卡") { model.addDiscoveryRoute(for: nic) }
                    }
                    Divider()
                    Button("喊一声（从这块网卡）") { model.shoutOnce(from: nic) }
                    if model.isShoutRepeat {
                        Button("停止一直喊") { model.toggleShoutRepeat(nic) }
                    } else {
                        Button("一直喊（每 15 秒）") { model.toggleShoutRepeat(nic) }
                    }
                } label: {
                    Image(systemName: "ellipsis")
                        .font(.system(size: 12))
                }
                .menuStyle(.borderlessButton)
                .menuIndicator(.hidden)
                .fixedSize()
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .contextMenu {
            if !nic.ipv6Addresses.isEmpty {
                Button(showIPv6 ? "隐藏 IPv6 地址" : "显示 IPv6 地址") {
                    showIPv6.toggle()
                }
            }
        }
    }

    private var statusIcon: String {
        switch nic.status {
        case .connected: return "checkmark.circle.fill"
        case .disconnected: return "circle.dashed"
        case .disabled: return "minus.circle.fill"
        }
    }

    private var statusColor: Color {
        switch nic.status {
        case .connected: return .green
        case .disconnected: return .secondary
        case .disabled: return .orange
        }
    }
}
