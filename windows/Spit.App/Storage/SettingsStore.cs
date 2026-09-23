using System.IO;
using System.Text.Json;

namespace Spit.App;

/// `settings.json`, the Windows form of the Mac's UserDefaults. Unknown keys are ignored and missing
/// keys take their defaults, so a file from an older or newer build still loads (build spec §14); a
/// file that doesn't parse at all gives defaults rather than stopping Spit from starting.
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly Lock gate = new();
    private readonly string path;
    private Settings current;

    public SettingsStore(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        path = paths.SettingsFile;
        current = Load(path);
        // A first launch writes its defaults at once, so the model it starts with is pinned in the file. Without this
        // an install that never changed a setting keeps no `modelFile`, and a later build with a different
        // `ModelCatalog.DefaultFile` would switch it silently and download the new model (prd-windows-parity.md
        // rule 7: the default is for new installs only).
        if (!File.Exists(path)) Save(path, current);
    }

    /// Raised after a change, with the new settings, on the thread that made it.
    public event EventHandler<Settings>? Changed;

    public Settings Current
    {
        get { lock (gate) return current; }
    }

    /// Applies `change` and rewrites the file atomically. The change takes effect even if the write
    /// fails (logged): a setting applies immediately, as on the Mac.
    public Settings Update(Func<Settings, Settings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Settings next;
        lock (gate)
        {
            next = change(current).Normalised();
            if (next == current) return current;
            current = next;
            Save(path, next);
        }
        Changed?.Invoke(this, next);
        return next;
    }

    private static Settings Load(string path)
    {
        if (!File.Exists(path)) return Settings.Defaults;
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllBytes(path), Json)?.Normalised() ?? Settings.Defaults;
        }
        catch (JsonException e)
        {
            Log.Failure("settings", "parse settings.json", e);
            return Settings.Defaults;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Failure("settings", "read settings.json", e);
            return Settings.Defaults;
        }
    }

    private static void Save(string path, Settings settings)
    {
        try
        {
            AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(settings, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Failure("settings", "write settings.json", e);
        }
    }
}
