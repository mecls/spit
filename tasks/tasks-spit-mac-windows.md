# Tasks — Spit for Mac and Windows

Source spec: `tasks/prd-spit-mac-windows.md`. Rule numbers below (R1–R46) and spike numbers (S1–S5)
refer to it.

## Status (2026-09-13)

**Done and verified without a person:** tasks 0–7. Branch `spit-mac-windows`, pull request #1 open against `main`,
CI green on its head commit. Mac: `Spit.dmg` passes all 16 `verify-dmg.sh` checks; 143 Mac tests. Windows: 213 core
tests (108 ported by name), Windows-only tests including a whole-UI walk and a real paste into Notepad, a smoke test
with the shipped model (Whisper small, 12.9 s for the 12.5 s fixture on a GPU-less runner), and silent install → run →
single instance → uninstall on a GitHub Windows runner. Five code reviews; every confirmed finding fixed (build spec §16).

**Missing — needs Miguel or a real PC:**
- Spikes S1–S5 on the PC (1.3–1.7): no real key press, microphone or GPU has touched the Windows app.
- `release-windows.yml` (6.6) has never run: it runs only on a tag push.
- Release 0.2.0 (8.1–8.4): tag, `package.sh --release`, both manual checklists against the draft, SmartScreen
  screenshots for the `/spit` placeholder, latency SQL, publish, deploy `site/`.
- Follow-ups found during the build (9.x): the live-transcription fallback, Mac equivalents of three Windows fixes,
  and open question 6.

## Relevant Files

### Mac (existing)

- `mac/project.yml` — hard-codes `0.1.0` in `CFBundleShortVersionString` and `MARKETING_VERSION`;
  gains `ARCHS: arm64` for Release and a version taken from `VERSION` (R6, R14).
- `mac/Voice/Info.plist` — the second copy of `0.1.0` that can drift from `project.yml` (R6).
- `mac/scripts/package.sh` — today builds a zip and only *warns* before signing ad-hoc; becomes the
  DMG builder, and `--release` makes it strict and uploads (R10, R12, R13).
- `mac/Voice/App/DictationMachine.swift`, `mac/Voice/Hotkey/TapLatch.swift`,
  `mac/Voice/Hotkey/HotkeyInterpreter.swift` — the gesture rules and reducer the Windows client ports
  line for line (R22).
- `mac/Voice/Audio/EnergyGate.swift`, `mac/Voice/Audio/RingBuffer.swift`,
  `mac/Voice/Audio/AudioRecorder.swift` — speech gate, 90 s ring buffer, 20 s warm capture (R22).
- `mac/Voice/ASR/Stitch.swift`, `mac/Voice/ASR/StreamingTranscriber.swift`,
  `mac/Voice/ASR/VoiceAudioProcessor.swift`, `mac/Voice/ASR/WhisperKitTranscriber.swift`,
  `mac/Voice/ASR/ModelManager.swift` — streaming loop, stitching, decode options and model download
  rules that whisper.cpp has to re-implement (R22, R41, R42).
- `mac/Voice/Refine/SkipGate.swift`, `Budget.swift`, `Outbox.swift`, `RefineService.swift`,
  `SyncService.swift`, `VoiceAPI.swift` — the server-facing logic that becomes `Spit.Core` (R22, R46).
- `mac/Voice/Inject/TextInjector.swift`, `PasteboardSnapshot.swift`, `FrontmostContext.swift` — paste
  timing, the 5 MB snapshot limit and context capture that the Windows paste path mirrors (R31–R34).
- `mac/Voice/Insights/InsightsPresentation.swift`, `HeatmapGrid.swift`, `InsightsModel.swift`,
  `InsightsCache.swift` — Insights logic to port into `Spit.Core` (R22).
- `mac/Voice/App/Coordinator.swift`, `mac/Voice/App/VoiceApp.swift` — owner of the timings in the R22
  table, and the `MenuBarExtra` order the tray menu copies (R37).
- `mac/Voice/UI/HUDPanel.swift`, `HUDView.swift`, `MainWindow.swift`, `SettingsView.swift`,
  `OnboardingView.swift`, `Strings.swift` — the UI the WPF windows mirror (R37, R38, R40).
