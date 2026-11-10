// The Core library's checks. Prints PASS/FAIL per check; exit 0 when all pass, 1 otherwise.
// `--adapters` also lists what AdapterReader finds on this machine (read-only).
using System.Net;
using IpSwitcher.Core;

var passed = 0;
var failed = 0;
void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
    if (ok) passed++; else failed++;
}

IPAddress Ip(string s) => IPAddress.Parse(s);
AdapterInfo Adapter(bool dhcp, string[] addresses, string[] gateways, bool up = true) => new(
    "AA-BB-CC-DD-EE-FF", "Ethernet", "Test adapter", up, false, dhcp,
    addresses.Select(a => { var p = a.Split('/'); return new Ipv4Address(Ip(p[0]), int.Parse(p[1])); }).ToList(),
    gateways.Select(Ip).ToList(), [], 7);

// --- Ipv4 ---
Check("a dotted quad parses", Ipv4.TryParse("192.168.1.50", out var a1) && a1.ToString() == "192.168.1.50");
Check("surrounding spaces are ignored", Ipv4.TryParse(" 10.0.0.1 ", out _));
Check("three parts are refused (IPAddress.Parse would take 10.1.1)", !Ipv4.TryParse("10.1.1", out _));
Check("a part over 255 is refused", !Ipv4.TryParse("192.168.1.256", out _));
Check("a leading zero is refused", !Ipv4.TryParse("192.168.01.1", out _));
Check("hex is refused", !Ipv4.TryParse("0x0A.0.0.1", out _));
Check("empty is refused", !Ipv4.TryParse("", out _) && !Ipv4.TryParse(null, out _));
Check("prefix 24 parses", Ipv4.TryParsePrefix("24", out var p1) && p1 == 24);
Check("prefix /16 parses", Ipv4.TryParsePrefix("/16", out var p2) && p2 == 16);
Check("mask 255.255.255.0 is /24", Ipv4.TryParsePrefix("255.255.255.0", out var p3) && p3 == 24);
Check("mask 255.255.252.0 is /22", Ipv4.TryParsePrefix("255.255.252.0", out var p4) && p4 == 22);
Check("mask 255.255.255.255 is /32", Ipv4.TryParsePrefix("255.255.255.255", out var p5) && p5 == 32);
Check("a non-contiguous mask is refused", !Ipv4.TryParsePrefix("255.0.255.0", out _));
Check("prefix 33 is refused", !Ipv4.TryParsePrefix("33", out _));
Check("mask text of /24", Ipv4.MaskText(24) == "255.255.255.0");
Check("same subnet /24", Ipv4.SameSubnet(Ip("192.168.1.5"), Ip("192.168.1.1"), 24));
Check("different subnet /24", !Ipv4.SameSubnet(Ip("192.168.2.5"), Ip("192.168.1.1"), 24));
Check("subnet copy of /24", Ipv4.FirstHostCidr(Ip("172.22.0.248"), 24) == "172.22.0.1/24");
Check("subnet copy of /16", Ipv4.FirstHostCidr(Ip("172.22.0.248"), 16) == "172.22.0.1/16");
Check("subnet copy of /22 crosses an octet", Ipv4.FirstHostCidr(Ip("192.168.11.92"), 22) == "192.168.8.1/22");
Check("subnet copy of /8", Ipv4.FirstHostCidr(Ip("10.200.3.4"), 8) == "10.0.0.1/8");
Check("subnet copy of /31 and /32 is the network", Ipv4.FirstHostCidr(Ip("10.0.0.7"), 31) == "10.0.0.6/31" && Ipv4.FirstHostCidr(Ip("10.0.0.7"), 32) == "10.0.0.7/32");
Check("169.254 is link-local", Ipv4.IsLinkLocal(Ip("169.254.12.4")) && !Ipv4.IsLinkLocal(Ip("169.253.0.1")));

// --- PresetValidator ---
IReadOnlyList<string> V(string name, string addr, string mask, string gw = "", string dns = "", params string[] others) =>
    PresetValidator.Validate(name, addr, mask, gw, dns, others, out _);

