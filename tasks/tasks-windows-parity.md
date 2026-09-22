# Windows Parity — Task List

Source spec: `tasks/prd-windows-parity.md`. Rule numbers below refer to its §2, thresholds to its §5.

## Relevant Files

### The measurement harness that already exists — reuse it, do not write a second one

- `windows/Spit.App/App/SmokeTest.cs` - `--smoke-test <wav> --report <json>` already emits everything S1 needs: `AudioMs`, `TranscribeMs`, `Runtime`, `RuntimeRequested`, `BackendLog`, `AsrModel`. It also emits `StreamMs`, `StreamSegments` and **`WholePassFallback`** — the exact boolean rule 15 is about
- `windows/Spit.App/Asr/WhisperTranscriber.cs` - `RuntimeVariable = "SPIT_WHISPER_RUNTIME"` at `:27` forces `cpu`/`vulkan`; `BackendLog` at `:35` proves which one actually loaded (rule 5); `ConfigureRuntimes()` at `:173` sets the fall-through order
- `.github/workflows/windows-ci.yml` - `:116-120` already drives the smoke test across runtimes; copy that loop's shape for the S1 matrix
- `windows/Spit.App/Program.cs` - `KeyLogFlag = "--key-log"` at `:8`, the S4 harness

### Windows — what the spikes settle

- `windows/Spit.Core/Asr/ModelCatalog.cs` - `DefaultFile` at `:19`, and the `:7-10` header comment whose reasoning rule 6 replaces with a measured number
- `windows/Spit.App/Platform/KeyboardHook.cs`, `MenuMask.cs`, `KeyLog.cs` - S4's subjects: `CallNextHookEx` (rule 11), `vkE8` masking (rule 12)
- `windows/Spit.Core/Hotkey/HotkeyChoice.cs`, `HotkeyTranslator.cs` - lose the `rightAlt` case if S4 fails (rule 2)
- `windows/Spit.App/Platform/ElevationProbe.cs` - S3's subject
- `windows/Spit.App/Inject/ClipboardSession.cs`, `ClipboardSnapshot.cs` - S2's subjects; the 1.5 s restore (rules 9-10)

### Live transcription (rules 14-16)

- `windows/Spit.Core/Asr/Stitch.cs` - `Join`, `TryJoin`, `TryJoinAllowingTailSkip` at `:27/:36/:46`
- `windows/Spit.Core/Asr/StreamTail.cs` - `OverlapMs = 1500` at `:15`, `MinimumTailMs = 400` at `:8`
- `windows/Spit.Core/Asr/StreamingPolicy.cs` - `RequiredSegmentsForConfirmation = 2` at `:23`, `MinimumNewAudioSeconds = 1` at `:27`; ported from WhisperKit, never measured on Windows
- `windows/Spit.App/Asr/LiveTranscription.cs`, `windows/Spit.Core/Asr/StreamingSession.cs` - the loop being tuned

### Mac — the back-port (rules 17-19)

- `mac/Voice/ASR/Stitch.swift` - has only `join(streamed:tail:)`; **gains `tryJoin` and `tryJoinAllowingTailSkip`** (rule 17)
- `mac/Voice/App/Coordinator.swift` - `:251` finishes the stream on `.discardRecording`; audit for Windows' second path at `Coordinator.cs:614` (rule 19). No guard against latching during model load (rule 18)
- `mac/Voice/Hotkey/TapLatch.swift`, `mac/Voice/App/DictationMachine.swift` - where rule 18's guard lands

### Docs and evidence

- `docs/SPIKES.md` - every spike's raw evidence (rule 1); the existing format to follow
- `tasks/prd-spit-mac-windows.md` - §5 holds the 13-item Windows checklist and the latency SQL
- `README.md` - the "What's missing" list this work closes out
- `SintraLabs/site/spit/index.html` - **outside this repo**; the marked placeholder for rule 21's screenshots

### Test files

