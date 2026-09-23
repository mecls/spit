using System.Globalization;

namespace Spit.App;

/// <summary>
/// `Spit.exe --elevation-log`: the console diagnostic for spike S3. Watches the foreground window and, each time a
/// different process comes to the front, prints what `ElevationProbe` reads from it and the paste route that answer
/// would pick — so rule 30 is checked against a real elevated Notepad from a non-elevated Spit, which CI (always
/// elevated) cannot do. Process names and integrity levels only; window titles are never read (rule 26).
/// </summary>
public static class ElevationLog
{
    private const string Category = "elevation-log";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public static int Run()
    {
        var (output, _) = DiagnosticConsole.Open(Category);
        var own = Environment.ProcessId;
        output.WriteLine("Spit elevation log (spike S3). Close this window to quit.");
        output.WriteLine($"Spit itself: integrity {Level(ElevationProbe.IntegrityOf(own, out _), false)}, elevated {ElevationProbe.IsCurrentProcessElevated()}. S3 needs Spit NOT elevated.");
        output.WriteLine("Bring Notepad run as administrator to the front, then a normal Notepad. Each new foreground process prints once.");
        output.WriteLine();

        int? last = null;
        while (true)
        {
            var processId = ForegroundContext.ForegroundProcessId();
            if (processId is { } id && id != last)
            {
                var exe = ForegroundContext.ForProcess(id)?.BundleId ?? "(unreadable)";
                var integrity = ElevationProbe.IntegrityOf(id, out var accessDenied);
                var elevated = ElevationProbe.IsElevated(id);
                var blocks = ElevationProbe.BlocksInputFromSpit(id);
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{DateTime.Now:HH:mm:ss} {exe} (pid {id}): integrity {Level(integrity, accessDenied)}, IsElevated {elevated}, BlocksInputFromSpit {blocks} -> route {TextInjector.Route(exe, blocks)}"));
            }
            last = processId;
            Thread.Sleep(PollInterval);
        }
    }

    private static string Level(int? rid, bool accessDenied) => rid switch
    {
        null => accessDenied ? "unreadable (access denied)" : "unreadable",
        < 0x2000 => $"Low (0x{rid:X4})",
        < 0x3000 => $"Medium (0x{rid:X4})",
        < 0x4000 => $"High (0x{rid:X4})",
        _ => $"System (0x{rid:X4})",
    };
}