- `mac/VoiceTests/` — the 14 files in R22 hold the 108 tests to port (checked on 2026-09-12:
  DictationMachine 7, TapLatch 11, HotkeyInterpreter 10, SkipGate 16, Stitch 8, StreamTail 8, Budget 1,
  EnergyGate 3, Outbox 1, RefineService 8, SyncService 3, Insights 24, TextInjectorRouting 5,
  RingBuffer 3).
- `mac/Fixtures/en.wav`, `mac/Fixtures/pt-synthetic.wav` — the audio for spike S1.

### Windows (new)

- `VERSION` — **new.** The single version source for both clients (R6).
- `windows/Spit.sln` — **new.**
- `windows/Spit.Core/` — **new**, `net10.0`. The reducer, gestures, gates, stitching, API client,
  sync, outbox and Insights logic, with no Windows dependency (R23).
- `windows/Spit.Core.Tests/` — **new.** The 108 ported tests plus the R28 and R46 tests; must run on
  the Mac (R23).
- `windows/Spit.App/` — **new**, `net10.0-windows`, WPF. Hook, audio, clipboard, Whisper.net, tray,
  windows, installer entry point (R24–R44).
- `.github/workflows/windows-ci.yml`, `.github/workflows/release-windows.yml` — **new** (R10).

### Server, docs and site

- `server/src/routes/settings.ts` — the `hotkey` enum at line 13 that the Windows client echoes and
  never changes (R46). Read-only.
- `server/src/routes/schemas.ts` — length limits the Windows `context` and `asrModel` must fit (R45).
  Read-only.
- `docs/API.md` — line 48 documents `Settings`; gains the note that `hotkey` is Mac-only (R46).
- `README.md` — status and what is missing, test counts, the DMG and Windows commands, and the pointer to `/spit`.
- `../../site/spit/index.html` — **new.** The download page. `site/` sits at
  `SintraLabs/site`, **outside this git repository**, and is not under git at all (R17–R19).
- `../../site/styles.css` — tokens and layout classes the page reuses (R17).

### Notes

- **Branch:** `spit-mac-windows`, open as pull request #1 against `main`.
- **Mac tests** live in `mac/VoiceTests/`, one `<Type>Tests.swift` per type, and run with
  `cd mac && xcodebuild test -scheme Voice` (143 tests, 1 skipped, as of 2026-09-12).
- **Server tests** live in `server/test/*.test.ts` and run with `cd server && npm test`. This spec
  changes no server code, so they should stay green untouched.
- **Windows tests** go in `windows/Spit.Core.Tests/`, one `<Type>Tests.cs` per ported Mac file with
  the same test names, and run with `dotnet test windows/Spit.Core.Tests`.
- **.NET on this Mac:** `/usr/local/share/dotnet` has only SDK `6.0.400`. SDK `10.0.401` was installed
  per user into `~/.dotnet` on 2026-09-13 with `dotnet-install.sh --channel 10.0`; put
  `export PATH="$HOME/.dotnet:$PATH"` in front of every `dotnet` command.
- **Pinned model hashes** (Hugging Face LFS `oid`, read 2026-09-13):
  `ggml-small-q8_0.bin` (the Windows default) 264,464,607 bytes, SHA-256
  `49c8fb02b65e6049d5fa6c04f81f53b867b5ec9540406812c643f177317f779f`;
  `ggml-large-v3-turbo-q5_0.bin` 574,041,195 bytes, SHA-256
  `394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2`;
  `ggml-large-v3-turbo.bin` 1,624,555,275 bytes, SHA-256
  `1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69`.
- **Test packages** (NuGet, 2026-09-13): `xunit.v3` 4.0.1, `xunit.runner.visualstudio` 4.0.0,
  `Microsoft.NET.Test.Sdk` 18.10.0; `vpk` tool 1.2.0 to match `Velopack` 1.2.0.
- **(PC)** marks a step that needs Miguel's Windows PC or Miguel's own decision. Nothing else may wait
  on one; build around it and leave the box unticked.
- `gh` and `xcodegen` are installed (`/opt/homebrew/bin`).
- Tag `v0.1.0` already exists on GitHub and points at `467c597`. The first release tag is `v0.2.0`
  (R7).

## Instructions for Completing Tasks

