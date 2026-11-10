using System.Text;

namespace IpSwitcher.Core;

/// <summary>
/// The PowerShell (NetTCPIP cmdlets) that applies a selection to one adapter. Every selection
/// starts from a clean slate: all IPv4 addresses and default routes are removed from both the
/// active and the persistent store, then the selection is applied fresh (a static address on an
/// unplugged adapter goes through netsh, the only call that saves it there). The script ends by
/// printing what the adapter holds as one line of JSON (<see cref="AdapterState"/>).
/// </summary>
public static class ApplyScript
{
    /// <summary>
    /// The script for a static preset. The preset is re-validated here, because the presets file
    /// can be edited by hand and its values go into the script text.
    /// </summary>
    public static string Static(int interfaceIndex, Preset preset, bool isUp)
    {
        var errors = PresetValidator.Validate(preset.Name, preset.Address, preset.Prefix.ToString(),
            preset.Gateway ?? "", string.Join(",", preset.Dns), [], out var clean);
        if (clean is null) throw new ArgumentException($"Preset \"{preset.Name}\" is not valid: {string.Join(" ", errors)}");

        var s = Begin(interfaceIndex);
        if (isUp)
        {
            s.AppendLine("Step 'turn off DHCP' { Set-NetIPInterface -InterfaceIndex $i -AddressFamily IPv4 -Dhcp Disabled }");
            Clear(s);
            s.Append($"$p = @{{ InterfaceIndex = $i; AddressFamily = 'IPv4'; IPAddress = '{clean.Address}'; PrefixLength = {clean.Prefix}");
            if (clean.Gateway is not null) s.Append($"; DefaultGateway = '{clean.Gateway}'");
            s.AppendLine(" }");
            s.AppendLine("Step 'set the address' { New-NetIPAddress @p | Out-Null }");
        }
        else
        {
            // Unplugged, Windows drops every API's DHCP-off request: Set-NetIPInterface reports success and
            // changes nothing (New-NetIPAddress then fails with "Inconsistent parameters PolicyStore
            // PersistentStore and Dhcp Enabled"), and netsh saves the address but leaves DHCP on. netsh for
            // the address and gateway, then EnableDHCP=0 written to the registry, which takes effect at once.
            Clear(s);
            var gateway = clean.Gateway is null ? "gateway=none" : $"gateway={clean.Gateway} gwmetric=0";
            s.AppendLine($$"""
                Step 'set the address (netsh)' {
                    $alias = (Get-NetAdapter -InterfaceIndex $i).Name
                    $r = netsh interface ipv4 set address name="$alias" source=static address={{clean.Address}} mask={{Ipv4.MaskText(clean.Prefix)}} {{gateway}}
                    if ($LASTEXITCODE) { throw "netsh: $(($r | ? { $_ }) -join ' ')" }
                }
                """);
            s.AppendLine("Step 'turn off DHCP (saved setting)' { Set-ItemProperty (TcpipKey) -Name EnableDHCP -Value 0 -Type DWord }");
        }
        s.AppendLine(clean.Dns.Count > 0
            ? $"Step 'set DNS' {{ Set-DnsClientServerAddress -InterfaceIndex $i -ServerAddresses @({string.Join(", ", clean.Dns.Select(d => $"'{d}'"))}) }}"
            : "Step 'clear DNS' { Set-DnsClientServerAddress -InterfaceIndex $i -ResetServerAddresses }");
        // Duplicate address detection runs for about a second after an address is added.
        if (isUp) s.AppendLine("WaitFor { -not (Get-NetIPAddress -InterfaceIndex $i -AddressFamily IPv4 -ErrorAction SilentlyContinue | ? AddressState -eq 'Tentative') } 5");
        return End(s);
    }

    /// <summary>The script for DHCP: clean slate, DHCP on for address and DNS, then renew this adapter's lease only.</summary>
    public static string Dhcp(int interfaceIndex, bool isUp)
    {
        var s = Begin(interfaceIndex);
        Clear(s);
        s.AppendLine("Step 'turn on DHCP' { Set-NetIPInterface -InterfaceIndex $i -AddressFamily IPv4 -Dhcp Enabled }");
        // Unplugged, Windows can drop the DHCP flag change (it drops DHCP off; see Static), so the saved
        // setting is written to the registry as well.
        if (!isUp) s.AppendLine("Step 'turn on DHCP (saved setting)' { Set-ItemProperty (TcpipKey) -Name EnableDHCP -Value 1 -Type DWord }");
        s.AppendLine("Step 'DNS from DHCP' { Set-DnsClientServerAddress -InterfaceIndex $i -ResetServerAddresses }");
        if (isUp)
        {
            // Win32_NetworkAdapterConfiguration.RenewDHCPLease renews one adapter; ipconfig /renew without a name renews all.
            s.AppendLine("""
                $renew = $null
                try {
                    $c = Get-CimInstance Win32_NetworkAdapterConfiguration -Filter "InterfaceIndex=$i"
                    $renew = [int](Invoke-CimMethod -InputObject $c -MethodName RenewDHCPLease).ReturnValue
                } catch { $errors += "renew the lease: $($_.Exception.Message)" }
                """);
            s.AppendLine("WaitFor { Get-NetIPAddress -InterfaceIndex $i -AddressFamily IPv4 -ErrorAction SilentlyContinue | ? { $_.PrefixOrigin -eq 'Dhcp' -and $_.AddressState -eq 'Preferred' } } 10");
        }
        return End(s);
    }

