import Foundation

/// On-disk configuration, owned by root inside `/etc/srm`.
public struct SRMConfig: Codable, Sendable {
    public static let currentVersion = 1

    public var version: Int
    /// Seconds between two reconcile passes.
    public var pollInterval: Double
    /// UID allowed to talk to the privileged daemon. `nil` disables the check.
    public var authorizedUID: Int?
    public var routes: [RouteSpec]

    public init(
        version: Int = SRMConfig.currentVersion,
        pollInterval: Double = 5,
        authorizedUID: Int? = nil,
        routes: [RouteSpec] = []
    ) {
        self.version = version
        self.pollInterval = pollInterval
        self.authorizedUID = authorizedUID
        self.routes = routes
    }
}

public struct ConfigStore {
    public static let defaultDirectory = "/etc/srm"
    public static let defaultConfigPath = "/etc/srm/routes.json"

    public var path: String

    public init(path: String = ConfigStore.defaultConfigPath) {
        self.path = path
    }

    public func load() throws -> SRMConfig {
        guard FileManager.default.fileExists(atPath: path) else {
            return SRMConfig()
        }
        let data = try Data(contentsOf: URL(fileURLWithPath: path))
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return try decoder.decode(SRMConfig.self, from: data)
    }

    public func save(_ config: SRMConfig) throws {
        let directory = URL(fileURLWithPath: path).deletingLastPathComponent()
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [
            .posixPermissions: 0o755,
        ])
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        encoder.dateEncodingStrategy = .iso8601
        let data = try encoder.encode(config)
        try data.write(to: URL(fileURLWithPath: path), options: .atomic)
        // The configuration is consumed by a root daemon: keep it owner-only.
        try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: path)
    }
}
