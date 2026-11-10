using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using IpSwitcher.Core;

namespace IpSwitcher;

/// <summary>One adapter's card in the flyout.</summary>
public sealed class AdapterCard
{
    public required AdapterInfo Info { get; init; }
    public string Name => Info.Name;
    public string Tooltip => $"{Info.Description}\n{Info.Mac}";
    public required string StatusText { get; init; }
    public required Brush StatusBrush { get; init; }
    public required string AddressText { get; init; }
    public required string ModeText { get; init; }
    public required Brush ModeBrush { get; init; }
    public required List<Choice> Choices { get; init; }
    public string Message { get; init; } = "";
    public Brush? MessageBrush { get; init; }
    public Visibility MessageVisibility => Message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>The line under a card's buttons: what the last apply did, or that one is running.</summary>
sealed record CardMessage(string Text, bool IsError);

/// <summary>A button on a card: DHCP, one of the adapter's presets, or the + that adds one.</summary>
public sealed class Choice
{
    public required AdapterCard Card { get; set; }
    public Preset? Preset { get; init; }
    public bool IsAdd { get; init; }
    public bool IsDhcp => Preset is null && !IsAdd;
    public bool IsActive { get; init; }
    /// <summary>False while an apply is running on this adapter.</summary>
    public bool IsEnabled { get; init; } = true;
    public string Label => IsAdd ? "" : Preset?.Name ?? "DHCP";
    public string Summary => IsAdd ? "New preset for this adapter"
        : Preset?.Summary ?? "Get an address from the network's DHCP server, then renew this adapter's lease";
}

/// <summary>
/// The window the tray icon opens: every physical adapter, its address and mode, and its
/// choices. Sits above the notification area and hides when it loses focus.
/// </summary>
public partial class FlyoutWindow : Window
{
    readonly PresetStore _store;
    readonly string? _path;
    readonly Dictionary<string, CardMessage> _messages = [];
    readonly HashSet<string> _busy = [];
    readonly DispatcherTimer _debounce;
    DateTime _hiddenAt;
    bool _dialogOpen;

    /// <summary>The tray icon's hover text after each refresh.</summary>
    public event Action<string>? StatusChanged;
    public event Action? QuitRequested;
    /// <summary>A change was chosen but this process isn't elevated.</summary>
    public event Action? ElevationNeeded;

    /// <param name="path">Where presets are saved; null never saves (the snapshot).</param>
    public FlyoutWindow(PresetStore store, string? path)
    {
        InitializeComponent();
        _store = store;
        _path = path;

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Refresh(); };
        // Windows raises these on a worker thread, several times per change.
        NetworkChange.NetworkAddressChanged += (_, _) => Dispatcher.BeginInvoke(RestartDebounce);
        NetworkChange.NetworkAvailabilityChanged += (_, _) => Dispatcher.BeginInvoke(RestartDebounce);

        Deactivated += (_, _) =>
        {
            if (_dialogOpen) return;
            Hide();
            _hiddenAt = DateTime.UtcNow;
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
        SourceInitialized += (_, _) => RoundCorners();
        SizeChanged += (_, _) => PlaceAtTray();
    }

    void RestartDebounce() { _debounce.Stop(); _debounce.Start(); }

    public void Toggle()
    {
        if (IsVisible) { Hide(); return; }
        // Clicking the tray icon while open first takes focus (hiding it), then clicks; don't reopen.
        if ((DateTime.UtcNow - _hiddenAt).TotalMilliseconds < 400) return;
        ShowAtTray();
    }

    public void ShowAtTray()
    {
        Refresh();
        Show();
        PlaceAtTray();
        Activate();
    }

    /// <summary>Bottom-right of the primary work area, which is above a bottom taskbar.</summary>
    void PlaceAtTray()
    {
        var area = SystemParameters.WorkArea;
        Scroller.MaxHeight = Math.Max(200, area.Height - 140);
        Left = area.Right - ActualWidth - 12;
        Top = area.Bottom - ActualHeight - 12;
    }

    public void Refresh()
    {
        var adapters = AdapterReader.Read();
        var cards = adapters.Select(BuildCard).ToList();
        AdapterList.ItemsSource = cards;
        EmptyText.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        WarningText.Text = _store.LoadWarning ?? "";
        WarningBar.Visibility = _store.LoadWarning is null ? Visibility.Collapsed : Visibility.Visible;

        StatusChanged?.Invoke(cards.Count == 0 ? "IP Switcher" :
            string.Join("\n", cards.Select(c => $"{c.Name}: {(c.Info.IsUp ? c.ModeText : "not connected")}")));
    }