var ok = PresetValidator.Validate("Crestron", "192.168.1.50", "255.255.255.0", "192.168.1.1", "8.8.8.8, 1.1.1.1", [], out var preset);
Check("a full preset validates", ok.Count == 0 && preset is { Name: "Crestron", Address: "192.168.1.50", Prefix: 24, Gateway: "192.168.1.1" } && preset.Dns.SequenceEqual(["8.8.8.8", "1.1.1.1"]));
Check("gateway and DNS may be empty", V("Bench", "172.22.0.50", "24").Count == 0);
Check("the preset summary reads naturally", preset!.Summary == "192.168.1.50/24 · gateway 192.168.1.1 · DNS 8.8.8.8, 1.1.1.1");
Check("a name is required", V(" ", "10.0.0.5", "24").Count == 1);
Check("DHCP is not a preset name", V("dhcp", "10.0.0.5", "24").Count == 1);
Check("a duplicate name on the adapter is refused, ignoring case", V("bench", "10.0.0.5", "24", "", "", "Bench").Count == 1);
Check("the network address is refused", V("x", "192.168.1.0", "24").Count == 1);
Check("the broadcast address is refused", V("x", "192.168.1.255", "24").Count == 1);
Check("a /31 allows either address", V("x", "10.0.0.0", "31").Count == 0);
Check("a /32 validates", V("x", "10.0.0.7", "32").Count == 0);
Check("loopback is refused", V("x", "127.0.0.5", "8").Count == 1);
Check("multicast is refused", V("x", "224.0.0.5", "24").Count == 1);
Check("a gateway off the subnet is refused", V("x", "192.168.1.50", "24", "192.168.2.1").Count == 1);
Check("a gateway equal to the address is refused", V("x", "192.168.1.50", "24", "192.168.1.50").Count == 1);
Check("a bad DNS entry is named", V("x", "192.168.1.50", "24", "", "8.8.8.8 nope").Single().Contains("nope"));
Check("several problems are all reported", V("", "300.1.1.1", "255.0.255.0").Count == 3);

// --- PresetStore ---
var dir = Path.Combine(Path.GetTempPath(), "ip-switcher-tests-" + Guid.NewGuid().ToString("N"));
var path = Path.Combine(dir, "presets.json");
var store = new PresetStore();
store.Put("aa:bb:cc:dd:ee:ff", "Ethernet", new Preset { Name = "Bench", Address = "172.22.0.50", Prefix = 24 });
store.Put("AA-BB-CC-DD-EE-FF", "Ethernet", new Preset { Name = "Crestron", Address = "192.168.1.50", Prefix = 24 });
store.Put("11-22-33-44-55-66", "USB", new Preset { Name = "Bench", Address = "172.22.0.60", Prefix = 24 });
Check("presets are per adapter, MAC case and separator ignored", store.For("aa-bb-cc-dd-ee-ff").Count == 2 && store.For("11:22:33:44:55:66").Count == 1);
Check("an unknown adapter has no presets", store.For("00-00-00-00-00-01").Count == 0);
store.Put("AA-BB-CC-DD-EE-FF", "Ethernet 2", new Preset { Name = "Bench lab", Address = "172.22.0.51", Prefix = 24 }, replacing: "Bench");
var names = store.For("AA-BB-CC-DD-EE-FF").Select(p => p.Name).ToList();
Check("editing replaces the preset in place", names.SequenceEqual(["Bench lab", "Crestron"]));
Check("the adapter's last name is kept", store.Adapters["AA-BB-CC-DD-EE-FF"].LastName == "Ethernet 2");
store.Save(path);
var loaded = PresetStore.Load(path);
Check("save and load round-trip", loaded.For("AA-BB-CC-DD-EE-FF").Select(p => p.Address).SequenceEqual(["172.22.0.51", "192.168.1.50"]) && loaded.LoadWarning is null);
Check("save leaves no temporary file", !File.Exists(path + ".tmp"));
loaded.Delete("aa-bb-cc-dd-ee-ff", "Crestron");
Check("delete removes one preset", loaded.For("AA-BB-CC-DD-EE-FF").Select(p => p.Name).SequenceEqual(["Bench lab"]));
Check("a missing file loads empty", PresetStore.Load(Path.Combine(dir, "none.json")).Adapters.Count == 0);
File.WriteAllText(path, "{ not json");
var broken = PresetStore.Load(path);
Check("an unreadable file is moved aside, not lost", broken.LoadWarning is not null && !File.Exists(path)
      && Directory.GetFiles(dir, "presets.json.unreadable-*").Length == 1);
Directory.Delete(dir, recursive: true);