As you complete each sub-task, check it off by changing `- [ ]` to `- [x]`, and save the file
then — not at the end of the parent task. Someone picking this up after an interruption can
only trust the boxes if they were ticked as the work happened.

## Tasks

- [x] 0.0 Create feature branch `spit-mac-windows` off `main`
- [ ] 1.0 Settle everything that could change the plan
  - [x] 1.1 Install the .NET 10 SDK on the Mac (`~/.dotnet`, SDK 10.0.401) and confirm a WPF project
        compiles here with `-p:EnableWindowsTargeting=true` (it does: XAML compiles to BAML)
  - [x] 1.2 Read the pinned model SHA-256s from Hugging Face and record them in Notes above
  - [ ] 1.3 **(PC)** S1: transcribe `mac/Fixtures/en.wav` and `pt-synthetic.wav` with the CPU and
        Vulkan runtimes and both models; write the one-pass milliseconds into spec §5. Meanwhile CI
        measured a GPU-less runner: large-v3-turbo q5_0 53–85 s, small q8_0 12.9 s for the 12.5 s clip (docs/SPIKES.md)
  - [ ] 1.4 **(PC)** S2: log which process sends the first `WM_RENDERFORMAT` with Clipboard History on,
        in Notepad, Chrome and Word. Until it passes, keep R32's 1.5 s restore (the default built here)
  - [ ] 1.5 **(PC)** S3: check `ElevationProbe` (4.6) against Notepad run as administrator and normal
        Notepad
  - [ ] 1.6 **(PC)** S4: run `Spit.exe --key-log` (4.2) and record `vkCode`, `scanCode`, flags and repeats
        for Right Ctrl, Right Alt, AltGr+2 on pt-PT and Right Ctrl+C; check `vkE8` masking in Notepad
        and File Explorer
  - [ ] 1.7 **(PC)** S5: install a `0.2.0` Setup.exe, then a `0.2.1` one; confirm one install and an
        intact data folder, token and Run entry. CI covers the silent install/uninstall half (6.5)
  - [x] 1.8 Miguel answers open questions (2026-09-13): link `/spit` from the home page — yes; accept Smart App
        Control — yes; Windows default model — Whisper small; keep Right Ctrl + Right Alt — yes; site deployed later
- [x] 2.0 One version and a strict Mac DMG
  - [x] 2.1 Add root `VERSION` containing `0.2.0` (R6, R7)
  - [x] 2.2 Make `mac/project.yml` read the version: `CFBundleShortVersionString: $(MARKETING_VERSION)`
        in `info.properties`, `MARKETING_VERSION` set by `package.sh` from `VERSION`; replace the literal
        in `mac/Voice/Info.plist` with `$(MARKETING_VERSION)` so there is one copy
  - [x] 2.3 Set `ARCHS: arm64` under the `Voice` target's `settings.configs.Release` (R14)
  - [x] 2.4 Rewrite `mac/scripts/package.sh`: read `VERSION`, `xcodegen generate`, `xcodebuild test`,
        Release build with `MARKETING_VERSION=$VERSION`, sign, stage `Spit.app` + `Applications` symlink,
        `hdiutil create -volname Spit … -format UDZO build/Spit.dmg` (R12)
  - [x] 2.5 Add `--release`: exit non-zero when no Apple Development identity exists, run
        `codesign --verify --deep --strict`, require `TeamIdentifier=FZC6P6XRGD`, then
        `gh release upload vX.Y.Z build/Spit.dmg --clobber` and regenerate `SHA256SUMS.txt` from both
        assets (R10, R13)
  - [x] 2.6 Add `mac/scripts/verify-dmg.sh`: `hdiutil verify`, attach read-only with `-nobrowse`,
        check `Spit.app` and the `Applications` symlink, `codesign --verify --deep --strict`, TeamIdentifier,
        `lipo -archs` = `arm64`, `CFBundleShortVersionString` = `VERSION`, bundle id `co.miraside.voice`,
        then detach. `package.sh` runs it last
  - [x] 2.7 Run `package.sh` (no `--release`) and `verify-dmg.sh`; all checks pass; `xcodebuild test`
        still 143 tests, 1 skipped, 0 failures
