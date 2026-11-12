import SwiftUI
import SRMCore

struct ContentView: View {
    @EnvironmentObject private var model: AppModel

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            MainTabView()
            Divider()
            footer
        }
        .frame(width: 520)
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
            Text("SRM")
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

    private var footer: some View {
        HStack {
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