// --- AdapterStatus ---
var presets = new List<Preset>
{
    new() { Name = "Bench", Address = "172.22.0.50", Prefix = 24 },
    new() { Name = "Crestron", Address = "192.168.1.50", Prefix = 24, Gateway = "192.168.1.1" },
};
Check("DHCP with a lease", AdapterStatus.Describe(Adapter(true, ["10.0.0.23/24"], ["10.0.0.1"]), presets) is { ModeText: "DHCP", Warning: false });
Check("DHCP on 169.254 is no lease", AdapterStatus.Describe(Adapter(true, ["169.254.12.4/16"], []), presets) is { ModeText: "DHCP · no lease", Warning: true });
Check("DHCP unplugged is not a warning", AdapterStatus.Describe(Adapter(true, [], [], up: false), presets) is { AddressText: "Not connected", Warning: false });
Check("a static address matching a preset names it", AdapterStatus.Describe(Adapter(false, ["172.22.0.50/24"], []), presets).ActivePreset?.Name == "Bench");
Check("the gateway is part of the match", AdapterStatus.Match(Adapter(false, ["192.168.1.50/24"], []), presets) is null
      && AdapterStatus.Match(Adapter(false, ["192.168.1.50/24"], ["192.168.1.1"]), presets)?.Name == "Crestron");
Check("the prefix is part of the match", AdapterStatus.Match(Adapter(false, ["172.22.0.50/16"], []), presets) is null);
Check("a static address with no preset says so", AdapterStatus.Describe(Adapter(false, ["10.9.9.9/24"], []), presets).ModeText == "Static · not a saved preset");
Check("stacked addresses are flagged", AdapterStatus.Describe(Adapter(false, ["172.22.0.50/24", "192.168.1.50/24"], []), presets) is { Warning: true, ActivePreset: null });

// --- AdapterReader ---
Check("Hyper-V's vEthernet is virtual", AdapterReader.IsVirtual("Hyper-V Virtual Ethernet Adapter"));
Check("VirtualBox host-only is virtual", AdapterReader.IsVirtual("VirtualBox Host-Only Ethernet Adapter"));
Check("a USB dongle is not virtual", !AdapterReader.IsVirtual("ASIX AX88179 USB 3.0 to Gigabit Ethernet Adapter"));
Check("an Intel NIC is not virtual", !AdapterReader.IsVirtual("Intel(R) Ethernet Connection (16) I219-LM"));
Check("a Wi-Fi card's #3 port is hidden", AdapterReader.IsExtraWirelessPort("Intel(R) Wi-Fi 7 BE201 320MHz #3", wireless: true));
Check("the Wi-Fi card itself is shown", !AdapterReader.IsExtraWirelessPort("Intel(R) Wi-Fi 7 BE201 320MHz", wireless: true));
Check("a second identical USB dongle (#2) is shown", !AdapterReader.IsExtraWirelessPort("ASIX AX88179 USB 3.0 to Gigabit Ethernet Adapter #2", wireless: false));
if (OperatingSystem.IsWindows())
{
    var found = AdapterReader.Read();
    Check("this machine's adapters read without error, none virtual", found.All(x => !AdapterReader.IsVirtual(x.Description)));
    if (args.Contains("--adapters"))
        foreach (var x in found)
            Console.WriteLine($"      {x.Name} [{x.Description}] {x.Mac} up={x.IsUp} dhcp={x.DhcpEnabled} {string.Join(", ", x.Addresses)} gw={string.Join(", ", x.Gateways)}");
}

// --- ApplyScript ---
var bench = new Preset { Name = "Crestron", Address = "192.168.1.50", Prefix = 24, Gateway = "192.168.1.1", Dns = ["8.8.8.8"] };
var stat = ApplyScript.Static(27, bench, isUp: true);
int At(string script, string text) => script.IndexOf(text, StringComparison.Ordinal);
Check("static: DHCP off, then clear, then the address", At(stat, "-Dhcp Disabled") < At(stat, "Remove-NetIPAddress") && At(stat, "Remove-NetIPAddress") < At(stat, "New-NetIPAddress"));
Check("static: clears both stores", stat.Contains("'ActiveStore', 'PersistentStore'") && stat.Contains("Remove-NetRoute"));
Check("static: the preset's values are in the script", stat.Contains("IPAddress = '192.168.1.50'; PrefixLength = 24; DefaultGateway = '192.168.1.1'") && stat.Contains("-ServerAddresses @('8.8.8.8')"));
Check("static: targets the adapter by index", stat.Contains("$i = 27"));
var noGw = ApplyScript.Static(27, new Preset { Name = "Bench", Address = "172.22.0.50", Prefix = 24 }, isUp: false);
Check("static: no gateway, DNS reset, no wait when unplugged", !noGw.Contains("DefaultGateway") && noGw.Contains("-ResetServerAddresses") && !noGw.Contains("WaitFor {"));
// Measured on an unplugged USB adapter: only netsh saves a static address there.
var unpluggedGw = ApplyScript.Static(27, bench, isUp: false);
Check("static, unplugged: clear, then netsh; no Set-NetIPInterface or New-NetIPAddress",
    At(unpluggedGw, "Remove-NetIPAddress") < At(unpluggedGw, "netsh interface ipv4 set address")
    && !unpluggedGw.Contains("-Dhcp Disabled") && !unpluggedGw.Contains("New-NetIPAddress"));