- [x] 3.0 `Spit.Core` and its tests on the Mac
  - [x] 3.1 Create `windows/Spit.sln`, `windows/Directory.Build.props` (reads `../VERSION` into
        `<Version>`, `Nullable` and `TreatWarningsAsErrors` on), `windows/Spit.Core/Spit.Core.csproj`
        (`net10.0`) and `windows/Spit.Core.Tests/Spit.Core.Tests.csproj` (xunit.v3 4.0.1)
  - [x] 3.2 Port `Dictation.swift` and `DictationMachine.swift` → `Spit.Core/App/`, and
        `DictationMachineTests` (7)
  - [x] 3.3 Port `TapLatch.swift`, `HotkeyInterpreter.swift` and `HotkeyChoice` (values `rightCtrl`,
        `rightAlt`) → `Spit.Core/Hotkey/`, with a pure `RawKeyEvent → KeyEvent` translator for R27/R28;
        port `TapLatchTests` (11) and `HotkeyInterpreterTests` (10); add `HotkeyTranslatorTests` for
        autorepeat, scan code `0x21D` and `LLKHF_INJECTED`
  - [x] 3.4 Port `EnergyGate.swift` and `RingBuffer.swift` → `Spit.Core/Audio/`, plus `EnergyGateTests`
        (3) and `RingBufferTests` (3)
  - [x] 3.5 Port `Stitch.swift`, `Coordinator.tail` / `minimumTailMs` / `overlapMs` (as `StreamTail`)
        and the confirmation/VAD rules from R22 (as `StreamingPolicy`) → `Spit.Core/Asr/`, plus
        `StitchTests` (8) and `StreamTailTests` (8)
  - [x] 3.6 Port `SkipGate`, `Budget`, `Outbox`, `VoiceAPI` DTOs + `IVoiceApiClient`, `RefineService`
        → `Spit.Core/Refine/`, plus `SkipGateTests` (16), `BudgetTests` (1), `OutboxTests` (1),
        `RefineServiceTests` (8) and a `StubApi` test double mirroring `StubAPI.swift`
  - [x] 3.7 Port `SyncService` with R46's echo rule → `Spit.Core/Refine/`, plus `SyncServiceTests` (3)
        and two R46 tests (echoes `rightCommand`; no PUT before a successful `/v1/me`)
  - [x] 3.8 Port `Insights`, `HeatmapGrid`, `InsightsPresentation`, `InsightsFormat`, `InsightsCache`
        and the non-UI half of `InsightsModel` → `Spit.Core/Insights/`, with `IanaTimeZone` (R43), plus
        `InsightsTests` (24) and a time-zone test (Windows id → IANA; failure → no request)
  - [x] 3.9 Port `TextInjector.mustUseClipboard` as `PasteRouting` (own process, elevated target) →
        `Spit.Core/Inject/`, plus `TextInjectorRoutingTests` (5)
  - [x] 3.10 Add the real `HttpVoiceApiClient` (10 s timeout, budget for `/v1/refine`, `HEAD /health`
        3 s) in `Spit.Core/Refine/` with a test against a local `HttpListener`-free fake handler
  - [x] 3.11 Add `windows/scripts/parity-check.sh`: for each of the 14 files, the Swift `func test`
        count equals the C# `[Fact]` count in the same-named class, and the names match; run it
  - [x] 3.12 `dotnet test windows/Spit.Core.Tests` passes on the Mac
