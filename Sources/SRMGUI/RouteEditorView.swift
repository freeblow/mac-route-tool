import SwiftUI
import SRMCore

struct RouteEditorView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss

    @State private var spec: RouteSpec
    @State private var preflightError: String?
    private let isNew: Bool

    init(spec: RouteSpec, isNew: Bool) {
        _spec = State(initialValue: spec)
        self.isNew = isNew
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text(isNew ? "添加静态路由" : "编辑静态路由")
                .font(.system(size: 14, weight: .semibold))

            VStack(alignment: .leading, spacing: 10) {
                field("目标网段") {
                    TextField("10.40.85.0/24 或 default", text: $spec.destination)
                        .textFieldStyle(.roundedBorder)
                        .font(.system(size: 12, design: .monospaced))
                }

                field("网关") {
                    TextField("10.40.81.254", text: Binding(
                        get: { spec.gateway ?? "" },
                        set: { spec.gateway = $0.isEmpty ? nil : $0 }
                    ))
                    .textFieldStyle(.roundedBorder)
                    .font(.system(size: 12, design: .monospaced))
                }

                field("预期出口接口") {
                    Picker("", selection: Binding(
                        get: { spec.interfaceName ?? "" },
                        set: { spec.interfaceName = $0.isEmpty ? nil : $0 }
                    )) {
                        Text("自动（按网关推断）").tag("")
                        ForEach(model.interfaces) { item in
                            Text(item.subtitle).tag(item.name)
                        }
                    }
                    .labelsHidden()
                    .frame(maxWidth: 260)
                }

                Toggle("直连路由（目标就在本网段，忽略网关）", isOn: $spec.direct)
                    .toggleStyle(.checkbox)
                    .font(.system(size: 12))

                Toggle("立即启用", isOn: $spec.enabled)
                    .toggleStyle(.checkbox)
                    .font(.system(size: 12))

                field("备注") {
                    TextField("可留空", text: $spec.note)
                        .textFieldStyle(.roundedBorder)
                        .font(.system(size: 12))
                }
            }

            if let validationError {
                HStack(alignment: .top, spacing: 6) {
                    Image(systemName: "exclamationmark.triangle.fill")
                        .foregroundStyle(.orange)
                    Text(validationError)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            if let routeWarning {
                Text(routeWarning)
                    .font(.caption)
                    .foregroundStyle(.orange)
                    .fixedSize(horizontal: false, vertical: true)
            }
            if let preflightError {
                Text(preflightError)
                    .font(.caption)
                    .foregroundStyle(.red)
                    .fixedSize(horizontal: false, vertical: true)
            }

            HStack {
                Spacer()
                Button("取消") { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button(isNew ? "添加" : "保存") {
                    preflightError = nil
                    if !spec.direct,
                       let gateway = spec.normalizedGateway,
                       let wanted = spec.interfaceName,
                       !wanted.isEmpty,
                       let actual = RouteApplier.resolveInterface(for: gateway),
                       actual != wanted {
                        preflightError = "网关 \(gateway) 当前经由 \(actual)，与所选接口 \(wanted) 不一致。请检查网关地址或接口。"
                        return
                    }
                    model.save(spec, isNew: isNew)
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
                .disabled(validationError != nil)
            }
        }
        .padding(20)
        .frame(width: 420)
        .onAppear { model.loadInterfaces() }
    }

    @ViewBuilder
    private func field<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title)
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
            content()
        }
    }

    private var validationError: String? {
        var candidate = spec
        candidate.destination = spec.destination.trimmingCharacters(in: .whitespaces)
        candidate.gateway = spec.gateway?.trimmingCharacters(in: .whitespaces)
        do {
            try candidate.validate()
        } catch {
            return error.localizedDescription
        }
        if candidate.normalizedGateway == nil && !candidate.direct {
            return "请填写网关地址，或勾选「直连路由」"
        }
        return nil
    }

    private var routeWarning: String? {
        guard spec.enabled, spec.prefixLength == 32,
              let destination = spec.normalizedDestination.split(separator: "/").first,
              let target = IPv4.parse(String(destination)) else { return nil }
        for networkInterface in model.interfaces where networkInterface.isUsable && !networkInterface.isLoopback {
            for cidr in networkInterface.ipv4Addresses {
                let parts = cidr.split(separator: "/")
                guard parts.count == 2, let address = IPv4.parse(String(parts[0])),
                      let prefix = Int(parts[1]) else { continue }
                if IPv4.network(target, prefix: prefix) == IPv4.network(address, prefix: prefix) {
                    return "目标已在 \(networkInterface.name) 的直连网段 \(cidr) 中，通常不需要添加静态路由。Ping 通目标不代表填写的网关正确。"
                }
            }
        }
        return nil
    }
}
