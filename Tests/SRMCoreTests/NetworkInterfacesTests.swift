import XCTest
@testable import SRMCore

final class NetworkInterfacesTests: XCTestCase {
    /// 真实 getifaddrs：非 loopback 已启用网卡应能读到硬件地址，
    /// 格式为 xx:xx:xx:xx:xx:xx 且非全零。环境无物理网卡（如纯虚机）时跳过。
    func testHardwareAddressReadFromAFLinkAddress() throws {
        let interfaces = NetworkInterfaces.all()
        let physical = interfaces.filter { !$0.isLoopback && $0.isUp && $0.isRunning }
        guard !physical.isEmpty else {
            throw XCTSkip("当前环境没有已启用的非 loopback 网卡")
        }

        let withMAC = physical.filter { $0.hardwareAddress != nil }
        XCTAssertFalse(
            withMAC.isEmpty,
            "非 loopback 网卡应能读取硬件地址，实际：\(physical.map { "\($0.name)=\($0.hardwareAddress ?? "nil")" }.joined(separator: ", "))"
        )

        let pattern = "^([0-9a-f]{2}:){5}[0-9a-f]{2}$"
        for nic in withMAC {
            let mac = nic.hardwareAddress ?? ""
            XCTAssertNotNil(mac.range(of: pattern, options: .regularExpression), "\(nic.name) MAC 格式异常：\(mac)")
            XCTAssertNotEqual(mac, "00:00:00:00:00:00", "\(nic.name) MAC 不应为全零")
        }
    }
}