- [x] 4.0 `Spit.App` platform layer
  - [x] 4.1 Create `windows/Spit.App/Spit.App.csproj` (`net10.0-windows`, `UseWPF`, `win-x64`,
        `AssemblyName` `Spit`, `ApplicationManifest` asInvoker) with the R24 packages, a hand-written
        `Program.Main` whose first line is `VelopackApp.Build().Run()`, and `App.xaml` as Page (R44)
  - [x] 4.2 `Platform/KeyboardHook.cs`: `WH_KEYBOARD_LL` on a dedicated thread with its own message
        loop, always `CallNextHookEx`, posts `RawKeyEvent` to the UI thread, `Reinstall()` on resume,
        unlock and every 5 min idle; `--key-log` mode writes events to the console for S4 (R27–R29)
  - [x] 4.3 `Platform/MenuMask.cs`: inject `vkE8` while Right Alt is held when it is the hotkey (R35)
  - [x] 4.4 `Audio/AudioCapture.cs`: NAudio WASAPI capture → `WdlResamplingSampleProvider` → 16 kHz mono
        Float32 into `RingBuffer`, 20 s warm keep, default-device-change rebuild that keeps samples,
        `E_ACCESSDENIED` → blocked error (R22, R39)
  - [x] 4.5 `Inject/ClipboardSnapshot.cs` and `Inject/TextInjector.cs`: allowed memory formats ≤ 5 MB,
        `OpenClipboard` retry 10 ms up to 200 ms with Spit's HWND, text with
        `ExcludeClipboardContentFromMonitorProcessing`, Ctrl+V via `SendInput` 30 ms later, restore at
        1.5 s (R25, R32, R33)
  - [x] 4.6 `Platform/ElevationProbe.cs` and `Inject/ForegroundContext.cs`: input counts as
        blocked when the target's integrity level is above Spit's (access denied = blocked); exe name + `FileDescription`,
        UWP child-window resolution, never the title (R30, R31)
  - [x] 4.7 `Spit.Core/Asr/ModelCatalog.cs` + `ModelDownloader.cs` (default `ggml-small-q8_0.bin`): download to `.partial`, verify pinned SHA-256, rename; progress;
        storage under `%LOCALAPPDATA%\Miraside\Spit\models` (R5, R41)
  - [x] 4.8 `Asr/WhisperTranscriber.cs`: Whisper.net factory, temperature 0, language hint, prompt from
        dictionary terms with one retry without it, 1 s silence warm-up, one inference at a time behind a
        `SemaphoreSlim`, `asrModel = whisper.cpp/<file>` (R22, R41, R42)
  - [x] 4.9 `Spit.Core/Asr/StreamingSession.cs` + `Spit.App/Asr/LiveTranscription.cs`: the 100 ms poll / > 1 s new audio / all-but-last-2 loop over
        `StreamingPolicy`, finishing any pass before the tail pass (R22, R42)
  - [x] 4.10 `Storage/TokenStore.cs` (Credential Manager, target `co.miraside.voice:<server URL>`,
        `CRED_PERSIST_LOCAL_MACHINE`), `Storage/Settings.cs` (`settings.json`), `Storage/DictionaryCache.cs`,
        `Storage/AppPaths.cs`, `Storage/LaunchAtLogin.cs` (HKCU Run) (R5, R44)
  - [x] 4.11 `App/Coordinator.cs`: wires hotkey → capture → gate → transcribe → refine → paste → report
        exactly like `Coordinator.swift`, with the 1.2 s return-to-idle and 60 s pre-warm