- `windows/Spit.Core.Tests/StitchTests.cs`, `StreamTailTests.cs`, `StreamingPolicyTests.cs` - must still pass after any live-transcription tuning
- `windows/Spit.Core.Tests/HotkeyTranslatorTests.cs`, `HotkeyInterpreterTests.cs` - change only if S4 drops `rightAlt`
- `windows/Spit.App.Tests/ElevationProbeTests.cs`, `ClipboardSnapshotTests.cs` - the existing unit cover for S3 and S2
- `mac/VoiceTests/StitchTests.swift` - gains the Windows cases, renamed to the Mac's convention (rule 20)
- `mac/VoiceTests/TapLatchTests.swift` or `DictationMachineTests.swift` - rule 18's new test

### Notes

- **Windows tests** live beside the code in `windows/Spit.Core.Tests/` and `windows/Spit.App.Tests/`. Run from the repo root: `dotnet test windows/Spit.sln`. The runner is **Microsoft.Testing.Platform**, set in root `global.json` — the .NET 10 SDK refuses VSTest for xunit.v3, so do not add `Microsoft.NET.Test.Sdk`.
- `Spit.Core.Tests` runs on the Mac too; `Spit.App.Tests` is Windows-only (`WindowsFactAttribute`).
- **Mac tests** are XCTest in one flat bundle at `mac/VoiceTests/`. Run from `mac/`: `xcodebuild -project Voice.xcodeproj -scheme Voice -derivedDataPath build/test-dd -quiet test`. Currently 143 tests, 1 skipped. **There is no macOS CI** — a person runs every one.
- After any `mac/project.yml` change: `xcodegen generate` from `mac/` first.
- The installer is built by `windows/scripts/pack.ps1`; Velopack names it `Spit-win-Setup.exe` and the script renames it to `Spit-Setup.exe`.
- Tasks marked **(PC)** need Miguel's Windows machine. Everything else is doable on the Mac.

## Instructions for Completing Tasks

As you complete each sub-task, check it off by changing `- [ ]` to `- [x]`, and save the file
then — not at the end of the parent task. Someone picking this up after an interruption can
only trust the boxes if they were ticked as the work happened.

## Tasks

- [ ] 0.0 Create a feature branch for this work
  - [ ] 0.1 Branch `feat/windows-parity` off `main` — **not** off the current `docs/parakeet-english-engine`, which is an open, unrelated docs PR

- [ ] 1.0 Prepare the spike session on the Mac, so PC time is spent measuring rather than authoring
  - [ ] 1.1 Add a `--model <file>` argument to `SmokeTest.Parse` / `Run` in `windows/Spit.App/App/SmokeTest.cs`. **Blocking for S1:** `:96` currently hardcodes `var file = ModelCatalog.DefaultFile`, so the harness can only ever measure one of the three models. Default to `ModelCatalog.DefaultFile` when the flag is absent, so `windows-ci.yml` keeps working unchanged
  - [ ] 1.2 Extend the smoke `Report` record with the wall-clock of the whole run and the resolved model path, if `TranscribeMs` and `AsrModel` do not already cover what rule 4's median needs
  - [ ] 1.3 Add a test in `windows/Spit.App.Tests/SmokeTestLoaderTests.cs` for `--model`: absent → `DefaultFile`; present with a catalog file → that file; present with an unknown file → usage exit code 2, not a crash
  - [ ] 1.4 Write `windows/scripts/spike-s1.ps1`: 2 runtimes (`SPIT_WHISPER_RUNTIME=cpu|vulkan`) × 3 models × 2 fixtures (`mac/Fixtures/en.wav`, `pt-synthetic.wav`) × 3 repetitions, one `--report` JSON per run into a folder, **discarding the first repetition of each pair** (rule 4). Copy the loop shape from `.github/workflows/windows-ci.yml:116-120`
  - [ ] 1.5 Make `spike-s1.ps1` print a markdown table of medians plus, for every Vulkan row, the `BackendLog` line naming the device — a Vulkan row with no device line is a failed measurement, not a slow one (rule 5)
  - [ ] 1.6 Add five empty sections to `docs/SPIKES.md` (S1–S5) in its existing format, each with the pass condition and the pre-agreed failure branch from spec rule 2, so evidence lands in the right shape under time pressure
  - [ ] 1.7 Build the two S5 installers now — `0.2.0-test` and `0.2.1-test` — via `windows/scripts/pack.ps1`, so the PC session does not start with a build. This is the one spike with a real setup cost
  - [ ] 1.8 Run `dotnet test windows/Spit.sln` on the Mac and record the passing count as the pre-session baseline (`Spit.Core.Tests` runs here; `Spit.App.Tests` is Windows-only)

