using System.IO;
using System.Security;
using Microsoft.Win32;

namespace Spit.App;

/// Launch at login through `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. The value points at
/// Velopack's stub `%LocalAppData%\Spit\Spit.exe` rather than the running exe: Velopack runs the app
/// from `current\`, which an update replaces, while the stub stays put and starts whichever version is
/// installed (rule 44).
public sealed class LaunchAtLogin
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultValueName = "Spit";

    /// `valueName` is overridable so tests write `SpitTest-<guid>`, never the real entry.
    public LaunchAtLogin(string valueName = DefaultValueName, string? executablePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);
        ValueName = valueName;
        ExecutablePath = executablePath ?? ResolveExecutablePath(
            Environment.ProcessPath ?? throw new InvalidOperationException("The running executable has no path."));
    }

    public string ValueName { get; }

    /// What the Run value launches.
    public string ExecutablePath { get; }

    /// Velopack's layout is `<root>\current\Spit.exe` with the stub at `<root>\Spit.exe`. Anything else
    /// (a dev build, the smoke test) launches the exe that is running.
    public static string ResolveExecutablePath(string runningExe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runningExe);
        var directory = Path.GetDirectoryName(runningExe);
        if (directory is not null && string.Equals(Path.GetFileName(directory), "current", StringComparison.OrdinalIgnoreCase))
        {
            var root = Path.GetDirectoryName(directory);
            if (root is not null) return Path.Combine(root, Path.GetFileName(runningExe));
        }
        return runningExe;
    }

    /// Reads the registry itself, so the toggle shows the real state rather than what Spit last wrote.
    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            Log.Failure("launch-at-login", "read Run key", e);
            return false;
        }
    }

    /// Throws `InvalidOperationException` (the OS message, the original as inner) when the write fails, so
    /// the toggle can revert rather than claim a state it failed to reach (Mac GeneralTab).
    public void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new IOException("The Run key could not be opened.");
            if (enabled)
                key.SetValue(ValueName, $"\"{ExecutablePath}\"", RegistryValueKind.String);
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            Log.Failure("launch-at-login", enabled ? "enable" : "disable", e);
            throw new InvalidOperationException(e.Message, e);
        }
    }

    /// Velopack's uninstall hook (`Program.Main`). Velopack removes the program but not the Run value Settings
    /// wrote, and left behind, Startup apps lists a Spit whose file is gone (checklist item 15). Only a value that
    /// launches this install is removed; one pointing anywhere else — a dev build's — is not this uninstall's to
    /// touch. Never throws: an uninstall must not fail over a startup entry.
    public void RemoveIfItLaunchesThisInstall()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) is not string value) return;
            if (!string.Equals(value.Trim().Trim('"'), ExecutablePath, StringComparison.OrdinalIgnoreCase)) return;
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            Log.Failure("launch-at-login", "remove on uninstall", e);
        }
    }
}
