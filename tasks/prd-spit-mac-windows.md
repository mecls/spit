# Spit for Mac and Windows — Implementation Spec

## 1. Objective

Today this app reaches only people who can build it from source or be handed a zip plus an
`xattr -d com.apple.quarantine` incantation (README, "Install for teammates"). It is macOS-only, and
nothing about it is downloadable. Until 2026-09-12 it also installed as `Voice.app`; rules 1, 3 and 4
(the Mac app's visible name is Spit) are already implemented.

This spec makes **Spit** installable by friends from a web page, on both platforms, released together:

- **macOS:** `Spit.dmg`, built by `mac/scripts/package.sh`, signed with the existing Apple Development
  identity and not notarised.
- **Windows:** `Spit-Setup.exe` for a **new Windows client** with the same features as the Mac app.
  The Mac client cannot be packaged for Windows: its hotkey (CGEventTap), speech engine (WhisperKit on
  CoreML), bar (NSPanel) and paste (⌘V through CGEvent) are all Apple-only. The Windows client is a
  second implementation of the same rules, talking to the same server.
- **Web:** a new `/spit` page on the Sintra Labs site with both downloads and honest install steps.
- **Access:** unchanged. Tokens are issued by hand on the VPS.

The server API needs no changes (rule 45).

Decisions taken on 2026-09-12:

| Question | Decision |
|---|---|
| Sequencing | Mac and Windows ship **together**; the page goes live when both binaries work |
| Windows scope | **Full parity** with the Mac app |
| Name | **Spit** everywhere a user reads it; internal identifiers (`co.miraside.voice`) unchanged |
| Web | A separate **`/spit` page**; the site's flagship section is not touched |
| Access | **Hand-issued tokens**, per friend, per device |
| Test hardware | Miguel has a Windows PC for the manual checklist |

Windows facts below were checked against primary sources on 2026-09-12 (Microsoft Learn, NuGet, project
docs). Where no primary source exists, the rule names the spike in §5 that settles it.

## 2. Business rules (invariants — never violate)

### A. Name and identity

1. **Everything a user reads says "Spit".** On the Mac: `CFBundleDisplayName`, `PRODUCT_NAME` (so the
   bundle is `Spit.app`), `NSMicrophoneUsageDescription` ("Spit records your speech…"), and every
   string in `Strings.swift` that says "Voice" (`appName`, `quit`, `onboardingTitle`, `insightsEmpty`,
   `serverURLChangeNote`). Also the DMG volume name and `README.md`'s install steps (`Spit.app`, `Spit-<version>.zip`).
   On Windows: every window title,
   the tray tooltip, the Start-menu and desktop shortcuts, and the installer.

2. **The GitHub repo stays `mecls/spit`.** Every download URL in this spec uses it. The product name and
   the repo name already match, so no rename is needed.

3. **Internal identifiers do not change on the Mac.** Bundle id `co.miraside.voice`, Keychain service
   `co.miraside.voice`, the `~/Library/Application Support/Voice/` directory (models, `dictionary.json`,
   `insights-cache.json`), the UserDefaults domain and the `Logger` subsystem all stay as they are.
   An existing install depends on every one of them: TCC grants are keyed on bundle id + team, the token
   sits under the Keychain service, and the model folder is a 630 MB download. Renaming any of them
   silently logs the user out, re-prompts three permissions, or re-downloads the model.

4. **The product is renamed through XcodeGen's `productName`, and the module stays `Voice`.** Two
   settings in `mac/project.yml`, both required:
   - `productName: Spit` on the `Voice` target. XcodeGen derives the product file reference, the scheme's
     buildable name and the test bundle's `TEST_HOST` from it. Setting only the `PRODUCT_NAME` build
     setting leaves `TEST_HOST` pointing at `Voice.app/Contents/MacOS/Voice`, which no longer exists, so
     the tests cannot launch.
   - `PRODUCT_MODULE_NAME: Voice`. It defaults to the product name, so without it the Swift module would
     become `Spit` and break `@testable import Voice` across `mac/VoiceTests`.

   The XcodeGen target, the scheme and `Voice.xcodeproj` keep their names.

5. **Windows keeps its data outside the install directory.** Velopack installs per user to
   `%LocalAppData%\{packId}` and replaces that directory on update. With packId `Spit`, the app lives
   in `%LocalAppData%\Spit\`, so everything the app *writes* lives in
   `%LOCALAPPDATA%\Miraside\Spit\` (`models\`, `dictionary.json`, `insights-cache.json`,
   `settings.json`, `logs\`). An update or uninstall must never be able to delete a 574 MB model or the
   user's settings.

### B. Versions and releases

6. **One version number, both platforms, one tag.** A root `VERSION` file holds `X.Y.Z`. `package.sh`
   writes it into `MARKETING_VERSION` and `CFBundleShortVersionString`. Today `0.1.0` is hard-coded in
   **both** `mac/project.yml` and `mac/Voice/Info.plist`, and they can drift. The Windows `.csproj`
   reads the same file as `<Version>`. CI fails when the pushed tag `vX.Y.Z` differs from `VERSION`.
   Both clients then send the same `clientVersion` for a release.

7. **The first public release is `0.2.0`.** Colleagues already have `Voice-0.1.0.zip` files. Reusing the
   number would give two different artefacts the same version.

8. **A release is one GitHub Release on `mecls/spit` with exactly three assets:** `Spit.dmg`,
   `Spit-Setup.exe` and `SHA256SUMS.txt`. Asset names carry **no version**. The page links to
   `https://github.com/mecls/spit/releases/latest/download/<name>` (GitHub's documented pattern), and
   that URL only works if every release uses the same names. Velopack names its installer
   `{packId}-Setup.exe`, so packId `Spit` produces the right name with no renaming step. Velopack's
   other outputs (`*-Portable.zip`, `*.nupkg`, `releases.*.json`, `assets.*.json`, `RELEASES`) are
   **not** attached, because there is no auto-update (rule 44).

9. **A release is created as a draft and published only when both binaries are attached and both manual
   checklists (§5) have passed.** "Latest" is "the most recent non-prerelease, non-draft release" (GitHub
   REST docs), so a draft is never served. A release carrying only one platform's asset is never
   published: the page's other button would point at the previous release. That is decision 1A made
   mechanical.

10. **The DMG is built on Miguel's Mac; the Setup.exe is built by GitHub Actions.** The Apple Development
    identity lives in Miguel's login keychain and is not exported to CI. On a `v*` tag push,
    `.github/workflows/release-windows.yml` runs on `windows-latest` with `permissions: contents: write`.
    It checks the tag, runs `dotnet test`, runs `vpk pack`, creates the draft release if missing, and
    uploads `Spit-Setup.exe` only. `package.sh --release` uploads `Spit.dmg` with
    `gh release upload vX.Y.Z build/Spit.dmg --clobber` and regenerates `SHA256SUMS.txt` from both
    assets.

11. **Nothing secret enters a build; the repo is public.** The workflow uses only `GITHUB_TOKEN`. The
    server URL (`https://voice.miraside.co`) is already public in `Preferences.swift`. `GO_LIVE.md` and
    `deploy/VPS.md` stay gitignored.

### C. macOS DMG

12. **The DMG is `Spit.app` plus an `Applications` symlink, built with `hdiutil`:**
    `hdiutil create -volname Spit -srcfolder <staging> -ov -format UDZO build/Spit.dmg`.
    No `create-dmg` dependency: it isn't installed, and `hdiutil` ships with macOS.

13. **A release build refuses to ship an ad-hoc signature.** Today `package.sh` only warns when no Apple
    Development identity is found, then signs ad-hoc. With `--release` it must exit non-zero, run
    `codesign --verify --deep --strict`, and require `TeamIdentifier=FZC6P6XRGD`. An ad-hoc signature
    changes on every build, so every friend would lose Microphone, Input Monitoring and Accessibility on
    every update. They would see no error, only a hotkey that stops working.

14. **Apple Silicon only.** Release builds set `ARCHS: arm64`. WhisperKit's audio encoder runs on the
    Neural Engine. On Intel it would fall back to the CPU at an unmeasured speed, and nobody has an Intel
    Mac to test on. A clear "Apple Silicon required" is better than a 30-second transcription nobody
    warned about.

15. **Not notarised, and the page describes exactly what happens.** On macOS 15+, Control-click › Open no
    longer bypasses Gatekeeper. The page's steps are:
    1. Open Spit. The "Apple could not verify…" dialog appears.
    2. Click Done.
    3. Go to System Settings › Privacy & Security and click **Open Anyway**.
    4. Confirm.

    No other workaround (`xattr`, Terminal) appears on the page.

16. **Updating on macOS means dragging the new `Spit.app` over the old one.** The signing identity is
    constant (rule 13), so permission grants survive. Colleagues coming from `Voice.app` are told to quit
    Voice and delete `Voice.app` first, because two bundles with the same id confuse Launch Services.
    Their token, model and settings carry over (rule 3). They must switch *Launch at login* on again,
    because `SMAppService.mainApp` registered the old bundle path.

### D. The `/spit` page

17. **The page is `site/spit/index.html`, served at `/spit`.** It reuses `site/styles.css` (tokens
    such as `--azul`, `--branco` and `--tinta`, the grounds `.ground-light` and `.ground-blue`, and the
    `.shell` / `.section` layout). No framework, no build step, no new fonts, per `site/README.md`.

18. **Every claim on the page is true of the shipped build.** The page must say:
    - Speech is recognised **on the device**, and audio never leaves it.
    - The recognised **text is sent to Miguel's server** for cleanup, and **stored there** (history and
      Insights). This sentence is mandatory: the flagship section of the same site says "no server, no
      network call" about another product, and a friend could reasonably assume it applies here too.
    - You need an **access token from Miguel**. There is no sign-up.
    - Requirements: macOS 15+ on Apple Silicon; Windows 10 or 11 on x64.
    - First launch downloads the speech model: ~630 MB on Mac, 574 MB on Windows.
    - Both apps are unsigned. The page shows the exact warning each OS displays and the exact clicks
      past it. The Windows wording is copied from screenshots taken during the Windows checklist, not
      from memory, because Microsoft doesn't document the exact SmartScreen text.
    - **Smart App Control.** On Windows 11 with Smart App Control on, Spit cannot run at all, and
      there is no per-app exception. The only route is turning Smart App Control off (open question 2).
    - **Admin windows.** Spit's hotkey and paste don't work while an app running as administrator is
      focused (rule 30).

