using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace IpSwitcher;

/// <summary>
/// The notification-area icon: left click opens the flyout, right click a short menu. The glyph
/// is drawn from Segoe Fluent Icons in the taskbar's own colour (white on a dark taskbar).
/// </summary>
sealed class TrayIcon : IDisposable
{
    const string EthernetGlyph = "";

    readonly NotifyIcon _icon;
    IntPtr _hIcon;

    public event Action? Open;
    public event Action? Quit;
    public event Action? ToggleLogonTask;

    public TrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => Open?.Invoke());
        menu.Items.Add("Network Connections", null, (_, _) => OpenNetworkConnections());
        menu.Items.Add(new ToolStripSeparator());
        var logon = new ToolStripMenuItem("Start elevated at logon", null, (_, _) => ToggleLogonTask?.Invoke());
        menu.Items.Add(logon);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit?.Invoke());
        menu.Opening += (_, _) => logon.Checked = Elevation.TaskExists();

        _icon = new NotifyIcon { Text = "IP Switcher", ContextMenuStrip = menu, Icon = DrawIcon() };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Open?.Invoke(); };
        _icon.Visible = true;
        SystemEvents.UserPreferenceChanged += OnThemeChanged;
    }

    /// <summary>The hover text. Windows cuts it at 127 characters.</summary>
    public void SetText(string text) => _icon.Text = text.Length <= 127 ? text : text[..126] + "…";

    public static void OpenNetworkConnections() =>
        Process.Start(new ProcessStartInfo("ncpa.cpl") { UseShellExecute = true });

    void OnThemeChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        var old = _hIcon;
        _icon.Icon = DrawIcon();
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    Icon DrawIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using var font = new Font("Segoe Fluent Icons", 24, GraphicsUnit.Pixel);
            using var fallback = new Font("Segoe MDL2 Assets", 24, GraphicsUnit.Pixel);
            var use = font.Name == "Segoe Fluent Icons" ? font : fallback;
            using var brush = new SolidBrush(TaskbarIsLight() ? Color.Black : Color.White);
            var size = g.MeasureString(EthernetGlyph, use, PointF.Empty, StringFormat.GenericTypographic);
            g.DrawString(EthernetGlyph, use, brush, (32 - size.Width) / 2, (32 - size.Height) / 2, StringFormat.GenericTypographic);
        }
        _hIcon = bitmap.GetHicon();
        return Icon.FromHandle(_hIcon);
    }

    static bool TaskbarIsLight() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "SystemUsesLightTheme", 0) is 1;

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnThemeChanged;
        _icon.Visible = false;
        _icon.Dispose();
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);
}
