using System.IO;
using Velopack;

namespace Spit.App;

public static class Program
{
    public const string KeyLogFlag = "--key-log";
    public const string ClipLogFlag = "--clip-log";
    public const string ElevationLogFlag = "--elevation-log";

    [STAThread]
    public static int Main(string[] args)
    {
        // Must be the first line (rule 44): Velopack's install/uninstall hooks run here and exit.
        VelopackApp.Build().Run();

        // The spike diagnostics (docs/SPIKES.md S4, S2, S3): consoles, no UI, no single-instance lock.
        if (args.Contains(KeyLogFlag)) return KeyLog.Run();
        if (args.Contains(ClipLogFlag)) return ClipLog.Run();
        if (args.Contains(ElevationLogFlag)) return ElevationLog.Run();
        // CI only: no UI and no single-instance lock, so it runs beside an installed Spit.
        if (args.Contains(SmokeTest.Flag))
        {
            return SmokeTest.Parse(args) is { } smoke
                ? SmokeTest.Run(smoke.Wav, smoke.Report, smoke.Model, args.Contains(SmokeTest.WarmPassFlag))
                : SmokeTest.UsageExitCode;
        }
        if (args.Contains(ClipRecorder.Flag))
        {
            return ClipRecorder.Parse(args) is { } folder ? ClipRecorder.Run(folder) : SmokeTest.UsageExitCode;
        }

        using var instance = new SingleInstance();
        if (!AcquireOrSignal(instance)) return 0;

        var app = new App(instance);
        app.InitializeComponent();
        return app.Run();
    }

    /// False when another Spit is running, which has then been asked to come forward (rule 36). A failure to
    /// create the named objects is logged and treated as first: a second hook is a smaller harm than no Spit.
    private static bool AcquireOrSignal(SingleInstance instance)
    {
        try
        {
            return instance.TryAcquire();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            Log.Failure("single-instance", "acquire", e);
            return true;
        }
    }
}