19. **Download buttons use the `latest/download` URLs, and the page shows no version number.** A version
    string on a static page is a line someone forgets to edit. Buttons are plain `<a href>` links that
    work with JavaScript off. JS may *highlight* the button for the visitor's OS but never hides the
    other. `SHA256SUMS.txt` is linked beside them.

### E. Access

20. **One token per device, not per person.** For a friend's Mac:
    `node dist/cli/users.js add "<Name>" --label mac`. For the same friend's PC:
    `node dist/cli/users.js token <userId> --label pc`. `device_tokens` already supports this, and it
    means a lost laptop is revoked (`users revoke <tokenId>`) without logging out the other machine.

21. **No auth or rate-limit changes.** 120 requests/min per token and 30 failed auths/min per IP stay as
    they are (`docs/API.md`).

### F. Windows client — the parity contract

22. **Parity means the same rules with the same constants, proven by the same tests.** Two
    implementations of one set of rules drift apart, and the only defence is shared test cases. Every
    constant below has the same value on both clients. **A change to any of them on one client is made on
    the other in the same PR.**

    | Rule | Mac source | Value |
    |---|---|---|
    | Shortest dictation / tap threshold | `DictationMachine.minimumMs` (TapLatch references it) | 400 ms |
    | Double-tap window | `TapLatch.windowMs` | 300 ms, compared in whole ms |
    | Capture format | `AudioRecorder.sampleRate` | 16 kHz mono Float32 |
    | Recording cap | `AudioRecorder.maxSeconds` | 90 s (ring buffer of 1,440,000 samples) |
    | Keep capture warm after a dictation | `AudioRecorder.scheduleIdleStop` | 20 s |
    | Speech gate | `EnergyGate.hasSpeech` | 20 ms frames, p95 RMS ≥ 0.01 |
    | Stream tail pass | `Coordinator.minimumTailMs` / `overlapMs` | 400 ms / 1500 ms |
    | Stitch anchor | `Stitch.anchorWords` / `minimumAnchor` | 5 / 3 words; last occurrence wins |
    | Streaming confirmation | WhisperKit `AudioStreamTranscriber` | all but the last 2 segments; run a pass only when > 1 s of new audio; poll every 100 ms; final text = confirmed + unconfirmed |
    | Streaming VAD | `VoiceAudioProcessor.speechFloor`, `silenceThreshold` | energy = min(1, RMS × 30); below 0.3 is silence |
    | Skip gate | `SkipGate` | ≤ 12 words; the same filler, command and punctuation lists |
    | Refine budget | `Budget.ms` | clamp(2500 + 12 × rawChars, 3500, 15000) ms |
    | Outbox | `Outbox.cap` | memory only, 200 entries, oldest dropped |
    | Sync | `SyncService` | `GET /v1/me` at launch and every 600 s |
    | Pre-warm | `Coordinator.prewarmConnection` | `HEAD /health` at most once per 60 s, only after the mic opens, 3 s timeout |
    | Request timeout | `VoiceAPI` | 10 s; for `/v1/refine`, the budget |
    | Bar returns to idle | `Coordinator.scheduleReturnToIdle` | 1.2 s after done or a message |
    | Paste timing | `TextInjector` | keystroke 30 ms after the clipboard write; give up at 1.5 s (restore signal: rule 32) |
    | Clipboard snapshot | `PasteboardSnapshot.maxBytes` | 5 MB per format |
    | Whisper decode | `WhisperKitTranscriber` | temperature 0, no fallback; language hint or detection; dictionary terms as the prompt (≤ 150 tokens); an empty result with a prompt gets one retry without it; > 30 s is chunked with progress; warm up with 1 s of silence after load |
    | Heatmap | `HeatmapGrid.level`, `InsightsModel.weeks` | 0 / 1–2 / 3–5 / 6+; 21 weeks |

    The Windows test project ports **every test** in these Mac files and keeps the test names:
    `DictationMachineTests`, `TapLatchTests`, `HotkeyInterpreterTests` (keys adapted),
    `SkipGateTests`, `StitchTests`, `StreamTailTests`, `BudgetTests`, `EnergyGateTests`, `OutboxTests`,
    `RefineServiceTests`, `SyncServiceTests`, `InsightsTests`, `TextInjectorRoutingTests` (the
    secure-input cases become own-process and elevated-target cases) and `RingBufferTests`. That is 108
    tests by `grep -c "func test"` today. The Windows count for these files must equal the Mac count.

