namespace Spit.Core.Tests;

// Port of mac/VoiceTests/DictationMachineTests.swift.
public sealed class DictationMachineTests
{
    private static DictationMachine Ready()
    {
        var m = new DictationMachine();
        _ = m.Handle(new MachineEvent.ModelReady());
        return m;
    }

    private static Effect[] Fx(params Effect[] effects) => effects;
    private static Effect Hud(HUDState state) => new Effect.Hud(state);

    [Fact]
    public void testIgnoresHotkeyWhileModelLoading()
    {
        var m = new DictationMachine();
        Assert.Equal(Fx(Hud(new HUDState.ModelLoading(0))), m.Handle(new MachineEvent.HotkeyDown(null)));
        Assert.Empty(m.Queue);
    }

    /// A double-tap during the first launch's model load must not start a latched session. The gesture itself
    /// is valid — `TapLatch` says `Latch` — so what stops it is `CanLatch`.
    [Fact]
    public void testADoubleTapWhileTheModelIsLoadingCannotLatch()
    {
        var m = new DictationMachine();
        Assert.False(m.CanLatch);
        Assert.Equal(Fx(Hud(new HUDState.ModelLoading(0))), m.Handle(new MachineEvent.HotkeyDown(null)));
        Assert.Empty(m.Queue);

        // The gesture does reach `Latch`, so the refusal above is the machine's and not an accident of the tap timing.
        var t0 = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        var latch = new TapLatch();
        Assert.Equal([TapLatch.Outcome.StartDictation], latch.Handle(HotkeyAction.Press, t0));
        Assert.Equal([TapLatch.Outcome.HoldOpen], latch.Handle(HotkeyAction.Release, t0.AddMilliseconds(100)));
        Assert.Equal([TapLatch.Outcome.Latch], latch.Handle(HotkeyAction.Press, t0.AddMilliseconds(200)));

        _ = m.Handle(new MachineEvent.ModelReady());
        Assert.True(m.CanLatch);
        Assert.Equal(Fx(new Effect.StartRecording(), Hud(new HUDState.Listening())), m.Handle(new MachineEvent.HotkeyDown(null)));
    }

    [Fact]
    public void testHappyPath()
    {
        var m = Ready();
        Assert.Equal(Fx(new Effect.StartRecording(), Hud(new HUDState.Listening())), m.Handle(new MachineEvent.HotkeyDown(null)));
        Assert.Equal(Fx(new Effect.StopRecording()), m.Handle(new MachineEvent.HotkeyUp()));
        var id = m.Queue[0].ClientId;
        Assert.Equal(Fx(new Effect.Transcribe(id), Hud(new HUDState.Transcribing(null))),
            m.Handle(new MachineEvent.AudioStopped([0.1f, 0.2f], 1200, true)));
        Assert.Equal(Fx(new Effect.Refine(id), Hud(new HUDState.Cleaning())),
            m.Handle(new MachineEvent.Transcribed(id, "olá", "pt", 900)));
        Assert.Equal(Fx(new Effect.Insert(id, "Olá.")), m.Handle(new MachineEvent.Refined(id, new RefineResult.Cleaned("Olá."))));
        Assert.Equal(Fx(new Effect.ReportInjected(id, Injected.Cleaned), Hud(new HUDState.Done("Olá.", Strings.ViaCleaned))),
            m.Handle(new MachineEvent.Inserted(id, Injected.Cleaned)));
        Assert.Empty(m.Queue);
    }

    [Fact]
    public void testShortOrSilentRecordingIsDropped()
    {
        var m = Ready();
        _ = m.Handle(new MachineEvent.HotkeyDown(null)); _ = m.Handle(new MachineEvent.HotkeyUp());
        Assert.Equal(Fx(Hud(new HUDState.Message(Strings.NothingHeard))), m.Handle(new MachineEvent.AudioStopped([], 200, true)));
        Assert.Empty(m.Queue);
        _ = m.Handle(new MachineEvent.HotkeyDown(null)); _ = m.Handle(new MachineEvent.HotkeyUp());
        Assert.Equal(Fx(Hud(new HUDState.Message(Strings.NothingHeard))), m.Handle(new MachineEvent.AudioStopped([0], 3000, false)));
    }

    [Fact]
    public void testCancelWhileListeningDiscards()
    {
        var m = Ready();
        _ = m.Handle(new MachineEvent.HotkeyDown(null));
        Assert.Equal(Fx(new Effect.DiscardRecording(), Hud(new HUDState.Hidden())), m.Handle(new MachineEvent.CancelRequested()));
        Assert.Empty(m.Queue);
        // a hotkeyUp after cancel is a no-op
        Assert.Empty(m.Handle(new MachineEvent.HotkeyUp()));
    }

