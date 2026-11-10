using System.Text.Json;

namespace IpSwitcher.Core;

/// <summary>
/// Every adapter's presets, keyed by MAC address ("AA-BB-CC-DD-EE-FF"), kept in one JSON file.
/// An adapter keeps its presets when Windows renames it or it moves to another USB port.
/// </summary>
public sealed class PresetStore
{
    public sealed class AdapterEntry
    {
        /// <summary>The adapter's name when it was last seen, so the file is readable by hand.</summary>
        public string LastName { get; set; } = "";
        public List<Preset> Presets { get; set; } = [];
    }

    public Dictionary<string, AdapterEntry> Adapters { get; set; } = [];

    /// <summary>Set by <see cref="Load"/> when the file could not be read and was moved aside.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? LoadWarning { get; private set; }

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ip-switcher", "presets.json");

    /// <summary>
    /// Reads the file, or starts empty when there is none. A file that does not parse is renamed
    /// to <c>presets.json.unreadable-{time}</c> rather than overwritten, so no presets are lost.
    /// </summary>
    public static PresetStore Load(string path)
    {
        if (!File.Exists(path)) return new PresetStore();
        try
        {
            var store = JsonSerializer.Deserialize<PresetStore>(File.ReadAllText(path), Json) ?? new PresetStore();
            store.Adapters = store.Adapters.ToDictionary(kv => NormalizeMac(kv.Key), kv => kv.Value);
            return store;
        }
        catch (JsonException e)
        {
            var aside = $"{path}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(path, aside);
            return new PresetStore { LoadWarning = $"The presets file couldn't be read ({e.Message}). It was kept as {Path.GetFileName(aside)}." };
        }
    }

    /// <summary>Writes to a temporary file and swaps it in, so a crash mid-write leaves the old file.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
    }

    public IReadOnlyList<Preset> For(string mac) =>
        Adapters.TryGetValue(NormalizeMac(mac), out var entry) ? entry.Presets : [];

    /// <summary>Adds <paramref name="preset"/>, or replaces the preset named <paramref name="replacing"/> in place.</summary>
    public void Put(string mac, string adapterName, Preset preset, string? replacing = null)
    {
        mac = NormalizeMac(mac);
        if (!Adapters.TryGetValue(mac, out var entry)) Adapters[mac] = entry = new AdapterEntry();
        entry.LastName = adapterName;
        var index = replacing is null ? -1 : entry.Presets.FindIndex(p => p.Name == replacing);
        if (index >= 0) entry.Presets[index] = preset;
        else entry.Presets.Add(preset);
    }

    public void Delete(string mac, string name)
    {
        if (Adapters.TryGetValue(NormalizeMac(mac), out var entry))
            entry.Presets.RemoveAll(p => p.Name == name);
    }

    public static string NormalizeMac(string mac) => mac.Trim().ToUpperInvariant().Replace(':', '-');
}