23. **Pure logic lives in a project with no Windows dependency.** The layout:
    - `windows/Spit.Core` (`net10.0`): the reducer, gesture rules, gates, stitching, API client, sync,
      outbox and Insights presentation.
    - `windows/Spit.App` (`net10.0-windows`, WPF): hooks, audio, clipboard, speech engine and UI.
    - `windows/Spit.Core.Tests`: the ported tests.

    Miguel develops on a Mac, so `dotnet test windows/Spit.Core.Tests` must run there; only
    `Spit.App` needs the PC. .NET 10 is the current LTS (support ends 14 Nov 2028). .NET 8 and 9 both
    end on 10 Nov 2026, and NAudio 3.x requires .NET 9 or later.

24. **Pinned dependencies** (versions current on 2026-09-12, all MIT):

    | Package | Version | Purpose |
    |---|---|---|
    | `Whisper.net` + `Whisper.net.Runtime`, `.Runtime.NoAvx`, `.Runtime.Vulkan` | 1.9.1 | Speech engine |
    | `NAudio` | 3.1.0 | Capture (WASAPI) and `WdlResamplingSampleProvider` to 16 kHz |
    | `H.NotifyIcon.Wpf` | 2.4.1 | Tray icon |
    | `Velopack` | 1.2.0 | Installer |
    | `Meziantou.Framework.Win32.CredentialManager` | 3.0.4 | Token storage |

    The CUDA runtimes are deliberately left out. They need a CUDA toolkit (≥ 13.0.1 or ≥ 12.4.1) on the
    user's machine, which friends won't have. Vulkan uses the ordinary GPU driver. Whisper.net falls back
    automatically, in order, and stops at the first runtime that loads.