    [Fact]
    public void testBurstKeepsPasteOrder()
    {
        var m = Ready();
        _ = m.Handle(new MachineEvent.HotkeyDown(null)); _ = m.Handle(new MachineEvent.HotkeyUp());
        var a = m.Queue[0].ClientId;
        _ = m.Handle(new MachineEvent.AudioStopped([1], 1000, true));
        _ = m.Handle(new MachineEvent.Transcribed(a, "a", "en", 1));
        // second dictation starts while the first is refining
        Assert.Equal(Fx(new Effect.StartRecording(), Hud(new HUDState.Listening())), m.Handle(new MachineEvent.HotkeyDown(null)));
        _ = m.Handle(new MachineEvent.HotkeyUp());
        var b = m.Queue[1].ClientId;
        _ = m.Handle(new MachineEvent.AudioStopped([1], 1000, true));
        _ = m.Handle(new MachineEvent.Transcribed(b, "b", "en", 1));
        // b's cleanup arrives first: it must wait
        Assert.Empty(m.Handle(new MachineEvent.Refined(b, new RefineResult.Cleaned("B."))));
        Assert.Equal(Fx(new Effect.Insert(a, "A.")), m.Handle(new MachineEvent.Refined(a, new RefineResult.Cleaned("A."))));
        Assert.Equal(Fx(new Effect.ReportInjected(a, Injected.Cleaned), new Effect.Insert(b, "B.")),
            m.Handle(new MachineEvent.Inserted(a, Injected.Cleaned)));
        Assert.Equal(Fx(new Effect.ReportInjected(b, Injected.Cleaned), Hud(new HUDState.Done("B.", Strings.ViaCleaned))),
            m.Handle(new MachineEvent.Inserted(b, Injected.Cleaned)));
    }

    [Fact]
    public void testRawFallbackPastesRawAndReportsRaw()
    {
        var m = Ready();
        _ = m.Handle(new MachineEvent.HotkeyDown(null)); _ = m.Handle(new MachineEvent.HotkeyUp());
        var id = m.Queue[0].ClientId;
        _ = m.Handle(new MachineEvent.AudioStopped([1], 1000, true));
        _ = m.Handle(new MachineEvent.Transcribed(id, "raw text", "en", 1));
        Assert.Equal(Fx(new Effect.Insert(id, "raw text")),
            m.Handle(new MachineEvent.Refined(id, new RefineResult.RawFallback(FallbackReason.Offline))));
        // Said after the paste, in place of Done: said before it, Done replaced it within milliseconds.
        Assert.Equal(Fx(new Effect.ReportInjected(id, Injected.Raw), Hud(new HUDState.Message(Strings.PastedRaw))),
            m.Handle(new MachineEvent.Inserted(id, Injected.Raw)));
    }

    [Fact]
    public void testAnInvalidTokenSaysSoOnceTheRawTextIsPasted()
    {
        var m = Ready();
        _ = m.Handle(new MachineEvent.HotkeyDown(null)); _ = m.Handle(new MachineEvent.HotkeyUp());
        var id = m.Queue[0].ClientId;
        _ = m.Handle(new MachineEvent.AudioStopped([1], 1000, true));
        _ = m.Handle(new MachineEvent.Transcribed(id, "raw text", "en", 1));
        Assert.Equal(Fx(new Effect.Insert(id, "raw text")),
            m.Handle(new MachineEvent.Refined(id, new RefineResult.RawFallback(FallbackReason.Unauthorized))));
        Assert.Equal(Fx(new Effect.ReportInjected(id, Injected.Raw), Hud(new HUDState.Message(Strings.TokenInvalid))),
            m.Handle(new MachineEvent.Inserted(id, Injected.Raw)));
    }

    [Fact]
    public void testTranscriptionFailureDropsTheDictation()
    {
        var m = Ready();
        _ = m.Handle(new MachineEvent.HotkeyDown(null)); _ = m.Handle(new MachineEvent.HotkeyUp());
        var id = m.Queue[0].ClientId;
        _ = m.Handle(new MachineEvent.AudioStopped([1], 1000, true));
        Assert.Equal(Fx(Hud(new HUDState.Message(Strings.NothingHeard))), m.Handle(new MachineEvent.TranscriptionFailed(id)));
        Assert.Empty(m.Queue);
    }
}
