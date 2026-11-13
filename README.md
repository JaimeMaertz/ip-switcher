# ip-switcher

A Windows tray app for switching network adapters between DHCP and saved static presets.

Left-click the tray icon to open a Windows 11-style flyout listing every physical adapter
with its name, link state, address and gateway, and whether it's on DHCP, one of its
presets, or something else. Each adapter shows its own choices as buttons. One click clears
the adapter, applies the choice fresh, and reads back what Windows actually holds.

I built it because I switch adapters between DHCP and job-specific static addresses many
times a day, and Windows' own dialogs take five windows to do it.

- **Presets are per adapter and keyed by MAC address**, so a USB dongle keeps its presets
  whatever Windows names it and whichever port it's in.
- **Presets are created, edited and deleted in the app.** "Save current settings as
  preset" pre-fills the editor from the adapter.
- **No UAC prompt per switch.** A logon task starts the app elevated, and you approve it
  once.
- C# / WPF on .NET 10 with the Fluent theme. Follows light and dark mode.

## Install

Download `ip-switcher.exe` from the [Releases](https://github.com/JaimeMaertz/ip-switcher/releases)
page and put it in a folder only administrators can write to, such as
`C:\Program Files\ip-switcher\`. The logon task runs that exe elevated without a prompt, so
anything that could replace it in a user-writable folder would get the same elevation. It
needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

The exe isn't code-signed, so Windows SmartScreen may warn on first run: choose **More info**
then **Run anyway**.

Run it. The first time you change an adapter, it offers to set up the elevated logon task
(one UAC prompt). The tray menu's **Start elevated at logon** turns the task on and off.

```
ip-switcher.exe                          the tray app (one per user session)
ip-switcher.exe --install | --uninstall  create or remove the elevated logon task (asks UAC)
ip-switcher.exe --snapshot out.png [--theme Light|Dark] [--presets FILE]
                                         render the flyout to a PNG and exit
ip-switcher.exe --snapshot-editor out.png [--theme Light|Dark]
                                         the same for the preset editor
```

## What it shows

- **Adapters:** physical Ethernet and Wi-Fi with IPv4 bound. It hides Hyper-V's vEthernet
  (WSL's), VirtualBox and VMware host-only adapters, VPN taps, Bluetooth, and a Wi-Fi
  card's extra ports (Wi-Fi Direct, hotspot). Wired adapters with ` #N` in their name, such
  as two identical dongles, are shown.
- **Mode line:**
  - `DHCP`
  - `DHCP · no lease` (up with only a 169.254 address, flagged)
  - `Static · {preset}` when the address, prefix and gateway match one of the adapter's
    presets
  - `Static · not a saved preset`
  - a flagged stacked configuration (more than one address or gateway), which a clean apply
    removes
- **Buttons:** DHCP, the adapter's presets, and **+**. The active one is accent-coloured.
  Right-click a preset to edit or delete it. The **⋯** menu has New preset, Save current
  settings as preset, Copy IP address, Copy subnet (first host plus prefix, so
  `172.22.0.248/24` copies `172.22.0.1/24`, ready for a subnet scanner), and Copy MAC
  address.
- **The editor** lists every problem at once:
  - name missing, or already used on that adapter
  - address not a usable host (network, broadcast, loopback, multicast)
  - mask not contiguous (accepts `255.255.255.0`, `24` or `/24`)
  - gateway off the subnet, or equal to the address
  - DNS entries that aren't IPv4

The flyout refreshes on Windows' address-change events and each time it opens. The tray
icon's hover text lists each adapter's mode.

Presets live in `%APPDATA%\ip-switcher\presets.json`. If that file doesn't parse (say, after
a hand edit), it's renamed `presets.json.unreadable-{time}` and the flyout says so, so your
presets are never silently overwritten.

## How a switch is applied

Every switch starts from a clean slate. ip-switcher never stacks DHCP with a static
address and never adds secondary addresses, because stacked configurations make Windows
misbehave. Each switch goes through three steps:

