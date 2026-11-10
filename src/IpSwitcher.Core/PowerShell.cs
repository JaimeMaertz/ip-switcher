using System.Diagnostics;
using System.Text;

namespace IpSwitcher.Core;

/// <summary>Runs an <see cref="ApplyScript"/> in Windows PowerShell and parses its JSON line.</summary>
public static class PowerShell
{
    /// <summary>Returns the adapter state the script printed, or null and why not.</summary>
    public static (AdapterState? State, string Error) Run(string script, TimeSpan timeout)
    {
        // -EncodedCommand takes the script as UTF-16 base64: no quoting of the script text at all.
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(script))}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeout))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return (null, $"PowerShell didn't finish within {timeout.TotalSeconds:0} s.");
        }
        var line = stdout.Result.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));
        if (line is null)
        {
            var err = stderr.Result.Trim();
            return (null, err.Length > 0 ? err : $"PowerShell printed no result (exit {p.ExitCode}).");
        }
        try { return (AdapterState.Parse(line), ""); }
        catch (System.Text.Json.JsonException e) { return (null, $"PowerShell's result didn't parse: {e.Message}"); }
    }
}
