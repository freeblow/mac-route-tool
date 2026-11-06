import AppKit
import SwiftUI
import SRMCore

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var statusItem: NSStatusItem!
    private var popover: NSPopover!
    let model = AppModel()

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)

        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let button = statusItem.button {
            button.image = NSImage(systemSymbolName: "point.3.connected.trianglepath.dotted", accessibilityDescription: "静态路由")
            button.image?.isTemplate = true
            button.target = self
            button.action = #selector(togglePopover(_:))
            button.sendAction(on: [.leftMouseUp, .rightMouseUp])
        }

        popover = NSPopover()
        popover.behavior = .transient
        popover.contentViewController = NSHostingController(rootView: ContentView().environmentObject(model))

        model.start()

        // 调试用：SRM_DEBUG_WINDOW=1 时启动即弹出面板，便于截图核对界面。
        if ProcessInfo.processInfo.environment["SRM_DEBUG_EDITOR"] != nil {
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { [weak self] in
                self?.model.editorTarget = .new
                self?.showPopover()
            }
        }
        if ProcessInfo.processInfo.environment["SRM_DEBUG_WINDOW"] != nil {
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.0) { [weak self] in
                self?.showPopover()
            }
        }
    }

    private func showPopover() {
        guard let button = statusItem.button else { return }
        popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
        popover.contentViewController?.view.window?.makeKey()
    }

    @objc private func togglePopover(_ sender: Any?) {
        if let event = NSApp.currentEvent, event.type == .rightMouseUp {
            showContextMenu()
            return
        }
        if popover.isShown {
            popover.performClose(sender)
        } else {
            guard let button = statusItem.button else { return }
            popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
            popover.contentViewController?.view.window?.makeKey()
        }
    }

    private func showContextMenu() {
        let menu = NSMenu()
        let refresh = NSMenuItem(title: "重新同步路由", action: #selector(refreshNow), keyEquivalent: "r")
        refresh.target = self
        let install = NSMenuItem(title: "安装/修复守护进程…", action: #selector(installService), keyEquivalent: "")
        install.target = self
        let open = NSMenuItem(title: "打开配置文件", action: #selector(openConfig), keyEquivalent: "")
        open.target = self
        let quit = NSMenuItem(title: "退出", action: #selector(quit), keyEquivalent: "q")
        quit.target = self
        menu.addItem(refresh)
        menu.addItem(install)
        menu.addItem(open)
        menu.addItem(.separator())
        menu.addItem(quit)
        statusItem.menu = menu
        statusItem.button?.performClick(nil)
        statusItem.menu = nil
    }

    @objc private func refreshNow() {
        model.applyNow()
    }

    @objc private func installService() {
        model.installService()
    }

    @objc private func openConfig() {
        let path = ConfigStore.defaultConfigPath
        if FileManager.default.fileExists(atPath: path) {
            NSWorkspace.shared.open(URL(fileURLWithPath: path))
        } else {
            let alert = NSAlert()
            alert.messageText = "配置文件不存在"
            alert.informativeText = "路径：\(path)\n安装守护进程后由 srmd 创建。"
            alert.runModal()
        }
    }

    @objc private func quit() {
        NSApp.terminate(nil)
    }
}