Check("static, unplugged: netsh gets address, dotted mask, gateway",
    unpluggedGw.Contains("source=static address=192.168.1.50 mask=255.255.255.0 gateway=192.168.1.1 gwmetric=0")
    && noGw.Contains("address=172.22.0.50 mask=255.255.255.0 gateway=none"));
Check("static, unplugged: netsh, then the saved DHCP flag set to 0 in the registry",
    At(unpluggedGw, "netsh interface ipv4 set address") < At(unpluggedGw, "-Name EnableDHCP -Value 0"));
Check("static, connected: unchanged path, no netsh, no registry write", !stat.Contains("netsh") && !stat.Contains("Set-ItemProperty"));
Check("DHCP, unplugged: the saved DHCP flag set to 1 after Set-NetIPInterface",
    At(ApplyScript.Dhcp(27, false), "-Dhcp Enabled") < At(ApplyScript.Dhcp(27, false), "-Name EnableDHCP -Value 1"));
Check("DHCP, connected: no registry write", !ApplyScript.Dhcp(27, true).Contains("Set-ItemProperty"));
Check("read-back reports the saved DHCP flag from the registry", ApplyScript.ReadOnly(27).Contains("dhcpSaved = $dhcpSaved") && ApplyScript.ReadOnly(27).Contains("EnableDHCP"));
var injected = new Preset { Name = "x", Address = "10.0.0.5'; Remove-Item C:\\ -Recurse; '", Prefix = 24 };
Check("static: a hand-edited preset with script in it is refused", Throws(() => ApplyScript.Static(27, injected, true)));
Check("static: a hand-edited bad gateway is refused", Throws(() => ApplyScript.Static(27, new Preset { Name = "x", Address = "10.0.0.5", Prefix = 24, Gateway = "10.0.1.1" }, true)));
var dhcp = ApplyScript.Dhcp(27, isUp: true);
Check("DHCP: clear, then DHCP on, then renew this adapter", At(dhcp, "Remove-NetIPAddress") < At(dhcp, "-Dhcp Enabled") && At(dhcp, "-Dhcp Enabled") < At(dhcp, "RenewDHCPLease"));
Check("DHCP: renews by interface index, not every adapter", dhcp.Contains("\"InterfaceIndex=$i\"") && !dhcp.Contains("ipconfig"));
Check("DHCP: unplugged skips the renew", !ApplyScript.Dhcp(27, isUp: false).Contains("RenewDHCPLease"));
Check("read-only changes nothing", !ApplyScript.ReadOnly(27).Contains("Set-Net") && !ApplyScript.ReadOnly(27).Contains("Remove-") && !ApplyScript.ReadOnly(27).Contains("New-Net") && !ApplyScript.ReadOnly(27).Contains("Set-ItemProperty"));

// --- AdapterState ---
const string staticJson = """{"dhcp":false,"addresses":[{"address":"192.168.1.50","prefix":24,"store":"ActiveStore","state":"Preferred","origin":"Manual"},{"address":"192.168.1.50","prefix":24,"store":"PersistentStore","state":"Preferred","origin":"Manual"}],"gateways":[{"nextHop":"192.168.1.1","store":"ActiveStore"},{"nextHop":"192.168.1.1","store":"PersistentStore"}],"dns":["8.8.8.8"],"renew":null,"errors":[]}""";
var st = AdapterState.Parse(staticJson);
Check("parse: addresses and gateways by store", st.InStore("PersistentStore").Count() == 1 && st.GatewaysIn("ActiveStore").Single().NextHop == "192.168.1.1" && st.Dhcp == false);
var single = AdapterState.Parse("""{"dhcp":true,"addresses":{"address":"10.0.0.23","prefix":24,"store":"ActiveStore","state":"Preferred","origin":"Dhcp"},"gateways":null,"dns":"10.0.0.1","renew":0,"errors":"renew the lease: boom"}""");
Check("parse: PowerShell 5.1's bare single items", single.Addresses.Count == 1 && single.Gateways.Count == 0 && single.Dns.SequenceEqual(["10.0.0.1"]) && single.Errors.Count == 1 && single.Renew == 0);