1. **Clear the adapter.** Remove every IPv4 address, every default route and the DNS
   servers, in both the active and the persistent store. The persistent store is where a
   leftover gateway comes back from after a replug or reboot.
2. **Apply the choice.** For a static preset: address, prefix, gateway, then DNS. For
   DHCP: enable DHCP for address and DNS, then renew the lease **on that adapter only**.
3. **Read the adapter back** and show what Windows holds, not what was asked for. If
   anything is left over or was refused (a duplicate address, a leftover gateway, DNS that
   didn't take), the card lists it as a failure.

The work is done with PowerShell's NetTCPIP cmdlets, generated per switch and run with
`-EncodedCommand`. Preset values are validated again before they go into a script, since
the presets file can be edited by hand.

### Unplugged adapters

Windows ignores every API's request to turn DHCP off on an unplugged adapter:

- `Set-NetIPInterface -Dhcp Disabled` reports success and changes nothing.
- `New-NetIPAddress` writes the live address and then fails with *Inconsistent parameters
  PolicyStore PersistentStore and Dhcp Enabled*.
- `-PolicyStore PersistentStore` on its own is refused.
- `netsh interface ipv4 set address … source=static` saves the address but leaves DHCP on.

What works is `netsh` plus writing `EnableDHCP=0` under
`HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{guid}`. That takes
effect at once, and on plug-in the adapter comes up with the static address only. So for an
unplugged adapter, ip-switcher clears it, runs `netsh` and writes the registry flag. DHCP on
an unplugged adapter writes `EnableDHCP=1`. The read-back reports that saved flag.

## Build and test

Needs the .NET 10 SDK on Windows. From the repository root:

```
dotnet build src\IpSwitcher.App
dotnet run --project src\IpSwitcher.Tests                  # Core checks, PASS/FAIL per line; exit code 0 when all pass
dotnet run --project src\IpSwitcher.Tests -- --adapters    # also lists how this machine's adapters read

dotnet publish src\IpSwitcher.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

The tests change nothing on the machine. They parse every generated script with
PowerShell's own parser and run only the read-back.

To check a UI change, render both themes with `--snapshot` and look at them. Note that a
snapshot shows your real adapters and addresses.

If you publish over a copy the logon task runs, quit the app first, then
`schtasks /Run /TN "IP Switcher"` restarts it elevated with no prompt. The task runs
whichever exe installed it (`schtasks /Query /TN "IP Switcher" /XML` shows which).

## Source layout

| Path                    | What it holds                                                                                                                                                 |
| ----------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `src/IpSwitcher.Core/`  | the logic, no UI: address and mask parsing, presets and their validation, the presets file, reading adapters, the generated PowerShell, the read-back verdict |
| `src/IpSwitcher.App/`   | the WPF app: startup and single instance, elevation and the logon task, the tray icon, the flyout and the preset editor                                       |
| `src/IpSwitcher.Tests/` | the Core checks                                                                                                                                               |

## Related tools

- [pinger](https://github.com/JaimeMaertz/pinger): sweep the subnet you just switched to
- [net-diagram](https://github.com/JaimeMaertz/net-diagram): draws how switches and devices connect
- [ip-table-database-editor](https://github.com/JaimeMaertz/ip-table-database-editor): builds pinger's device database from IP tables

## License

Source-available: an MIT-based license with no-resale and no-bundling conditions (see
[LICENSE](LICENSE)), not an OSI open-source license. You're free to use it and modify it,
including at work and on paid jobs (installing it on a client's computers included), to share
it free of charge on its own, and to charge for installing, training on or supporting it. Keep
the copyright notice and the license with any copy.

What needs permission is selling it, selling a product or hosted service built on it, or
bundling it with a commercial product: shipping it in or alongside software, hardware or a
service that is sold, licensed or rented, even if this tool is included free. For that,
contact JaimeMaertz@gmail.com.

Contributions are welcome: see [CONTRIBUTING.md](CONTRIBUTING.md).
