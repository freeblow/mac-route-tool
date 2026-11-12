import XCTest
@testable import SRMCore

final class ScanResultsTests: XCTestCase {
    func testStateDisplayText() {
        XCTAssertEqual(ScanRow.State.reserved.displayText, "保留地址")
        XCTAssertEqual(ScanRow.State.selfHost.displayText, "本机")
        XCTAssertEqual(ScanRow.State.gateway.displayText, "网关")
        XCTAssertEqual(ScanRow.State.occupied.displayText, "已被占用")
        XCTAssertEqual(ScanRow.State.free.displayText, "空闲可用")
        XCTAssertEqual(ScanRow.State.unknown.displayText, "查不到")
    }

    func testStateRawValuesAreStable() {
        XCTAssertEqual(ScanRow.State(rawValue: "self"), .selfHost)
        XCTAssertEqual(ScanRow.State.selfHost.rawValue, "self")
    }

    func testSummarize() {
        let rows: [ScanRow] = [
            ScanRow(address: "10.0.0.0", state: .reserved),
            ScanRow(address: "10.0.0.1", state: .gateway, rttMs: 1.5),
            ScanRow(address: "10.0.0.2", state: .occupied, mac: "aa:bb:cc:dd:ee:ff"),
            ScanRow(address: "10.0.0.3", state: .free),
            ScanRow(address: "10.0.0.4", state: .unknown),
        ]
        let summary = SubnetScanner.summarize(rows: rows, interfaceUsed: "en0", elapsed: 3.25)
        XCTAssertEqual(summary.freeCount, 1)
        XCTAssertEqual(summary.occupiedCount, 1)
        XCTAssertEqual(summary.gatewaySelfCount, 1)
        XCTAssertEqual(summary.reservedCount, 1)
        XCTAssertEqual(summary.unknownCount, 1)
        XCTAssertEqual(summary.interfaceUsed, "en0")
        XCTAssertEqual(summary.elapsedSeconds, 3.3)
    }

    func testCSVExport() {
        let rows: [ScanRow] = [
            ScanRow(address: "10.0.0.2", state: .occupied, mac: "aa:bb:cc:dd:ee:ff", rttMs: 2.5, note: "含,逗号"),
            ScanRow(address: "10.0.0.3", state: .free, note: nil),
        ]
        let csv = ScanCSV.export(rows)
        XCTAssertTrue(csv.hasPrefix("地址,状态,MAC 地址,响应(ms),说明\n"))
        XCTAssertTrue(csv.contains("10.0.0.2,已被占用,aa:bb:cc:dd:ee:ff,2.5,\"含,逗号\""))
        XCTAssertTrue(csv.contains("10.0.0.3,空闲可用,,,\n"))
        XCTAssertTrue(csv.hasSuffix("\n"))
    }
}
