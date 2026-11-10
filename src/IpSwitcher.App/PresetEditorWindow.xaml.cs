using System.Collections.Generic;
using System.Windows;
using IpSwitcher.Core;

namespace IpSwitcher;

/// <summary>Creates or edits one adapter's preset. Every problem with the input is listed at once.</summary>
public partial class PresetEditorWindow : Window
{
    readonly IReadOnlyList<string> _otherNames;

    /// <summary>The validated preset after Save.</summary>
    public Preset? Result { get; private set; }

    /// <param name="start">The preset to edit, or values to start a new one from; null starts blank.</param>
    /// <param name="otherNames">The adapter's other preset names, which this one may not reuse.</param>
    public PresetEditorWindow(string adapterName, Preset? start, bool isNew, IReadOnlyList<string> otherNames)
    {
        InitializeComponent();
        _otherNames = otherNames;
        Title = isNew ? "New preset" : "Edit preset";
        Heading.Text = Title;
        AdapterText.Text = $"For {adapterName}";
        SaveButton.Style = TryFindResource("AccentButtonStyle") as Style ?? SaveButton.Style;

        if (start is not null)
        {
            NameBox.Text = start.Name;
            AddressBox.Text = start.Address;
            MaskBox.Text = start.Prefix > 0 ? Ipv4.MaskText(start.Prefix) : MaskBox.Text;
            GatewayBox.Text = start.Gateway ?? "";
            DnsBox.Text = string.Join(", ", start.Dns);
        }
        Loaded += (_, _) => (string.IsNullOrEmpty(NameBox.Text) ? NameBox : AddressBox).Focus();
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (ShowValidation() is not { } preset) return;
        Result = preset;
        DialogResult = true;
    }

    /// <summary>Validates the fields, shows any problems, and returns the preset when there are none.</summary>
    public Preset? ShowValidation()
    {
        var errors = PresetValidator.Validate(NameBox.Text, AddressBox.Text, MaskBox.Text, GatewayBox.Text, DnsBox.Text,
            _otherNames, out var preset);
        ErrorText.Text = string.Join("\n", errors);
        ErrorBar.Visibility = errors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        return preset;
    }
}
