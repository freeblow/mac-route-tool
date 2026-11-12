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
    }
}
