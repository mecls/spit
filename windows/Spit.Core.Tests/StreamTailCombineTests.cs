namespace Spit.Core.Tests;

/// `StreamTail.Combine`: a Windows-only rule, so it lives outside the ported classes. `Stitch.TryJoin`'s own cases
/// moved to `StitchTests` when the Mac gained it.
public sealed class StreamTailCombineTests
{
    [Fact]
    public void AStreamThatCoveredNoMoreThanTheOverlapIsReplacedByTheTail()
    {
        // The CI smoke run: two streamed words, then a tail pass from the first sample.
        Assert.Equal("Hi Joel, quick update. The dashboard is running.",
            StreamTail.Combine("Hi Joel", coveredMs: 1200, "Hi Joel, quick update. The dashboard is running."));
        Assert.Equal("one two three", StreamTail.Combine("one", StreamTail.OverlapMs, "one two three"));
    }

    [Fact]
    public void ASeamFoundInTheTextIsStitched()
    {
        var text = StreamTail.Combine(
            "we should ship the build on friday after the review",
            coveredMs: 6000,
            "friday after the review and then tell everyone");

        Assert.Equal("we should ship the build on friday after the review and then tell everyone", text);
    }

    [Fact]
    public void AnEmptyTailKeepsTheStreamedText()
    {
        Assert.Equal("Hi Joel", StreamTail.Combine(" Hi Joel ", coveredMs: 2400, "  "));
    }

    [Theory]
    [InlineData("Hi Joel,", 2400, "Hi Joel, quick update.")]                       // too few words to anchor
    [InlineData("Hi Joel, quick", 2400, "Joel, quick update. The dashboard")]      // three words, no seam
    [InlineData("Hi Joel, quick update.", 2600, "quick update. The dashboard is")] // four words, no seam
    public void WithoutASeamOnlyAWholeRecordingPassIsTrusted(string streamed, int coveredMs, string tail)
    {
        // Each of these pasted words twice when appended (third and fourth reviews).
        Assert.Null(StreamTail.Combine(streamed, coveredMs, tail));
    }

    [Fact]
    public void AStreamThatCoveredTheWholeRecordingRunsNoTailPassAtAll()
    {
        // A short "Thanks Joel." fully streamed must paste at once, with no pass after the key comes up.
        var samples = Tone(seconds: 1.5);

        Assert.Null(StreamTail.Tail(samples, afterMs: 1500, totalMs: 1500));
    }

    [Fact]
    public void ACutOffWordAtTheTailsStartStillFindsTheSeam()
    {
        // The overlap cut "dashboard" to "board"; the fifth review showed this sent ordinary speech to a whole pass.
        var text = StreamTail.Combine(
            "the MiraSite dashboard is running on Convex now",
            coveredMs: 6000,
            "board is running on Convex now, and the Olamaki lives on the VPS");

        Assert.Equal("the MiraSite dashboard is running on Convex now, and the Olamaki lives on the VPS", text);
    }

    [Fact]
    public void NewSpeechAfterASilentOverlapIsAppended()
    {
        Assert.Equal("Thanks for the update. See you on Monday.",
            StreamTail.Combine("Thanks for the update.", coveredMs: 6000, "See you on Monday.", overlapHasSpeech: false));
        Assert.Null(StreamTail.Combine("Thanks for the update.", coveredMs: 6000, "See you on Monday.", overlapHasSpeech: true));
    }

    [Fact]
    public void OverlapHasSpeechReadsTheOverlapAudioOnly()
    {
        // 3 s: speech for the first second, silence after. Covered to 3 s, the overlap (1.5–3 s) is silent.
        var samples = new float[16_000 * 3];
        for (var i = 0; i < 16_000; i++) samples[i] = (float)(0.3 * Math.Sin(i * 0.05));

        Assert.False(StreamTail.OverlapHasSpeech(samples, coveredMs: 3000, totalMs: 3000));
        Assert.True(StreamTail.OverlapHasSpeech(samples, coveredMs: 2000, totalMs: 3000));
    }

    private static float[] Tone(double seconds)
    {
        var samples = new float[(int)(16_000 * seconds)];
        for (var i = 0; i < samples.Length; i++) samples[i] = (float)(0.3 * Math.Sin(i * 0.05));
        return samples;
    }
}
