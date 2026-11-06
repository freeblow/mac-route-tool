import XCTest
import Darwin
@testable import SRMCore

final class ProcessRunnerTests: XCTestCase {
    func testRepeatedCommandsDoNotLeakFileDescriptors() throws {
        func openDescriptors() -> Int {
            (0..<256).reduce(0) { count, fd in
                count + (Darwin.fcntl(Int32(fd), F_GETFD) >= 0 ? 1 : 0)
            }
        }

        let before = openDescriptors()
        for _ in 0..<20 {
            XCTAssertTrue(try ProcessRunner.run("/usr/bin/true", []).success)
        }
        XCTAssertLessThanOrEqual(openDescriptors(), before + 4)
    }
}

final class RouteSpecTests: XCTestCase {
    func testNormalizeDestination() {
        XCTAssertEqual(RouteSpec.normalizeDestination("default"), "0.0.0.0/0")
        XCTAssertEqual(RouteSpec.normalizeDestination("10.40.85.0/24"), "10.40.85.0/24")
        XCTAssertEqual(RouteSpec.normalizeDestination("10.40.85/24"), "10.40.85.0/24")
        XCTAssertEqual(RouteSpec.normalizeDestination("10.40.85.7/24"), "10.40.85.0/24")
        XCTAssertEqual(RouteSpec.normalizeDestination("10.40.85.7"), "10.40.85.7/32")
        XCTAssertEqual(RouteSpec.normalizeDestination("127"), "127.0.0.0/8")
        XCTAssertEqual(RouteSpec.normalizeDestination("10.40"), "10.40.0.0/16")
        XCTAssertEqual(RouteSpec.normalizeDestination("10.40.85"), "10.40.85.0/24")
        XCTAssertNil(RouteSpec.normalizeDestination(""))
        XCTAssertNil(RouteSpec.normalizeDestination("hello"))
        XCTAssertNil(RouteSpec.normalizeDestination("10.40.85.0/33"))
        XCTAssertNil(RouteSpec.normalizeDestination("10.40.85.256/24"))
    }

    func testValidate() {
        var spec = RouteSpec(destination: "10.40.85.0/24", gateway: "10.40.81.254")
        XCTAssertNoThrow(try spec.validate())

        spec.gateway = "999.1.1.1"
        XCTAssertThrowsError(try spec.validate())

        spec.gateway = nil
        spec.destination = "nonsense"
        XCTAssertThrowsError(try spec.validate())
    }

    func testMatchKey() {
        let spec = RouteSpec(destination: "10.40.85/24", gateway: "10.40.81.254", interfaceName: "en0")
        XCTAssertEqual(spec.matchKey, "10.40.85.0/24|10.40.81.254|en0")
    }
}

final class RouteTableTests: XCTestCase {
    private let sample = """
    Routing tables

    Internet:
    Destination        Gateway            Flags               Netif Expire
    default            192.168.15.254     UGScg                 en1
    default            10.40.81.254       UGScIg                en0
    10.40.81/24        link#7             UCS                   en0      !
    10.40.85/24        10.40.81.254       UGSc                  en0
    127                link#14            UCS                   lo0
    """

    func testParse() {
        let routes = RouteTableReader.parse(sample)
        XCTAssertEqual(routes.count, 5)

        XCTAssertEqual(routes[0].destination, "0.0.0.0/0")
        XCTAssertEqual(routes[0].gateway, "192.168.15.254")
        XCTAssertEqual(routes[0].interfaceName, "en1")
        XCTAssertFalse(routes[0].isInterfaceScoped)

        XCTAssertEqual(routes[1].interfaceName, "en0")
        XCTAssertTrue(routes[1].isInterfaceScoped)
        XCTAssertTrue(routes[1].isStatic)

        XCTAssertEqual(routes[2].destination, "10.40.81.0/24")
        XCTAssertEqual(routes[2].gateway, "link#7")

        XCTAssertEqual(routes[3].destination, "10.40.85.0/24")
        XCTAssertEqual(routes[3].interfaceName, "en0")

        XCTAssertEqual(routes[4].destination, "127.0.0.0/8")
        XCTAssertEqual(routes[4].interfaceName, "lo0")
    }

