import SwiftUI
import SRMCore

enum MainTab: String, CaseIterable, Identifiable {
    case routes = "静态路由"
    case nics = "网卡"
    case scan = "IP 检测"

    var id: String { rawValue }
}

/// 三个标签页共用的容器：popover 与主窗口都挂它。
struct MainTabView: View {
    @State private var selection: MainTab = .routes

    var body: some View {
        VStack(spacing: 0) {
            Picker("", selection: $selection) {
                ForEach(MainTab.allCases) { tab in
                    Text(tab.rawValue).tag(tab)
                }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .padding(.horizontal, 12)
            .padding(.vertical, 8)

            Divider()

            switch selection {
            case .routes:
                RoutePaneView()
            case .nics:
                NICPaneView()
            case .scan:
                ScanPaneView()
            }
        }
        .modifier(RouteEditorPresentationModifier())
    }
}

/// 路由编辑器与错误提示的统一挂载点：popover 与主窗口两个宿主都包含
/// MainTabView，各自就地呈现 sheet，主窗口的编辑入口因此同样可用。
private struct RouteEditorPresentationModifier: ViewModifier {
    @EnvironmentObject private var model: AppModel

    func body(content: Content) -> some View {
        content
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
}
