using System.Globalization;

namespace SRM.Core;

public static class Ipv4
{
    public static bool TryParse(string? text, out uint address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split('.');
        if (parts.Length != 4) return false;
        uint value = 0;
        foreach (var part in parts)
        {
            if (part.Length == 0 || part.Length > 3) return false;
            if (!byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var octet)) return false;
            value = (value << 8) | octet;
        }
        address = value;
        return true;
    }

    public static string ToText(uint address) =>
        $"{(address >> 24) & 255}.{(address >> 16) & 255}.{(address >> 8) & 255}.{address & 255}";

    public static uint FromNetworkOrder(uint value) =>
        (value >> 24) | ((value >> 8) & 0x0000FF00) | ((value << 8) & 0x00FF0000) | (value << 24);

    public static uint MaskFromPrefix(int prefix) => prefix <= 0 ? 0u : uint.MaxValue << (32 - prefix);

    public static string MaskText(int prefix) => ToText(MaskFromPrefix(prefix));

    public static int PrefixFromMask(uint mask)
    {
        var prefix = 0;
        for (var bit = 31; bit >= 0; bit--)
        {
            if ((mask & (1u << bit)) == 0) break;
            prefix++;
        }
        return prefix;
    }

    public static bool TryParsePrefix(string? input, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;
        var value = (input ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            error = "请输入目标地址或网段。";
            return false;
        }
        if (string.Equals(value, "default", StringComparison.OrdinalIgnoreCase)) value = "0.0.0.0/0";
        var prefix = 32;
        var slash = value.IndexOf('/');
        if (slash >= 0)
        {
            var lengthText = value[(slash + 1)..];
            value = value[..slash];
            if (!int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out prefix) || prefix > 32)
            {
                error = "掩码长度应在 0 到 32 之间。";
                return false;
            }
        }
        if (!TryParse(value, out var address))
        {
            error = "目标地址应为 IPv4 或 CIDR，例如 10.40.85.0/24。";
            return false;
        }
        var network = address & MaskFromPrefix(prefix);
        normalized = $"{ToText(network)}/{prefix}";
        return true;
    }

    public static bool TryParseSubnet(string destination, out uint network, out uint mask)
    {
        network = 0;
        mask = 0;
        if (!TryParsePrefix(destination, out var normalized, out _)) return false;
        var parts = normalized.Split('/');
        if (!TryParse(parts[0], out network)) return false;
        mask = MaskFromPrefix(int.Parse(parts[1], CultureInfo.InvariantCulture));
        return true;
    }

    public static bool Contains(uint network, uint mask, uint address) => (address & mask) == (network & mask);
}
