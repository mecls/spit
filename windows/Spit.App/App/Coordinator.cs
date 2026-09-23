using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Spit.Core;

namespace Spit.App;

/// <summary>
/// Port of mac/Voice/App/Coordinator.swift: wires the reducer to the OS adapters, and owns the two clocks the
/// pure types refuse to read — the gesture clock and the release→paste clock.
///
/// Dispatcher-thread only, the Windows form of `@MainActor`. Adapters raise their events on the dispatcher's
/// context, async work resumes on it, and every clipboard call is made from it: Spit's clipboard owner window
/// lives on this thread, and Windows sends that window messages while another app takes the clipboard.
///
/// Nothing the user says is logged here — lengths, timings and error types only (rule 25).
/// </summary>
public sealed class Coordinator : IDisposable
{
    /// `scheduleReturnToIdle`.
    public static readonly TimeSpan ReturnToIdle = TimeSpan.FromSeconds(1.2);

    /// `prewarmConnection`: one warm a minute keeps the pooled connection fresh without chatter.
    public static readonly TimeSpan PrewarmInterval = TimeSpan.FromSeconds(60);

    private const string Category = "coordinator";
    private const int LevelsKept = 20;
    private static readonly TimeSpan KeyPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan LatchWindowRetry = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan QuitRestoreWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan QuitTranscriberWait = TimeSpan.FromSeconds(3);
    private static readonly nint MessageOnlyParent = new(-3);   // HWND_MESSAGE

    private readonly AppModel model;
    private readonly SettingsStore store;
    private readonly UiShell shell;
    private readonly TimeProvider time;
    private readonly SynchronizationContext context;
    private readonly string serverUrl;

    private readonly DictationMachine machine;
    private readonly TapLatch tapLatch = new();
    private readonly MenuMask menuMask;
    private readonly KeyboardHook hook;
    private readonly HookWatchdog watchdog;
    private readonly AudioCapture capture;
    private readonly LiveTranscription live;
    private readonly ModelDownloader downloader;
    private readonly DictionaryCache dictionary;
    private readonly HwndSource clipboardOwner;
    private readonly TextInjector injector;
    private readonly Sounds sounds;
    private readonly TokenStore tokens = new();
    private readonly HttpVoiceApiClient api;
    private readonly RefineService refiner;
    private readonly SyncService sync;
    private readonly InsightsModel insights;
    private readonly CancellationTokenSource shutdown = new();

    private HotkeyTranslator translator;
    private HotkeyInterpreter interpreter;
    // Mac D2: a key choice picked while the current key is held waits for its release or cancel.
    private HotkeyChoice? pendingChoice;

    private WhisperTranscriber? transcriber;
    private bool modelBusy;
    private bool modelAgain;
    private bool downloading;
    private int downloadGeneration;

    // Rule 15's clock. `pendingRelease` is stamped at `.stopRecording` and claimed by the next `.transcribe`;
    // `releaseAt` then holds it per dictation until the paste closes it. These maps, and the two below, are
    // swept against the queue after every event, so a dictation that never pastes leaves nothing behind.
    private long? pendingRelease;
    private readonly Dictionary<Guid, long> releaseAt = [];
    /// Confirmed streaming segments per dictation; 1 means no chunk boundary, which lets the skip gate skip.
    private readonly Dictionary<Guid, int> streamedSegments = [];
    /// The foreground process at hotkey-down, for the paste-time elevation check when nothing is foreground.
    private readonly Dictionary<Guid, int> targetProcess = [];
    private int? pendingTargetProcess;
    private string? startFailure;
    private static readonly TimeSpan ReloadPollInterval = TimeSpan.FromMilliseconds(200);
    /// Set by `.transcribe`; read straight after the `.audioStopped` a stop sends, to tell a stop that became a
    /// transcription from one the reducer rejected.
    private bool transcribeRequested;

    private long? lastPrewarm;
    private readonly List<float> levels = new(LevelsKept);
    private ITimer? keyPoll;
    private int keyPollGeneration;
    private ITimer? latchWindow;
    private int latchGeneration;
    private ITimer? hideTimer;
    private int hideGeneration;
    private ITimer? syncTimer;
    private int syncsRunning;
    private bool started;
    private bool quitting;
    private bool disposed;

    /// Builds every service without starting any of them: no hook, no microphone, no network, no model.
    /// Call on the dispatcher thread.
    public Coordinator(AppModel model, SettingsStore store, AppPaths paths, UiShell shell, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(shell);
        this.model = model;
        this.store = store;
        this.shell = shell;
        this.time = time ?? TimeProvider.System;
        context = new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher);
        machine = new DictationMachine(this.time);

        var settings = store.Current;
        var choice = HotkeyChoiceExtensions.FromRawValue(settings.Hotkey) ?? HotkeyChoice.RightCtrl;
        translator = new HotkeyTranslator(choice);
        interpreter = new HotkeyInterpreter(choice);
        menuMask = new MenuMask(() => translator.Choice);
        hook = new KeyboardHook(context);
        watchdog = new HookWatchdog(hook.Reinstall, IsIdle, context, this.time);