- [ ] 2.0 **(PC)** Session 1 — run the five spikes in rule 3's order (S4 → S3 → S2 → S1 → S5) and commit the evidence
  - [ ] 2.1 Install from `Spit-Setup.exe` the way a friend would — downloaded, not copied from a build folder — and confirm the install needs no admin prompt
  - [ ] 2.2 **S4:** run `Spit.exe --key-log` and record `vkCode`, `scanCode`, flags and repeat counts for four gestures: hold Right Ctrl; hold Right Alt; AltGr+2 on the pt-PT layout; Right Ctrl+C
  - [ ] 2.3 **S4:** check `vkE8` menu masking in **both** Notepad and File Explorer — they use different menu implementations, and rule 12 requires both (`windows/Spit.App/Platform/MenuMask.cs`)
  - [ ] 2.4 **S4:** confirm nothing is swallowed (rule 11) — Right Ctrl+C still copies, AltGr+2 still types `@`
  - [ ] 2.5 Write S4's raw evidence into `docs/SPIKES.md`. **If masking failed in either app, apply rule 2's branch now** — drop `rightAlt` — before any other spike runs against a hotkey that is going away
  - [ ] 2.6 **S3:** from non-elevated Spit, read the foreground process with Notepad running as administrator, then with normal Notepad; confirm `ElevationProbe` answers correctly for both
  - [ ] 2.7 Write S3's two answers into `docs/SPIKES.md`
  - [ ] 2.8 **S2:** Clipboard History **on**, delayed-render text carrying `ExcludeClipboardContentFromMonitorProcessing`; paste into Notepad, Chrome and Word; log which process triggers the first `WM_RENDERFORMAT` in each
  - [ ] 2.9 **S2:** press Win+V after each paste and confirm the dictation is absent. Rule 9 is unconditional — if the text is in history, stop the session and fix it before continuing
  - [ ] 2.10 Write S2's three process names into `docs/SPIKES.md`
  - [ ] 2.11 **S1:** run `spike-s1.ps1` and collect the JSON reports
  - [ ] 2.12 **S1:** check every Vulkan row has a `BackendLog` device line naming a real GPU. If Vulkan silently fell through to CPU (rule 5), the numbers are void — fix the runtime resolution and re-run before recording anything
  - [ ] 2.13 Write S1's 12 medians and every device string into `docs/SPIKES.md`
  - [ ] 2.14 **S5:** install `0.2.0-test`, then `0.2.1-test`; confirm exactly one entry in Installed Apps, and that the data directory, stored token and Run entry all survive
  - [ ] 2.15 Write S5's result into `docs/SPIKES.md`, then commit all five sections

- [ ] 3.0 Act on what the spikes returned — the hotkey, the clipboard delay, and the default model
  - [ ] 3.1 If S4 failed: remove the `rightAlt` case from `windows/Spit.Core/Hotkey/HotkeyChoice.cs` and `HotkeyTranslator.cs`, drop its cases from `HotkeyTranslatorTests.cs` / `HotkeyInterpreterTests.cs`, and remove the option from the Settings hotkey picker
  - [ ] 3.2 If S2 showed the target process always renders first: shorten the 1.5 s restore in `windows/Spit.App/Inject/ClipboardSession.cs`. **Never to zero** — rule 10; restoring early means Ctrl+V pastes the dictation instead of the user's own clipboard
  - [ ] 3.3 If S4 and S2 passed as written: record "no change, rule held" in `docs/SPIKES.md` explicitly. A spike that changed nothing still has an outcome
  - [ ] 3.4 Compare S1's median Vulkan one-pass time for `ggml-large-v3-turbo-q5_0.bin` on `en.wav` against rule 6's **≤ 3.15 s**, and write the comparison down as a sentence with both numbers in it
  - [ ] 3.5 If it clears: change `ModelCatalog.DefaultFile` to `TurboCompressedFile` in `windows/Spit.Core/Asr/ModelCatalog.cs:19`
  - [ ] 3.6 Rewrite `ModelCatalog.cs:7-10`'s header comment either way, citing the measured GPU number instead of the current "friends' PCs mostly have no GPU Vulkan can use" assumption (rule 6)
  - [ ] 3.7 Verify rule 7 holds in code: an existing install keeps the model it already downloaded and does not re-fetch 574 MB. Check `SettingsStore`'s `ModelFile` read path — `DefaultFile` must only apply when no setting exists
  - [ ] 3.8 If the default changed: update `Strings.ModelLabelSmall` / `ModelLabelTurbo` so the labels no longer imply small is the fast choice
  - [ ] 3.9 Run `dotnet test windows/Spit.sln` and confirm the baseline from 1.8 still passes, minus any `rightAlt` cases deliberately removed in 3.1

