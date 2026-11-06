import SwiftUI
import SRMCore

struct ContentView: View {
    @EnvironmentObject private var model: AppModel

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            content
            Divider()
            footer
        }
        .frame(width: 430)
        .background(Color(NSColor.windowBackgroundColor))
        .sheet(item: $model.editorTarget) { target in
            switch target {
            case .new:
                RouteEditorView(spec: RouteSpec(destination: "", gateway: ""), isNew: true)
            case .edit(let spec):
                RouteEditorView(spec: spec, isNew: false)
            }
        }
        .alert(
            "出错了",
            isPresented: Binding(
                get: { model.errorMessage != nil && model.editorTarget == nil },
                set: { if !$0 { model.errorMessage = nil } }
            ),
            actions: { Button("好") { model.errorMessage = nil } },
            message: { Text(model.errorMessage ?? "") }
        )
    }

    private var header: some View {
        HStack {
            Text("静态路由")
                .font(.system(size: 13, weight: .semibold))
            Spacer()
            if model.isRefreshing {
                ProgressView().controlSize(.small)
            }
            Button {
                model.refresh()
            } label: {
                Image(systemName: "arrow.clockwise")
            }
            .buttonStyle(.borderless)
            .help("刷新")
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
    }

    @ViewBuilder
    private var content: some View {
        if model.routes.isEmpty {
            VStack(spacing: 8) {
                Image(systemName: "arrow.triangle.branch")
                    .font(.system(size: 28))
                    .foregroundStyle(.tertiary)
                Text("还没有受管理的路由")
                    .foregroundStyle(.secondary)
                Text("点击下方「添加路由」创建第一条静态路由")
                    .font(.caption)
                    .foregroundStyle(.tertiary)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .padding(.vertical, 30)
        } else {
            ScrollView {
                LazyVStack(spacing: 0) {
                    ForEach(model.routes) { route in
                        RouteRow(route: route)
                        Divider().padding(.leading, 12)
                    }
                }
            }
            .frame(maxHeight: 320)
        }
    }

    private var footer: some View {
        HStack(spacing: 8) {
            Button {
                model.editorTarget = .new
            } label: {
                Label("添加路由", systemImage: "plus")
            }
            .buttonStyle(.borderedProminent)

            Button {
                model.applyNow()
            } label: {
                Label("重新同步", systemImage: "arrow.triangle.2.circlepath")
            }
            .buttonStyle(.bordered)
            .help("删除并重写全部受管理的路由")

            Spacer()

            daemonStatus
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
    }

    @ViewBuilder
    private var daemonStatus: some View {
        if let daemon = model.daemon {
            HStack(spacing: 4) {
                Circle()
                    .fill(Color.green)
                    .frame(width: 6, height: 6)
                Text("srmd \(daemon.version) · \(Int(daemon.pollInterval))s")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            .help("守护进程 pid \(daemon.pid)")
        } else {
            Button {
                model.installService()
            } label: {
                VStack(alignment: .leading, spacing: 2) {
                    HStack(spacing: 4) {
                        if model.isInstallingService {
                            ProgressView().controlSize(.mini)
                        } else {
                            Circle()
                                .fill(Color.red)
                                .frame(width: 6, height: 6)
                        }
                        Text(model.isInstallingService ? "正在修复守护进程…" : "守护进程未连接")
                            .font(.caption)
                    }
                    Text(model.isInstallingService ? "请在系统弹窗中授权" : "点击修复守护进程")
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .disabled(model.isInstallingService)
            .help(model.connectionError ?? "点击安装或修复守护进程")
        }
    }
}

struct RouteRow: View {
    @EnvironmentObject private var model: AppModel
    let route: RouteStatus

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: iconName)
                .foregroundStyle(iconColor)
                .font(.system(size: 13))
                .padding(.top, 2)

            VStack(alignment: .leading, spacing: 2) {
                Text(route.spec.normalizedDestination)
                    .font(.system(size: 12, weight: .medium, design: .monospaced))
                Text(subtitle)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                if let message = route.message, !message.isEmpty {
                    Text(message)
                        .font(.caption2)
                        .foregroundStyle(route.state == .failed ? Color.red : Color.secondary)
                        .lineLimit(2)
                }
                HStack(spacing: 5) {
                    Button {
                        model.testConnectivity(route)
                    } label: {
                        if model.probingRoutes.contains(route.id) {
                            ProgressView().controlSize(.mini)
                        } else {
                            Label("测试连通性", systemImage: "network")
                        }
                    }
                    .buttonStyle(.link)
                    .font(.caption2)
                    .disabled(!route.spec.enabled || model.probingRoutes.contains(route.id))
                    if let result = model.probeResults[route.id] {
                        Image(systemName: result.reachable ? "checkmark.circle.fill" : "xmark.circle.fill")
                            .foregroundStyle(result.reachable ? Color.green : Color.orange)
                        Text("\(result.isGateway ? "网关" : "目标") \(result.target)：\(result.reachable ? "可达" : "未响应") · \(result.detail)")
                            .foregroundStyle(.secondary)
                            .lineLimit(2)
                    }
                }
                .font(.caption2)
            }

            Spacer(minLength: 8)

            Toggle("", isOn: Binding(
                get: { route.spec.enabled },
                set: { model.setEnabled(route, enabled: $0) }
            ))
            .labelsHidden()
            .toggleStyle(.checkbox)
            .help(route.spec.enabled ? "停用" : "启用")

            Menu {
                Button("编辑…") { model.editorTarget = .edit(route.spec) }
                Button(route.spec.enabled ? "停用" : "启用") {
                    model.setEnabled(route, enabled: !route.spec.enabled)
                }
                Divider()
                Button("删除", role: .destructive) { model.delete(route) }
            } label: {
                Image(systemName: "ellipsis")
                    .font(.system(size: 12))
            }
            .menuStyle(.borderlessButton)
            .menuIndicator(.hidden)
            .fixedSize()
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .contentShape(Rectangle())
        .contextMenu {
            Button("编辑…") { model.editorTarget = .edit(route.spec) }
            Button(route.spec.enabled ? "停用" : "启用") {
                model.setEnabled(route, enabled: !route.spec.enabled)
            }
            Button("删除", role: .destructive) { model.delete(route) }
        }
    }

    private var subtitle: String {
        var parts: [String] = []
        if let gateway = route.spec.normalizedGateway {
            parts.append("via \(gateway)")
        } else {
            parts.append("直连")
        }
        if let name = route.spec.interfaceName, !name.isEmpty {
            parts.append("dev \(name)")
        } else if let resolved = route.resolvedInterface {
            parts.append("dev \(resolved)")
        }
        if !route.spec.note.isEmpty {
            parts.append("· \(route.spec.note)")
        }
        return parts.joined(separator: " · ")
    }

    private var iconName: String {
        switch route.state {
        case .active: return "checkmark.circle.fill"
        case .pending: return "clock.fill"
        case .failed: return "exclamationmark.triangle.fill"
        case .disabled: return "circle.dashed"
        case .unmanaged: return "circle"
        }
    }

    private var iconColor: Color {
        switch route.state {
        case .active: return .green
        case .pending: return .orange
        case .failed: return .red
        case .disabled: return .secondary
        case .unmanaged: return .secondary
        }
    }
}
