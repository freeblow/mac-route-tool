import XCTest
import Darwin
@testable import SRMCore

/// RouteManager 与 route(8) 的跨层集成测试：锁定「无网关直连路由」
/// （局域网发现 spec：224.0.0.167/32 + -interface）从 add 到 reconcile 的完整路径。
final class RouteManagerIntegrationTests: XCTestCase {
    private func makeManager(dryRun: Bool) throws -> (manager: RouteManager, directory: String) {
        let directory = NSTemporaryDirectory() + "srm-integration-\(UUID().uuidString)"
        try FileManager.default.createDirectory(atPath: directory, withIntermediateDirectories: true)
        let manager = try RouteManager(options: .init(
            configPath: "\(directory)/routes.json",
            dryRun: dryRun,
            stateDirectory: directory
        ))
        return (manager, directory)
    }

    /// GUI「让局域网发现走这块网卡」构造的形态；集成环境用 lo0 作为无副作用的目标接口。
    private var discoverySpec: RouteSpec {
        RouteSpec(
            destination: "224.0.0.167/32",
            gateway: nil,
            interfaceName: "lo0",
            direct: true,
            note: "局域网发现组播"
        )
    }

    func testAddAcceptsDirectRouteWithoutGateway() throws {
        let (manager, directory) = try makeManager(dryRun: true)
        defer { try? FileManager.default.removeItem(atPath: directory) }

        let saved = try manager.add(discoverySpec)
        XCTAssertEqual(saved.matchKey, "224.0.0.167/32||lo0")
        XCTAssertTrue(manager.config.routes.contains { $0.id == saved.id })
    }

    func testAddRejectsDirectRouteWithoutInterface() throws {
        let (manager, directory) = try makeManager(dryRun: true)
        defer { try? FileManager.default.removeItem(atPath: directory) }

        XCTAssertThrowsError(
            try manager.add(RouteSpec(destination: "224.0.0.167/32", gateway: nil, interfaceName: nil, direct: true))
        ) { error in
            XCTAssertEqual(error as? RouteValidationError, .badInterface("直连路由必须指定出接口"))
        }
    }

    func testAddStillRequiresGatewayForGatewayRoutes() throws {
        let (manager, directory) = try makeManager(dryRun: true)
        defer { try? FileManager.default.removeItem(atPath: directory) }

        XCTAssertThrowsError(
            try manager.add(RouteSpec(destination: "10.40.85.0/24", gateway: nil, direct: false))
        ) { error in
            XCTAssertEqual(error as? RouteValidationError, .badGateway(""))
        }
    }

    func testReconcileDryRunBuildsInterfaceRouteArguments() throws {
        let (manager, directory) = try makeManager(dryRun: true)
        defer { try? FileManager.default.removeItem(atPath: directory) }

        let saved = try manager.add(discoverySpec)
        let statuses = manager.reconcile()
        let status = try XCTUnwrap(statuses.first { $0.spec.id == saved.id })

        // dry-run 下 apply 不再以「缺少网关地址」失败，而是给出直达 route(8) 的参数。
        XCTAssertEqual(status.state, .pending)
        XCTAssertEqual(status.message, "dry-run：将执行 route -n add -net -interface lo0 224.0.0.167/32")
        XCTAssertEqual(status.resolvedInterface, "lo0")
    }

    /// 真实执行（非 dry-run）：route add/delete 224.0.0.167/32 -interface lo0。
    /// 写路由表需要 root；root 环境断言内核表出现/消失，否则断言失败信息来自
    /// route 命令本身（而非「缺少网关地址」），证明参数组合已直达执行层。
    func testDirectRouteLifecycleRunsRouteCommand() throws {
        let (manager, directory) = try makeManager(dryRun: false)
        defer { try? FileManager.default.removeItem(atPath: directory) }

        let saved = try manager.add(discoverySpec)
        let statuses = manager.reconcile()
        let status = try XCTUnwrap(statuses.first { $0.spec.id == saved.id })

        if getuid() == 0 {
            XCTAssertEqual(status.state, .active)
            XCTAssertEqual(status.resolvedInterface, "lo0")
            XCTAssertTrue((try RouteTableReader.readIPv4()).contains { $0.matches(saved) })

            try manager.delete(id: saved.id)
            XCTAssertFalse((try RouteTableReader.readIPv4()).contains { $0.matches(saved) })
        } else {
            XCTAssertEqual(status.state, .failed)
            XCTAssertTrue(
                status.message?.localizedCaseInsensitiveContains("root") ?? false,
                "应执行到 route 命令被权限拒绝，实际失败信息：\(status.message ?? "nil")"
            )
        }
    }
}