25. **Nothing the user said is ever written to disk, the same promise as the Mac.** No temporary WAV
    files. No transcript text in logs, only lengths and timings (as `StreamingTranscriber` logs). The
    outbox stays in memory, `dictionary.json` holds terms only, and `insights-cache.json` holds numbers
    only. Pasted text carries the registered clipboard format `ExcludeClipboardContentFromMonitorProcessing`,
    so it stays out of both clipboard history and cloud clipboard. That is the Windows equivalent of the
    Mac's `.currentHostOnly` plus `org.nspasteboard.TransientType` / `ConcealedType`.

26. **Window titles are never sent or logged.** A title can contain an email subject or a document name.
    App identity comes only from the executable (rule 31).

### G. Windows client — where Windows differs

27. **Hotkey: Right Ctrl by default, Right Alt as the alternative.** There is no primary source on Fn,
    but keyboards handle it in firmware and Windows never receives it, so the Mac's default doesn't exist
    there. Choices are `rightCtrl` (default) and `rightAlt`. A right-hand key counts as Right Ctrl when
    the hook reports `VK_RCONTROL`, or `VK_CONTROL` with `LLKHF_EXTENDED` set; spike S4 confirms which.
    The `HotkeyInterpreter` rules apply unchanged: any other key pressed while the hotkey is held
    cancels (the shortcut rule), Esc cancels, and a latched session ignores every key except Esc.

