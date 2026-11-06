// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "srm",
    platforms: [.macOS(.v13)],
    products: [
        .executable(name: "srmd", targets: ["srmd"]),
        .executable(name: "srm", targets: ["srm"]),
        .executable(name: "SRMGUI", targets: ["SRMGUI"]),
    ],
    targets: [
        .target(
            name: "SRMCore",
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
        .executableTarget(
            name: "srmd",
            dependencies: ["SRMCore"],
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
        .executableTarget(
            name: "srm",
            dependencies: ["SRMCore"],
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
        .executableTarget(
            name: "SRMGUI",
            dependencies: ["SRMCore"],
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
        .testTarget(
            name: "SRMCoreTests",
            dependencies: ["SRMCore"],
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
    ]
)