    AdapterCard BuildCard(AdapterInfo info)
    {
        var presets = _store.For(info.Mac);
        var status = AdapterStatus.Describe(info, presets);
        var choices = new List<Choice>();
        var busy = _busy.Contains(info.Mac);
        var message = _messages.GetValueOrDefault(info.Mac);
        var card = new AdapterCard
        {
            Info = info,
            StatusText = info.IsUp ? "Connected" : "Not connected",
            StatusBrush = info.IsUp ? Theme("SystemFillColorSuccessBrush", Brushes.SeaGreen) : Theme("TextFillColorTertiaryBrush", Brushes.Gray),
            AddressText = status.AddressText,
            ModeText = status.ModeText,
            ModeBrush = status.Warning ? Theme("SystemFillColorCautionBrush", Brushes.DarkOrange) : Theme("TextFillColorSecondaryBrush", Brushes.Gray),
            Choices = choices,
            Message = message?.Text ?? "",
            MessageBrush = message is { IsError: true } ? Theme("SystemFillColorCriticalBrush", Brushes.Firebrick) : Theme("TextFillColorSecondaryBrush", Brushes.Gray),
        };
        choices.Add(new Choice { Card = card, IsActive = info.DhcpEnabled, IsEnabled = !busy });
        choices.AddRange(presets.Select(p => new Choice { Card = card, Preset = p, IsActive = ReferenceEquals(p, status.ActivePreset), IsEnabled = !busy }));
        choices.Add(new Choice { Card = card, IsAdd = true });
        return card;
    }

    static Brush Theme(string key, Brush fallback) => Application.Current.TryFindResource(key) as Brush ?? fallback;

    // --- Choices ---

    async void Choice_Click(object sender, RoutedEventArgs e)
    {
        var choice = (Choice)((FrameworkElement)sender).DataContext;
        var info = choice.Card.Info;
        if (choice.IsAdd) { EditPreset(info, null); return; }
        if (_busy.Contains(info.Mac)) return;
        if (!Elevation.IsElevated)
        {
            _dialogOpen = true;
            try { ElevationNeeded?.Invoke(); }
            finally { _dialogOpen = false; }
            return;
        }

        _busy.Add(info.Mac);
        _messages[info.Mac] = new CardMessage(choice.IsDhcp ? "Switching to DHCP and renewing the lease…" : $"Applying {choice.Label}…", false);
        Refresh();
        var outcome = await System.Threading.Tasks.Task.Run(() => Apply(info, choice.Preset));
        _busy.Remove(info.Mac);
        _messages[info.Mac] = new CardMessage(
            string.Join("\n", outcome.Problems.Prepend(outcome.Summary)), !outcome.Ok);
        Refresh();
    }

    /// <summary>Clean slate, apply, read back. Runs off the UI thread; PowerShell takes a few seconds.</summary>
    static ApplyOutcome Apply(AdapterInfo adapter, Preset? preset)
    {
        string script;
        try
        {
            script = preset is null
                ? ApplyScript.Dhcp(adapter.InterfaceIndex, adapter.IsUp)
                : ApplyScript.Static(adapter.InterfaceIndex, preset, adapter.IsUp);
        }
        catch (ArgumentException e) { return new ApplyOutcome(false, "This preset can't be applied.", [e.Message]); }

        var (state, error) = PowerShell.Run(script, TimeSpan.FromSeconds(60));
        if (state is null) return new ApplyOutcome(false, $"{preset?.Name ?? "DHCP"} failed.", [error]);
        return preset is null ? ApplyOutcome.CheckDhcp(state, adapter.IsUp) : ApplyOutcome.CheckStatic(preset, state, adapter.IsUp);
    }

    void Choice_RightClick(object sender, MouseButtonEventArgs e)
    {
        var element = (FrameworkElement)sender;
        var choice = (Choice)element.DataContext;
        if (choice.Preset is not { } preset) return;
        var menu = Menu(element);
        menu.Items.Add(Item("Edit…", () => EditPreset(choice.Card.Info, preset)));
        menu.Items.Add(Item("Delete", () => DeletePreset(choice.Card.Info, preset)));
        menu.IsOpen = true;
        e.Handled = true;
    }