28. **Three kinds of key event are not "another key".** Each of these would otherwise cancel a
    dictation that is working:
    - **Autorepeat of the held hotkey.** The hook receives repeated key-downs while a key is held; the
      Mac's `flagsChanged` has no autorepeat.
    - **The fake Left Ctrl that AltGr produces** (scan code `0x21D`, AutoHotkey's `SC_FAKE_LCTRL`). On
      layouts with AltGr, including pt-PT, Right Alt sends Left Ctrl + Right Alt.
    - **Injected events (`LLKHF_INJECTED`)**, including Spit's own Ctrl+V.

29. **The keyboard hook listens and never swallows.** Every callback returns `CallNextHookEx`, matching
    the Mac's `.listenOnly` tap. Swallowing Right Alt would also break @, €, [ and { on Portuguese
    keyboards. The callback only posts the event to the UI thread and returns. If it takes longer than
    `LowLevelHooksTimeout` (1000 ms at most since Windows 10 1709), "the hook is silently removed … There
    is no way for the application to know". The hook therefore runs on a dedicated thread with its own
    message loop. Because removal can't be detected, it is **re-installed** on resume from sleep, on
    session unlock, and every 5 minutes while idle (no key held, not recording, not latched). Never
    re-install mid-gesture, which would orphan a held key.

30. **Admin windows are a documented limitation, not a bug to work around.** While an elevated window has
    focus, a non-elevated low-level hook sees no keys: only UIAccess processes can hook all integrity
    levels, and UIAccess needs a signed app installed under Program Files. `SendInput` into an elevated
    window "fails … neither GetLastError nor the return value will indicate" it. So:
    - The hotkey can't start a dictation from an admin window. The bar's mic button still can.
    - If the foreground window at paste time is elevated, the text goes clipboard-only with the message
      "Admin window — text copied to clipboard". Spike S3 settles how elevation is detected. If it
      can't be determined, access-denied counts as elevated, because a silent failed paste followed by
      a clipboard restore loses the dictation.
    - The page states this limitation (rule 18).

31. **App identity for `context` comes from the executable and its description.** `appBundleId` is the
    foreground process's executable file name, lowercased (`chrome.exe`), read with
    `QueryFullProcessImageName` using `PROCESS_QUERY_LIMITED_INFORMATION`. `appName` is that file's
    `FileDescription` (`Google Chrome`), falling back to the file name without its extension. Both are
    captured at hotkey-down, like `FrontmostContext.current()`. The server labels the Insights Apps card
    by `appName`, so "Google Chrome" from a Mac and from a PC merge into one bar. For UWP apps the
    foreground process is `ApplicationFrameHost.exe`; resolve the hosted app through its child window,
    and if that fails send `appName: "Windows app"`, never the window title (rule 26).

32. **Clipboard restore errs late, never early.** Restoring too early is the worst failure: Ctrl+V then
    pastes the user's *old* clipboard into their document. Restoring late only leaves the dictation on the
    clipboard a moment longer. The Mac restores 100 ms after the target reads its lazy data provider.
    Windows has an analogue, delayed rendering (`SetClipboardData(fmt, NULL)` → `WM_RENDERFORMAT`), but
    the first reader may be Clipboard History or another clipboard manager rather than the target. Microsoft
    also advises placing text of 4 KiB or less directly. Therefore:
    - **Default:** place the text directly and restore at the 1.5 s ceiling.
    - **Read-triggered restore (100 ms after the target's read):** adopt it only if spike S2 shows the
      first `WM_RENDERFORMAT` always comes from the target when the content carries
      `ExcludeClipboardContentFromMonitorProcessing`, with Clipboard History on.

33. **The clipboard snapshot copies memory formats only.** The allowed formats are `CF_UNICODETEXT`,
    `CF_DIBV5` / `CF_DIB`, `CF_HDROP`, and the registered formats `HTML Format`, `Rich Text Format` and
    `PNG`, each ≤ 5 MB — except the two bitmap formats, ≤ 128 MB (amended 2026-09-23: a Windows screenshot is an
    uncompressed DIB, 8.3 MB at 1920×1080, and leaving it out emptied the clipboard; `prd-windows-parity.md` rule 10). GDI-handle formats (`CF_BITMAP`, `CF_ENHMETAFILE`) and formats still waiting for
    delayed rendering are left out, the same trade-off as `PasteboardSnapshot.allowedTypes`.
    `OpenClipboard` fails while another window holds the clipboard, so opening retries every 10 ms for up
    to 200 ms. If it still fails, the dictation isn't pasted: the bar says "Couldn't use the
    clipboard — use Copy last dictation" and it is reported with `injected: 'none'`. The clipboard is
    always opened with Spit's own window handle, because a NULL owner makes `SetClipboardData` fail after
    `EmptyClipboard`.

34. **Password fields paste normally on Windows.** The Mac diverts them to the clipboard because Secure
    Event Input makes a synthetic ⌘V impossible. Windows has no equivalent block, so a paste works there.
    The only available detector (UI Automation `IsPasswordProperty`) is unreliable in Chromium and
    Electron, which expose web content to UIA only when they detect assistive technology. An unreliable
    detector would divert random dictations to the clipboard. The remaining clipboard-only cases on
    Windows are: the target is Spit itself (`TextInjector.mustUseClipboard`), and the target is
    elevated (rule 30).

35. **Right Alt must not open menus.** In classic Win32 apps, pressing and releasing Alt alone activates
    the menu bar, which would then receive the paste. When `rightAlt` is the hotkey, Spit injects an
    unassigned key (`vkE8`, AutoHotkey's default menu-mask key) while Right Alt is held. Rule 28's
    injected-event filter keeps that from cancelling the dictation. Spike S4 confirms this in Notepad and
    File Explorer.

36. **One running instance.** A second launch brings the existing window forward and exits (a named mutex
    plus an activation message). On the Mac, Launch Services does this for free. On Windows, two
    instances would install two hooks and paste every dictation twice.

37. **The tray icon and windows mirror the Mac's menu bar and window.**
    - **Tray icon states** follow `menuIcon`: unauthorized → mic with a cross; latched → red record;
      listening → filled mic; otherwise → mic.
    - **Left-click** opens the main window, which has Insights and Settings (`MainWindow`).
    - **Right-click menu**, with the same items in the same order as the Mac's `MenuBarExtra`: status
      line, model status, Mode, Language, Copy last dictation, Pause/Resume, Show bar, Live transcription
      (experimental), Insights…, Set up…, Settings…, Quit Spit.
    - **Closing the main window** hides it and never quits (Mac rule 4). The taskbar button exists only
      while the window is open.

38. **The bar never activates Spit.** It is a borderless, topmost WPF window:
    - **Styles:** `WS_EX_NOACTIVATE` (a click doesn't make it the foreground window) and
      `WS_EX_TOOLWINDOW` (not on the taskbar or in Alt+Tab), with `ShowActivated=false` set before the
      first `Show()`.
    - **Size:** exactly its content, so it can't swallow a click beyond what is visible.
    - **Position** follows `HUDPanel.barOrigin`. **x** is centred on the full bounds of the monitor
      holding the foreground window. **y** sits 10 px above the bottom of that monitor's *work area*, so
      it clears the taskbar.
    - **Look and states** match `HUDView`: a resting lozenge, a 20-bar waveform while listening, red
      when latched, status text otherwise.

39. **Microphone failures say what to do.**
    - **Capture fails with `E_ACCESSDENIED`:** "Microphone blocked — Settings › Privacy & security ›
      Microphone", with a button that opens `ms-settings:privacy-microphone`.
    - **Any other start failure:** "No microphone available — check Settings › System › Sound › Input".
    - **Default capture device changes mid-dictation** (a headset plugged in): capture rebuilds on the new
      device and keeps the samples already recorded, which is the Mac's AirPods rule. Resampling to 16 kHz
      mono uses NAudio's managed `WdlResamplingSampleProvider`.

40. **Set-up replaces the Mac's permissions screen.** Windows has no Input Monitoring or Accessibility
    grants. The Set-up window has three live rows:
    - **Microphone:** granted or blocked.
    - **Hotkey:** "Hold Right Ctrl now". Goes green when the hook sees a press.
    - **Token:** "Connected as *name*".

    Done is enabled when Microphone and Hotkey are green, mirroring `OnboardingView`.

41. **Speech engine: whisper.cpp through Whisper.net. Whisper small is the Windows default** (decided 2026-09-13,
    open question 3: large-v3-turbo took 53–85 s for 12.5 s of audio on a GPU-less 4-vCPU CI runner, and most
    friends' PCs have no GPU). The Mac keeps large-v3-turbo on its Neural Engine; model-list parity is given up.
    - **Recommended:** "Small (264 MB)", `ggml-small-q8_0.bin`.
    - **More accurate, needs a fast PC:** "Large v3 Turbo (compressed, 574 MB)", `ggml-large-v3-turbo-q5_0.bin`.
    - **Maximum accuracy:** "Large v3 Turbo (full, 1.6 GB)", `ggml-large-v3-turbo.bin`.
    - **Download:** from `https://huggingface.co/ggerganov/whisper.cpp/resolve/main/<file>` to a
      `.partial` file, checked against a SHA-256 pinned in code, then renamed. Only a file with its final
      name is ever loaded, the Windows form of `ModelManager.requiredEntries`.
    - **Prompt:** dictionary terms go through `WithPrompt()`, which Whisper.net marks experimental. The
      retry-without-prompt rule (22) already covers a prompt that breaks decoding.
    - **`asrModel`** is sent as `whisper.cpp/<file name without .bin>` (≤ 80 chars). Windows dictations
      then form a separate population from the Mac's `large-v3-v20240930_turbo_632MB` when latency is read
      back.

42. **Only one inference runs on a model instance at a time.** Live transcription re-implements
    WhisperKit's streaming loop (rule 22), because whisper.cpp has none. When the key comes up, any
    in-flight stream pass finishes before the tail pass starts; the two never overlap on the same instance.

43. **Time zone is sent as IANA, never as a Windows id.** `GET /v1/insights?tz=` rejects
    `GMT Standard Time` with a `400`. Convert with `TimeZoneInfo.TryConvertWindowsIdToIanaId`. If
    conversion fails, don't call the endpoint; show "Couldn't determine your time zone". Never fall back to
    `UTC`, which produces a plausible wrong streak (the server's own reasoning in `docs/API.md`).

44. **Installing and starting up.**
    - **Installer:** Velopack's `Spit-Setup.exe` installs per user with no admin prompt, creates Start
      Menu and desktop shortcuts, and launches the app.
    - **Updates:** no auto-update. Velopack's `UpdateManager` is not used, because the Mac has no updater
      either. Updating means running a newer `Spit-Setup.exe`; spike S5 proves it upgrades in place.
    - **Startup code:** `VelopackApp.Build().Run()` is the first line of a hand-written `Main`, with
      `App.xaml` set to build action Page, as Velopack requires.
    - **Uninstall** (Settings › Apps) removes the program but not `%LOCALAPPDATA%\Miraside\Spit\` or the
      token. The page explains how to remove both.
    - **Token:** stored in Windows Credential Manager, target `co.miraside.voice:<server URL>` (mirroring
      the Mac's Keychain account = server URL), with `CRED_PERSIST_LOCAL_MACHINE` so it never roams to
      another PC (rule 20). Only the Server settings page's Save and Sign out buttons write or delete it
      (Mac D6).
    - **Launch at login:** the toggle writes or removes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Spit`,
      pointing at Velopack's stable stub `%LocalAppData%\Spit\Spit.exe`, which survives updates. The
      toggle reads the real registry state when the page opens, and reverts with an error message if the
      write fails (Mac `GeneralTab`).
    - **Sounds:** the start and stop cues are two original short WAV files shipped with the app, preloaded
      and played asynchronously with `SoundPlayer`. Apple's Tink and Pop are not redistributable.

### H. Server

45. **No API changes.** The Windows client's values already validate: `context.appBundleId` and `appName`
    ≤ 200 chars, `asrModel` ≤ 80, `clientVersion` ≤ 40 (`server/src/routes/schemas.ts`). Rule 46 keeps
    `hotkey` inside its existing enum. The only server-side edit is documentation.

46. **The hotkey setting is local to the PC, and a PC never overwrites the Mac's.** The server's
    `settings.hotkey` is `'fn' | 'rightOption' | 'rightCommand'` (`server/src/routes/settings.ts:13`),
    stored per user, not per device. The Windows client:
    - never applies the server's `hotkey`;
    - on `PUT /v1/settings` (mode or language changed), sends `hotkey` **exactly as last received** from
      `GET /v1/me`, and preserves `llmModel` the same way (the Mac's G3 merge);
    - if no `GET /v1/me` has succeeded this launch, doesn't PUT at all. The next successful sync's server
      values win, which already happens on the Mac when its PUT fails.

    Without this, changing Mode on the PC would reset the Mac's hotkey to `fn`. `docs/API.md` gains a note
    that `settings.hotkey` is Mac-only and non-Mac clients echo it back unchanged.

## 3. Flows

### A. Cutting a release

1. Bump `VERSION`, commit, tag `vX.Y.Z`, push the tag.
2. GitHub Actions (`release-windows.yml`):
   - Check tag == `VERSION`. On failure, stop; nothing is published.
   - Run `dotnet test`. On failure, stop.
   - Run `vpk pack`.
   - Create the draft release if absent.
   - Upload `Spit-Setup.exe`.
3. On the Mac, run `mac/scripts/package.sh --release`:
   - `xcodegen`, then `xcodebuild test`. On failure, stop.
   - Release build, then sign. With no Apple Development identity, **stop** (rule 13).
   - Verify the signature, run `hdiutil`, upload `Spit.dmg`, then regenerate and upload
     `SHA256SUMS.txt`.
4. Run both manual checklists (§5) **against the draft's own assets**, downloaded through a browser so
   quarantine and Mark-of-the-Web apply. On any failure: fix, tag the next patch version, and delete the
   draft. Never replace an asset in a draft that has already been tested.
5. Publish the release. `curl -sIL` on `…/releases/latest/download/Spit.dmg` and on
   `…/Spit-Setup.exe` both end at the new assets.

### B. A friend installs on a Mac

1. Miguel issues a token (rule 20) and sends it privately.
2. The friend opens `/spit`, clicks Download for Mac, opens `Spit.dmg` and drags Spit to
   Applications.
3. Opening Spit shows the Gatekeeper dialog; they use Privacy & Security › Open Anyway (rule 15).
4. Set up Spit: Microphone, Input Monitoring, Accessibility, the Fn row, then Relaunch.
5. Settings › Server: paste the token, Save, and see "Connected as *name*". The model downloads
   (~630 MB).
6. Hold Fn, speak, release: text appears at the cursor.

### C. A friend installs on Windows

1. Token as above, with label `pc`.
2. `/spit` → Download for Windows. Edge warns about files not on its reputation list, and the friend
   keeps the file.
3. Run `Spit-Setup.exe`. SmartScreen warns; the friend clicks through as the page shows. **With Smart
   App Control on, the app is blocked with no override** (open question 2).
4. Set up Spit (rule 40): microphone, hold Right Ctrl, token. The model downloads (574 MB).
5. Hold Right Ctrl, speak, release: text appears at the cursor.

### D. A Windows dictation, where it differs from the Mac

The pipeline is the Mac's (hotkey → capture → gate → transcribe → skip gate or refine → paste → report),
with these Windows-only branches:

| Situation | What happens |
|---|---|
| An admin window has focus when the key is pressed | Nothing: the hook can't see the key (rule 30). The bar's mic button still works |
| Foreground window is elevated at paste time | Clipboard-only, with a message (rule 30) |
| Clipboard held by another process for > 200 ms | Not pasted; message; `injected: 'none'` (rule 33) |
| Hook silently removed by Windows | Re-installed at the next trigger (rule 29) |
| Microphone access denied | Message and settings button; dictation discarded (rule 39) |
| Model still downloading | Bar shows progress; key presses do nothing (reducer rule) |

## 4. Surfaces

| Surface | Change |
|---|---|
| `VERSION` (new) | Single version source (rule 6) |
| `mac/project.yml`, `mac/Voice/Info.plist` | `PRODUCT_NAME: Spit`, `PRODUCT_MODULE_NAME: Voice`, display name, microphone text, `ARCHS: arm64` for Release, version from `VERSION` |
| `mac/Voice/UI/Strings.swift` | Visible "Voice" → "Spit" (rule 1) — done |
| `mac/scripts/package.sh` | DMG output; `--release` means strict signing plus upload (rules 10, 12, 13) |
| `windows/` (new) | `Spit.sln`, `Spit.Core`, `Spit.App`, `Spit.Core.Tests` (rule 23) |
| `.github/workflows/release-windows.yml` (new) | Tag-triggered test, pack and draft upload (rule 10) |
| GitHub Release `vX.Y.Z` | Three assets (rule 8) |
| `site/spit/index.html` (new) | Download page (rules 17–19) |
| `README.md` | Install steps name `Spit.app` (done); "Install" points at `/spit` |
| `docs/API.md` | Note that `settings.hotkey` is Mac-only (rule 46) |

## 5. Validation

### Spikes — run on Miguel's PC before any Windows UI is built

Each spike has a pass condition and names the rule it settles.

| # | Spike | Pass condition | If it fails |
|---|---|---|---|
| S1 | Transcribe `mac/Fixtures/en.wav` and `pt-synthetic.wav` with Whisper.net, CPU and Vulkan runtimes, both models; record one-pass ms | Numbers recorded here | Open question 3 decides |
| S2 | With Clipboard History **on**, delayed-render text carrying `ExcludeClipboardContentFromMonitorProcessing`; paste into Notepad, Chrome and Word; log which process triggers the first `WM_RENDERFORMAT` | The target, every time, in all three | Keep rule 32's 1.5 s default |
| S3 | From a non-elevated process, determine whether the foreground process (Notepad run as administrator) is elevated | Correct answer for elevated and normal Notepad | Rule 30's "access-denied counts as elevated" |
| S4 | Log `vkCode`, `scanCode`, flags and repeats for: hold Right Ctrl; hold Right Alt; AltGr+2 on pt-PT; Right Ctrl+C. Check menu masking with `vkE8` in Notepad and File Explorer | Rules 27, 28 and 35 hold as written | Drop `rightAlt`; Right Ctrl only |
| S5 | Install a `0.2.0-test` Setup.exe, then a `0.2.1-test` Setup.exe | One install; data directory, token and Run entry intact | Page tells users to uninstall first |

### Automated

- `cd mac && xcodebuild test -scheme Voice` passes the whole suite. After the rename on 2026-09-12 it ran
  **143 tests, 1 skipped (the opt-in ASR test), 0 failures**, and the built bundle was `Spit.app` with
  `CFBundleIdentifier=co.miraside.voice` and `TeamIdentifier=FZC6P6XRGD`; that proves rules 3 and 4.
  (The README's "137" is out of date.)
- `dotnet test windows/Spit.Core.Tests` passes on macOS and in CI. For each file listed in rule 22, the
  test names and counts match the Mac's.
- A Windows `SyncService` test: the `/v1/me` stub returns `hotkey: 'rightCommand'`; changing Mode sends a
  `PUT /v1/settings` whose body contains `hotkey: 'rightCommand'`. With no successful `/v1/me`, no PUT is
  sent.
- A Windows interpreter test for each rule-28 case: autorepeat of the hotkey, scan code `0x21D`, and an
  `LLKHF_INJECTED` key-down all leave a held dictation running.
- `codesign -dv build/Build/Products/Release/Spit.app` shows `TeamIdentifier=FZC6P6XRGD`, and
  `hdiutil verify build/Spit.dmg` passes.
- While the new release is still a draft, `…/latest/download/Spit-Setup.exe` resolves to the previous
  release's asset (404 before the first release). After publishing, it resolves to the new one.

### Mac manual checklist

Run on a fresh macOS user account, with the file downloaded through Safari.

1. The Gatekeeper dialog appears, and Open Anyway works exactly as the page says.
2. All three permissions granted; dictation works in TextEdit and in a Chrome text field.
3. Installing the next build over it causes **no** permission re-prompt.
4. Upgrading from `Voice.app`: token, model and settings carry over, with no model re-download.

### Windows manual checklist

Run on Miguel's PC, with the file downloaded through Edge. Take screenshots of every warning for the
page's copy.

1. SmartScreen and Edge warnings are captured; install needs no admin prompt.
2. Hold Right Ctrl and dictate into Notepad, Chrome, and an Office app.
3. Copy an image, dictate, press Ctrl+V: the image pastes (clipboard restored).
4. Win+V history doesn't contain the dictated text.
5. Notepad run as administrator: the hotkey does nothing there; the bar's mic button session goes
   clipboard-only with the admin message.
6. Double-tap latches; Esc cancels; a 90 s latched session ends with "Reached the 90 s limit".
7. Plug a headset in mid-dictation: the whole dictation is transcribed.
8. Microphone privacy off: the message and settings button appear.
9. Network off gives "Pasted raw". Network back on, then one more dictation: the offline one shows up in
   `GET /v1/dictations`.
10. Launch Spit twice: one instance, and its window comes forward.
11. Close the window: the hotkey still works. Launch at login survives a reboot.
12. Change Mode on the PC: `GET /v1/settings` still shows the Mac's hotkey.
13. Insights shows the same totals as the Mac for the same user, and "Google Chrome" is a single bar.
14. Sleep and wake the PC, then lock and unlock: the hotkey still works (rule 29).
15. Uninstall from Settings › Apps removes the program and leaves `%LOCALAPPDATA%\Miraside\Spit\`.

### Interim measurements (CI, 2026-09-13)

S1 cannot run without the PC; GitHub's `windows-latest` runner (4 vCPU, no GPU) gave a first reading on
`mac/Fixtures/en.wav` (12,522 ms): large-v3-turbo q5_0 took 53–85 s (Vulkan and CPU runtimes identical, since Vulkan
found no GPU); Whisper small q8_0 took 12.9 s. On that basis Whisper small became the Windows default (rule 41). Full
readings are in `docs/SPIKES.md`.

### Numbers

After 20 dictations of 10–12 s on the PC, with live transcription off:

```sql
SELECT total_ms FROM dictations
 WHERE asr_model LIKE 'whisper.cpp/%' AND audio_ms BETWEEN 10000 AND 12000 AND total_ms IS NOT NULL
 ORDER BY total_ms;
```

Record p50 and p90 here, beside the Mac target of p50 ≤ 800 ms and p90 ≤ 1200 ms
(`tasks/prd-sub-second-dictation.md`). Open question 3 sets the Windows threshold.

## 6. Out of scope

- **Apple notarisation and Windows code signing.** Decided against for now. Open question 2 is the case
  that could reverse it.
- **Auto-update on either platform.** The Mac app has none, and parity is the brief.
- **Self-sign-up, invite codes, per-friend cost caps.** Decision 5A.
- **Intel Macs** (rule 14), **Windows on ARM** (Whisper.net's GPU runtimes don't list ARM64, and there's
  no device to test on), and **Linux**.
- **Dictating into admin windows** (rule 30). It needs UIAccess, which needs a signed install under
  Program Files.
- **Per-device settings on the server.** Rule 46 avoids needing them.
- **Homebrew cask, Microsoft Store, Mac App Store.** The site's `brew install --cask` line belongs to
  the flagship section, which decision 4B leaves alone.
- **Translations.** The Mac app is English-only too.
- **Crash reporting or telemetry** beyond what `/v1/dictations` already records.

## 7. Open questions

**Answered by Miguel on 2026-09-13:** 1 — yes, `/spit` is a row in the home page's 03 Index. 2 — accept the Smart App
Control block; no signing. 3 — Whisper small is the default and the recommendation on Windows (rule 41). 4 — keep
Right Ctrl and Right Alt. 5 — Miguel deploys the site later. 6 is still open.

1. **Is `/spit` linked from `site/index.html`** (for example a row in 03 Index), or shared only by URL?
   Decision 4B leaves the flagship section itself alone. *Miguel.*
2. **Smart App Control blocks unsigned apps with no per-app override.** Friends with SAC on can install
   only by turning it off (recent Windows updates let it be turned back on later). Velopack's signing docs
   point to Azure Artifact Signing at US$10/month, which would remove both this block and the SmartScreen
   warning. *Miguel decides: accept the block, or sign.*
3. **What Windows latency is acceptable, and what if a CPU-only PC misses it?** There are no published
   x86 benchmarks for large-v3-turbo; S1 measures it. If CPU-only is far slower than the Mac, adding a
   smaller model breaks model-list parity (rule 41). *Miguel decides after S1.*
4. **What about laptops with no Right Ctrl?** Some 2024+ laptops replaced it with the Copilot key. On an
   AltGr layout like pt-PT, Right Alt is then the only choice, and holding AltGr to type @ or € briefly
   starts a dictation (a start sound, then a cancel). A third option may be needed. *Miguel decides after
   S4.*
5. **How is `site/` deployed, and does `/spit` resolve to `spit/index.html` there?** `site/` has no
   git repo or deploy config in this workspace. *Miguel.*
6. **Is there a spending ceiling for friends' cleanup on the Ollama key?** Nothing limits it today beyond
   120 requests/min per token. *Miguel.*