// --- ApplyOutcome ---
Check("static: exact match is OK", ApplyOutcome.CheckStatic(bench, st, isUp: true) is { Ok: true, Summary: "Crestron applied: 192.168.1.50/24." });
var leftover = AdapterState.Parse(staticJson.Replace("""{"nextHop":"192.168.1.1","store":"PersistentStore"}""", """{"nextHop":"192.168.1.1","store":"PersistentStore"},{"nextHop":"172.22.0.4","store":"PersistentStore"}"""));
var lo = ApplyOutcome.CheckStatic(bench, leftover, isUp: true);
Check("static: a leftover saved gateway fails and is named", !lo.Ok && lo.Problems.Single().Contains("172.22.0.4") && lo.Problems.Single().Contains("saved settings"));
var dup = ApplyOutcome.CheckStatic(bench, AdapterState.Parse(staticJson.Replace("\"state\":\"Preferred\",\"origin\":\"Manual\"},{", "\"state\":\"Duplicate\",\"origin\":\"Manual\"},{")), isUp: true);
Check("static: a duplicate address fails with the reason", !dup.Ok && dup.Problems.Any(p => p.Contains("duplicate")));
var unplugged = AdapterState.Parse("""{"dhcp":false,"addresses":[{"address":"172.22.0.50","prefix":24,"store":"PersistentStore","state":"Preferred","origin":"Manual"}],"gateways":[],"dns":[],"errors":[]}""");
Check("static: unplugged checks only the saved settings", ApplyOutcome.CheckStatic(new Preset { Name = "Bench", Address = "172.22.0.50", Prefix = 24, Gateway = "" }, unplugged, isUp: false) is { Ok: true } o1 && o1.Summary.Contains("when the adapter connects"));
var savedOnly = unplugged with { Dhcp = true, DhcpSaved = false };   // what the dongle reads after netsh
var benchNoGw = new Preset { Name = "Bench", Address = "172.22.0.50", Prefix = 24, Gateway = "" };
Check("static, unplugged: live DHCP reading Enabled is ignored when the saved flag is off", ApplyOutcome.CheckStatic(benchNoGw, savedOnly, isUp: false).Ok);
Check("static, unplugged: saved DHCP still on fails", !ApplyOutcome.CheckStatic(benchNoGw, unplugged with { DhcpSaved = true }, isUp: false).Ok);
Check("static, connected: saved DHCP still on fails", !ApplyOutcome.CheckStatic(bench, st with { DhcpSaved = true }, true).Ok);
Check("parse: dhcpSaved", AdapterState.Parse(staticJson.Replace("\"dhcp\":false,", "\"dhcp\":false,\"dhcpSaved\":false,")).DhcpSaved == false && st.DhcpSaved is null);
Check("static: a script error fails the apply", !ApplyOutcome.CheckStatic(bench, st with { Errors = ["set the address: denied"] }, true).Ok);
Check("static: wrong DNS fails", !ApplyOutcome.CheckStatic(bench, st with { Dns = ["1.1.1.1"] }, true).Ok);

