using System.Text.Json;

namespace IpSwitcher.Core;

/// <summary>
/// What an adapter holds after an apply, as <see cref="ApplyScript"/> prints it. <see cref="Dhcp"/> is
/// the live setting; <see cref="DhcpSaved"/> is the saved one from the registry, the only true answer
/// on an unplugged adapter, where the live setting keeps reading Enabled after a static address is saved.
/// </summary>
public sealed record AdapterState(
    bool? Dhcp,
    IReadOnlyList<AdapterState.Address> Addresses,
    IReadOnlyList<AdapterState.Gateway> Gateways,
    IReadOnlyList<string> Dns,
    int? Renew,
    IReadOnlyList<string> Errors,
    bool? DhcpSaved = null)
{
    public sealed record Address(string Ip, int Prefix, string Store, string State, string Origin);
    public sealed record Gateway(string NextHop, string Store);

    public IEnumerable<Address> InStore(string store) => Addresses.Where(a => a.Store == store);
    public IEnumerable<Gateway> GatewaysIn(string store) => Gateways.Where(g => g.Store == store);

    /// <summary>
    /// Parses the script's JSON line. Windows PowerShell 5.1's ConvertTo-Json writes a one-item
    /// array as the bare item in some cases, so every list accepts either.
    /// </summary>
    public static AdapterState Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new AdapterState(
            Dhcp: root.TryGetProperty("dhcp", out var d) && d.ValueKind is JsonValueKind.True or JsonValueKind.False ? d.GetBoolean() : null,
            Addresses: Items(root, "addresses").Select(e => new Address(
                Str(e, "address"), e.GetProperty("prefix").GetInt32(), Str(e, "store"), Str(e, "state"), Str(e, "origin"))).ToList(),
            Gateways: Items(root, "gateways").Select(e => new Gateway(Str(e, "nextHop"), Str(e, "store"))).ToList(),
            Dns: Items(root, "dns").Select(e => e.GetString() ?? "").ToList(),
            Renew: root.TryGetProperty("renew", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : null,
            Errors: Items(root, "errors").Select(e => e.GetString() ?? "").ToList(),
            DhcpSaved: root.TryGetProperty("dhcpSaved", out var ds) && ds.ValueKind is JsonValueKind.True or JsonValueKind.False ? ds.GetBoolean() : null);
    }

    static IEnumerable<JsonElement> Items(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v)) return [];
        return v.ValueKind switch
        {
            JsonValueKind.Array => v.EnumerateArray().ToList(),
            JsonValueKind.Null or JsonValueKind.Undefined => [],
            _ => [v],
        };
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
}

/// <summary>The verdict on an apply: did the adapter end up holding exactly what was asked for?</summary>
public sealed record ApplyOutcome(bool Ok, string Summary, IReadOnlyList<string> Problems)
{
    const string Active = "ActiveStore";
    const string Persistent = "PersistentStore";

    // RenewDHCPLease return codes (Win32_NetworkAdapterConfiguration) worth naming.
    static readonly Dictionary<int, string> RenewCodes = new()
    {
        [0] = "renewed",
        [1] = "renewed (a restart is needed)",
        [82] = "no DHCP server answered",
        [83] = "the lease could not be renewed",
        [84] = "IP is not enabled on this adapter",
        [91] = "access denied",
    };

    /// <summary>Checks the read-back after a static preset was applied.</summary>
    public static ApplyOutcome CheckStatic(Preset preset, AdapterState s, bool isUp)
    {
        var problems = new List<string>(s.Errors);
        var want = $"{preset.Address}/{preset.Prefix}";

        if (DhcpOn(s, isUp)) problems.Add("DHCP is still on.");
        CheckStore(Persistent, "saved settings");
        if (isUp) CheckStore(Active, "live settings");

        var duplicate = s.InStore(Active).FirstOrDefault(a => a.Ip == preset.Address && a.State == "Duplicate");
        if (duplicate is not null) problems.Add($"Windows reports {preset.Address} as a duplicate: another device on the network already has it.");

        if (preset.Dns.Count > 0 && !s.Dns.SequenceEqual(preset.Dns))
            problems.Add($"DNS is {Show(s.Dns)}, expected {string.Join(", ", preset.Dns)}.");

        return problems.Count == 0
            ? new(true, isUp ? $"{preset.Name} applied: {want}." : $"{preset.Name} saved: {want}. It takes effect when the adapter connects.", [])
            : new(false, $"{preset.Name} didn't apply cleanly.", problems);

        void CheckStore(string store, string label)
        {
            var addresses = s.InStore(store).Select(a => $"{a.Ip}/{a.Prefix}").ToList();
            if (!addresses.SequenceEqual([want])) problems.Add($"Address in the {label} is {Show(addresses)}, expected {want}.");
            var gateways = s.GatewaysIn(store).Select(g => g.NextHop).ToList();
            List<string> wantGateways = string.IsNullOrEmpty(preset.Gateway) ? [] : [preset.Gateway];
            if (!gateways.SequenceEqual(wantGateways))
                problems.Add($"Gateway in the {label} is {Show(gateways)}, expected {(wantGateways.Count == 0 ? "none" : preset.Gateway)}.");
        }
    }

    /// <summary>Checks the read-back after DHCP was applied. No lease is reported, not treated as failure.</summary>
    public static ApplyOutcome CheckDhcp(AdapterState s, bool isUp)
    {
        var problems = new List<string>(s.Errors);
        if (!DhcpOn(s, isUp) || s.DhcpSaved == false) problems.Add("DHCP is not on.");
        var saved = s.InStore(Persistent).Select(a => $"{a.Ip}/{a.Prefix}").ToList();
        if (saved.Count > 0) problems.Add($"Static addresses are still saved: {string.Join(", ", saved)}.");
        var savedGateways = s.GatewaysIn(Persistent).Select(g => g.NextHop).ToList();
        if (savedGateways.Count > 0) problems.Add($"Static gateways are still saved: {string.Join(", ", savedGateways)}.");
        var manual = s.InStore(Active).Where(a => a.Origin == "Manual").Select(a => a.Ip).ToList();
        if (manual.Count > 0) problems.Add($"Static addresses are still live: {string.Join(", ", manual)}.");
        if (problems.Count > 0) return new(false, "DHCP didn't apply cleanly.", problems);

        if (!isUp) return new(true, "DHCP on. The lease comes when the adapter connects.", []);
        var lease = s.InStore(Active).FirstOrDefault(a => a.Origin == "Dhcp" && a.State == "Preferred");
        if (lease is not null) return new(true, $"DHCP lease: {lease.Ip}/{lease.Prefix}.", []);
        var why = s.Renew is { } code ? RenewCodes.GetValueOrDefault(code, $"renew returned {code}") : "no lease yet";
        return new(true, $"DHCP on, but no lease ({why}).", []);
    }

    // Connected: on if either the live or the saved setting says so. Unplugged: the saved setting decides,
    // falling back to the live one only if the registry couldn't be read.
    static bool DhcpOn(AdapterState s, bool isUp) =>
        isUp ? s.Dhcp == true || s.DhcpSaved == true : (s.DhcpSaved ?? s.Dhcp) == true;

    static string Show(IReadOnlyList<string> items) => items.Count == 0 ? "none" : string.Join(", ", items);
}
