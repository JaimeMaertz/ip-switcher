using System.Net;

namespace IpSwitcher.Core;

/// <summary>A saved static configuration for one adapter.</summary>
public sealed class Preset
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public int Prefix { get; set; }
    public string? Gateway { get; set; }
    public List<string> Dns { get; set; } = [];

    /// <summary>One line for a tooltip: "192.168.1.50/24 · gateway 192.168.1.1 · DNS 8.8.8.8".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { $"{Address}/{Prefix}" };
            if (!string.IsNullOrEmpty(Gateway)) parts.Add($"gateway {Gateway}");
            if (Dns.Count > 0) parts.Add($"DNS {string.Join(", ", Dns)}");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>Checks what was typed into the preset editor and builds the preset from it.</summary>
public static class PresetValidator
{
    /// <summary>
    /// Returns the problems with the input, empty when it is good, in which case
    /// <paramref name="preset"/> holds the result. <paramref name="otherNames"/> are the adapter's
    /// other presets (not the one being edited), for the duplicate-name check.
    /// </summary>
    public static IReadOnlyList<string> Validate(
        string name, string address, string mask, string gateway, string dns,
        IEnumerable<string> otherNames, out Preset? preset)
    {
        preset = null;
        var errors = new List<string>();

        name = name.Trim();
        if (name.Length == 0) errors.Add("Give the preset a name.");
        else if (name.Equals("DHCP", StringComparison.OrdinalIgnoreCase)) errors.Add("\"DHCP\" is already on every adapter; pick another name.");
        else if (otherNames.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase))) errors.Add($"This adapter already has a preset named \"{name}\".");

        var addressOk = Ipv4.TryParse(address, out var ip);
        if (!addressOk) errors.Add("IP address must look like 192.168.1.50.");
        else if (!IsUsableHost(ip)) { errors.Add($"{ip} can't be an adapter's address."); addressOk = false; }

        var prefixOk = Ipv4.TryParsePrefix(mask, out var prefix) && prefix is >= 1 and <= 32;
        if (!prefixOk) errors.Add("Subnet mask must be a mask (255.255.255.0) or a prefix length (24).");

        if (addressOk && prefixOk && prefix <= 30)
        {
            var host = Ipv4.ToUInt(ip) & ~Ipv4.Mask(prefix);
            if (host == 0) errors.Add($"{ip} is the network address of /{prefix}, not a host.");
            else if (host == ~Ipv4.Mask(prefix)) errors.Add($"{ip} is the broadcast address of /{prefix}, not a host.");
        }

        string? gatewayText = null;
        if (gateway.Trim().Length > 0)
        {
            if (!Ipv4.TryParse(gateway, out var gw)) errors.Add("Gateway must look like 192.168.1.1, or be left empty.");
            else if (addressOk && gw.Equals(ip)) errors.Add("Gateway can't be the adapter's own address.");
            else if (addressOk && prefixOk && !Ipv4.SameSubnet(ip, gw, prefix)) errors.Add($"Gateway {gw} isn't on {ip}/{prefix}'s subnet.");
            else gatewayText = gw.ToString();
        }

        var dnsList = new List<string>();
        foreach (var entry in dns.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (Ipv4.TryParse(entry, out var server)) dnsList.Add(server.ToString());
            else errors.Add($"DNS server \"{entry}\" isn't an IPv4 address.");
        }

        if (errors.Count == 0)
            preset = new Preset { Name = name, Address = ip.ToString(), Prefix = prefix, Gateway = gatewayText, Dns = dnsList };
        return errors;
    }

    static bool IsUsableHost(IPAddress ip)
    {
        var first = ip.GetAddressBytes()[0];
        return first is not (0 or 127) && first < 224;
    }
}
