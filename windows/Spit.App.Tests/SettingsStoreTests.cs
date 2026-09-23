using System.IO;
using System.Text.Json;
using Spit.Core;

namespace Spit.App.Tests;

/// Build spec §7 and §14: atomic writes, unknown keys ignored, missing keys defaulted, a corrupt file
/// never stops Spit from starting.
public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempDirectory temp = new();

    public void Dispose() => temp.Dispose();

    [WindowsFact]
    public void MissingFile_GivesTheMacDefaultsWithWindowsValues()
    {
        var settings = new SettingsStore(new AppPaths(temp.Path)).Current;

        Assert.Equal("rightCtrl", settings.Hotkey);
        Assert.True(settings.Sounds);
        Assert.True(settings.ShowTextInHUD);
        Assert.Equal("ggml-small-q8_0.bin", settings.ModelFile);
        Assert.Equal("https://voice.miraside.co", settings.ServerURL);
        Assert.Equal("clean", settings.Mode);
        Assert.Equal("auto", settings.Language);
        Assert.False(settings.Onboarded);
        Assert.True(settings.ShowBar);
        Assert.False(settings.LiveTranscription);
    }

    /// Rule 7 (prd-windows-parity.md): a changed default reaches new installs only. An install that never changed a
    /// setting must still have its model in the file, or the next build's default would quietly replace it.
    [WindowsFact]
    public void AFirstLaunch_PinsTheModelItStartsWith()
    {
        var paths = new AppPaths(temp.Path);
        _ = new SettingsStore(paths);

        using var json = JsonDocument.Parse(File.ReadAllBytes(paths.SettingsFile));
        Assert.Equal(ModelCatalog.DefaultFile, json.RootElement.GetProperty("modelFile").GetString());
    }

    [WindowsFact]
    public void Update_WritesAtomicallyWithTheMacKeyNamesAndRaisesChanged()
    {
        var paths = new AppPaths(temp.Path);
        var store = new SettingsStore(paths);
        var raised = new List<Settings>();
        store.Changed += (_, settings) => raised.Add(settings);

        store.Update(s => s with { Hotkey = Settings.HotkeyRightAlt, Sounds = false });
        store.Update(s => s);

        Assert.Single(raised);
        Assert.False(File.Exists(paths.SettingsFile + ".tmp"));
        var reloaded = new SettingsStore(paths).Current;
        Assert.Equal(Settings.HotkeyRightAlt, reloaded.Hotkey);
        Assert.False(reloaded.Sounds);
        using var json = JsonDocument.Parse(File.ReadAllBytes(paths.SettingsFile));
        Assert.Equal("rightAlt", json.RootElement.GetProperty("hotkey").GetString());
        Assert.True(json.RootElement.GetProperty("showTextInHUD").GetBoolean());
    }

    [WindowsFact]
    public void CorruptFile_FallsBackToDefaultsAndIsReplacedOnTheNextWrite()
    {
        var paths = new AppPaths(temp.Path);
        File.WriteAllText(paths.SettingsFile, "{ this is not json");

        var store = new SettingsStore(paths);
        Assert.Equal(Settings.Defaults, store.Current);

        store.Update(s => s with { Onboarded = true });
        Assert.True(new SettingsStore(paths).Current.Onboarded);
    }

    [WindowsFact]
    public void UnknownKeysAreIgnoredAndMissingKeysDefaulted()
    {
        var paths = new AppPaths(temp.Path);
        File.WriteAllText(paths.SettingsFile, """{ "hotkey": "rightAlt", "fromANewerBuild": 42, "mode": null }""");

        var settings = new SettingsStore(paths).Current;

        Assert.Equal("rightAlt", settings.Hotkey);
        Assert.True(settings.Sounds);
        Assert.Equal("clean", settings.Mode);
    }

    [WindowsFact]
    public void AHotkeyThisBuildDoesNotKnow_ReadsAsRightCtrl()
    {
        var paths = new AppPaths(temp.Path);
        File.WriteAllText(paths.SettingsFile, """{ "hotkey": "fn" }""");

        Assert.Equal(Settings.HotkeyRightCtrl, new SettingsStore(paths).Current.Hotkey);
    }
}
