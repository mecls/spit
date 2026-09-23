namespace Spit.Core;

// Port of mac/Voice/App/DictationMachine.swift (RefineResult lives in Dictation.cs).

public abstract record MachineEvent
{
    private MachineEvent() { }
    public sealed record ModelReady : MachineEvent;
    public sealed record ModelProgress(double Progress) : MachineEvent;
    public sealed record HotkeyDown(FrontmostApp? App) : MachineEvent;
    public sealed record HotkeyUp : MachineEvent;
    public sealed record CancelRequested : MachineEvent;
    public sealed record AudioStopped(float[] Samples, int Ms, bool Speech) : MachineEvent;
    public sealed record Transcribed(Guid Id, string Text, string Language, int Ms) : MachineEvent;
    public sealed record TranscriptionFailed(Guid Id) : MachineEvent;
    public sealed record Refined(Guid Id, RefineResult Result) : MachineEvent;
    public sealed record Inserted(Guid Id, Injected How) : MachineEvent;
    public sealed record InsertFailed(Guid Id) : MachineEvent;
}

public abstract record HUDState
{
    private HUDState() { }
    public sealed record Hidden : HUDState;
    public sealed record Listening : HUDState;
    public sealed record Transcribing(double? Progress) : HUDState;
    public sealed record Cleaning : HUDState;
    public sealed record Done(string? Preview, string? Via) : HUDState;
    public sealed record Message(string Text) : HUDState;
    public sealed record ModelLoading(double Progress) : HUDState;
}

public abstract record Effect
{
    private Effect() { }
    public sealed record StartRecording : Effect;
    public sealed record StopRecording : Effect;
    public sealed record DiscardRecording : Effect;
    public sealed record Transcribe(Guid Id) : Effect;
    public sealed record Refine(Guid Id) : Effect;
    public sealed record Insert(Guid Id, string Text) : Effect;
    public sealed record Hud(HUDState State) : Effect;
    public sealed record ReportInjected(Guid Id, Injected How) : Effect;
}

/// The Mac's `DictationMachine.Phase`. Top-level because C# cannot nest a type with the same name as
/// the `Phase` property that exposes it.
public abstract record DictationPhase
{
    private DictationPhase() { }
    public sealed record ModelLoading(double Progress) : DictationPhase;
    public sealed record Ready : DictationPhase;
}

/// Pure reducer. Owns the queue of in-flight dictations; pastes strictly in order.
public sealed class DictationMachine
{
    public const int MinimumMs = 400;

    private readonly List<Dictation> queue = [];
    private readonly TimeProvider time;
    private Guid? recording;          // dictation currently capturing audio
    private bool cancelledRecording;

    /// `time` stamps `Dictation.StartedAt`, the one clock read the Mac reducer makes (`Date()`);
    /// injected so the reducer never reads the system clock itself.
    public DictationMachine(TimeProvider? time = null) => this.time = time ?? TimeProvider.System;

    public DictationPhase Phase { get; private set; } = new DictationPhase.ModelLoading(0);

    /// Whether a double-tap may latch: false while the model is still loading, when `HotkeyDown` refuses to
    /// start a dictation, so a latch would sit over a session that never began. The Mac's `canLatch`.
    public bool CanLatch => Phase is DictationPhase.Ready;
    public IReadOnlyList<Dictation> Queue => queue;