var lease = AdapterState.Parse("""{"dhcp":true,"addresses":[{"address":"172.22.0.152","prefix":24,"store":"ActiveStore","state":"Preferred","origin":"Dhcp"}],"gateways":[{"nextHop":"172.22.0.4","store":"ActiveStore"}],"dns":[],"renew":0,"errors":[]}""");
Check("DHCP: a lease is reported", ApplyOutcome.CheckDhcp(lease, true) is { Ok: true, Summary: "DHCP lease: 172.22.0.152/24." });
var noLease = AdapterState.Parse("""{"dhcp":true,"addresses":[{"address":"169.254.3.4","prefix":16,"store":"ActiveStore","state":"Preferred","origin":"WellKnown"}],"gateways":[],"dns":[],"renew":82,"errors":[]}""");
Check("DHCP: no server is OK but says so", ApplyOutcome.CheckDhcp(noLease, true) is { Ok: true } o2 && o2.Summary.Contains("no DHCP server answered"));
var stale = AdapterState.Parse("""{"dhcp":true,"addresses":[{"address":"192.168.1.50","prefix":24,"store":"PersistentStore","state":"Preferred","origin":"Manual"}],"gateways":[{"nextHop":"192.168.1.1","store":"PersistentStore"}],"dns":[],"renew":0,"errors":[]}""");
Check("DHCP: saved static leftovers fail, both named", ApplyOutcome.CheckDhcp(stale, true) is { Ok: false } o3 && o3.Problems.Count == 2);
Check("DHCP: still off fails", !ApplyOutcome.CheckDhcp(lease with { Dhcp = false }, true).Ok);
Check("DHCP: saved setting still off fails", !ApplyOutcome.CheckDhcp(lease with { DhcpSaved = false }, true).Ok);
var dhcpUnplugged = AdapterState.Parse("""{"dhcp":true,"dhcpSaved":true,"addresses":[],"gateways":[],"dns":[],"errors":[]}""");
Check("DHCP, unplugged: saved on is OK", ApplyOutcome.CheckDhcp(dhcpUnplugged, false).Ok);
Check("DHCP, unplugged: saved off fails even though live reads on", !ApplyOutcome.CheckDhcp(dhcpUnplugged with { DhcpSaved = false }, false).Ok);

// --- LogonTask ---
var xml = LogonTask.Xml(@"C:\Tools\ip-switcher.exe", @"DESKTOP\j&m");
var task = System.Xml.Linq.XDocument.Parse(xml.Replace("encoding=\"UTF-16\"", ""));
System.Xml.Linq.XNamespace tn = "http://schemas.microsoft.com/windows/2004/02/mit/task";
Check("task: well-formed, user escaped", task.Descendants(tn + "UserId").All(u => u.Value == @"DESKTOP\j&m"));
Check("task: elevated, no time limit, runs on battery",
    task.Descendants(tn + "RunLevel").Single().Value == "HighestAvailable" && task.Descendants(tn + "ExecutionTimeLimit").Single().Value == "PT0S"
    && task.Descendants(tn + "DisallowStartIfOnBatteries").Single().Value == "false");

// --- Live, read-only: the read-back script against this machine's adapters ---
if (OperatingSystem.IsWindows())
{
    // PowerShell's own parser on every script the app can generate, without running them.
    var scripts = new Dictionary<string, string>
    {
        ["static, gateway and DNS"] = stat,
        ["static, no gateway, unplugged"] = noGw,
        ["static, gateway and DNS, unplugged"] = unpluggedGw,
        ["DHCP, connected"] = dhcp,
        ["DHCP, unplugged"] = ApplyScript.Dhcp(27, isUp: false),
        ["read-only"] = ApplyScript.ReadOnly(27),
    };
    foreach (var (name, script) in scripts)
    {
        var b64 = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var check = $"$t = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{b64}')); $e = $null; " +
                    "[void][System.Management.Automation.Language.Parser]::ParseInput($t, [ref]$null, [ref]$e); " +
                    "if ($e.Count) { $e | % { \"$($_.Extent.StartLineNumber): $($_.Message)\" } } else { 'OK' }";
        var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -EncodedCommand {Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(check))}")
        { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var result = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        Check($"PowerShell parses the {name} script", result == "OK");
        if (result != "OK") Console.WriteLine("      " + result.Replace("\n", "\n      "));
    }

    foreach (var adapter in AdapterReader.Read().Where(x => !x.IsWireless))
    {
        var (state, _) = PowerShell.Run(ApplyScript.ReadOnly(adapter.InterfaceIndex), TimeSpan.FromSeconds(30));
        var desc = $"{adapter.Name}: " + (state is null ? "no output" : $"dhcp={state.Dhcp} {string.Join(", ", state.Addresses.Select(x => $"{x.Ip}/{x.Prefix} {x.Store} {x.Origin}"))} gw={string.Join(", ", state.Gateways.Select(g => $"{g.NextHop} {g.Store}"))}");
        var agrees = state is not null && state.Errors.Count == 0 && state.Dhcp == adapter.DhcpEnabled
            && state.InStore("ActiveStore").Select(x => x.Ip).Order().SequenceEqual(adapter.Addresses.Select(x => x.Address.ToString()).Order());
        Check($"live read-back agrees with the adapter reader ({adapter.Name})", agrees);
        if (args.Contains("--adapters") || !agrees) Console.WriteLine("      " + desc);
    }
}

Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

static bool Throws(Action action)
{
    try { action(); return false; } catch (ArgumentException) { return true; }
}