- [ ] 4.0 **(PC)** Session 2 — the 13-item manual checklist, with screenshots and triage
  - [ ] 4.1 Item 1 + rule 21: download through Edge and capture screenshots of the Edge warning, SmartScreen, and Smart App Control if it fires. Confirm the install needs no admin prompt
  - [ ] 4.2 Item 2: hold the hotkey and dictate into Notepad, Chrome and an Office app
  - [ ] 4.3 Item 3: copy an image, dictate, press Ctrl+V — the image pastes (rule 10)
  - [ ] 4.4 Item 4: Win+V history does not contain the dictated text (rule 9, unconditional)
  - [ ] 4.5 Item 5: with Notepad running as administrator, the hotkey does nothing there, and the bar's mic-button session goes clipboard-only **with the admin message shown** — the silent failure is the unacceptable one (rule 13)
  - [ ] 4.6 Item 6: double-tap latches; Esc cancels; a 90 s latched session ends with "Reached the 90 s limit"
  - [ ] 4.7 Item 7: plug a headset in mid-dictation — the whole dictation is still transcribed
  - [ ] 4.8 Item 8: with microphone privacy off, the message and the settings button both appear
  - [ ] 4.9 Item 9: network off gives "Pasted raw"; network back on plus one more dictation, and the offline one appears in `GET /v1/dictations`
  - [ ] 4.10 Item 10: launch Spit twice — one instance, and its window comes forward
  - [ ] 4.11 Item 11: close the window and confirm the hotkey still works; reboot and confirm launch-at-login survived
  - [ ] 4.12 Item 12: change Mode on the PC, then confirm `GET /v1/settings` still shows the Mac's hotkey (rule 46 — Windows never writes the hotkey)
  - [ ] 4.13 Item 13: Insights shows the same totals as the Mac for the same user, and "Google Chrome" is a single bar
  - [ ] 4.14 Triage every failure into one of spec §3.2's three buckets — blocks-release, documented-limitation, or follow-up — and write the bucket down against the item. An item silently skipped is a failed checklist (§5.2)

- [ ] 5.0 **(PC)** Session 3 — measure, then fix, live transcription
  - [ ] 5.1 Turn live transcription on and dictate 10 real clips of 10–30 s, capturing the smoke `Report` fields per clip: `WholePassFallback`, `StreamMs`, `StreamSegments`, `StreamedText`, `Text`, `TranscribeMs`
  - [ ] 5.2 Count how many of the 10 came back with `WholePassFallback == true`. That is the baseline, and it is the failure rule 15 exists to remove — the field already measures it, so no new instrumentation is needed
  - [ ] 5.3 Read the logged streamed text against the tail text for the failing clips and identify **why** no seam was found — a word boundary, a repeated phrase, a gap longer than `MinimumTailMs`. Rule 16: do not touch a constant before this is written down
  - [ ] 5.4 Tune `Stitch.TryJoinAllowingTailSkip` (`windows/Spit.Core/Asr/Stitch.cs:46`) and `StreamTail.OverlapMs` (`StreamTail.cs:15`, currently 1500) against those logs — not against the CI fixture, which is one clip of read speech
  - [ ] 5.5 Keep `StitchTests.cs`, `StreamTailTests.cs` and `StreamingPolicyTests.cs` passing throughout. If a tuning change needs one of them edited, the change is probably wrong
  - [ ] 5.6 Re-run the same 10 clips and check rule 15's two conditions: streamed+stitched text character-identical to one-pass on **≥ 8 of 10**, and release-to-final-text **≤ 1.3 × the one-pass time on all 10**
  - [ ] 5.7 If both hold, record it in `docs/SPIKES.md` and raise whether `LiveTranscription` should still default to `false` — that is Miguel's call, not the builder's. If either fails, leave the default at `false` and attach the logs to a numbered follow-up (rule 14)