    public IReadOnlyList<Effect> Handle(MachineEvent e)
    {
        switch (e)
        {
            case MachineEvent.ModelProgress p:
                Phase = new DictationPhase.ModelLoading(p.Progress);
                return [new Effect.Hud(new HUDState.ModelLoading(p.Progress))];

            case MachineEvent.ModelReady:
                Phase = new DictationPhase.Ready();
                return [new Effect.Hud(new HUDState.Hidden())];

            case MachineEvent.HotkeyDown down:
            {
                if (Phase is DictationPhase.ModelLoading loading)
                    return [new Effect.Hud(new HUDState.ModelLoading(loading.Progress))];
                if (recording is not null) return [];
                var d = new Dictation(Guid.NewGuid(), time.GetUtcNow(), down.App);
                queue.Add(d); recording = d.ClientId; cancelledRecording = false;
                return [new Effect.StartRecording(), new Effect.Hud(new HUDState.Listening())];
            }

            case MachineEvent.HotkeyUp:
                if (recording is null || cancelledRecording) return [];
                return [new Effect.StopRecording()];

            case MachineEvent.CancelRequested:
            {
                if (recording is not { } id) return [];
                queue.RemoveAll(d => d.ClientId == id);
                recording = null; cancelledRecording = true;
                return [new Effect.DiscardRecording(), new Effect.Hud(new HUDState.Hidden())];
            }

            case MachineEvent.AudioStopped stopped:
            {
                if (recording is not { } id || IndexOf(id) is not { } i) return [];
                recording = null;
                if (stopped.Ms < MinimumMs || !stopped.Speech || stopped.Samples.Length == 0)
                {
                    queue.RemoveAt(i);
                    return [new Effect.Hud(new HUDState.Message(Strings.NothingHeard))];
                }
                queue[i].Samples = stopped.Samples; queue[i].AudioMs = stopped.Ms; queue[i].Stage = DictationStage.Transcribing;
                return [new Effect.Transcribe(id), new Effect.Hud(new HUDState.Transcribing(null))];
            }

            case MachineEvent.Transcribed t:
            {
                if (IndexOf(t.Id) is not { } i) return [];
                queue[i].Samples = [];
                var trimmed = t.Text.Trim();
                if (trimmed.Length == 0)
                {
                    queue.RemoveAt(i);
                    return [new Effect.Hud(new HUDState.Message(Strings.NothingHeard))];
                }
                queue[i].Raw = trimmed; queue[i].Language = t.Language; queue[i].AsrMs = t.Ms; queue[i].Stage = DictationStage.Refining;
                return [new Effect.Refine(t.Id), new Effect.Hud(new HUDState.Cleaning())];
            }

            case MachineEvent.TranscriptionFailed f:
                queue.RemoveAll(d => d.ClientId == f.Id);
                return [new Effect.Hud(new HUDState.Message(Strings.NothingHeard))];

            case MachineEvent.Refined r:
            {
                if (IndexOf(r.Id) is not { } i) return [];
                switch (r.Result)
                {
                    case RefineResult.Cleaned c: queue[i].Cleaned = c.Text; break;
                    case RefineResult.Literal: queue[i].Cleaned = null; break;
                    case RefineResult.Skipped:
                        queue[i].Cleaned = null;
                        queue[i].LlmModel = CleanupEngine.Skipped;
                        break;
                    case RefineResult.RawFallback fb:
                        // Said once the paste is done (`Inserted`), not now: `Done` followed within ~30 ms and
                        // replaced it before anyone could read it, and a clipboard-only paste's own message
                        // (an admin window) came before it and was replaced by it.
                        queue[i].Fallback = fb.Reason; queue[i].Cleaned = null;
                        break;
                }
                queue[i].Stage = DictationStage.ReadyToInsert;
                return InsertHeadIfReady();
            }

            case MachineEvent.Inserted ins:
            {
                if (IndexOf(ins.Id) is not { } i) return [];
                var preview = queue[i].TextToInsert;
                // Which path produced this text, so the gate's decisions are visible while its
                // thresholds are being tuned. Derived, not timed — the release→paste measurement lives
                // in the coordinator, which keeps this reducer pure and its effects comparable in tests.
                var via = queue[i].LlmModel == CleanupEngine.Skipped ? Strings.ViaSkipped
                    : (queue[i].Cleaned is not null ? Strings.ViaCleaned : null);
                var fallback = queue[i].Fallback;
                queue.RemoveAt(i);
                var effects = new List<Effect> { new Effect.ReportInjected(ins.Id, ins.How) };
                var next = InsertHeadIfReady();
                effects.AddRange(next);
                // A raw fallback says so in place of `Done`, for as long as `Done` would have stayed.
                if (next.Count == 0)
                {
                    effects.Add(new Effect.Hud(fallback is { } reason
                        ? new HUDState.Message(reason == FallbackReason.Unauthorized ? Strings.TokenInvalid : Strings.PastedRaw)
                        : new HUDState.Done(preview, via)));
                }
                return effects;
            }

            case MachineEvent.InsertFailed f:
                queue.RemoveAll(d => d.ClientId == f.Id);
                return [new Effect.Hud(new HUDState.Message(Strings.SecureField)), .. InsertHeadIfReady()];

            default:
                return [];
        }
    }

    private int? IndexOf(Guid id)
    {
        var i = queue.FindIndex(d => d.ClientId == id);
        return i < 0 ? null : i;
    }

    /// Only the head of the queue may paste, and only once its text is ready and nothing else is inserting.
    private List<Effect> InsertHeadIfReady()
    {
        if (queue.Count == 0) return [];
        var head = queue[0];
        if (head.Stage != DictationStage.ReadyToInsert || head.TextToInsert is not { } text) return [];
        head.Stage = DictationStage.Inserting;
        return [new Effect.Insert(head.ClientId, text)];
    }
}
