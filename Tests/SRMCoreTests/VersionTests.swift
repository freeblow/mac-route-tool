import XCTest
@testable import SRMCore

final class SRMVersionTests: XCTestCase {
    func testOlderDaemonIsOutdated() {
        XCTAssertTrue(SRMVersion.isOutdated(running: "0.1.0", expected: "0.2.0"))
        XCTAssertTrue(SRMVersion.isOutdated(running: "0.1.9", expected: "0.2.0"))
        XCTAssertTrue(SRMVersion.isOutdated(running: "1.9.0", expected: "2.0.0"))
    }

    func testSameOrNewerDaemonIsNotOutdated() {
        XCTAssertFalse(SRMVersion.isOutdated(running: "0.2.0", expected: "0.2.0"))
        XCTAssertFalse(SRMVersion.isOutdated(running: "0.3.0", expected: "0.2.0"))
        XCTAssertFalse(SRMVersion.isOutdated(running: "1.0.0", expected: "0.2.0"))
    }

    func testMissingRunningVersionCountsAsNotOutdated() {
        // 连不上守护进程属于连接错误，不该弹「版本过旧」横幅。
        XCTAssertNil(SRMVersion.parse(""))
    }

    func testUnparsableVersionCountsAsOutdated() {
        XCTAssertTrue(SRMVersion.isOutdated(running: "", expected: "0.2.0"))
        XCTAssertTrue(SRMVersion.isOutdated(running: "dev", expected: "0.2.0"))
    }

    func testPartialVersionsCompareByGivenComponents() {
        XCTAssertFalse(SRMVersion.isOutdated(running: "0.2", expected: "0.2.0"))   // 缺省补 0
        XCTAssertTrue(SRMVersion.isOutdated(running: "0.1", expected: "0.2.0"))
    }

    func testExpectedVersionMatchesShippedTools() {
        XCTAssertEqual(SRMVersion.expected, "0.2.2")
    }
}