- [ ] 6.0 Back-port the three Windows fixes to the Mac *(no PC needed — can run in parallel with 2.0–5.0)*
  - [ ] 6.1 Port `TryJoin` and `TryJoinAllowingTailSkip` from `windows/Spit.Core/Asr/Stitch.cs:36,46` into `mac/Voice/ASR/Stitch.swift` as `tryJoin` and `tryJoinAllowingTailSkip`, keeping Swift naming (rule 17)
  - [ ] 6.2 Find every caller of the Mac's `Stitch.join(streamed:tail:)` and switch the tail path to the try-variant, so a tail with no seam is **not** appended unconditionally — that unconditional append is the duplicated-words bug
  - [ ] 6.3 Port the Windows `StitchTests.cs` cases into `mac/VoiceTests/StitchTests.swift`, renamed to the Mac's convention (rule 20)
  - [ ] 6.4 Add the rule 18 guard: the Mac must refuse to latch while the model is still loading. Windows does this at `windows/Spit.App/App/Coordinator.cs:952` (`loading.IsReady` plus `machine.Phase is DictationPhase.Ready`); the Mac's `Coordinator` has no equivalent
  - [ ] 6.5 Add a test for 6.4 in `mac/VoiceTests/TapLatchTests.swift` or `DictationMachineTests.swift`: a double-tap during model load does not start a latched session
  - [ ] 6.6 **Audit before writing code (rule 19):** the Mac calls `streaming.finish()` on `.discardRecording` (`Coordinator.swift:251`); Windows finishes on two paths (`Coordinator.cs:540` and `:614`, the latter when `transcribeRequested` is false). Determine whether the Mac covers the second
  - [ ] 6.7 If 6.6 found a gap, fix it with a test. If it found none, write a line in `docs/SPIKES.md` naming both Mac paths — spec open question 4 is closed either way, and a no-op commit is worse than a recorded audit
  - [ ] 6.8 Run the Mac suite from `mac/`: `xcodebuild -project Voice.xcodeproj -scheme Voice -derivedDataPath build/test-dd -quiet test`. 143 tests plus whatever 6.3 and 6.5 added, 1 skipped, 0 failures

- [ ] 7.0 Close out: real-use latency, the docs that still claim Windows is unverified, and the follow-ups
  - [ ] 7.1 **(PC)** Do 20 real dictations on the PC in ordinary use, not as a test
  - [ ] 7.2 Run the latency SQL from `tasks/prd-spit-mac-windows.md` §5 and record p50 and p90 of `total_ms` grouped by `asr_model` (§5.6)
  - [ ] 7.3 Compare the `whisper.cpp/%` rows against the Mac's `whisperkit/%` rows in the same query — the namespacing already separates the populations, and this is the number that says whether parity was actually reached
  - [ ] 7.4 Update `README.md`: the Windows row no longer says "Never run on a physical PC", and the "What's missing" list loses items 1, 2 and 5
  - [ ] 7.5 Fill the marked placeholder in `SintraLabs/site/spit/index.html` (**outside this repo**) with 4.1's screenshots, and remove the copy describing Windows as the slower platform if rule 8 fired
  - [ ] 7.6 Tick off `tasks/tasks-spit-mac-windows.md` items 1.3–1.7 (the five spikes) and 9.1–9.2, pointing each at the `docs/SPIKES.md` section that settles it
  - [ ] 7.7 Record what is still open: item 9.3 (a spending ceiling for friends' cleanup on the Ollama key) and any 4.14 follow-ups, so closing this list does not quietly drop them
