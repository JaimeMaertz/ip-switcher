using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IpSwitcher.Core;

namespace IpSwitcher;

/// <summary>
/// Starts the tray icon and owns the flyout. One instance per user session.
///
/// <c>ip-switcher.exe --snapshot out.png [--theme Light|Dark]</c> renders the flyout with this
/// machine's adapters to a PNG and exits, without a tray icon or a visible window.
/// <c>--snapshot-editor out.png</c> does the same for the preset editor, showing a validation error.
/// <c>--presets FILE</c> uses another presets file than the one in %APPDATA%.
/// <c>--install [USER]</c> creates the elevated logon task (asking UAC) and starts the app through
/// it; <c>--uninstall</c> removes the task; <c>--start-task</c> waits for a quitting instance, then
/// runs the task.
/// </summary>
public partial class App : Application
{
    Mutex? _single;
    TrayIcon? _tray;
    FlyoutWindow? _flyout;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        var theme = Arg(args, "--theme");
        if (theme is not null) ThemeMode = new ThemeMode(theme);

        var presetsPath = Arg(args, "--presets") is { } p ? Path.GetFullPath(p) : PresetStore.DefaultPath;

        var snapshot = Arg(args, "--snapshot");
        if (snapshot is not null)
        {
            var flyout = new FlyoutWindow(PresetStore.Load(presetsPath), path: null);
            flyout.Refresh();
            Snapshot(flyout, Path.GetFullPath(snapshot));
            return;
        }
        var editorSnapshot = Arg(args, "--snapshot-editor");
        if (editorSnapshot is not null)
        {
            var editor = new PresetEditorWindow("Ethernet",
                new Preset { Name = "Crestron 1.x", Address = "192.168.1.50", Prefix = 24, Gateway = "192.168.2.1" },
                isNew: false, otherNames: []);
            editor.ShowValidation();
            Snapshot(editor, Path.GetFullPath(editorSnapshot));
            return;
        }

        if (args.Contains("--install")) { Install(Arg(args, "--install") ?? Elevation.User); return; }
        if (args.Contains("--uninstall")) { Uninstall(); return; }
        if (args.Contains("--start-task"))
        {
            // Handed off by an instance that is quitting: wait for it, then start elevated through the task.
            Elevation.WaitForOtherInstanceToExit(TimeSpan.FromSeconds(10));
            if (!Elevation.StartTask()) MessageBox.Show("The logon task didn't start. Run ip-switcher.exe --install again.", "IP Switcher");
            Shutdown();
            return;
        }

        var first = false;
        try { _single = new Mutex(true, Elevation.MutexName, out first); }
        catch (UnauthorizedAccessException) { }   // an elevated instance holds it
        if (!first)
        {
            MessageBox.Show("IP Switcher is already running. Its icon is in the taskbar's notification area.", "IP Switcher");
            Shutdown();
            return;
        }

        _flyout = new FlyoutWindow(PresetStore.Load(presetsPath), presetsPath);
        _tray = new TrayIcon();
        _tray.Open += () => _flyout.Toggle();
        _tray.Quit += Quit;
        _tray.ToggleLogonTask += ToggleLogonTask;
        _flyout.StatusChanged += text => _tray.SetText(text);
        _flyout.QuitRequested += Quit;
        _flyout.ElevationNeeded += OfferElevation;
        _flyout.Refresh();
    }

    void Quit()
    {
        _tray?.Dispose();
        _single?.ReleaseMutex();
        Shutdown();
    }

    /// <summary>Asked for when an adapter change is chosen without administrator rights.</summary>
    void OfferElevation()
    {
        if (Elevation.TaskExists())
        {
            var restart = MessageBox.Show(
                "Changing an adapter needs administrator rights. IP Switcher is set to start elevated at logon, but this copy was started by hand.\n\nRestart it elevated now?",
                "IP Switcher", MessageBoxButton.OKCancel, MessageBoxImage.None, MessageBoxResult.OK);
            if (restart != MessageBoxResult.OK) return;
            Elevation.RunPlain("--start-task");
            Quit();
            return;
        }

        var setup = MessageBox.Show(
            "Changing an adapter needs administrator rights.\n\nSet IP Switcher to start elevated at every logon? Windows asks once now, and never again when switching.",
            "IP Switcher", MessageBoxButton.OKCancel, MessageBoxImage.None, MessageBoxResult.OK);
        if (setup != MessageBoxResult.OK) return;
        if (Elevation.RunElevated($"--install \"{Elevation.User}\"")) Quit();
    }

    void ToggleLogonTask()
    {
        if (Elevation.TaskExists())
        {
            if (!Elevation.RunElevated("--uninstall")) return;
        }
        else if (Elevation.RunElevated($"--install \"{Elevation.User}\""))
        {
            Quit();   // the installer starts the elevated copy once this one is gone
        }
    }

    /// <summary>Runs elevated (from the UAC prompt): creates the task, then starts the app through it.</summary>
    void Install(string user)
    {
        if (!Elevation.IsElevated)
        {
            Elevation.RunElevated($"--install \"{user}\"");
            Shutdown();
            return;
        }
        // The task runs whichever exe installed it. A build-output copy is locked while it runs and
        // replaced by every build, so the task belongs on the published one.
        if (Environment.ProcessPath!.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase))
        {
            var answer = MessageBox.Show(
                $"This is a build-output copy:\n{Environment.ProcessPath}\n\nThe logon task should run the published copy (publish\\ip-switcher.exe). Set it up to run this copy anyway?",
                "IP Switcher", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (answer != MessageBoxResult.OK) { Shutdown(); return; }
        }
        var (ok, output) = Elevation.Install(user);
        if (!ok)
        {
            MessageBox.Show($"Setting up the logon task failed:\n\n{output}", "IP Switcher");
            Shutdown();
            return;
        }
        Elevation.WaitForOtherInstanceToExit(TimeSpan.FromSeconds(10));
        if (!Elevation.StartTask()) MessageBox.Show("The logon task was created but didn't start. It will start at the next logon.", "IP Switcher");
        Shutdown();
    }

    void Uninstall()
    {
        if (!Elevation.IsElevated)
        {
            Elevation.RunElevated("--uninstall");
            Shutdown();
            return;
        }
        var (ok, output) = Elevation.Uninstall();
        MessageBox.Show(ok ? "IP Switcher no longer starts at logon. This copy keeps running until you quit it."
                           : $"Removing the logon task failed:\n\n{output}", "IP Switcher");
        Shutdown();
    }

    void Snapshot(Window window, string path)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -10000;
        window.Top = -10000;
        window.ShowActivated = false;
        window.Show();
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            var root = (FrameworkElement)window.Content;
            var dpi = VisualTreeHelper.GetDpi(window);
            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(path)) encoder.Save(file);
            Shutdown();
        });
    }

    static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