    func testMatching() {
        let routes = RouteTableReader.parse(sample)

        let scoped = RouteSpec(destination: "10.40.85.0/24", gateway: "10.40.81.254", interfaceName: "en0")
        XCTAssertTrue(routes.contains { $0.matches(scoped) })

        let otherInterface = RouteSpec(destination: "10.40.85.0/24", gateway: "10.40.81.254", interfaceName: "en1")
        XCTAssertFalse(routes.contains { $0.matches(otherInterface) })

        let withoutInterface = RouteSpec(destination: "10.40.85.0/24", gateway: "10.40.81.254")
        XCTAssertTrue(routes.contains { $0.matches(withoutInterface) })

        let wrongGateway = RouteSpec(destination: "10.40.85.0/24", gateway: "10.40.81.253", interfaceName: "en0")
        XCTAssertFalse(routes.contains { $0.matches(wrongGateway) })
    }
}

final class RouteApplierTests: XCTestCase {
    func testAddArguments() {
        let spec = RouteSpec(destination: "10.40.85/24", gateway: "10.40.81.254", interfaceName: "en0")
        XCTAssertEqual(
            RouteApplier.addArguments(for: spec),
            ["-n", "add", "-net", "10.40.85.0/24", "10.40.81.254"]
        )
    }

    func testAddArgumentsWithoutInterface() {
        let spec = RouteSpec(destination: "default", gateway: "10.40.81.254")
        XCTAssertEqual(
            RouteApplier.addArguments(for: spec),
            ["-n", "add", "-net", "0.0.0.0/0", "10.40.81.254"]
        )
    }

    func testDirectRouteUsesInterfaceModifier() {
        let spec = RouteSpec(destination: "10.40.85.0/24", gateway: "10.40.81.254", interfaceName: "en0", direct: true)
        XCTAssertEqual(
            RouteApplier.addArguments(for: spec),
            ["-n", "add", "-net", "-interface", "en0", "10.40.85.0/24", "10.40.81.254"]
        )
    }

    func testDeleteArgumentsUseKernelFields() {
        let route = KernelRoute(destination: "10.40.85.0/24", gateway: "10.40.81.254", flags: "UGScI", interfaceName: "en0", expire: nil)
        XCTAssertEqual(
            RouteApplier.deleteArguments(for: route),
            ["-n", "delete", "-net", "-ifscope", "en0", "10.40.85.0/24", "10.40.81.254"]
        )
    }
}

final class IPv4Tests: XCTestCase {
    func testParse() {
        XCTAssertEqual(IPv4.parse("10.40.81.254"), 0x0A2851FE)
        XCTAssertNil(IPv4.parse("10.40.81"))
        XCTAssertNil(IPv4.parse("10.40.81.256"))
        XCTAssertNil(IPv4.parse("10.40.81.-1"))
    }

    func testInetAton() {
        XCTAssertEqual(IPv4.inetAton("10")?.address, IPv4.parse("10.0.0.0"))
        XCTAssertEqual(IPv4.inetAton("10")?.prefix, 8)
        XCTAssertEqual(IPv4.inetAton("10.40")?.prefix, 16)
        XCTAssertEqual(IPv4.inetAton("10.40.81")?.prefix, 24)
        XCTAssertEqual(IPv4.inetAton("10.40.81.5")?.prefix, 32)
    }

    func testMask() {
        XCTAssertEqual(IPv4.mask(prefix: 24), 0xFFFFFF00)
        XCTAssertEqual(IPv4.mask(prefix: 0), 0)
        XCTAssertEqual(IPv4.mask(prefix: 32), UInt32.max)
    }
}

final class RouteProbeTests: XCTestCase {
    func testProbeTargetDistinguishesHostAndNetwork() {
        let host = RouteSpec(destination: "10.20.10.60", gateway: "10.40.81.254")
        XCTAssertEqual(RouteProbe.target(for: host)?.address, "10.20.10.60")
        XCTAssertEqual(RouteProbe.target(for: host)?.isGateway, false)

        let network = RouteSpec(destination: "10.20.10.0/24", gateway: "10.40.81.254")
        XCTAssertEqual(RouteProbe.target(for: network)?.address, "10.40.81.254")
        XCTAssertEqual(RouteProbe.target(for: network)?.isGateway, true)
    }
}