    /// <summary>Only reads the adapter; changes nothing. Runs without administrator rights.</summary>
    public static string ReadOnly(int interfaceIndex) => End(Begin(interfaceIndex));

    static StringBuilder Begin(int interfaceIndex)
    {
        var s = new StringBuilder();
        s.AppendLine("$ErrorActionPreference = 'Stop'");
        s.AppendLine("$ProgressPreference = 'SilentlyContinue'");
        s.AppendLine($"$i = {interfaceIndex}");
        s.AppendLine("$errors = @()");
        s.AppendLine("$renew = $null");
        s.AppendLine("function Step($name, [scriptblock]$block) { try { & $block } catch { $script:errors += \"${name}: $($_.Exception.Message)\" } }");
        s.AppendLine("function WaitFor([scriptblock]$done, $seconds) { $end = (Get-Date).AddSeconds($seconds); while (-not (& $done) -and (Get-Date) -lt $end) { Start-Sleep -Milliseconds 250 } }");
        // The adapter's saved TCP/IP settings; EnableDHCP here is the saved DHCP flag.
        s.AppendLine("function TcpipKey { \"HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\$((Get-NetAdapter -InterfaceIndex $i).InterfaceGuid)\" }");
        return s;
    }

    static void Clear(StringBuilder s)
    {
        s.AppendLine("""
            foreach ($store in 'ActiveStore', 'PersistentStore') {
                Step "remove addresses ($store)" { Get-NetIPAddress -InterfaceIndex $i -AddressFamily IPv4 -PolicyStore $store -ErrorAction SilentlyContinue | Remove-NetIPAddress -Confirm:$false }
                Step "remove gateways ($store)" { Get-NetRoute -InterfaceIndex $i -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -PolicyStore $store -ErrorAction SilentlyContinue | Remove-NetRoute -Confirm:$false }
            }
            """);
    }

    static string End(StringBuilder s)
    {
        s.AppendLine("""
            $addresses = @(foreach ($store in 'ActiveStore', 'PersistentStore') {
                Get-NetIPAddress -InterfaceIndex $i -AddressFamily IPv4 -PolicyStore $store -ErrorAction SilentlyContinue |
                    % { [ordered]@{ address = $_.IPAddress; prefix = [int]$_.PrefixLength; store = $store; state = "$($_.AddressState)"; origin = "$($_.PrefixOrigin)" } }
            })
            $gateways = @(foreach ($store in 'ActiveStore', 'PersistentStore') {
                Get-NetRoute -InterfaceIndex $i -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -PolicyStore $store -ErrorAction SilentlyContinue |
                    % { [ordered]@{ nextHop = $_.NextHop; store = $store } }
            })
            $dhcp = $null
            try { $dhcp = "$((Get-NetIPInterface -InterfaceIndex $i -AddressFamily IPv4).Dhcp)" -eq 'Enabled' } catch { $errors += "read DHCP: $($_.Exception.Message)" }
            # The saved DHCP setting lives only in the registry. Unplugged, Get-NetIPInterface keeps saying
            # Enabled after a static address is saved, so this is the only true answer for that case.
            $dhcpSaved = $null
            try { $dhcpSaved = [int](Get-ItemProperty (TcpipKey) -Name EnableDHCP).EnableDHCP -eq 1 } catch { }
            $dns = @()
            try { $dns = @((Get-DnsClientServerAddress -InterfaceIndex $i -AddressFamily IPv4).ServerAddresses) } catch { }
            [ordered]@{ dhcp = $dhcp; dhcpSaved = $dhcpSaved; addresses = $addresses; gateways = $gateways; dns = $dns; renew = $renew; errors = @($errors) } | ConvertTo-Json -Depth 4 -Compress
            """);
        return s.ToString();
    }
}