        capture = new AudioCapture(context, this.time);
        live = new LiveTranscription(capture, context, this.time);
        downloader = new ModelDownloader(paths.Models);
        dictionary = new DictionaryCache(paths);
        // A message-only window on this thread owns every clipboard open (rule 33).
        clipboardOwner = new HwndSource(new HwndSourceParameters("SpitClipboardOwner") { ParentWindow = MessageOnlyParent, WindowStyle = 0 });
        injector = new TextInjector(() => clipboardOwner.Handle);
        sounds = new Sounds();

        // The server URL takes effect on relaunch (Strings.ServerURLChangeNote), so it is read once.
        serverUrl = settings.ServerURL;
        api = new HttpVoiceApiClient(ServerUri(serverUrl), () => tokens.Read(serverUrl), ClientVersion);
        var local = new LocalSettings(store);
        refiner = new RefineService(api, new Outbox(), local, ModelCatalog.AsrModelFor(settings.ModelFile), ClientVersion);
        sync = new SyncService(api, local, this.time);
        insights = new InsightsModel(api, new InsightsCache(paths.InsightsCacheFile), this.time);

        AssignIntents();
    }

    /// `clientVersion` on the wire: the repo's VERSION, stamped into the assembly at build time.
    public static string ClientVersion { get; } =
        typeof(Coordinator).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// For the Dictionary page, which calls the API directly as the Mac's does.
    public IVoiceApiClient Api => api;

    public InsightsModel Insights => insights;

    /// Mac `start()`. Every adapter failure is logged and shown, never fatal: a PC with no microphone, no
    /// network, no token and no model still gets a running Spit that says what is missing.
    public void Start()
    {
        if (started || disposed) return;
        started = true;

        capture.Level += OnLevel;
        capture.CapReached += CapReached;
        live.Changed += RefreshLiveText;
        hook.KeyEvent += OnKey;
        sync.OnDictionaryChange = terms => dictionary.Update(terms);
        sync.Changed += OnSyncChanged;
        SystemEvents.TimeChanged += OnTimeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        try
        {
            capture.Prepare();
        }
        catch (Exception e)
        {
            // Mac `try? recorder.prepare()`: the dictation itself reports a missing microphone.
            Log.Failure(Category, "prepare capture", e);
        }
        if (!hook.Start()) Log.Error(Category, "the keyboard hook is not installed; the bar's mic button still works");
        watchdog.Start();

        _ = RecheckMicrophoneAsync();
        _ = StartSyncAsync();
        _ = LoadModelAsync(reload: false);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        shutdown.Cancel();

        hook.KeyEvent -= OnKey;
        capture.Level -= OnLevel;
        capture.CapReached -= CapReached;
        live.Changed -= RefreshLiveText;
        sync.Changed -= OnSyncChanged;
        SystemEvents.TimeChanged -= OnTimeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        keyPollGeneration++;
        keyPoll?.Dispose();
        CancelLatchWindow();
        hideTimer?.Dispose();
        syncTimer?.Dispose();

        Quietly("keyboard hook", hook.Dispose);
        Quietly("hook watchdog", watchdog.Dispose);
        Quietly("capture", capture.Dispose);
        Quietly("sync", sync.Dispose);
        if (transcriber is { } loaded)
        {
            transcriber = null;
            _ = DisposeTranscriberAsync(loaded);
        }
        Quietly("sounds", sounds.Dispose);
        Quietly("model downloader", downloader.Dispose);
        Quietly("api", api.Dispose);
        Quietly("clipboard owner", clipboardOwner.Dispose);
    }

    // MARK: - Intents

    private void AssignIntents()
    {
        model.MicTapped = () => Done(ToggleLatchedFromBar);
        model.CopyLastDictation = () => Done(CopyLastDictation);
        model.TogglePause = () => Done(() => model.Paused = !model.Paused);
        model.ModeChanged = mode => sync.PushAsync(mode: mode);
        model.LanguageChanged = language => sync.PushAsync(language: language);
        model.HotkeyChanged = choice => Done(() => SetHotkey(choice));
        model.OpenSetup = () => Done(() =>
        {
            shell.ShowSetup();
            _ = RecheckMicrophoneAsync();
        });
        model.Quit = QuitAsync;
        model.DownloadModel = DownloadModelAsync;
        model.DeleteModel = file => Done(() => DeleteModel(file));
        model.ReloadModel = () => LoadModelAsync(reload: true);
        model.ModelFileChanged = ModelFileChangedAsync;
        model.SaveToken = SaveTokenAsync;
        model.SignOut = SignOutAsync;
        model.TestConnection = RunSyncAsync;
        model.DictionaryChanged = RunSyncAsync;
        // Not awaited by the page: the numbers arrive through `PresentationChanged`.
        model.RefreshInsights = () => Done(() => _ = insights.Refresh());
    }

    private void CopyLastDictation()
    {
        if (model.LastText is not { } text) return;
        if (!injector.CopyToClipboard(text)) ShowHud(new HUDState.Message(Strings.ClipboardBusy));
    }

    private void SetHotkey(HotkeyChoice choice)
    {
        if (interpreter.IsHeld)
        {
            pendingChoice = choice;
            return;
        }
        ApplyHotkey(choice);
    }

    private void ApplyHotkey(HotkeyChoice choice)
    {
        pendingChoice = null;
        if (choice == interpreter.Choice) return;
        // A rebuilt interpreter must not silently un-latch a hands-free session.
        var wasLatched = interpreter.Latched;
        interpreter = new HotkeyInterpreter(choice) { Latched = wasLatched };
        translator = new HotkeyTranslator(choice);
        Log.Info(Category, $"hotkey is now {choice.RawValue()}");
    }

    // MARK: - Hotkey

    private void OnKey(RawKeyEvent e)
    {
        if (disposed) return;
        menuMask.Observe(e);
        if (translator.Translate(e) is { } keyEvent)
        {
            // The clock is read here and nowhere below: `TapLatch` and `DictationMachine` stay pure. Gesture time
            // is when the key moved by the hook's stamp, not when a busy dispatcher reached it.
            Interpret(keyEvent, KeyEventClock.EventTime(e.Time, unchecked((uint)Environment.TickCount), time.GetUtcNow()));
        }
        UpdateKeyPoll();
    }

    private void Interpret(KeyEvent keyEvent, DateTimeOffset at)
    {
        if (interpreter.Handle(keyEvent) is not { } action) return;
        if (action is HotkeyAction.Release or HotkeyAction.Cancel && pendingChoice is { } pending) ApplyHotkey(pending);
        if (action == HotkeyAction.Press) model.HotkeySeen = true;
        if (model.Paused) return;
        HandleHotkey(action, at);
    }

    /// Polls the physical key while the hotkey is held. Ctrl+Alt+Del, locking the PC or a UAC prompt moves input
    /// to another desktop and the key-up goes with it; unnoticed, the dictation records to the 90 s cap and the
    /// next real press reads as autorepeat.
    private void UpdateKeyPoll()
    {
        if (translator.IsHotkeyDown == (keyPoll is not null)) return;
        keyPollGeneration++;
        keyPoll?.Dispose();
        keyPoll = null;
        if (!translator.IsHotkeyDown || disposed) return;
        var generation = keyPollGeneration;
        keyPoll = time.CreateTimer(_ => context.Post(_ =>
        {
            if (generation == keyPollGeneration) SyncHotkeyState();
        }, null), null, KeyPollInterval, KeyPollInterval);
    }

    /// Feeds the release the hook never delivered, through the same interpreter and gesture rules as a real one.
    private void SyncHotkeyState()
    {
        if (disposed) return;
        if (translator.IsHotkeyDown && (Native.GetAsyncKeyState(translator.VirtualKey) & 0x8000) == 0
            && translator.ForceRelease() is { } release)
        {
            Log.Info(Category, "the hotkey's key-up never arrived; releasing it");
            Interpret(release, time.GetUtcNow());
        }
        UpdateKeyPoll();
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e) => context.Post(_ => SyncHotkeyState(), null);

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e) => context.Post(_ => SyncHotkeyState(), null);

    private void HandleHotkey(HotkeyAction action, DateTimeOffset now)
    {
        foreach (var outcome in tapLatch.Handle(action, now)) Apply(outcome);
        if (action == HotkeyAction.Cancel) CancelFromKeyboard();
    }

    /// Esc, or a shortcut chord during a hold. `TapLatch` only forgets the gesture ("the reducer discards the
    /// dictation"); the Mac never sends that discard, which leaves the microphone open until the 90 s cap.
    private void CancelFromKeyboard()
    {
        CancelLatchWindow();
        if (model.IsLatched) ClearLatch();
        Send(new MachineEvent.CancelRequested());
    }

    private void Apply(TapLatch.Outcome outcome)
    {
        switch (outcome)
        {
            case TapLatch.Outcome.StartDictation:
                SendHotkeyDown();
                // `.hotkeyDown` refuses while the model loads, and a microphone can fail to open. The gesture must
                // not advance over a dictation that never began: a double-tap would latch with no recording, show
                // the red indicator over a closed microphone, and swallow the first real press once the model is ready.
                if (!capture.IsCapturing) tapLatch.Reset();
                break;

            case TapLatch.Outcome.HoldOpen:
                // Too quick to be a dictation. Keep recording — the reducer would discard it as too short
                // anyway — and wait to see whether a second tap arrives.
                ScheduleLatchWindow(TimeSpan.FromMilliseconds(TapLatch.WindowMs));
                break;

            case TapLatch.Outcome.Latch:
                CancelLatchWindow();
                if (!machine.CanLatch || !capture.IsCapturing)
                {
                    tapLatch.Reset();
                    break;
                }
                SetLatched();
                break;

            case TapLatch.Outcome.EndSession:
                CancelLatchWindow();
                ClearLatch();
                Send(new MachineEvent.HotkeyUp());
                break;

            case TapLatch.Outcome.Abandon:
                CancelLatchWindow();
                ClearLatch();
                // Cancel first, then show: `.cancelRequested` returns `.hud(.hidden)`, which would wipe a
                // message shown before it, and a stray tap would look like the app ignoring the user.
                Send(new MachineEvent.CancelRequested());
                ShowHud(new HUDState.Message(Strings.TapTooShort));
                break;
        }
    }

    /// The app the paste is aimed at is the one in front when the key went down, not whatever is in front
    /// at paste time.
    private void SendHotkeyDown()
    {
        FrontmostApp? app = null;
        try
        {
            pendingTargetProcess = ForegroundContext.ForegroundProcessId();
            app = ForegroundContext.Current();
        }
        catch (Exception e)
        {
            Log.Failure(Category, "read the foreground app", e);
        }
        Send(new MachineEvent.HotkeyDown(app));
        pendingTargetProcess = null;
    }

    private void ScheduleLatchWindow(TimeSpan delay)
    {
        CancelLatchWindow();
        var generation = latchGeneration;
        latchWindow = time.CreateTimer(_ => context.Post(_ => LatchWindowFired(generation), null), null, delay, Timeout.InfiniteTimeSpan);
    }

    private void CancelLatchWindow()
    {
        // The generation retires a timer that already fired and posted before it was cancelled.
        latchGeneration++;
        latchWindow?.Dispose();
        latchWindow = null;
    }

    private void LatchWindowFired(int generation)
    {
        if (generation != latchGeneration || disposed) return;
        var outcomes = tapLatch.WindowExpired(time.GetUtcNow());
        // A timer can land a fraction of a millisecond before the whole-millisecond deadline; asking again
        // shortly is what keeps a stray tap from holding the microphone open.
        if (outcomes.Count == 0 && tapLatch.IsActive && !tapLatch.IsLatched)
        {
            ScheduleLatchWindow(LatchWindowRetry);
            return;
        }
        latchWindow?.Dispose();
        latchWindow = null;
        foreach (var outcome in outcomes) Apply(outcome);
    }

    private void SetLatched()
    {
        model.IsLatched = true;
        interpreter.Latched = true;
    }

    /// Every route a session can end funnels through here: a stale latched indicator claims a microphone is
    /// open when it is not.
    private void ClearLatch()
    {
        model.IsLatched = false;
        interpreter.Latched = false;
    }

    /// The 90 s ceiling. Reachable in a hands-free session left running; it must read as a limit, not a crash.
    private void CapReached()
    {
        if (disposed) return;
        CancelLatchWindow();
        var wasLatched = model.IsLatched;
        ClearLatch();
        tapLatch.Reset();
        Send(new MachineEvent.HotkeyUp());
        if (wasLatched) ShowHud(new HUDState.Message(Strings.LatchCapReached));
    }

    /// The bar's mic button. Mouse-started sessions are always hands-free: there is no mouse equivalent of
    /// holding a key. It is also the only way to dictate from an admin window, which the hook cannot see (rule 30).
    private void ToggleLatchedFromBar()
    {
        if (model.Paused) return;
        if (model.IsLatched)
        {
            CancelLatchWindow();
            ClearLatch();
            tapLatch.Reset();
            Send(new MachineEvent.HotkeyUp());
            return;
        }
        SendHotkeyDown();
        // Only latch over a recording that really started: `.hotkeyDown` refuses while the model loads, and a
        // microphone can fail to open.
        if (model.Hud is not HUDState.Listening) return;
        tapLatch.ForceLatched();
        SetLatched();
    }

    private bool IsIdle() => !translator.IsHotkeyDown && !capture.IsCapturing && !model.IsLatched;

    // MARK: - Reducer

    private void Send(MachineEvent e)
    {
        foreach (var effect in machine.Handle(e)) Perform(effect);
        PurgeFinishedDictations();

        // Handled after the batch, not inside `.startRecording`: `.hotkeyDown`'s effects go on to show
        // Listening, which would wipe the message over a microphone that never opened.
        if (startFailure is not { } message) return;
        startFailure = null;
        CancelLatchWindow();
        ClearLatch();
        tapLatch.Reset();
        Send(new MachineEvent.CancelRequested());
        ShowHud(new HUDState.Message(message));
    }

    /// Drops per-dictation bookkeeping for ids the reducer has finished with. The queue is the authority on
    /// what is in flight, so this catches every exit path, including ones added later.
    private void PurgeFinishedDictations()
    {
        if (releaseAt.Count == 0 && streamedSegments.Count == 0 && targetProcess.Count == 0) return;
        var inFlight = machine.Queue.Select(d => d.ClientId).ToHashSet();
        foreach (var id in releaseAt.Keys.Where(id => !inFlight.Contains(id)).ToArray()) releaseAt.Remove(id);
        foreach (var id in streamedSegments.Keys.Where(id => !inFlight.Contains(id)).ToArray()) streamedSegments.Remove(id);
        foreach (var id in targetProcess.Keys.Where(id => !inFlight.Contains(id)).ToArray()) targetProcess.Remove(id);
    }

    private void Perform(Effect effect)
    {
        switch (effect)
        {
            case Effect.StartRecording:
                StartRecording();
                break;

            case Effect.StopRecording:
                StopRecording();
                break;

            case Effect.DiscardRecording:
                // Never leave a stream running past its dictation.
                if (live.IsRunning) _ = FinishStreamQuietlyAsync();
                capture.Discard();
                break;

            case Effect.Transcribe transcribe:
                Transcribe(transcribe.Id);
                break;

            case Effect.Refine refine:
                _ = RefineAsync(refine.Id);
                break;

            case Effect.Insert insert:
                model.LastText = insert.Text;
                _ = InsertAsync(insert.Id, insert.Text);
                break;

            case Effect.ReportInjected report:
                streamedSegments.Remove(report.Id);
                _ = ReportAsync(report.Id, report.How, TakeTotalMs(report.Id));
                break;

            case Effect.Hud hud:
                ShowHud(hud.State);
                break;
        }
    }

    private void StartRecording()
    {
        levels.Clear();
        model.Levels = [];
        model.LiveText = null;
        if (pendingTargetProcess is { } process && machine.Queue.Count > 0) targetProcess[machine.Queue[^1].ClientId] = process;

        try
        {
            capture.Start();
            model.Microphone = MicrophoneState.Available;
        }
        catch (MicrophoneBlockedException e)
        {
            Log.Failure(Category, "start recording", e);
            model.Microphone = MicrophoneState.Blocked;
            startFailure = Strings.MicrophoneBlocked;
            return;
        }
        catch (Exception e)
        {
            // NoMicrophoneException, or anything else that kept capture from running: "Listening" over a
            // microphone recording nothing is indistinguishable from working until no text appears.
            Log.Failure(Category, "start recording", e);
            if (e is NoMicrophoneException) model.Microphone = MicrophoneState.NoDevice;
            startFailure = Strings.NoMicrophone;
            return;
        }

        StartStreamingIfEnabled();
        PrewarmConnection();   // rule 19: strictly after the microphone is open
        if (store.Current.Sounds) sounds.PlayStart();
    }

    private void StopRecording()
    {
        // Rule 15: the release→paste clock starts before capture is torn down, because stopping is part of
        // the latency the user feels.
        pendingRelease = time.GetTimestamp();
        var (samples, ms) = capture.Stop();
        if (store.Current.Sounds) sounds.PlayStop();
        transcribeRequested = false;
        Send(new MachineEvent.AudioStopped(samples, ms, EnergyGate.HasSpeech(samples)));
        // A stop the reducer rejects ("Nothing heard": too short, or no speech) never reaches `.transcribe`, the
        // only other place a stream is finished. Left running, the next dictation inherits it: it pastes this
        // dictation's words and never decodes its own opening audio (proven by review against StreamingSession).
        if (!transcribeRequested && live.IsRunning) _ = FinishStreamQuietlyAsync();
    }

    private void StartStreamingIfEnabled()
    {
        // `.startRecording` only happens once the model is ready, so this is the loaded Whisper instance.
        if (!store.Current.LiveTranscription || transcriber is not { IsReady: true } current) return;
        live.Start(current, Hint());
    }

    private void PrewarmConnection()
    {
        if (lastPrewarm is { } last && time.GetElapsedTime(last) < PrewarmInterval) return;
        lastPrewarm = time.GetTimestamp();
        api.Prewarm();
    }

    private void Transcribe(Guid id)
    {
        transcribeRequested = true;
        // `.transcribe` comes out of the `.audioStopped` that `.stopRecording` sends in the same turn, so this is
        // the first point at which the release just stamped has an id to belong to.
        if (pendingRelease is { } release)
        {
            releaseAt[id] = release;
            pendingRelease = null;
        }
        if (Find(id) is not { } dictation) return;
        _ = TranscribeAsync(id, dictation.Samples, dictation.AudioMs, Hint());
    }

    private async Task TranscribeAsync(Guid id, float[] samples, int audioMs, TranscribeHint hint)
    {
        var current = transcriber;
        MachineEvent outcome;
        try
        {
            // The streamed transcript is already finished — that is the point, and why `asrMs` is 0 for it.
            var streamed = live.IsRunning ? await live.FinishAsync() : null;
            if (streamed is not null && current is not null)
            {
                // The stream stops at its last pass, and whatever arrived after that pass began is never
                // transcribed — measured on the Mac at 29 % of a dictation. So the tail gets one pass of its
                // own, over the leftover audio only, stitched at a seam found in the text.
                var text = streamed.Text;
                var asrMs = 0;
                if (StreamTail.Tail(samples, streamed.CoveredMs, audioMs) is { } tail)
                {
                    Log.Info(Category, $"transcribing stream tail: {audioMs - streamed.CoveredMs} ms of {audioMs} ms");
                    try
                    {
                        var t = await Task.Run(() => current.TranscribeAsync(tail, hint, progress: null));
                        asrMs = t.DurationMs;
                        if (StreamTail.Combine(text, streamed.CoveredMs, t.Text, StreamTail.OverlapHasSpeech(samples, streamed.CoveredMs, audioMs)) is { } combined)
                        {
                            text = combined;
                        }
                        else
                        {
                            // No seam: stitching would paste the overlap twice, so only a pass over the whole recording is right.
                            Log.Info(Category, $"no seam between stream and tail; transcribing all {audioMs} ms");
                            var whole = await Task.Run(() => current.TranscribeAsync(samples, hint, progress: null));
                            text = whole.Text;
                            asrMs += whole.DurationMs;
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Failure(Category, "transcribe stream tail", e);
                    }
                }
                streamedSegments[id] = streamed.Segments;
                outcome = new MachineEvent.Transcribed(id, text, hint.Language ?? "auto", asrMs);
            }
            else
            {
                var loaded = current ?? throw new InvalidOperationException("No speech model is loaded.");
                // Off the dispatcher: a decode must never be what keeps the bar from painting.
                var transcript = await Task.Run(() => loaded.TranscribeAsync(samples, hint, p => context.Post(_ => ShowTranscribingProgress(id, p), null)));
                outcome = new MachineEvent.Transcribed(id, transcript.Text, transcript.Language, transcript.DurationMs);
            }
        }
        catch (Exception e)
        {
            Log.Failure(Category, "transcribe", e);
            outcome = new MachineEvent.TranscriptionFailed(id);
        }
        if (!disposed) Send(outcome);
    }

    private void ShowTranscribingProgress(Guid id, double progress)
    {
        // A progress report can land after the text did; it must not pull the bar back from Cleaning or Done.
        if (disposed || model.Hud is not HUDState.Transcribing || Find(id) is not { Stage: DictationStage.Transcribing }) return;
        ShowHud(new HUDState.Transcribing(progress));
    }

    private async Task FinishStreamQuietlyAsync()
    {
        try
        {
            await live.FinishAsync();
        }
        catch (Exception e)
        {
            Log.Failure(Category, "finish a discarded stream", e);
        }
    }

    private async Task RefineAsync(Guid id)
    {
        if (Find(id) is not { } dictation) return;
        // Carried on the dictation rather than through the reducer: how the text was produced is not state the
        // machine decides anything on.
        dictation.StreamedSegments = streamedSegments.GetValueOrDefault(id, 1);
        RefineResult result;
        try
        {
            result = await refiner.RefineAsync(dictation, store.Current.Mode);
        }
        catch (Exception e)
        {
            Log.Failure(Category, "refine", e);
            result = new RefineResult.RawFallback(FallbackReason.Offline);
        }
        if (!disposed) Send(new MachineEvent.Refined(id, result));
    }

    private async Task InsertAsync(Guid id, string text)
    {
        var dictation = Find(id);
        var target = dictation?.App?.BundleId;
        // Rule 30: the window that will receive Ctrl+V decides, so elevation is checked now; the process from
        // hotkey-down stands in when nothing is in front.
        var process = ForegroundContext.ForegroundProcessId() ?? (targetProcess.TryGetValue(id, out var captured) ? captured : null);
        var elevated = process is { } pid && ElevationProbe.BlocksInputFromSpit(pid);

        InsertResult result;
        try
        {
            // Awaited on the dispatcher, never ConfigureAwait(false): the restore that follows must also open the
            // clipboard from this thread.
            result = await injector.InsertAsync(text, TextInjector.Route(target, elevated));
        }
        catch (Exception e)
        {
            Log.Failure(Category, "insert", e);
            result = InsertResult.Failed;
        }
        if (disposed) return;

        switch (result)
        {
            case InsertResult.Pasted:
                Send(new MachineEvent.Inserted(id, Find(id)?.Cleaned is null ? Injected.Raw : Injected.Cleaned));
                break;
            case InsertResult.ClipboardOnlySelf:
                Send(new MachineEvent.Inserted(id, Injected.Clipboard));
                ShowHud(new HUDState.Message(Strings.SecureField));
                break;
            case InsertResult.ClipboardOnlyElevated:
                Send(new MachineEvent.Inserted(id, Injected.Clipboard));
                ShowHud(new HUDState.Message(Strings.AdminWindow));
                break;
            case InsertResult.ClipboardOnlyKeystrokeFailed:
                Send(new MachineEvent.Inserted(id, Injected.Clipboard));
                ShowHud(new HUDState.Message(Strings.ClipboardBusy));
                break;
            default:
                // Rule 33: not pasted and not on the clipboard; the report still goes, as `none`.
                var totalMs = TakeTotalMs(id);
                Send(new MachineEvent.InsertFailed(id));
                ShowHud(new HUDState.Message(Strings.ClipboardBusy));
                _ = ReportAsync(id, Injected.None, totalMs);
                break;
        }
    }

    private int? TakeTotalMs(Guid id) =>
        releaseAt.Remove(id, out var released) ? (int)time.GetElapsedTime(released).TotalMilliseconds : null;

    private async Task ReportAsync(Guid id, Injected how, int? totalMs)
    {
        if (totalMs is { } ms) Log.Info(Category, $"release to paste: {ms} ms ({how})");
        try
        {
            await refiner.ReportInjectedAsync(id, how, totalMs);
        }
        catch (Exception e)
        {
            Log.Failure(Category, "report injected", e);
        }
    }

    private Dictation? Find(Guid id) => machine.Queue.FirstOrDefault(d => d.ClientId == id);

    private TranscribeHint Hint()
    {
        var language = store.Current.Language;
        return new TranscribeHint(language == "auto" ? null : language, dictionary.Terms);
    }

    // MARK: - Bar

    private void ShowHud(HUDState state)
    {
        model.Hud = state;
        hideGeneration++;
        hideTimer?.Dispose();
        hideTimer = null;
        if (state is HUDState.Hidden) model.LiveText = null;
        // The bar never hides with the dictation, so done and messages revert its content to idle.
        if (state is not (HUDState.Done or HUDState.Message)) return;
        var generation = hideGeneration;
        hideTimer = time.CreateTimer(_ => context.Post(_ =>
        {
            if (generation == hideGeneration && !disposed) ShowHud(new HUDState.Hidden());
        }, null), null, ReturnToIdle, Timeout.InfiniteTimeSpan);
    }

    private void OnLevel(float level)
    {
        if (levels.Count == LevelsKept) levels.RemoveAt(0);
        levels.Add(level);
        if (model.Hud is HUDState.Listening) model.Levels = levels.ToArray();
    }

    /// Settled text plus the current hypothesis, behind `showTextInHUD` — on a bar that never hides, that
    /// switch is the whole privacy story.
    private void RefreshLiveText()
    {
        if (disposed || !live.IsRunning) return;
        var settings = store.Current;
        if (!settings.LiveTranscription || !settings.ShowTextInHUD)
        {
            model.LiveText = null;
            return;
        }
        var combined = (live.ConfirmedText + " " + live.UnconfirmedText).Trim();
        model.LiveText = combined.Length == 0 ? null : combined;
    }

    // MARK: - Microphone

    private async Task RecheckMicrophoneAsync()
    {
        try
        {
            var state = await Task.Run(MicrophoneAccess.Check);
            if (!disposed) model.Microphone = state;
        }
        catch (Exception e)
        {
            Log.Failure(Category, "check the microphone", e);
        }
    }

    // MARK: - Model

    /// Mac `prepareModel` and `reloadModel` in one: the file picked in settings is downloaded when missing (the
    /// Mac downloads on first launch), then loaded. A request made while one runs is folded into one more run.
    private async Task LoadModelAsync(bool reload)
    {
        if (modelBusy)
        {
            modelAgain = true;
            return;
        }
        modelBusy = true;
        try
        {
            var asReload = reload;
            do
            {
                modelAgain = false;
                await LoadSelectedModelAsync(asReload);
                asReload = true;
            }
            while (modelAgain && !disposed);
        }
        finally
        {
            modelBusy = false;
        }
    }

    private async Task LoadSelectedModelAsync(bool reload)
    {
        if (disposed) return;
        // Unloading the model under a dictation in flight disposes the instance its stream and tail use, and the
        // dictation ends as "Nothing heard". Wait for it; the machine is not asked anything until then.
        if (reload && (capture.IsCapturing || machine.Queue.Count > 0))
        {
            Log.Info(Category, "model change waits for the dictation in flight");
            while (!disposed && (capture.IsCapturing || machine.Queue.Count > 0)) await Task.Delay(ReloadPollInterval);
            if (disposed) return;
        }
        var file = store.Current.ModelFile;
        // H2/H3: the model being loaded, which the Model page keeps from being deleted.
        model.ActiveModelLabel = AppModel.LabelFor(file);
        refiner.AsrModel = ModelCatalog.AsrModelFor(file);
        if (reload) Send(new MachineEvent.ModelProgress(0));
        if (transcriber is { } previous)
        {
            transcriber = null;
            await DisposeTranscriberAsync(previous);
        }

        if (!downloader.IsDownloaded(file))
        {
            model.ModelDownloaded = false;
            if (!await DownloadAsync(file, feedsMachine: true) || disposed) return;
        }

        var next = new WhisperTranscriber(downloader, file);
        transcriber = next;
        model.ModelStatus = $"{Strings.ModelLoading} 0 %";
        try
        {
            await Task.Run(() => next.PrepareAsync(p => context.Post(_ => OnPrepareProgress(next, p), null)));
            if (disposed || !ReferenceEquals(transcriber, next)) return;
            model.ModelStatus = Strings.ModelReady;
            Log.Info(Category, $"model ready: {next.AsrModel}");
            Send(new MachineEvent.ModelReady());
        }
        catch (Exception e)
        {
            // The machine stays in model-loading until a reload succeeds; Settings' Reload model is the way out.
            Log.Failure(Category, "prepare the model", e);
            if (disposed || !ReferenceEquals(transcriber, next)) return;
            model.ModelStatus = Strings.ModelError(e.Message);
            ShowHud(new HUDState.Message(model.ModelStatus));
        }
    }

    private void OnPrepareProgress(WhisperTranscriber loading, double progress)
    {
        // Posted, so it can arrive after `.modelReady`; sending it then would put the machine back in loading.
        if (disposed || !ReferenceEquals(transcriber, loading) || loading.IsReady || machine.Phase is DictationPhase.Ready) return;
        model.ModelStatus = $"{Strings.ModelLoading} {(int)(progress * 100)} %";
        Send(new MachineEvent.ModelProgress(progress));
    }

    /// False when the download failed (shown) or another is already running. `feedsMachine` puts the progress on
    /// the bar, for the download the dictation is waiting on.
    private async Task<bool> DownloadAsync(string file, bool feedsMachine)
    {
        if (downloading) return false;
        downloading = true;
        var generation = ++downloadGeneration;
        var reported = -1;
        model.ModelDownloadProgress = 0;
        model.ModelStatus = Strings.ModelDownloading(0);
        var clock = Stopwatch.StartNew();
        Log.Info(Category, $"downloading {file}");
        try
        {
            await downloader.DownloadAsync(file, p =>
            {
                // Called on the download's thread; one post per whole percent.
                var percent = (int)(p * 100);
                if (Interlocked.Exchange(ref reported, percent) == percent) return;
                context.Post(_ => OnDownloadProgress(generation, p, feedsMachine), null);
            }, shutdown.Token);
            Log.Info(Category, $"downloaded {file} in {clock.ElapsedMilliseconds} ms");
            return true;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception e)
        {
            Log.Failure(Category, "download the model", e);
            if (disposed) return false;
            model.ModelStatus = e is InvalidDataException ? Strings.ModelChecksumMismatch : Strings.ModelDownloadFailed(e.Message);
            ShowHud(new HUDState.Message(model.ModelStatus));
            return false;
        }
        finally
        {
            downloading = false;
            downloadGeneration++;   // retires progress still queued behind the result
            if (!disposed)
            {
                model.ModelDownloadProgress = null;
                model.ModelDownloaded = downloader.IsDownloaded(store.Current.ModelFile);
            }
        }
    }

    private void OnDownloadProgress(int generation, double progress, bool feedsMachine)
    {
        if (disposed || generation != downloadGeneration) return;
        model.ModelDownloadProgress = progress;
        model.ModelStatus = Strings.ModelDownloading((int)(progress * 100));
        if (feedsMachine && machine.Phase is DictationPhase.ModelLoading) Send(new MachineEvent.ModelProgress(progress));
    }

    private async Task DownloadModelAsync(string file)
    {
        if (!await DownloadAsync(file, feedsMachine: false)) return;
        // Mac D5: Download picks up whatever the picker selected.
        if (file == store.Current.ModelFile) await LoadModelAsync(reload: true);
    }

    private async Task ModelFileChangedAsync(string file)
    {
        var downloaded = downloader.IsDownloaded(file);
        model.ModelDownloaded = downloaded;
        // Mac H2: switch straight away only to a model that is already here; otherwise Download does it.
        var alreadyLoaded = transcriber is { IsReady: true } loaded && loaded.ModelFile == file;
        if (downloaded && !alreadyLoaded) await LoadModelAsync(reload: true);
    }

    /// Throws for the page to show. The page already disables Delete for the loaded model (Mac H3); this is
    /// the same rule where it cannot be bypassed.
    private void DeleteModel(string file)
    {
        if (transcriber is { IsReady: true } loaded && loaded.ModelFile == file)
            throw new InvalidOperationException("it is the model in use");
        downloader.Delete(file);
        model.ModelDownloaded = downloader.IsDownloaded(store.Current.ModelFile);
        Log.Info(Category, $"deleted {file}");
    }

    private static async Task DisposeTranscriberAsync(WhisperTranscriber loaded)
    {
        try
        {
            await loaded.DisposeAsync();
        }
        catch (Exception e)
        {
            Log.Failure(Category, "unload the model", e);
        }
    }

    // MARK: - Server

    /// Sync at launch and every `SyncService.IntervalSeconds`, driven from here rather than `SyncService.Start`
    /// so the Set-up window's "Checking…" has an end even when the request fails.
    private async Task StartSyncAsync()
    {
        try
        {
            var hasToken = await Task.Run(() => tokens.Read(serverUrl) is not null);
            if (disposed) return;
            model.HasToken = hasToken;
        }
        catch (Exception e)
        {
            Log.Failure(Category, "read the token", e);
        }
        await RunSyncAsync();
        if (disposed) return;
        var period = TimeSpan.FromSeconds(SyncService.IntervalSeconds);
        syncTimer = time.CreateTimer(_ => context.Post(_ => _ = RunSyncAsync(), null), null, period, period);
    }

    private async Task RunSyncAsync()
    {
        if (disposed) return;
        syncsRunning++;
        if (model.HasToken) model.TokenChecking = true;
        try
        {
            await sync.SyncAsync();
        }
        finally
        {
            if (--syncsRunning == 0) model.TokenChecking = false;
        }
        if (!disposed) PublishSync();
    }

    private void OnSyncChanged(object? sender, EventArgs e) => context.Post(_ =>
    {
        if (!disposed) PublishSync();
    }, null);

    private void PublishSync()
    {
        model.Unauthorized = sync.Unauthorized;
        model.UserName = sync.UserName;
        // Build spec §4: the tray's status line says what is wrong, not "Ready", while the token is refused.
        model.StatusLine = sync.Unauthorized ? Strings.TokenInvalid : Strings.IdleFor(model.Hotkey.Label());
    }

    /// Only Save and Sign out write the credential (rule 44).
    private async Task SaveTokenAsync(string token)
    {
        // The URL in Settings now, as the Mac saves under `Preferences.serverURL`: after a URL change the token
        // must be where the relaunched app will look for it.
        tokens.Save(store.Current.ServerURL, token);
        // A /v1/me still carrying the old token must not mark this one invalid when it lands.
        sync.TokenChanged();
        model.HasToken = true;
        await RunSyncAsync();
    }

    private Task SignOutAsync()
    {
        tokens.Delete(store.Current.ServerURL);
        sync.SignOut();
        model.HasToken = false;
        PublishSync();
        return Task.CompletedTask;
    }

    private void OnTimeChanged(object? sender, EventArgs e) =>
        // A time-zone change must re-bucket Insights days on the next refresh (rule 43).
        TimeZoneInfo.ClearCachedData();

    private static Uri ServerUri(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)) return uri;
        Log.Error(Category, "the server URL in settings.json is not an http(s) URL; using the default server");
        return new Uri(Settings.Defaults.ServerURL);
    }

    // MARK: - Quit

    private async Task QuitAsync()
    {
        if (quitting) return;
        quitting = true;
        Log.Info(Category, "quit");
        // The user's own clipboard comes back before the window that restores it is destroyed.
        await Task.WhenAny(injector.WaitForRestoreAsync(), Task.Delay(QuitRestoreWait));
        var loaded = transcriber;
        transcriber = null;
        Dispose();
        if (loaded is not null) await Task.WhenAny(DisposeTranscriberAsync(loaded), Task.Delay(QuitTranscriberWait));
        shell.Dispose();
        Application.Current?.Shutdown();
    }

    private static Task Done(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    private static void Quietly(string what, Action dispose)
    {
        try
        {
            dispose();
        }
        catch (Exception e)
        {
            Log.Failure(Category, $"dispose {what}", e);
        }
    }
}