- [x] 5.0 `Spit.App` user interface
  - [x] 5.1 `UI/TrayIcon.cs`: H.NotifyIcon with the four `menuIcon` states and the R37 menu order
  - [x] 5.2 `UI/BarWindow.xaml`: `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, `ShowActivated=false`, sized to
        content, positioned by the R38 rule, lozenge / 20-bar waveform / red latched / status text
  - [x] 5.3 `UI/MainWindow.xaml`: sidebar with Insights and Settings; closing hides (R37)
  - [x] 5.4 `UI/InsightsView.xaml`: four cards and the 21-week heatmap over `InsightsPresentation`
  - [x] 5.5 `UI/SettingsView.xaml`: General, Model, Server, Dictionary pages mirroring `SettingsView.swift`
  - [x] 5.6 `UI/SetupWindow.xaml`: Microphone, Hotkey and Token rows; Done enabled on the first two (R40)
  - [x] 5.7 `UI/Strings.cs` with the Mac strings, Windows key names and the new Windows messages
        (admin window, clipboard busy, microphone blocked, time zone)
  - [x] 5.8 `Assets/start.wav`, `Assets/stop.wav` generated in-repo (original tones) and played with a
        preloaded `SoundPlayer`; `Assets/Spit.ico` from `mac/branding`
  - [x] 5.9 Single instance: named mutex `Local\co.miraside.voice.spit` plus an activation message (R36)
  - [x] 5.10 `--smoke-test` mode: start every service without the UI loop, load the model if present,
        transcribe a WAV passed on the command line, write `smoke.json`, exit 0/1 — used only by CI
- [ ] 6.0 Windows installer and CI
  - [x] 6.1 `windows/scripts/pack.ps1`: `dotnet publish -c Release -r win-x64 --self-contained`, then
        `vpk pack --packId Spit --packVersion (VERSION) --mainExe Spit.exe --packTitle Spit` → `Spit-Setup.exe`
  - [x] 6.2 `.github/workflows/windows-ci.yml` on `pull_request` and pushes to `spit-mac-windows`:
        Core tests on `ubuntu-latest`; on `windows-latest` build, test, pack, upload `Spit-Setup.exe`
        as a workflow artifact (never a release)
  - [x] 6.3 `windows/Spit.App.Tests` (Windows-only, run in CI): clipboard snapshot round-trip,
        own-process elevation probe, `ForegroundContext` for a spawned Notepad, credential write/read/delete
        under a test target, launch-at-login registry round-trip under a test value name
  - [x] 6.4 CI model job: cache the default model (`ggml-small-q8_0.bin`) by SHA-256, run `Spit.exe --smoke-test mac/Fixtures/en.wav`,
        assert non-empty text and record the milliseconds in the job summary
  - [x] 6.5 CI install job: `Spit-Setup.exe --silent`, check `%LocalAppData%\Spit\Spit.exe` exists, start
        it, confirm the process is alive after 15 s and a second launch exits, then uninstall and confirm
        `%LOCALAPPDATA%\Miraside\Spit\` remains
  - [ ] 6.6 `.github/workflows/release-windows.yml` on `v*` tags: tag == `VERSION`, `dotnet test`, pack,
        `gh release create --draft` if absent, upload `Spit-Setup.exe` only (R8–R11). Written and reviewed; it
        runs only on a tag push, so 8.1 is its first real run
- [x] 7.0 Docs and the `/spit` page
  - [x] 7.1 `docs/API.md`: note under `Settings` that `hotkey` is Mac-only and other clients echo it (R46)
  - [x] 7.2 `README.md`: test counts (143 Mac, the new Windows count), a "Windows" section with the
        build and test commands, and "Install" pointing at `/spit`
  - [x] 7.3 `../../site/spit/index.html`: both buttons on `latest/download`, `SHA256SUMS.txt` link, every
        R18 claim, Mac Gatekeeper steps; the Windows warning copy left as a marked block until 8.2's
        screenshots exist (R17–R19). Not in git: `site/` is outside the repo
- [ ] 8.0 Release 0.2.0
  - [ ] 8.1 **(PC)** Tag `v0.2.0`; CI creates the draft and uploads `Spit-Setup.exe`; run
        `package.sh --release` to add `Spit.dmg` and `SHA256SUMS.txt`
  - [ ] 8.2 **(PC)** Run the Mac and Windows manual checklists (spec §5) against the draft's own assets,
        taking screenshots of every Windows warning; fill 7.3's marked block from them
  - [ ] 8.3 **(PC)** Run the §5 latency SQL after 20 dictations and record p50/p90
  - [ ] 8.4 **(PC)** Publish the release; `curl -sIL` both `latest/download` URLs; put `/spit` live
- [ ] 9.0 Follow-ups found during the build (none blocks the release)
  - [ ] 9.1 Live transcription on Windows fell back to a whole-recording pass on the CI clip (correct text, 37 s
        instead of 12.9 s). Log the streamed and tail texts from real dictations, then tune
        `Stitch.TryJoinAllowingTailSkip` / `StreamTail.OverlapMs`. Live transcription is off by default
        — Moved to `tasks/tasks-windows-parity.md` 5.0; the recorder and replay it needs are 1.11
  - [x] 9.2 Decide whether to port three Windows fixes to the Mac, which has the same patterns: appending a tail
        when no seam is found (duplicated words), a live stream left running after a rejected dictation, and a
        double-tap latching while the model loads (build spec §16)
        — Ported, all three: `tasks/tasks-windows-parity.md` 6.0 (commit 7db01d6), with 6.9 bringing the new
        Mac tests back to Windows by name
  - [ ] 9.3 Open question 6: a spending ceiling for friends' cleanup on the Ollama key
