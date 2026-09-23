namespace Spit.Core;

/// `GET /v1/me` on launch and every 10 minutes. Server settings win; the client writes through PUT.
/// Port of mac/Voice/Refine/SyncService.swift, with the Windows hotkey rule (rule 46):
///
/// - the server's `hotkey` is the Mac's (`fn` | `rightOption` | `rightCommand`) and is never applied
///   here — there is no hotkey callback and `ILocalSettings.Hotkey` is never written;
/// - a PUT sends `hotkey` and `llmModel` exactly as the server holds them (the Mac's G3 merge, onto a
///   `/v1/me` read just before the PUT), so changing Mode on the PC cannot reset the Mac's key;
/// - with no successful `/v1/me` this process lifetime there is nothing to echo, so no PUT is sent at
///   all. The next successful sync's server values win, as on the Mac when its PUT fails.
public sealed class SyncService : IDisposable
{
    /// `GET /v1/me` period (rule 22).
    public const int IntervalSeconds = 600;

    private readonly IVoiceApiClient _api;
    private readonly ILocalSettings _settings;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private ITimer? _timer;

    /// The last settings object received from `/v1/me` (or successfully sent back). Null until the first
    /// successful sync, which is what gates the PUT.
    private ServerSettings? _last;

    /// Bumped by `SignOut` and `TokenChanged`: a `/v1/me` sent with the previous credential must not decide the state
    /// of the new one — a late 200 would put a signed-out name back, a late 401 would call a fresh token invalid.
    private int _credentialChanges;

    public SyncService(IVoiceApiClient api, ILocalSettings settings, TimeProvider? timeProvider = null)
    {
        _api = api;
        _settings = settings;
        _time = timeProvider ?? TimeProvider.System;
    }

    public string? UserName { get; private set; }
    public bool Unauthorized { get; private set; }

    /// Raised when `UserName` or `Unauthorized` may have changed. On the thread that finished the sync.
    public event EventHandler? Changed;

    /// Receives the dictionary (`replacement ?? term`) on every successful sync — the Mac writes
    /// `DictionaryCache.shared`; the app persists it to `dictionary.json` (terms only, rule 25).
    public Action<IReadOnlyList<string>>? OnDictionaryChange { get; set; }

    public void Start()
    {
        _ = SyncAsync();
        var period = TimeSpan.FromSeconds(IntervalSeconds);
        _timer = _time.CreateTimer(_ => _ = SyncAsync(), null, period, period);
    }

    public async Task SyncAsync()
    {
        int credentialChanges;
        lock (_gate) credentialChanges = _credentialChanges;
        try
        {
            var me = await _api.MeAsync().ConfigureAwait(false);
            lock (_gate)
            {
                if (credentialChanges != _credentialChanges) return;
                UserName = me.User.Name;
                Unauthorized = false;
                _last = me.Settings;
            }
            _settings.Mode = me.Settings.Mode;
            _settings.Language = me.Settings.Language;
            // Rule 46: me.Settings.Hotkey is deliberately not applied.
            OnDictionaryChange?.Invoke(me.Dictionary.Select(t => t.Replacement ?? t.Term).ToArray());
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (ApiException e) when (e.Kind == ApiErrorKind.Unauthorized)
        {
            lock (_gate)
            {
                if (credentialChanges != _credentialChanges) return;
                Unauthorized = true;
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // Offline or server trouble: keep the cached settings; the next tick tries again.
        }
    }

    /// A user-initiated Mode or Language change. The local write always happens; the PUT only when a
    /// `/v1/me` has succeeded this launch and something actually changed.
    ///
    /// The PUT replaces the whole record, `hotkey` included, so the record is read again just before it. The copy
    /// from the last sync can be ten minutes old — long enough for the Mac to have changed its key — and echoing it
    /// would put the old key back (checklist item 12). If that read fails, nothing is sent: the same outcome as a
    /// failed PUT, where the next successful sync's values win.
    public async Task PushAsync(string? mode = null, string? language = null)
    {
        if (mode is not null) _settings.Mode = mode;
        if (language is not null) _settings.Language = language;

        int credentialChanges;
        lock (_gate)
        {
            // Rule 46: nothing received, nothing to echo — never invent a hotkey or clear an llmModel.
            if (_last is null) return;
            // No-op guard, before any network: kills the echo when a sync just wrote this same value into the
            // settings UI.
            if (_last with { Mode = mode ?? _last.Mode, Language = language ?? _last.Language } == _last) return;
            credentialChanges = _credentialChanges;
        }

        ServerSettings current;
        try
        {
            current = (await _api.MeAsync().ConfigureAwait(false)).Settings;
        }
        catch (Exception)
        {
            return;
        }

        ServerSettings merged;
        lock (_gate)
        {
            if (credentialChanges != _credentialChanges) return;
            _last = current;
            // G3: merge onto the server's settings as they are now, so the fields not being changed — `hotkey`,
            // `llmModel` — go back exactly as they stand.
            merged = current with { Mode = mode ?? current.Mode, Language = language ?? current.Language };
            if (merged == current) return;
        }

        try
        {
            await _api.PutSettingsAsync(merged).ConfigureAwait(false);
            lock (_gate) _last = merged;
        }
        catch (Exception)
        {
            // The next successful sync's server values win.
        }
    }

    /// The Server page's Sign out. The page deletes the token itself (only Save and Sign out write the
    /// credential, rule 44); this only drops the signed-in state. The timer keeps running; the next sync
    /// will 401 and stay unauthorized until a new token is saved.
    public void SignOut()
    {
        lock (_gate)
        {
            _credentialChanges++;
            Unauthorized = true;
            UserName = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// A token was saved: results of requests sent before now no longer describe it.
    public void TokenChanged()
    {
        lock (_gate) _credentialChanges++;
    }

    public void Dispose() => _timer?.Dispose();
}
