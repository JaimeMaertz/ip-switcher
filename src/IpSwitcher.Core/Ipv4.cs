using System.Net;

namespace IpSwitcher.Core;

/// <summary>IPv4 text, masks and subnets.</summary>
public static class Ipv4
{
    /// <summary>
    /// Parses dotted-quad text strictly: four decimal parts of 0-255, no leading zeros.
    /// <see cref="IPAddress.Parse(string)"/> also accepts "10.1" and "0x0A.0.0.1", which are never
    /// what someone typing an adapter address means.
    /// </summary>
    public static bool TryParse(string? text, out IPAddress address)
    {
        address = IPAddress.None;
        var parts = text?.Trim().Split('.');
        if (parts is not { Length: 4 }) return false;
        var bytes = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            var p = parts[i];
            if (p.Length is 0 or > 3 || !p.All(char.IsAsciiDigit)) return false;
            if (p.Length > 1 && p[0] == '0') return false;
            var value = int.Parse(p);
            if (value > 255) return false;
            bytes[i] = (byte)value;
        }
        address = new IPAddress(bytes);
        return true;
    }

    /// <summary>Parses a prefix length ("24", "/24") or a dotted subnet mask ("255.255.255.0").</summary>
    public static bool TryParsePrefix(string? text, out int prefix)
    {
        prefix = 0;
        var t = text?.Trim() ?? "";
        if (t.StartsWith('/')) t = t[1..];
        if (t.Length is > 0 and <= 2 && t.All(char.IsAsciiDigit))
        {
            prefix = int.Parse(t);
            return prefix <= 32;
        }
        if (!TryParse(t, out var mask)) return false;
        var bits = ToUInt(mask);
        var inverted = ~bits;
        // A mask is ones then zeros: inverted + 1 is a power of two (or 0 for /0).
        if ((inverted & (inverted + 1)) != 0) return false;
        prefix = 32 - System.Numerics.BitOperations.PopCount(inverted);
        return true;
    }

    public static uint ToUInt(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    public static IPAddress FromUInt(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

    public static uint Mask(int prefix) => prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);

    public static string MaskText(int prefix) => FromUInt(Mask(prefix)).ToString();

    public static bool SameSubnet(IPAddress a, IPAddress b, int prefix) =>
        (ToUInt(a) & Mask(prefix)) == (ToUInt(b) & Mask(prefix));

    public static bool IsLinkLocal(IPAddress address) => ToUInt(address) >> 16 == 0xA9FE;

    /// <summary>
    /// The subnet as its first host plus prefix: 172.22.0.248/24 gives "172.22.0.1/24", /16 gives
    /// "172.22.0.1/16". A /31 or /32 has no room for a first host after the network, so it gives
    /// the network address.
    /// </summary>
    public static string FirstHostCidr(IPAddress address, int prefix)
    {
        var network = ToUInt(address) & Mask(prefix);
        var first = prefix <= 30 ? network + 1 : network;
        return $"{FromUInt(first)}/{prefix}";
    }
}
