import Foundation

/// Minimal IPv4 helpers. Addresses are kept in host byte order as `UInt32`.
public enum IPv4 {
    public static func parse(_ string: String) -> UInt32? {
        let parts = string.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 4 else { return nil }
        var result: UInt32 = 0
        for part in parts {
            guard !part.isEmpty, part.allSatisfy({ $0.isASCII && $0.isNumber }),
                  let value = UInt32(part), value <= 255 else { return nil }
            result = (result << 8) | value
        }
        return result
    }

    public static func string(_ value: UInt32) -> String {
        "\((value >> 24) & 0xff).\((value >> 16) & 0xff).\((value >> 8) & 0xff).\(value & 0xff)"
    }

    public static func mask(prefix: Int) -> UInt32 {
        switch prefix {
        case ...0: return 0
        case 32: return UInt32.max
        default: return UInt32.max << UInt32(32 - prefix)
        }
    }

    public static func network(_ address: UInt32, prefix: Int) -> UInt32 {
        address & mask(prefix: prefix)
    }

    /// `inet_aton` semantics: "10" -> 10.0.0.0/8, "10.1" -> 10.1.0.0/16,
    /// "10.1.2" -> 10.1.2.0/24, "10.1.2.3" -> 10.1.2.3/32.
    public static func inetAton(_ string: String) -> (address: UInt32, prefix: Int)? {
        let parts = string.split(separator: ".", omittingEmptySubsequences: false)
        guard (1...4).contains(parts.count) else { return nil }
        var octets = [UInt32](repeating: 0, count: 4)
        for (index, part) in parts.enumerated() {
            guard !part.isEmpty, part.allSatisfy({ $0.isASCII && $0.isNumber }),
                  let value = UInt32(part), value <= 255 else { return nil }
            octets[index] = value
        }
        var address: UInt32 = 0
        for octet in octets { address = (address << 8) | octet }
        let prefix = 8 * parts.count
        return (network(address, prefix: prefix), prefix)
    }

    public static func isValidAddress(_ string: String) -> Bool {
        parse(string) != nil
    }
}
