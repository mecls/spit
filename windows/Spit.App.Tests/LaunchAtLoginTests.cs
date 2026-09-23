using Microsoft.Win32;

namespace Spit.App.Tests;

/// Rule 44: the Run value round-trips under `SpitTest-<guid>`, never the real `Spit` value.
public sealed class LaunchAtLoginTests
{
    [WindowsFact]
    public void EnableReadDisable_UnderATestValueName()
    {
        var name = "SpitTest-" + Guid.NewGuid().ToString("N");
        const string exe = @"C:\Users\friend\AppData\Local\Spit\Spit.exe";
        var login = new LaunchAtLogin(name, exe);
        try
        {
            Assert.False(login.IsEnabled());

            login.SetEnabled(true);
            Assert.True(login.IsEnabled());
            using (var key = Registry.CurrentUser.OpenSubKey(LaunchAtLogin.RunKeyPath))
            {
                Assert.NotNull(key);
                Assert.Equal($"\"{exe}\"", key.GetValue(name));
            }

            login.SetEnabled(false);
            Assert.False(login.IsEnabled());
            login.SetEnabled(false);   // disabling twice is not an error
        }
        finally
        {
            using var key = Registry.CurrentUser.OpenSubKey(LaunchAtLogin.RunKeyPath, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    /// Checklist item 15: uninstalling removes the startup entry it would otherwise leave pointing at a deleted file —
    /// but only one that launches this install.
    [WindowsFact]
    public void Uninstall_RemovesOnlyAValueThatLaunchesThisInstall()
    {
        var ours = "SpitTest-" + Guid.NewGuid().ToString("N");
        var theirs = "SpitTest-" + Guid.NewGuid().ToString("N");
        const string exe = @"C:\Users\friend\AppData\Local\Spit\Spit.exe";
        try
        {
            new LaunchAtLogin(ours, exe).SetEnabled(true);
            new LaunchAtLogin(theirs, @"C:\src\spit\windows\Spit.App\bin\Release\Spit.exe").SetEnabled(true);

            new LaunchAtLogin(ours, exe).RemoveIfItLaunchesThisInstall();
            new LaunchAtLogin(theirs, exe).RemoveIfItLaunchesThisInstall();

            Assert.False(new LaunchAtLogin(ours, exe).IsEnabled());
            Assert.True(new LaunchAtLogin(theirs, exe).IsEnabled());
            new LaunchAtLogin(ours, exe).RemoveIfItLaunchesThisInstall();   // nothing there is not an error
        }
        finally
        {
            using var key = Registry.CurrentUser.OpenSubKey(LaunchAtLogin.RunKeyPath, writable: true);
            key?.DeleteValue(ours, throwOnMissingValue: false);
            key?.DeleteValue(theirs, throwOnMissingValue: false);
        }
    }

    [WindowsFact]
    public void InstalledByVelopack_PointsAtTheStubThatSurvivesUpdates()
    {
        Assert.Equal(
            @"C:\Users\friend\AppData\Local\Spit\Spit.exe",
            LaunchAtLogin.ResolveExecutablePath(@"C:\Users\friend\AppData\Local\Spit\current\Spit.exe"));
    }

    [WindowsFact]
    public void NotInstalled_PointsAtTheRunningExe()
    {
        const string exe = @"C:\src\spit\windows\Spit.App\bin\Release\Spit.exe";

        Assert.Equal(exe, LaunchAtLogin.ResolveExecutablePath(exe));
    }
}
