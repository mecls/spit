# Windows Parity — Implementation Spec

Status: draft, not approved. Every task in §3 requires Miguel's PC; none of it can be done from the Mac.

## 1. Objective

Make the Windows client as trustworthy as the Mac one by **running it on a real PC for the first time**,
and fixing what that finds.

The Windows client is not missing code. `windows/` holds 213 platform-neutral tests (108 of them the
Mac's, ported by name) plus Windows-only ones; CI on a GitHub Windows runner builds the whole UI,
pastes into a real Notepad, transcribes a fixture, packs `Spit-Setup.exe`, and installs, runs and
uninstalls it. In places it is ahead of the Mac — `HookWatchdog`, `ElevationProbe`, `MenuMask`,
`SingleInstance`, `LaunchAtLogin` have no Mac equivalent.

What it has never had is **a real key press, a real microphone, a real GPU, or a real user**. Five
spikes (S1–S5) were specified before the UI was built and never run, so five design decisions are
currently sitting on their documented fallbacks rather than on evidence. A 15-item manual checklist
(the spec said 13; items 14-15, sleep/lock and uninstall, were missed) has never been executed. `README.md`'s "What's missing" list opens with exactly this.

Two things change now that were not true when those decisions were made:

- **The PC has a discrete GPU.** Every published Windows number in this repo comes from a GPU-less
  4-vCPU CI runner, where large-v3-turbo took 53–85 s for 12.5 s of audio and Whisper small took
  12.9 s (RTF ≈ 1.03 — barely faster than real time). The installer already ships a Vulkan runtime and
  `WhisperTranscriber` already tries it first. Nobody has ever measured it. The Windows default model
  was chosen *because* "friends' PCs mostly have no GPU Vulkan can use" (`ModelCatalog.cs:7-10`), and
  that reasoning has never been tested against hardware that does.
- **Live transcription and the Mac back-port are in scope.** Follow-ups 9.1 and 9.2 of
  `tasks/tasks-spit-mac-windows.md` are part of this work, not deferred.

Cutting the 0.2.0 release is deliberately **not** part of this (§6). This spec ends when Windows is
demonstrably good; shipping it is a separate decision.

## 2. Business rules (invariants — never violate)

### A. How the spikes are run

1. **A spike is not done until its evidence is committed.** Each of S1–S5 writes its raw output into
   `docs/SPIKES.md` — the actual numbers, the actual `vkCode`s, the actual log lines — not a sentence
   saying it passed. *Reason:* four of the five spikes exist to settle a rule that currently has a
   fallback; "it worked" cannot be re-checked in six months when the rule looks arbitrary and someone
   deletes it.

2. **Each spike's failure branch is already decided. Do not re-litigate it mid-session.**
   From `tasks/prd-spit-mac-windows.md` §5:

   | Spike | Settles | On failure |
   |---|---|---|
   | S1 | One-pass latency, CPU vs Vulkan, all three models | Open question 3 decides the default |
   | S2 | Which process triggers the first `WM_RENDERFORMAT` under Clipboard History | Keep rule 32's 1.5 s restore |
   | S3 | Whether `ElevationProbe` reads an elevated foreground process correctly | Rule 30's "access-denied counts as elevated" |
   | S4 | Right Ctrl / Right Alt / AltGr key events on a pt-PT keyboard | **Drop `rightAlt`; Right Ctrl only** |
   | S5 | Installing 0.2.1 over 0.2.0 | The `/spit` page tells users to uninstall first |

   *Reason:* these were chosen with a clear head before anyone had a result to be attached to. A spike
   that fails and then gets argued with is not a spike.

3. **Run the spikes before the checklist, in order S4 → S3 → S2 → S1 → S5.** S4 can change which
   hotkey exists, which invalidates every checklist item that presses one. S1 can change the default
   model, which changes the latency every other item feels. *Reason:* the §5 checklist assumes the
   hotkey and the model are settled; running it first means running it twice.

### B. Speed and the model decision

4. **S1 measures 2 runtimes × 3 models × 2 fixtures = 12 one-pass timings, each repeated 3 times, and
   reports the median.** Runtimes are forced with the existing `SPIT_WHISPER_RUNTIME=cpu` and `=vulkan`
   (`WhisperTranscriber.RuntimeVariable`, `windows/Spit.App/Asr/WhisperTranscriber.cs:27`). Fixtures are
   `mac/Fixtures/en.wav` and `mac/Fixtures/pt-synthetic.wav`. **Discard the first run of each pair** —
   it includes model load and first-inference warm-up, and the Mac's own spike measured that the first
   real transcription is ~2× slower even after prewarm.

5. **A Vulkan timing is only valid if `BackendLog` proves the GPU was used.**
   `WhisperTranscriber.BackendLog` captures whisper.cpp's own backend lines and the Vulkan device's
   name. Whisper.net's `RuntimeLibraryOrder` falls through `Vulkan → Cpu → CpuNoAvx` silently, so a
   "Vulkan" run that quietly loaded the CPU library looks like a slow GPU rather than a missing one.
   Record the device name string in `docs/SPIKES.md` next to every Vulkan number. *Reason:* this is the
   single most likely way to produce a confidently wrong conclusion about the model.

6. **The Windows default model changes to `ggml-large-v3-turbo-q5_0.bin` if and only if its median
   Vulkan one-pass time is ≤ 0.25 × audio duration.** For `en.wav` (12.6 s) that is **≤ 3.15 s**.
   *Reason for that number:* it is the Mac's own asserted bound — `WhisperKitTranscriberTests` fails if
   `en.wav` takes 3.0 s or more — so it is the existing, shipped definition of "fast enough" in this
   codebase rather than a new one invented here. If turbo misses it and small clears it, small stays
   the default and `ModelCatalog.cs:7-10`'s comment is rewritten to cite the measured GPU number
   instead of the assumption about friends' PCs.

7. **The default is a per-machine recommendation, never a silent migration.** Whatever S1 decides,
   an existing install keeps the model it already downloaded. Changing `ModelCatalog.DefaultFile`
   affects new installs only. *Reason:* a default change that silently re-downloads 574 MB on someone
   else's metered connection is a worse bug than a slow model.

8. **If the GPU makes turbo viable, the Mac and Windows converge on the same model family and the
   `/spit` page stops describing Windows as the slower platform.** Both clients then run
   large-v3-turbo. *Reason:* the accuracy gap between small and large-v3-turbo is the only remaining
   user-visible quality difference between the two clients, and closing it is what "just like the Mac
   one" actually means.

### C. Things that must be true whatever the spikes say

9. **A dictation never appears in Clipboard History.** Checklist item 4. This is a privacy invariant,
   not a polish item: the app's promise is that transcripts do not leak, and Win+V is a system-wide,
   cross-device, persistent store. If S2 shows text landing in history, the feature is broken and the
   fix ships before the checklist continues — it does not become a documented limitation.

10. **The clipboard always comes back.** Checklist item 3: copy an image, dictate, press Ctrl+V, the
    image pastes. Rule 32 already errs late (1.5 s) precisely because restoring early is the worst
    failure — Ctrl+V pasting the dictation instead of the user's own clipboard. S2 may let that delay
    shrink; it must never let it reach zero.

11. **The hotkey never swallows a key.** Rule 29: every `KeyboardHook` callback returns
    `CallNextHookEx`. S4 must confirm that Right Ctrl+C still copies and AltGr+2 still types `@` on the
    pt-PT keyboard. *Reason:* a dictation tool that eats a keystroke in another app is worse than one
    that does not launch.

12. **Right Alt must not open menus.** Rule 35: pressing and releasing Alt alone activates the menu bar
    in classic Win32 apps, and `MenuMask` injects `vkE8` to suppress it. S4 checks this in **both**
    Notepad and File Explorer, because they use different menu implementations. If masking fails in
    either, rule 2's fallback applies: drop `rightAlt`, Right Ctrl only.

13. **An elevated window is a documented limitation, not a silent failure.** Checklist item 5: with
    Notepad running as administrator, the hotkey does nothing there, and the bar's mic button session
    goes clipboard-only with the admin message. The failure that is unacceptable is the *silent* one —
    the user speaks, nothing appears, and nothing explains why.

### D. Live transcription (follow-up 9.1)

14. **Live transcription is fixed or it stays off — it does not ship half-working.** Today it falls
    back to a whole-recording pass: correct text, but 37 s instead of 12.9 s on the CI clip.
    `Preferences`/`Settings` keep `LiveTranscription: false` by default until rule 15 passes.

15. **A fixed live transcription means the tail pass re-reads only the overlap, and the seam is found.**
    Two measurable conditions, on at least 10 real dictations of 10–30 s:
    - **Text:** the streamed+stitched result is character-identical to the one-pass result for the same
      audio, on at least 8 of 10.
    - **Time:** total time from hotkey release to final text is ≤ 1.3 × the one-pass time for the same
      clip. *Reason:* the current failure is not wrong text, it is that `Stitch` finds no seam and the
      code re-transcribes everything, so the whole point of streaming is lost. A time bound is the only
      thing that detects that regression.

    Tune `Stitch.TryJoinAllowingTailSkip` and `StreamTail.OverlapMs` (currently 1500 ms,
    `windows/Spit.Core/Asr/StreamTail.cs:15`) against logged streamed and tail texts from real
    dictations — not against the CI fixture, which is a single clip of read speech.

16. **Log the streamed and tail text before tuning anything.** The existing failure was diagnosed from
    one CI clip. *Reason:* `StreamingPolicy.RequiredSegmentsForConfirmation = 2` and
    `MinimumNewAudioSeconds = 1` were ported from WhisperKit's behaviour, not derived from Windows
    measurements; tuning them without data is guessing twice.

### E. Back-porting to the Mac (follow-up 9.2)

17. **The Mac gets `Stitch.TryJoin` and `TryJoinAllowingTailSkip`.** Verified gap:
    `windows/Spit.Core/Asr/Stitch.cs` exposes `Join`, `TryJoin` and `TryJoinAllowingTailSkip`;
    `mac/Voice/ASR/Stitch.swift` exposes only `join(streamed:tail:)`. The Mac therefore appends the tail
    unconditionally when no seam is found, which duplicates words. Port the Windows logic and the
    Windows tests, keeping the Mac's naming.

18. **The Mac refuses to latch while the model is still loading.** Verified gap: Windows guards this at
    `Coordinator.Apply` (`windows/Spit.App/App/Coordinator.cs:372`, no latch without a running capture);
    the Mac's `Coordinator` has no equivalent check. A double-tap during the first launch's model load
    currently starts a latched session against a transcriber that cannot transcribe.

19. **The Mac never leaves a live stream running past its dictation — on every path, not just discard.**
    The Mac calls `streaming.finish()` on `.discardRecording` (`Coordinator.swift:251`). Windows finishes
    the stream on two paths (`Coordinator.cs:540` and `:614`, the latter when `transcribeRequested` is
    false). **Audit the Mac for the second path before assuming this one is already done** — if it is,
    record that in §5 rather than writing a no-op change.

20. **A back-ported fix ships with the Windows test, renamed to the Mac's convention.** The 108
    shared tests were ported by name in the other direction; keep the correspondence. *Reason:* a fix
    ported without its test is a fix that regresses the next time the two clients diverge.

### F. Evidence for the page

21. **Capture screenshots of every warning, on the way past.** SmartScreen, the Edge download warning,
    and Smart App Control if it fires. `SintraLabs/site/spit/index.html` has a marked placeholder
    waiting for them. *Reason:* this costs seconds during the checklist and a whole second PC session
    afterwards if skipped.

## 3. Flows

### 3.1 Session 1 — the spikes (in rule 3's order)

Run from a build of the current `main`, installed from `Spit-Setup.exe` as a user would.

1. **S4 — keys.** `Spit.exe --key-log` (`Program.KeyLogFlag`). Hold Right Ctrl; hold Right Alt;
   AltGr+2 on the pt-PT layout; Right Ctrl+C. Record `vkCode`, `scanCode`, flags and repeats for each.
   Then check `vkE8` menu masking in Notepad **and** File Explorer. → rules 27, 28, 35, and rule 12 here.
   *Fails → drop `rightAlt` now, before anything else is tested.*
2. **S3 — elevation.** From non-elevated Spit, read the foreground process with Notepad running as
   administrator, then normal Notepad. `ElevationProbe` must answer correctly for both. → rule 30.
3. **S2 — clipboard.** Clipboard History **on**. Delayed-render text carrying
   `ExcludeClipboardContentFromMonitorProcessing`. Paste into Notepad, Chrome and Word; log which
   process triggers the first `WM_RENDERFORMAT`. → rules 32, 33, and rules 9–10 here.
4. **S1 — speed.** Rule 4's matrix. Record every median and every `BackendLog` device string. → rule 6
   decides the default model.
5. **S5 — upgrade.** Install a `0.2.0-test` Setup.exe, then a `0.2.1-test` one. Confirm one install,
   and that the data directory, token and Run entry survive. → rule 44.

Each spike writes to `docs/SPIKES.md` as it completes (rule 1), not in a batch at the end.

### 3.2 Session 2 — the 15-item checklist

`tasks/prd-spit-mac-windows.md` §5 "Windows manual checklist", run against an installer downloaded
through Edge, with screenshots (rule 21). Run only after §3.1 is complete and any rule-2 fallback is
applied, because items 2, 5, 6 and 11 all press a hotkey that S4 may have removed.

Anything that fails is triaged into one of three buckets, and the bucket is recorded:
- **Blocks the release** — fix it, then re-run that item and the ones around it.
- **Documented limitation** — goes on the `/spit` page in the user's words, like the admin-window case.
- **Follow-up** — a numbered task, not a silent omission.

### 3.3 Session 3 — live transcription (rules 14–16)

1. Turn live transcription on. Dictate 10 real clips of 10–30 s. Log streamed text, tail text, seam
   result, and both timings per clip.
2. Read the logs. Only then tune `Stitch.TryJoinAllowingTailSkip` and `StreamTail.OverlapMs`.
3. Re-run the 10 clips. Rule 15's two conditions both hold, or the feature stays off by default and
   becomes a follow-up with the logs attached.

### 3.4 The Mac back-port (rules 17–20)

Done on the Mac, not the PC, and independent of the sessions above. Each of rules 17, 18 and 19 is a
separate commit with its test. Rule 19 begins with an audit that may find there is nothing to do.

## 4. Surfaces

No new surface. This work changes what existing surfaces do, in three places:

- **`ModelCatalog.DefaultFile` and `Strings.ModelLabel*`** — if rule 6 fires, the default entry changes
  and the labels stop implying small is the fast choice. New installs only (rule 7).
- **The `/spit` page** — the warning screenshots from rule 21 fill the marked placeholder, and rule 8
  may remove the copy describing Windows as slower.
- **`docs/SPIKES.md`** — gains five spike sections and, if §3.3 runs, a live-transcription section.

If S4 fails and `rightAlt` is dropped, the hotkey picker in Settings loses an option and
`HotkeyChoice` loses a case. That is the one user-visible removal this spec can produce.

## 5. Validation

### 5.1 Spikes — done when

All five have a section in `docs/SPIKES.md` containing raw evidence, not a verdict (rule 1):

| Spike | The evidence that must be there |
|---|---|
| S1 | 12 median timings + the `BackendLog` Vulkan device string for every Vulkan row |
| S2 | The process name that sent the first `WM_RENDERFORMAT`, for Notepad, Chrome and Word |
| S3 | `ElevationProbe`'s answer for elevated Notepad and for normal Notepad |
| S4 | `vkCode`, `scanCode`, flags and repeat counts for all four gestures, plus masking result in Notepad and Explorer |
| S5 | Install count, and the state of the data directory, token and Run entry after the upgrade |

### 5.2 The checklist — done when

All 15 items pass, or a failing item has been triaged into one of §3.2's three buckets **and written
down**. An item silently skipped is a failed checklist.

### 5.3 The model decision — done when

`docs/SPIKES.md` states the median Vulkan one-pass time for `ggml-large-v3-turbo-q5_0.bin` on
`en.wav`, and `ModelCatalog.cs`'s header comment cites that measured number instead of the current
assumption about friends' PCs — whichever way rule 6 lands.

Threshold, restated so it is checkable without re-reading rule 6: **≤ 3.15 s for `en.wav`.**

### 5.4 Live transcription — done when

Over 10 real dictations of 10–30 s: streamed+stitched text is character-identical to one-pass on ≥ 8,
and release-to-final-text is ≤ 1.3 × one-pass on all 10. Otherwise `LiveTranscription` stays `false`
and the logs are attached to a follow-up.

### 5.5 The Mac back-port — done when

- `mac/` gains `Stitch.tryJoin` and `tryJoinAllowingTailSkip` with the Windows tests ported by name,
  and the full Mac suite passes (currently 143 tests, 1 skipped).
- A double-tap during model load does not start a latched session — covered by a new test in
  `DictationMachineTests` or `TapLatchTests`.
- Rule 19's audit is recorded: either a commit, or a line in `docs/SPIKES.md` saying the Mac already
  covers both paths and naming them.

### 5.6 Latency, from real use

After the checklist, 20 real dictations on the PC, then the §5 SQL from `tasks/prd-spit-mac-windows.md`.
Record p50 and p90 of `total_ms`, grouped by `asr_model`, so the Windows population can be compared to
the Mac's directly — `dictations.asr_model` already namespaces the Windows client as
`whisper.cpp/<file>`.

## 6. Out of scope

- **Cutting and publishing the 0.2.0 release.** Tagging `v0.2.0`, the draft release, publishing,
  deploying `site/`. This spec ends at "Windows is verified good"; shipping is a separate decision that
  should be made with these results in hand, not bundled with them.
- **Code signing and SmartScreen.** An EV certificate would remove the SmartScreen warning and the
  Smart App Control block. Already accepted as a documented limitation (open question 2, answered
  2026-09-13); rule 21 captures the warnings rather than removing them.
- **Adding macOS CI.** Real and needed — the Mac's 143 tests are run by hand — but this work must not
  be the thing that first requires it.
- **Parakeet on Windows.** Settled separately in `tasks/prd-parakeet-english-engine.md` §6: two
  independent implementations refuse the app's decode contract.
- **Revisiting the deliberate platform differences in `prd-spit-mac-windows.md` §2G.** Rules 27–44
  (hotkey choice, no Fn key, admin windows, password fields pasting normally, IANA time zones) are
  decisions, not gaps. Only rule 41 — the default model — is reopened here, and only because the GPU
  assumption behind it has changed.

## 7. Open questions

1. **What is the PC's GPU, and does Vulkan actually bind to it?** Everything in §2B depends on this and
   nothing in the repo can answer it. `BackendLog`'s device string settles it in the first five minutes
   of S1. **Decided by: S1.**
2. **If turbo clears rule 6's bar on this GPU but the median friend's PC has no GPU, should the default
   still change?** Rule 6 as written decides on Miguel's hardware, which is the only hardware available.
   The honest alternative is to make the *recommendation* conditional on a detected Vulkan device at
   first run — more code, and unbuildable until S1 says whether it would ever fire.
   **Decided by: Miguel, once S1 has a number.**
3. **How many of the 15 checklist items can fail before the answer is "not ready" rather than "fix
   these three"?** Not specified, deliberately — it depends which ones. Items 3 and 4 (clipboard
   restore, Win+V) are rules 9–10 and are unconditional; the rest are judgement.
   **Decided by: Miguel, during §3.2.**
4. ~~**Is rule 19 a real gap or already covered?**~~ **Closed 2026-09-22: a real gap.** The Mac finished
   a stream in exactly two places — `.discardRecording` and `.transcribe` — and a stop the reducer
   rejects ("nothing heard": under `minimumMs`, or no speech) reached neither, so the stream was left
   running and the next dictation inherited it. Fixed the way Windows does it, with a
   `transcribeRequested` flag read straight after `send(.audioStopped(…))`.
5. **A spending ceiling for friends' text cleanup on the Ollama key.** Open question 6 from the
   original spec, still open, still unrelated to Windows — recorded here only so it is not lost when
   `tasks-spit-mac-windows.md` is closed out. **Decided by: Miguel.**
