using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;
using IpSwitcher.Core;

namespace IpSwitcher;

/// <summary>
/// Administrator rights. Changing an adapter needs them, so the app runs from a logon task with
/// highest privileges (<see cref="LogonTask"/>): one UAC prompt when the task is set up, none after.
/// </summary>
static class Elevation
{
    public const string MutexName = @"Local\ip-switcher";

    public static bool IsElevated =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static string User => WindowsIdentity.GetCurrent().Name;

    public static bool TaskExists() => Schtasks($"/Query /TN \"{LogonTask.Name}\"").Exit == 0;

    /// <summary>Starts this exe again with UAC's prompt. False when the prompt was declined.</summary>
    public static bool RunElevated(string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arguments) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            return false;
        }
    }

    /// <summary>Starts this exe again as an ordinary process (used to hand off after quitting).</summary>
    public static void RunPlain(string arguments) =>
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arguments) { UseShellExecute = false });

    /// <summary>Creates (or replaces) the logon task for <paramref name="user"/>. Needs administrator rights.</summary>
    public static (bool Ok, string Output) Install(string user)
    {
        var xmlPath = Path.Combine(Path.GetTempPath(), "ip-switcher-task.xml");
        File.WriteAllText(xmlPath, LogonTask.Xml(Environment.ProcessPath!, user), Encoding.Unicode);
        try
        {
            var (exit, output) = Schtasks($"/Create /TN \"{LogonTask.Name}\" /XML \"{xmlPath}\" /F");
            return (exit == 0, output);
        }
        finally { File.Delete(xmlPath); }
    }

    public static (bool Ok, string Output) Uninstall()
    {
        var (exit, output) = Schtasks($"/Delete /TN \"{LogonTask.Name}\" /F");
        return (exit == 0, output);
    }

    /// <summary>Runs the logon task now, which starts the app elevated without a prompt.</summary>
    public static bool StartTask() => Schtasks($"/Run /TN \"{LogonTask.Name}\"").Exit == 0;

    /// <summary>Waits for a running instance to quit, so the next one can take over.</summary>
    public static bool WaitForOtherInstanceToExit(TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            try { using var m = Mutex.OpenExisting(MutexName); }
            catch (WaitHandleCannotBeOpenedException) { return true; }
            catch (UnauthorizedAccessException) { }   // held by an elevated instance
            Thread.Sleep(200);
        }
        return false;
    }

    static (int Exit, string Output) Schtasks(string arguments)
    {
        var psi = new ProcessStartInfo("schtasks.exe", arguments)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output.Trim());
    }
}
