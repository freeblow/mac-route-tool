import AppKit

let application = NSApplication.shared
let delegate = MainActor.assumeIsolated { AppDelegate() }
MainActor.assumeIsolated {
    application.delegate = delegate
    application.setActivationPolicy(.accessory)
}
application.run()
