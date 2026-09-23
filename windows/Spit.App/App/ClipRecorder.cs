using System.Globalization;
using System.IO;
using System.Windows.Threading;
using NAudio.Wave;
using Spit.Core;

namespace Spit.App;

/// <summary>
/// `Spit.exe --record-clips &lt;folder&gt;`: session 3's microphone (prd-windows-parity.md §3.3). Records numbered
/// WAVs through the same `AudioCapture` a dictation uses — default device, 16 kHz mono Float32, written exactly as
/// the transcriber would receive it — so `spike-live.ps1` can replay each one through the smoke test's live path as
/// often as tuning needs. Rule 15 compares the streamed result with a one-pass over *the same audio*, and task 5.6
/// re-runs *the same clips* after tuning; neither is possible from dictations that were only ever spoken once.
///
/// The one place Spit writes a voice to disk, and only into a folder the person running it named.
/// </summary>
public static class ClipRecorder
{
    public const string Flag = "--record-clips";
    private const string Category = "record-clips";

    /// Rule 15's range. Clips outside it are kept but flagged, and `spike-live.ps1` leaves them out of the verdict.
    public const int MinimumSeconds = 10;
    public const int MaximumSeconds = 30;

    public static string? Parse(IReadOnlyList<string> args)
    {
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] == Flag && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) return args[i + 1];
        }
        return null;
    }

    public static int Run(string folder)
    {
        var (output, input) = DiagnosticConsole.Open(Category);
        Directory.CreateDirectory(folder);
        output.WriteLine($"Spit clip recorder (session 3). Clips go to {Path.GetFullPath(folder)}");
        output.WriteLine($"Enter starts a clip, Enter stops it. Speak the way you dictate, {MinimumSeconds}-{MaximumSeconds} s each. q quits.");

        var dispatcher = Dispatcher.CurrentDispatcher;
        var context = new DispatcherSynchronizationContext(dispatcher);
        SynchronizationContext.SetSynchronizationContext(context);
        using var capture = new AudioCapture(context);
        try
        {
            capture.Prepare();
        }
        catch (Exception e) when (e is MicrophoneBlockedException or NoMicrophoneException)
        {
            output.WriteLine($"No microphone: {e.GetType().Name}. Check Settings > Privacy > Microphone, then run this again.");
            return 1;
        }

        var recording = false;
        void Toggle()
        {
            if (!recording)
            {
                try
                {
                    capture.Start();
                }
                catch (Exception e) when (e is MicrophoneBlockedException or NoMicrophoneException)
                {
                    output.WriteLine($"Could not start: {e.GetType().Name}.");
                    return;
                }
                recording = true;
                output.WriteLine("  recording... Enter to stop");
                return;
            }
            recording = false;
            var (samples, ms) = capture.Stop();
            var path = NextPath(folder);
            Write(path, samples);
            var seconds = samples.Length / (double)AudioCapture.SampleRate;
            var range = seconds is >= MinimumSeconds and <= MaximumSeconds ? "" : $"  (outside {MinimumSeconds}-{MaximumSeconds} s: kept, but not counted by rule 15)";
            var speech = EnergyGate.HasSpeech(samples) ? "" : "  (no speech detected)";
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {Path.GetFileName(path)}: {seconds:F1} s{range}{speech}"));
        }

        capture.CapReached += () =>
        {
            output.WriteLine($"  reached the {AudioCapture.MaxSeconds} s limit");
            if (recording) Toggle();
        };

        var reader = new Thread(() =>
        {
            string? line;
            while ((line = input.ReadLine()) is not null && !line.Trim().Equals("q", StringComparison.OrdinalIgnoreCase))
            {
                dispatcher.BeginInvoke(Toggle);
            }
            dispatcher.BeginInvoke(() =>
            {
                if (recording) Toggle();
                dispatcher.InvokeShutdown();
            });
        })
        { IsBackground = true };
        reader.Start();
        Dispatcher.Run();
        return 0;
    }

    /// 16 kHz mono IEEE float: what `AudioCapture` hands the transcriber, so `SmokeTest.LoadSamples` reads back the
    /// same samples with no conversion in between.
    internal static void Write(string path, float[] samples)
    {
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(AudioCapture.SampleRate, 1));
        writer.WriteSamples(samples, 0, samples.Length);
    }

    /// clip-01.wav, clip-02.wav, …: numbering continues after whatever the folder already holds, so a second sitting
    /// adds clips rather than overwriting the first.
    internal static string NextPath(string folder)
    {
        for (var n = 1; ; n++)
        {
            var path = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"clip-{n:D2}.wav"));
            if (!File.Exists(path)) return path;
        }
    }
}
