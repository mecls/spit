using System.IO;
using NAudio.Wave;
using Spit.Core;

namespace Spit.App.Tests;

/// The smoke test's WAV loader must hand Whisper what capture hands it: 16 kHz mono Float32 of the file's
/// real length. A wrong rate or a summed stereo pair would still transcribe something, and CI would be green
/// over the wrong audio.
public sealed class SmokeTestLoaderTests : IDisposable
{
    private readonly TempDirectory temp = new();

    public void Dispose() => temp.Dispose();

    [WindowsFact]
    public void TheEnglishFixture_Loads16kMonoOfItsFullLength()
    {
        var samples = SmokeTest.LoadSamples(Fixture("en.wav"));

        // 400,708 bytes of 16-bit mono PCM at 16 kHz: 12.52 s.
        Assert.Equal(200_354, samples.Length);
        Assert.True(EnergyGate.HasSpeech(samples));
        Assert.All(samples, x => Assert.InRange(x, -1f, 1f));
    }

    [WindowsFact]
    public void AStereo44kFile_IsAveragedToMonoAndResampledTo16k()
    {
        Directory.CreateDirectory(temp.Path);
        var path = Path.Combine(temp.Path, "tone.wav");
        const int rate = 44_100;
        var frames = rate * 2;
        var interleaved = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var x = 0.5f * MathF.Sin(2 * MathF.PI * 440 * i / rate);
            interleaved[2 * i] = x;
            interleaved[2 * i + 1] = x;
        }
        using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, 2)))
        {
            writer.WriteSamples(interleaved, 0, interleaved.Length);
        }

        var samples = SmokeTest.LoadSamples(path);

        Assert.InRange(samples.Length, 31_600, 32_400);   // 2 s at 16 kHz, within the resampler's latency
        // A 0.5 sine has an RMS of 0.354; summing the channels instead of averaging would double it.
        Assert.InRange(EnergyGate.Rms(samples.AsSpan(8_000, 16_000)), 0.33f, 0.38f);
    }

    [WindowsFact]
    public void Parse_NeedsBothTheWavAndTheReport()
    {
        Assert.Equal(("a.wav", "r.json", ModelCatalog.DefaultFile), SmokeTest.Parse(["--smoke-test", "a.wav", "--report", "r.json"]));
        Assert.Null(SmokeTest.Parse(["--smoke-test", "a.wav"]));
        Assert.Null(SmokeTest.Parse(["--smoke-test", "--report", "r.json"]));
    }

    /// S1 runs the same binary 36 times over three models. A `--model` the catalog does not pin has to be a
    /// usage error, because the alternative — falling back to the default — hands back a full set of numbers
    /// filed under a model that never ran.
    [WindowsFact]
    public void Parse_TakesAPinnedModelAndRefusesAnythingElse()
    {
        string[] with = ["--smoke-test", "a.wav", "--report", "r.json", "--model", ModelCatalog.TurboCompressedFile];
        Assert.Equal(("a.wav", "r.json", ModelCatalog.TurboCompressedFile), SmokeTest.Parse(with));

        Assert.Null(SmokeTest.Parse(["--smoke-test", "a.wav", "--report", "r.json", "--model", "ggml-tiny.bin"]));
        Assert.Null(SmokeTest.Parse(["--smoke-test", "a.wav", "--report", "r.json", "--model"]));
        // The flag with the next flag as its value, rather than a file name.
        Assert.Null(SmokeTest.Parse(["--smoke-test", "a.wav", "--model", "--report", "r.json"]));
    }

    /// `--warm-pass` is read by `Program`, not `Parse`; a report path followed by it must still parse, or session 3's
    /// driver gets usage errors for every clip.
    [WindowsFact]
    public void Parse_IsUnchangedByWarmPass()
    {
        Assert.Equal(("a.wav", "r.json", ModelCatalog.DefaultFile),
            SmokeTest.Parse(["--smoke-test", "a.wav", "--report", "r.json", SmokeTest.WarmPassFlag]));
    }

    /// Session 3 records clips once and replays them many times; the replay must be the recording, sample for sample.
    [WindowsFact]
    public void ARecordedClip_ReplaysAsExactlyTheSamplesCaptureProduced()
    {
        Directory.CreateDirectory(temp.Path);
        var samples = new float[AudioCapture.SampleRate * 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = 0.4f * MathF.Sin(i * 0.05f);

        var path = ClipRecorder.NextPath(temp.Path);
        ClipRecorder.Write(path, samples);

        Assert.Equal("clip-01.wav", Path.GetFileName(path));
        Assert.Equal("clip-02.wav", Path.GetFileName(ClipRecorder.NextPath(temp.Path)));
        Assert.Equal(samples, SmokeTest.LoadSamples(path));
    }

    [WindowsFact]
    public void ClipRecorder_NeedsAFolder()
    {
        Assert.Equal("clips", ClipRecorder.Parse([ClipRecorder.Flag, "clips"]));
        Assert.Null(ClipRecorder.Parse([ClipRecorder.Flag]));
        Assert.Null(ClipRecorder.Parse([ClipRecorder.Flag, "--model"]));
    }

    [WindowsFact]
    public void ExpectedWords_SurviveTheMisspellingsWhisperIsKnownFor()
    {
        Assert.True(SmokeTest.ContainsExpectedWord("Welcome to Miracyte."));
        Assert.True(SmokeTest.ContainsExpectedWord("the Olamaki"));
        Assert.False(SmokeTest.ContainsExpectedWord("you"));
    }

    private static string Fixture(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "mac", "Fixtures", name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"mac/Fixtures/{name} was not found above {AppContext.BaseDirectory}", name);
    }
}