    void AdapterMenu_Click(object sender, RoutedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        var card = (AdapterCard)element.DataContext;
        var menu = Menu(element);
        menu.Items.Add(Item("New preset…", () => EditPreset(card.Info, null)));
        var current = Item("Save current settings as preset…", () => SaveCurrent(card.Info));
        current.IsEnabled = card.Info.Addresses.Any(a => !Ipv4.IsLinkLocal(a.Address));
        menu.Items.Add(current);
        menu.Items.Add(new Separator());
        // A routable address if there is one, else the 169.254 one.
        var address = card.Info.Addresses.OrderBy(a => Ipv4.IsLinkLocal(a.Address)).Cast<Ipv4Address?>().FirstOrDefault();
        var copyIp = Item(address is { } a1 ? $"Copy IP address ({a1.Address})" : "Copy IP address",
            () => Clipboard.SetText(address!.Value.Address.ToString()));
        var copySubnet = Item(address is { } a2 ? $"Copy subnet ({Ipv4.FirstHostCidr(a2.Address, a2.Prefix)})" : "Copy subnet",
            () => Clipboard.SetText(Ipv4.FirstHostCidr(address!.Value.Address, address.Value.Prefix)));
        copyIp.IsEnabled = copySubnet.IsEnabled = address is not null;
        menu.Items.Add(copyIp);
        menu.Items.Add(copySubnet);
        menu.Items.Add(Item("Copy MAC address", () => Clipboard.SetText(card.Info.Mac)));
        menu.IsOpen = true;
    }

    /// <summary>
    /// A context menu inherits its font from the element it opens from; the ⋯ button's is the icon
    /// font, which draws letters as icon glyphs. Menus take the window's font instead.
    /// </summary>
    ContextMenu Menu(FrameworkElement target) =>
        new() { PlacementTarget = target, FontFamily = FontFamily, FontSize = FontSize };

    static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    // --- Presets ---

    void EditPreset(AdapterInfo adapter, Preset? existing, Preset? prefill = null)
    {
        var others = _store.For(adapter.Mac).Where(p => !ReferenceEquals(p, existing)).Select(p => p.Name).ToList();
        var editor = new PresetEditorWindow(adapter.Name, existing ?? prefill, isNew: existing is null, others);
        if (RunDialog(editor) && editor.Result is { } result)
        {
            _store.Put(adapter.Mac, adapter.Name, result, existing?.Name);
            Save();
        }
    }

    void SaveCurrent(AdapterInfo adapter)
    {
        var address = adapter.Addresses.First(a => !Ipv4.IsLinkLocal(a.Address));
        EditPreset(adapter, null, new Preset
        {
            Address = address.Address.ToString(),
            Prefix = address.Prefix,
            Gateway = adapter.Gateways.FirstOrDefault()?.ToString(),
            Dns = adapter.Dns.Select(d => d.ToString()).ToList(),
        });
    }

    void DeletePreset(AdapterInfo adapter, Preset preset)
    {
        _dialogOpen = true;
        var answer = MessageBox.Show(this, $"Delete the preset \"{preset.Name}\" from {adapter.Name}?", "IP Switcher",
            MessageBoxButton.OKCancel, MessageBoxImage.None, MessageBoxResult.Cancel);
        _dialogOpen = false;
        if (answer != MessageBoxResult.OK) return;
        _store.Delete(adapter.Mac, preset.Name);
        Save();
    }

    /// <summary>Shows a dialog over the flyout, keeping the flyout open behind it.</summary>
    bool RunDialog(Window dialog)
    {
        _dialogOpen = true;
        dialog.Owner = this;
        try { return dialog.ShowDialog() == true; }
        finally { _dialogOpen = false; Activate(); }
    }

    void Save()
    {
        if (_path is not null) _store.Save(_path);
        Refresh();
    }

    // --- Footer ---

    void NetworkConnections_Click(object sender, RoutedEventArgs e) { Hide(); TrayIcon.OpenNetworkConnections(); }

    void Quit_Click(object sender, RoutedEventArgs e) => QuitRequested?.Invoke();

    // --- Window chrome ---

    /// <summary>Windows 11's rounded corners and border on a window with no title bar.</summary>
    void RoundCorners()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
