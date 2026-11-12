import XCTest
@testable import SRMCore

final class IPCProtocolTests: XCTestCase {
    func testRequestRoundTripWithNewFields() throws {
        let request = SRMRequest(
            id: 7, command: .scanStart, scanTarget: "192.168.15.0/24", jobId: nil)
        let data = try JSONEncoder().encode(request)
        let decoded = try JSONDecoder().decode(SRMRequest.self, from: data)
        XCTAssertEqual(decoded.command, SRMCommand.scanStart.rawValue)
        XCTAssertEqual(decoded.scanTarget, "192.168.15.0/24")
    }

    func testRequestDecodesOldFormat() throws {
        // 旧版守护进程/GUI 生成的 JSON 没有新字段，仍可解码。
        let json = """
        {"id": 3, "command": "list", "spec": null, "enabled": null}
        """
        let decoded = try JSONDecoder().decode(SRMRequest.self, from: Data(json.utf8))
        XCTAssertEqual(decoded.id, 3)
        XCTAssertEqual(decoded.command, "list")
        XCTAssertNil(decoded.scanTarget)
        XCTAssertNil(decoded.serviceOrder)
    }

    func testResponseRoundTripWithNicsAndScanJob() throws {
        var response = SRMResponse(id: 9, ok: true)
        response.nics = [NICInfo(serviceName: "Wi-Fi", deviceName: "en0", status: .connected,
                                 isVirtual: false, ipv4Addresses: ["192.168.15.10/24"],
                                 ipv6Addresses: [], gateway: "192.168.15.1",
                                 speedText: "1 Gbps", hardwareAddress: nil, rank: 1)]
        response.scanJob = ScanJobStatus(phase: .running, done: 3, total: 256, error: nil, rows: nil)
        response.jobId = "abc"

        let data = try JSONEncoder().encode(response)
        let decoded = try JSONDecoder().decode(SRMResponse.self, from: data)
        XCTAssertEqual(decoded.nics?.first?.serviceName, "Wi-Fi")
        XCTAssertEqual(decoded.scanJob?.phase, .running)
        XCTAssertEqual(decoded.jobId, "abc")
    }

    func testResponseDecodesOldFormat() throws {
        let json = """
        {"id": 5, "ok": true, "error": null}
        """
        let decoded = try JSONDecoder().decode(SRMResponse.self, from: Data(json.utf8))
        XCTAssertTrue(decoded.ok)
        XCTAssertNil(decoded.nics)
        XCTAssertNil(decoded.scanJob)
    }

    func testNewCommandCases() {
        XCTAssertEqual(SRMCommand.nicList.rawValue, "nicList")
        XCTAssertEqual(SRMCommand.scanStop.rawValue, "scanStop")
        XCTAssertEqual(SRMCommand(rawValue: "nicSetOrder"), .nicSetOrder)
    }
}
