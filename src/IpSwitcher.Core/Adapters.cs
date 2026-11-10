using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace IpSwitcher.Core;

public readonly record struct Ipv4Address(IPAddress Address, int Prefix)
{
    public override string ToString() => $"{Address}/{Prefix}";
}

/// <summary>One physical adapter as Windows reports it right now.</summary>
public sealed record AdapterInfo(
    string Mac,
    string Name,
    string Description,
    bool IsUp,
    bool IsWireless,
    bool DhcpEnabled,
    IReadOnlyList<Ipv4Address> Addresses,
    IReadOnlyList<IPAddress> Gateways,
    IReadOnlyList<IPAddress> Dns,
    int InterfaceIndex);

public static class AdapterReader
{
    static readonly HashSet<NetworkInterfaceType> PhysicalTypes =
    [
        NetworkInterfaceType.Ethernet,
        NetworkInterfaceType.GigabitEthernet,
        NetworkInterfaceType.FastEthernetT,
        NetworkInterfaceType.FastEthernetFx,
        NetworkInterfaceType.Ethernet3Megabit,
        NetworkInterfaceType.Wireless80211,
    ];

    /// <summary>
    /// Adapters that report an Ethernet type but are software: Hyper-V's vEthernet (WSL's own),
    /// VirtualBox and VMware host-only, VPN taps, Wi-Fi Direct, Bluetooth PAN. Switching these
    /// breaks the thing that owns them.
    /// </summary>
    static readonly string[] VirtualMarkers =
        ["Virtual", "VMware", "TAP-Windows", "Wintun", "WireGuard", "Bluetooth", "Loopback"];

    public static bool IsVirtual(string description) =>
        VirtualMarkers.Any(m => description.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A Wi-Fi card's extra ports (Wi-Fi Direct, mobile hotspot) carry the card's description with
    /// " #N" added. Only wireless: two identical USB Ethernet dongles also get " #2", and both are real.
    /// </summary>
    public static bool IsExtraWirelessPort(string description, bool wireless) =>
        wireless && System.Text.RegularExpressions.Regex.IsMatch(description, @" #\d+$");

    /// <summary>Physical adapters with IPv4 bound, wired first, each group by name.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static IReadOnlyList<AdapterInfo> Read()
    {
        var list = new List<AdapterInfo>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!PhysicalTypes.Contains(ni.NetworkInterfaceType) || IsVirtual(ni.Description)) continue;
            if (IsExtraWirelessPort(ni.Description, ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)) continue;
            var macBytes = ni.GetPhysicalAddress().GetAddressBytes();
            if (macBytes.Length != 6) continue;

            var props = ni.GetIPProperties();
            IPv4InterfaceProperties v4;
            try { v4 = props.GetIPv4Properties(); }
            catch (NetworkInformationException) { continue; }   // IPv4 unbound on this adapter
            if (v4 is null) continue;

            list.Add(new AdapterInfo(
                Mac: string.Join("-", macBytes.Select(b => b.ToString("X2"))),
                Name: ni.Name,
                Description: ni.Description,
                IsUp: ni.OperationalStatus == OperationalStatus.Up,
                IsWireless: ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                DhcpEnabled: v4.IsDhcpEnabled,
                Addresses: props.UnicastAddresses
                    .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(u => new Ipv4Address(u.Address, u.PrefixLength)).ToList(),
                Gateways: props.GatewayAddresses
                    .Select(g => g.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any)).ToList(),
                Dns: props.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList(),
                InterfaceIndex: v4.Index));
        }
        return list.OrderBy(a => a.IsWireless).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>What the flyout says about an adapter: its address line and which mode or preset it is on.</summary>
public sealed record AdapterStatus(string AddressText, string ModeText, bool Warning, Preset? ActivePreset)
{
    public static AdapterStatus Describe(AdapterInfo a, IReadOnlyList<Preset> presets)
    {
        var routable = a.Addresses.Where(x => !Ipv4.IsLinkLocal(x.Address)).ToList();
        var addressText = !a.IsUp ? "Not connected"
            : a.Addresses.Count == 0 ? "No IPv4 address"
            : string.Join(", ", a.Addresses);
        if (a.IsUp && a.Gateways.Count > 0) addressText += $"  ·  gateway {string.Join(", ", a.Gateways)}";

        if (a.DhcpEnabled)
        {
            var noLease = a.IsUp && routable.Count == 0;
            return new(addressText, noLease ? "DHCP · no lease" : "DHCP", noLease, null);
        }

        // Several addresses or gateways on one adapter is the stacked configuration a clean apply removes.
        if (a.Addresses.Count > 1 || a.Gateways.Count > 1)
            return new(addressText, $"Static · {a.Addresses.Count} addresses, {a.Gateways.Count} gateways stacked", true, null);

        var match = Match(a, presets);
        return match is null
            ? new(addressText, "Static · not a saved preset", false, null)
            : new(addressText, $"Static · {match.Name}", false, match);
    }

    /// <summary>The preset whose address, prefix and gateway are what the adapter has (DNS is not compared).</summary>
    public static Preset? Match(AdapterInfo a, IReadOnlyList<Preset> presets)
    {
        if (a.DhcpEnabled || a.Addresses.Count != 1) return null;
        var only = a.Addresses[0];
        var gateway = a.Gateways.Count == 1 ? a.Gateways[0].ToString() : null;
        return presets.FirstOrDefault(p =>
            p.Address == only.Address.ToString() && p.Prefix == only.Prefix &&
            (string.IsNullOrEmpty(p.Gateway) ? gateway is null : p.Gateway == gateway));
    }
}
