import XCTest
@testable import SRMCore

final class SubnetScanTargetTests: XCTestCase {
    func testParseCIDR() {
        let target = SubnetScanTarget.parse("192.168.15.0/24")
        XCTAssertNotNil(target)
        XCTAssertEqual(target?.addresses.count, 256)
        XCTAssertEqual(target?.addresses.first, IPv4.parse("192.168.15.0"))
        XCTAssertEqual(target?.addresses.last, IPv4.parse("192.168.15.255"))
        XCTAssertEqual(target?.kind, .subnet(prefix: 24))
        XCTAssertEqual(target?.displayText, "192.168.15.0/24")
    }

    func testParseCIDRRejectsTooLargeAndTooSmall() {
        XCTAssertNil(SubnetScanTarget.parse("10.0.0.0/8"))       // 超过 2048
        XCTAssertNil(SubnetScanTarget.parse("192.168.15.0/31"))  // /31 /32 整段拒绝
        XCTAssertNil(SubnetScanTarget.parse("192.168.15.0/32"))
        XCTAssertNil(SubnetScanTarget.parse("192.168.15.0/33"))
    }

    func testParseSingleAddress() {
        let target = SubnetScanTarget.parse("192.168.15.66")
        XCTAssertEqual(target?.addresses, [IPv4.parse("192.168.15.66")!])
        XCTAssertEqual(target?.kind, .single)
    }

    func testParseShorthandRange() {
        let target = SubnetScanTarget.parse("192.168.15.1-200")
        XCTAssertEqual(target?.addresses.count, 200)
        XCTAssertEqual(target?.addresses.first, IPv4.parse("192.168.15.1"))
        XCTAssertEqual(target?.addresses.last, IPv4.parse("192.168.15.200"))
        XCTAssertEqual(target?.kind, .range)
    }

    func testParseFullRange() {
        let target = SubnetScanTarget.parse("1.2.3.1-1.2.3.200")
        XCTAssertEqual(target?.addresses.count, 200)
        XCTAssertEqual(target?.addresses.first, IPv4.parse("1.2.3.1"))
    }

    func testParseUnicodeDash() {
        XCTAssertNotNil(SubnetScanTarget.parse("192.168.15.1－200"))   // 全角
        XCTAssertNotNil(SubnetScanTarget.parse("192.168.15.1—200"))   // 破折号
        XCTAssertNotNil(SubnetScanTarget.parse("192.168.15.1–200"))   // en dash
    }

    func testParseRejectsInvalid() {
        XCTAssertNil(SubnetScanTarget.parse(""))
        XCTAssertNil(SubnetScanTarget.parse("hello"))
        XCTAssertNil(SubnetScanTarget.parse("192.168.15.200-1"))      // 倒序
        XCTAssertNil(SubnetScanTarget.parse("192.168.15.1-300"))      // 末段 > 255
        XCTAssertNil(SubnetScanTarget.parse("192.168.15.1-1.2.3.4"))  // 范围跨网段过大检查之外，但超过单段限制
        XCTAssertNil(SubnetScanTarget.parse("0.0.0.0-255.255.255.255")) // 全范围溢出 UInt32 加法，需返回 nil 而非崩溃
    }
}
