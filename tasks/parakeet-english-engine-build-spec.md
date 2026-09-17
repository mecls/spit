# Parakeet English Engine (macOS) — Build Spec

**One-line mission:** Add NVIDIA Parakeet TDT 0.6B v2 to Spit as an opt-in, English-only on-device
ASR engine on macOS, running beside WhisperKit rather than replacing it — behind a measured
go/no-go gate that can close the work unbuilt.

Companion documents in this repo, both authoritative and more detailed than this one:
`tasks/prd-parakeet-english-engine.md` (the spec; its §2 rules are cited throughout as "rule N")
and `tasks/tasks-parakeet-english-engine.md` (the ordered task list). Read both before starting.

---

## 0. How to use this document

You are building this unattended. Nobody will answer questions while you work, so:

1. **This document outranks your instincts.** Where it names a rule, a number, or a stack
   choice, follow it even if you would have chosen differently.
2. **When you hit something genuinely unspecified, decide and keep moving.** Do not stall
   and do not invent scope. Pick the smallest choice consistent with §1 and §2, append it to
   the Decisions Log in §16 with one line of reasoning, and continue.
3. **§13 is your finish line.** Check your work against those scenarios rather than waiting
   for a human to confirm. Work is done when they pass, not when the code looks finished.
4. **The non-goals in §2 are binding.** If a change would be genuinely useful but sits
   outside them, note it in §16 as a suggestion and do not build it.

### 0.1 THIS RUN IS STAGED. READ THIS BEFORE ANYTHING ELSE.

This work cannot be completed in one unattended run, and trying is the main way to get it wrong.
It is split into three stages with a **hard human handoff** between Stage 1 and Stage 2.

| Stage | Who | What | Ends when |
|---|---|---|---|
| **Stage 1** | **You, unattended. This run.** | Build the measurement harness. No engine code. | §13 AC-1 passes. **Then STOP.** |
| Stage 2 | Miguel, by hand | Record 35 audio clips, run the harness, read the verdict | `docs/SPIKES.md` has a GO or a NO-GO |
| Stage 3 | A later agent run, only on GO | Build the engine — task list 2.0 → 7.0 | §13 AC-3 … AC-6 pass |

**Your job this run is Stage 1 and nothing else.** Concretely: task list items 0.1 and 1.6–1.9.

**You must stop at the end of Stage 1.** Do not record audio — you cannot; there is no
microphone and synthesised speech would invalidate the gate (§2). Do not proceed to task 2.0.
Do not write `ParakeetTranscriber.swift`. The gate exists because two open upstream bugs produce
**silent empty transcripts** on audio shaped exactly like push-to-talk dictation, and building
7–12 days of engine on an unmeasured assumption is the specific failure this staging prevents.

End your run by printing the handoff block in §15.1.

Stages 2 and 3 are documented here so that the later run has full context. **Do not execute them.**

---

## 1. Primary user and outcome

**Primary user:** Miguel, dictating into his own Mac — and the handful of friends and teammates
he has shipped Spit to. One person, one machine, holding a hotkey and talking. Not "users of
dictation software".

**Outcome:** Measurably more accurate English dictation than WhisperKit `large-v3-turbo` gives
today, on spontaneous speech, without giving up Portuguese, mixed pt/en, or the dictionary.

**Narrowing principle:** **Parakeet is an addition, never a replacement, and never automatic.**
Every time a choice feels tempting — route by language, make it the default, extend it to
Portuguese, port it to Windows — the answer is no, and §2 says why. WhisperKit remains the
default engine and the only engine that handles Portuguese and code-switched speech.

## 2. Non-goals

- **Windows, entirely** — two independent implementations refuse the app's decode contract for
  Parakeet. sherpa-onnx's hotwords need `modified_beam_search`, which on this exact model returns
  hallucinated or empty text ~20% of the time (k2-fsa/sherpa-onnx#3267, open). Whisper.net's
  merged Parakeet bindings throw `NotSupportedException` for initial prompts, language selection,
  language detection, temperature and temperature fallback — every line of
  `windows/Spit.App/Asr/WhisperTranscriber.cs:204-208`. Nothing is published to NuGet in any case.
- **Parakeet v3** — worse English than v2 (leaderboard average 4.86 vs 4.70; SPGISpeech 1.94 →
  3.63) and worse Portuguese than Whisper. Its 25 languages buy nothing, because rule 3 keeps
  Portuguese on WhisperKit.
- **Replacing WhisperKit** — the same work minus a picker entry, plus deleting the fallback that
  rules 7–9 exist to fall back to.
- **Automatic engine routing by language** — impossible, not deferred. Parakeet emits no language
  identifier; NVIDIA's maintainer, asked whether a language can be forced or the detected one read
  back, answered *"No. Only with canary model"*.
- **Portuguese or mixed pt/en on Parakeet** — v2 is English-only and produces confident, plausible,
  wrong English on Portuguese input.
- **Restoring the previous language setting when switching back to Whisper** — deliberate; see
  rule 3 and §6.3.
- **macOS CI** — real and needed, but this branch must not be the thing that first requires it.
- **Synthesising the Phase 0 corpus with TTS** — the corpus must come from a real microphone on
  the target hardware. `mac/Fixtures/pt-synthetic.wav` already demonstrates the limits of
  synthetic fixtures, and the failure modes being tested for are microphone- and
  pause-dependent.

## 3. Journeys

### Journey A — Stage 1: build the harness (happy path, **this run**)
1. You branch `feat/parakeet-english-engine` off `main`.
2. You add the FluidAudio SPM package to `mac/project.yml` and run `xcodegen generate`.
3. You add `mac/VoiceTests/WordErrorRate.swift` — a test-only word-level WER scorer.
4. You add `mac/VoiceTests/ParakeetSpikeTests.swift`, which loads clips from
   `mac/Fixtures/spike/`, transcribes each three ways, and prints a scoreable table.
5. You run the full default suite. It passes, and the spike test **skips** — because
   `VOICE_ASR_TESTS` is unset and the corpus does not exist yet.
6. You print the §15.1 handoff block and stop.

### Journey B — Stage 1 unhappy path: the corpus is not there
1. The spike test runs with `VOICE_ASR_TESTS=1` but `mac/Fixtures/spike/` is empty or
   `references.json` is missing.
2. It must **fail with a clear, actionable message**, not crash on a nil unwrap and not silently
   pass on zero clips: `"Phase 0 corpus missing: expected 35 clips and references.json under
   mac/Fixtures/spike/. See tasks/tasks-parakeet-english-engine.md §1.1–1.5."`
3. A zero-clip run reporting success is the specific bug this journey exists to prevent — it would
   read as a passed gate.

### Journey C — Stage 3: dictating on Parakeet (happy path, **not this run**)
1. User opens Settings › Model, picks the Parakeet entry, clicks **Download**. ~483 MB, then a
   CoreML/ANE specialization pass.
2. The language picker greys out and shows the §4 lock copy. `language` is written as `"en"` to
   `Preferences` and pushed to the server.
3. User holds the hotkey, speaks English, releases. `EnergyGate` confirms speech; the buffer is
   trimmed and re-padded; Parakeet decodes; vocabulary boosting corrects "Miraside"; refined text
   lands at the cursor.

### Journey D — Stage 3 unhappy path: Parakeet returns nothing (**not this run**)
1. User holds the hotkey, speaks two words, releases. `EnergyGate.hasSpeech` is true.
2. Parakeet returns `""` — the failure mode of NeMo #15757 and FluidAudio #746.
3. The engine retries **once** with the untrimmed buffer.
4. Still empty → the existing "nothing heard" HUD state appears. **An empty string is never
   pasted, and the failure is never silent.** The occurrence is counted by §13 AC-4's check and,
   in production, by the `word_count = 0` query in §12.

## 4. Screens and states

Stage 1 adds **no user-facing surface at all** — it adds a skipped test and a build dependency.
The block below is Stage 3's surface, documented for the later run. Do not build it this run.

### Settings › Model tab (`mac/Voice/UI/SettingsView.swift`, `ModelTab` at `:180`)
- **Primary action:** choose the ASR engine and model, and download it.
- **Secondary actions:** Reload (always enabled — the existing recovery path out of a stuck
  `.modelLoading`), Delete (disabled for the model currently loaded into the transcriber).
- **Empty:** picker shows all three entries; the Parakeet row's label carries **"English only"**
  in the label text itself, not in helper text below it, because the label is the only string
  guaranteed to be read. `Strings.activeModel(...)` disambiguates picked-vs-loaded.
- **Loading:** existing `coordinator.modelStatus` text ("loading NN %"), driven by
  `send(.modelProgress(_:))`. Parakeet's first load also pays a CoreML/ANE specialization cost of
  unknown length — the status must not appear hung during it; if no better signal is available,
  show an explicit "preparing model (first run only)" state rather than a frozen percentage.
- **Error:** existing pattern — failures surface through `coordinator.modelStatus`, and the
  always-enabled **Reload** button is the recovery path. Delete failures render in red via
  `deleteError` / `Strings.modelDeleteError(_:)`. Do not invent a second error mechanism.
- **Success:** `modelStatus` reads ready, `refreshDownloaded()` flips Download to disabled and
  Delete to enabled, and `Strings.activeModel(...)` names the Parakeet entry.

### Settings › Model tab — language control (Stage 3)
- **Default:** `Picker` over auto / pt / en, pushing to the server on change via
  `coordinator.sync.push(language:)`.
- **When a `.parakeet` entry is active:** rendered **disabled**, showing verbatim:
  > **English only.** This model does not recognise Portuguese and cannot detect which language you
  > spoke. Switch back to Whisper for Portuguese or mixed speech.

  Naming the *cannot detect* consequence is the point; "English only" alone reads as a
  recommendation rather than a hard limit.

## 5. Capabilities

**Stage 1 — build these, this run:**
- [ ] Feature branch `feat/parakeet-english-engine` off `main`
- [ ] FluidAudio SPM dependency, pinned to an exact version, in `mac/project.yml`
- [ ] `WordErrorRate.swift` — test-only word-level WER scorer
- [ ] `ParakeetSpikeTests.swift` — opt-in Phase 0 harness, three engines, per-clip table
- [ ] Missing-corpus guard that fails loudly (Journey B)
- [ ] First-launch ANE specialization timing, reported separately from steady-state decode
- [ ] Default suite still green; spike test skips cleanly

**Stages 2–3 — do NOT build this run:**
- [ ] Phase 0 corpus recorded and scored; `docs/SPIKES.md` verdict *(Stage 2, human)*
- [ ] Engine-tagged `ModelManager`, engine factory, `Transcriber` streaming capability *(2.0)*
- [ ] `ParakeetTranscriber` — trim, 0.3 s floor, empty-decode fallback, `language == "en"` *(3.0)*
- [ ] Vocabulary boosting with similarity floor and short-utterance skip *(4.0)*
- [ ] Settings entry and language lock *(5.0)*
- [ ] Live transcription via `SlidingWindowAsrManager` *(6.0)*
- [ ] `asrModel` namespacing, docs, CC-BY-4.0 attribution *(7.0)*

## 6. Invariants — enforce on the server

This is a native macOS app, not a client/server product: **there is no server to enforce most of
these.** The honest equivalent is that each rule is enforced at a single named chokepoint in code
and pinned by a test, rather than by a disabled control in the UI. Each rule below names its
enforcement point. The full set is rules 1–21 of `tasks/prd-parakeet-english-engine.md` §2; the
ones most likely to be violated by a build agent are restated here.

1. **The gate is hard and this run stops at it.** No engine code, no `ModelManager` refactor, no
   Settings change before `docs/SPIKES.md` records a passing Phase 0.
   *Enforced by:* §0.1 and your own stop. There is no automated check — this is the one invariant
   that depends on you obeying it.
2. **WhisperKit stays the default; nothing migrates.** `Preferences.defaults[Key.modelId]` remains
   `"large-v3-v20240930_turbo_632MB"`. No existing install changes engine on update.
   *Enforced by:* `mac/Voice/Storage/Preferences.swift`, pinned by existing tests.
3. **Selecting Parakeet pins `language = "en"`, and switching back does NOT restore it.** Written
   to `Preferences.language` and pushed via `coordinator.sync.push(language:)` to
   `PUT /v1/settings`, whose enum is `['auto','pt','en']` at `server/src/routes/settings.ts:12`.
   *Reason:* silently rewriting a server-side setting on a model swap is worse than making the
   user set it back once. *Enforced by:* `ModelTab`'s `onChange`, pinned by §13 AC-5.
4. **`Transcript.language` is the constant `"en"` on Parakeet — never `nil`, never detected.**
   Safe because the field is telemetry only: `SkipGate.reasonToClean(raw:mode:language:segments:)`
   at `mac/Voice/Refine/SkipGate.swift:68` accepts `language:` and never reads it — both filler
   lists always apply — and the server's `buildSystemPrompt` does not use `languageDetected`
   either. Its only consumer is the `dictations.language_detected` column. **Put this reasoning in
   a code comment** so nobody later "fixes" it into a nil.
5. **Trim both ends of the buffer before Parakeet sees it; never trim on the Whisper path.**
   Trim to the first and last sample over `EnergyGate`'s speech threshold, re-pad to exactly
   0.20 s each side, then floor the whole buffer at 0.3 s (below which FluidAudio throws
   `ASRError.invalidAudioData`). *Reason:* NeMo #15757 (400 ms trailing silence → `""`) and
   FluidAudio #746 (0.4–0.6 s leading silence → zero tokens, non-monotonic).
   `AudioRecorder.stop()` at `mac/Voice/Audio/AudioRecorder.swift:178` returns `buffer.drain()`
   untrimmed, so every real dictation carries both. *Enforced by:* `ParakeetTranscriber` only —
   changing `AudioRecorder` would invalidate the existing 143 tests.
6. **An empty decode is never a result.** Empty or whitespace-only output where
   `EnergyGate.hasSpeech` was true → retry once untrimmed → "nothing heard" HUD. **Never paste an
   empty string; never fail silently.** *Enforced by:* `ParakeetTranscriber`, pinned by §13 AC-4.
7. **The Whisper path must be byte-identical after the `ModelManager` refactor.**
   `ModelManagerTests.swift` must pass **unmodified**; if it needs editing, the refactor is wrong.
   *Reason:* a changed folder string silently re-downloads a 632 MB model on every existing
   install. *Enforced by:* §13 AC-3.
8. **Vocabulary boosting never fires under 3 words and never below 0.60 similarity.**
   *Reason:* FluidAudio #899 substituted `"Hey"` → `"Codex"` at similarity 0.12. A wrong proper
   noun in a two-word dictation is worse than an honestly-wrong transcript.
   *Enforced by:* `ParakeetTranscriber`, pinned by §13 AC-6.
9. **Ship the `.int8V2` encoder, not FluidAudio's default `.int8`.** The default is 6-bit
   palettized and corrupts words in the first ~5.5 s of a window under specific right-context
   (FluidAudio #760); NeMo PyTorch and parakeet-mlx get the same audio right, so it is an export
   artefact. *Enforced by:* `ParakeetTranscriber.prepare`, re-verified on any version bump.
10. **A partially-downloaded model reports `isDownloaded == false`.** Including the boosting
    model: a half-fetched pair is not downloaded. *Reason:* an interrupted download reporting
    success is the bug this prevents. *Enforced by:* `ModelManager.isDownloaded`.
11. **The default test suite stays fast and network-free.** Model-dependent tests stay behind
    `VOICE_ASR_TESTS`. *Reason:* there is no macOS CI — a human runs every test, and a suite
    needing a 480 MB download to go green stops being run. *Enforced by:* `XCTSkipUnless`.

## 7. Data model and lifecycle

No database and no migrations — this is a desktop app writing to three places.

### On-disk model folders
- **Represents:** downloaded CoreML weights.
- **Owned by:** the app, under `~/Library/Application Support/Voice/models` (`ModelManager.modelsDir`).
- **States:** absent → partially written → complete. Completeness is `requiredEntries` all present.
- **Reversible:** yes — Delete removes a folder; re-download restores it.
- **Never silently deleted:** the model currently loaded into the transcriber. The Delete button is
  already `.disabled(modelId == coordinator.activeModelId)`. Deleting a Parakeet entry must remove
  **both** the ASR model and its ~106 MB boosting model, or `isDownloaded` will disagree with disk.

### `Preferences` (UserDefaults)
- **Represents:** local settings; server settings (mode / language / hotkey) are mirrored here as
  an offline cache only — **the server wins on every sync.**
- **Keys this work touches:** `modelId` (default unchanged, rule 2), `language` (written by the
  lock, rule 3), `liveTranscription` (stays `false`).

### `dictations` rows (server-side, existing)
- **Touched only via `asr_model`**, which becomes namespaced: `"whisperkit/<id>"` and
  `"fluidaudio/parakeet-tdt-0.6b-v2"`, matching the Windows client's existing
  `"whisper.cpp/<file>"`. **Do not backfill existing rows** — this changes successors, not history.
- No schema change. `/v1/refine`, `/v1/settings` and `/v1/dictations` are unchanged.

### Phase 0 corpus (`mac/Fixtures/spike/`)
- **Represents:** 35 real recordings plus `references.json`.
- **Permanent and committed.** It outlives this spec: `Preferences.liveTranscription`'s own code
  comment names the absence of real audio fixtures as the blocker for measuring streamed-vs-one-pass
  WER, and this corpus unblocks that whether or not Parakeet ever ships.

## 8. Users, auth and permissions

**Single-user, no roles.** Spit is a personal desktop app; there is no multi-tenant surface and no
permission model to enforce. The only credential is a bearer device token in the macOS Keychain
(service = the server URL), read by `RefineService` and written only by the Settings › Server tab's
button actions. This work neither reads nor changes it.

OS-level permissions already granted and unchanged by this work: Microphone, Input Monitoring,
Accessibility. Note they are bound to the app's code signature — do not change
`PRODUCT_BUNDLE_IDENTIFIER`, `CODE_SIGN_IDENTITY` or `DEVELOPMENT_TEAM` in `mac/project.yml`, or
every grant silently drops and the app appears broken in three different ways.

## 9. Technical direction

Everything below already exists in the repo. Change none of it.

- **Language / runtime:** Swift, `SWIFT_VERSION: "5.0"`, `SWIFT_STRICT_CONCURRENCY: targeted`.
  Note FluidAudio's `Package.swift` declares `swift-tools-version: 6.0` — that is its tools
  version, not a language-mode requirement, and does not force the app off Swift 5.
- **Framework:** SwiftUI + AppKit (`NSPanel` HUD, `MenuBarExtra`), `deploymentTarget macOS 15.0`.
  FluidAudio's floor is macOS 14, so it is satisfied.
- **Project generation:** XcodeGen. `mac/project.yml` is the source of truth; **run
  `xcodegen generate` from `mac/` after every change to it.** Never hand-edit `Voice.xcodeproj`.
- **Database:** none on the client. The server is Node 22 + Fastify + SQLite (WAL), untouched here.
- **Rendering:** native; not applicable.
- **Background work:** Swift concurrency (`async`/`await`, actors). No job queue.
- **Dependency manager:** Swift Package Manager, declared in `mac/project.yml`'s `packages:` block
  alongside the existing `WhisperKit` entry (`argmaxinc/argmax-oss-swift`, `from: 0.18.0`).
- **Hosting / deploy target:** not applicable — a signed `.app` packaged by
  `mac/scripts/package.sh` into `Spit.dmg`. Do not run the release path in this work.
- **Architecture:** Apple Silicon only in Release (`ARCHS: arm64`) — the encoder runs on the Neural
  Engine and an Intel build would silently fall back to an untested CPU path.

### External integrations

| Integration | Used for | Timeout | Local fake |
|---|---|---|---|
| **FluidAudio** (SPM, `FluidInference/FluidAudio`) | Parakeet CoreML models + `AsrManager` + `VocabularyBoostingSession` + `SlidingWindowAsrManager` | Model download: none imposed; report progress via `send(.modelProgress(_:))`. Decode: none — it is local and synchronous-ish | Stage 1 needs no fake: the spike test skips without `VOICE_ASR_TESTS`. Stage 3's unit tests use **synthesised `[Float]` sample arrays**, never a downloaded model, so the default suite stays network-free (rule 11) |
| **WhisperKit** (SPM, existing) | The current engine; the spike's baseline | unchanged | existing `VOICE_ASR_TESTS` opt-in pattern |
| **Hugging Face** (via FluidAudio's downloader) | Fetching ~483 MB of CoreML weights | inherited | not needed in Stage 1 |
| **Spit VPS** (`voice.miraside.co`) | `PUT /v1/settings` for the language lock (Stage 3 only) | existing `RefineService` budget | existing `StubAPI.swift` in `mac/VoiceTests/` |

**Pinning FluidAudio.** Pin an exact version, not a range — rule 9 ships a non-default
quantisation and a minor bump can move it. At research time (2026-09-10) the latest was **0.15.7**;
resolve the current latest, pin that exact version, and **record which version you pinned in §16**.
Add a comment in `mac/project.yml` saying the Phase 0 thresholds must be re-verified on any bump.

**Documentation warning — this will cost you time if you ignore it.** FluidAudio's README,
`GettingStarted.md`, `API.md`, `CustomVocabulary.md` and Hugging Face card all show API shapes that
**do not exist** in the shipped package (`transcribe(samples)`, `configure(models:)`,
`initialize(models:)`, `transcribe(_:customVocabulary:)`). Read the package source in
`mac/build/SourcePackages/checkouts/`, not the docs. The shape that actually exists is roughly:

```swift
let models = try await AsrModels.downloadAndLoad(to: ModelManager.modelsDir, version: .v2)
let asr = AsrManager(config: .default)
try await asr.loadModels(models)
var state = try TdtDecoderState(decoderLayers: asr.decoderLayerCount)
let r = try await asr.transcribe(samples, decoderState: &state, language: hint)
```

Verify this against the resolved checkout before relying on it; if it differs, follow the source
and record the real signature in §16.

## 10. Security and privacy

- **Sensitive data:** raw and cleaned dictation transcripts. Audio is the most sensitive thing the
  app touches.
- **Never leaves the machine:** **audio.** All ASR is on-device; the wire carries text only. This
  is the app's core promise and the reason a cloud ASR API is not on the table. Adding Parakeet
  must not introduce any network call carrying audio — FluidAudio's only network use is fetching
  model weights.
- **Retention and deletion:** transcripts persist server-side in `dictations` under the user's own
  account. `LOG_TRANSCRIPTS=0` is refused in production. Model folders are deletable from Settings.
- **Riskiest surfaces here:**
  - *Model download* — ~483 MB over HTTPS from Hugging Face. Verify FluidAudio validates what it
    writes; a partial write must report `isDownloaded == false` (§6.10), never load.
  - *Telemetry* — §12's post-ship query counts empty decodes via `word_count = 0` and **never
    selects a transcript column.** `dictations.word_count` exists precisely so insights never has
    to. Do not write a query that reads `raw` or `cleaned`.
  - *Licence* — Parakeet weights are **CC-BY-4.0**, unlike Whisper's MIT, so attribution is
    required in a shipped app. FluidAudio's own model card contradicts itself (frontmatter
    `cc-by-4.0`, body "Apache 2.0"); the upstream NVIDIA licence governs — follow the stricter
    reading. *(Stage 3, task 7.6.)*

## 11. Build order

**Stage 1 — this run, in this order:**

1. `git checkout main && git pull`, then branch `feat/parakeet-english-engine`. Do **not** branch
   off the current `fix/ci-model-rate-limit` — that is an unrelated Hugging Face rate-limit fix.
2. Add FluidAudio to `packages:` in `mac/project.yml`, pinned exactly. Run `xcodegen generate`
   from `mac/`. Build. **Confirm the existing suite still passes before writing anything new** —
   if adding the dependency broke the build, stop and record it in §16; nothing downstream is
   meaningful.
3. Add `mac/VoiceTests/WordErrorRate.swift`: lowercase, strip punctuation, split on whitespace,
   word-level Levenshtein → WER. Test-only; do not add it to the app target. Unit-test it with
   hand-checked pairs (identical → 0.0; one substitution in five words → 0.2; empty hypothesis
   against a five-word reference → 1.0).
4. Add `mac/VoiceTests/ParakeetSpikeTests.swift`, modelled on `WhisperKitTranscriberTests.swift`:
   `XCTSkipUnless(ProcessInfo.processInfo.environment["VOICE_ASR_TESTS"] == "1")`, clips loaded via
   the existing `AudioFile.load16k` helper from `subdirectory: "Fixtures/spike"`. **Reuse
   `AudioFile`; do not write a second loader.**
5. Implement the missing-corpus guard from Journey B.
6. Implement the three-way transcription loop — WhisperKit `large-v3-v20240930_turbo_632MB`,
   Parakeet v2 `.int8V2`, Parakeet v2 `.int8` — printing per clip: engine, transcript, wall-clock
   ms, empty-or-not. Print a final per-engine summary table covering all four §13 AC-2 thresholds.
7. Time first-launch ANE specialization separately from steady-state decode (the Whisper baseline
   is 262 s for download + specialization + prewarm; Parakeet's is unmeasured).
8. Run the default suite (no env var): everything green, spike test **skipped**.
9. Print the §15.1 handoff block. **Stop.**

**Stage 3 — not this run.** Follow `tasks/tasks-parakeet-english-engine.md` 2.0 → 7.0 in order.
2.0 (engine abstraction, Whisper path unchanged) must complete and AC-3 must pass before 3.0
begins.

## 12. Testing

- **Unit:** `WordErrorRate` against hand-checked pairs. *(Stage 3 adds: trim, 0.3 s floor,
  empty-decode retry, `language == "en"`, boosting guards — all on synthesised `[Float]` arrays so
  they run in the default suite without a model.)*
- **Integration:** the spike harness itself is the integration test for the FluidAudio adapter. It
  is opt-in by design. *(Stage 3: `StubAPI.swift` covers the `PUT /v1/settings` push.)*
- **End-to-end:** Stage 1 has none — there is no user-facing behaviour yet. *(Stage 3: one opt-in
  case per engine transcribing `mac/Fixtures/en.wav`.)*
- **Regression, and this is the important one:** the existing **143 Mac tests must pass
  unmodified.** `ModelManagerTests.swift`, `StreamingTranscriberTests.swift`, `StitchTests.swift`
  and `StreamTailTests.swift` encode Whisper-specific assumptions and are the refactor's guard rail.
- **Command to run everything** (from `mac/`):
  ```
  xcodebuild -project Voice.xcodeproj -scheme Voice -derivedDataPath build/test-dd -quiet test
  ```
- **Opt-in model-dependent tests:** `VOICE_ASR_TESTS=1 xcodebuild … test`. The scheme already
  forwards that variable into the app-hosted bundle — no extra wiring needed.
- **After any `mac/project.yml` edit:** `xcodegen generate` from `mac/` first, or you are testing
  a stale project.

**Post-ship telemetry query** *(Stage 3, task 7.2 — documented here for completeness)*:

```sql
SELECT asr_model,
       COUNT(*)                                         AS dictations,
       SUM(CASE WHEN word_count = 0 THEN 1 ELSE 0 END)  AS empty_decodes,
       CAST(AVG(asr_ms) AS INT)                         AS avg_asr_ms,
       CAST(AVG(total_ms) AS INT)                       AS avg_total_ms
FROM dictations
WHERE created_at > (unixepoch() - 7*86400) * 1000
GROUP BY asr_model;
```

Expected after a week: `empty_decodes` for `fluidaudio/%` is **0**, and `avg_asr_ms` is at or below
the `whisperkit/%` row.

## 13. Acceptance scenarios — your finish line

**AC-1 is your finish line for this run.** AC-2 is Stage 2's, executed by a human. AC-3 through
AC-6 belong to Stage 3 and are listed so the later run can check itself.

### AC-1 — Stage 1 complete (Journey A) — **THIS RUN'S FINISH LINE**
- **Given** a clean checkout on `feat/parakeet-english-engine`, no `mac/Fixtures/spike/` directory,
  and `VOICE_ASR_TESTS` unset
- **When** `xcodegen generate` then
  `xcodebuild -project Voice.xcodeproj -scheme Voice -derivedDataPath build/test-dd -quiet test`
  is run from `mac/`
- **Then** the build succeeds; every pre-existing test passes with **no test file modified**;
  `ParakeetSpikeTests` reports **skipped**, not passed and not failed; `WordErrorRate`'s own unit
  tests pass; and `mac/project.yml` contains an exactly-pinned FluidAudio version whose number is
  recorded in §16

### AC-2 — the gate, run by hand (Stage 2, human)
- **Given** 35 recorded clips and `references.json` in `mac/Fixtures/spike/`
- **When** `VOICE_ASR_TESTS=1 xcodebuild … test` is run
- **Then** the harness prints a per-engine table reporting all four thresholds: (1) count of empty
  transcripts across the 35 non-silent clips, (2) mean WER on the 30 English clips per engine,
  (3) per-clip dictionary-term hits on the 5 term clips, (4) p50 wall-clock on the 20 short clips
- **And** the verdict is legible without further arithmetic: **GO** only if empties = 0 **and**
  Parakeet WER ≤ WhisperKit WER **and** term hits ≥ WhisperKit's **and** p50 ≤ WhisperKit's

### AC-3 — the Whisper path is untouched by the refactor (invariant §6.7 — Stage 3)
- **Given** the `ModelManager` engine-tagging refactor is complete
- **When** the suite is run
- **Then** `ModelManagerTests.swift` passes **with zero edits to the file**, and a Whisper-selected
  build resolves the same model folder path string as before the branch. If the test needed
  editing, the refactor is wrong — revert and redo it

### AC-4 — an empty decode never pastes an empty string (Journey D, invariant §6.6 — Stage 3)
- **Given** a buffer that `EnergyGate.hasSpeech` accepts, and a stubbed `AsrManager` returning `""`
  on both the trimmed and untrimmed attempts
- **When** `ParakeetTranscriber.transcribe(_:hint:progress:)` is called
- **Then** exactly two decode attempts are made; **no empty string reaches `TextInjector`**; the
  "nothing heard" HUD state is emitted; nothing is written to the pasteboard; and the pasteboard's
  prior contents are intact

### AC-5 — the language lock writes "en" and does not restore (invariant §6.3 — Stage 3)
- **Given** `Preferences.language == "pt"` and a Whisper model selected
- **When** the user selects the Parakeet entry, then selects a Whisper entry again
- **Then** after the first selection `Preferences.language == "en"`, exactly one
  `sync.push(language: "en")` is recorded against `StubAPI`, and the language control renders
  disabled; **and after switching back it is still `"en"`** — the non-restore is deliberate and
  this assertion exists so it is not "fixed" later

### AC-6 — boosting never fires on a short utterance (invariant §6.8 — Stage 3)
- **Given** a dictionary containing "Miraside" and "Convex", and a raw transcript of `"Hey"`
- **When** boosting runs
- **Then** the output is exactly `"Hey"` — unmodified. **And** given a raw transcript of
  `"the Miracyte dashboard is running"` the output substitutes "Miraside"; **and** given any
  transcript whose best match scores below 0.60, the output is unmodified

## 14. Failure recovery

- **Errors surface in:** test output for Stage 1. *(Stage 3: `coordinator.modelStatus` for load and
  download failures, the always-enabled Reload button as the recovery path, `deleteError` in red
  for delete failures. Reuse these — do not add a second error mechanism.)*
- **Background job retries:** not applicable; no job queue. The one retry in this design is rule
  6's single untrimmed re-decode, and it is bounded at exactly one.
- **Migrations / rollback:** no database migrations. The rollback for Stage 1 is
  `git checkout main` plus removing the FluidAudio line from `mac/project.yml` and regenerating.
  **On a Stage 2 NO-GO, do exactly that** — revert the dependency, keep the harness and corpus
  committed, and record the verdict in `docs/SPIKES.md`.
- **Never do this:**
  - **Never let the spike report success on zero clips.** A silently-passing empty gate is worse
    than a failing one — it would read as permission to build.
  - **Never edit an existing test to make it pass.** The existing 143 are the regression net; if
    one fails, your change is wrong.
  - **Never paste an empty string** (§6.6).
  - **Never proceed past the gate on your own judgement.** If the numbers are borderline, that is
    Miguel's call, not yours — record it and stop.

## 15. Deliverables

**This run (Stage 1):**
- [ ] Branch `feat/parakeet-english-engine`, branched from `main`
- [ ] `mac/project.yml` with FluidAudio pinned exactly, plus the re-verify-on-bump comment
- [ ] `mac/VoiceTests/WordErrorRate.swift` + its unit tests
- [ ] `mac/VoiceTests/ParakeetSpikeTests.swift` — opt-in, corpus-guarded, three engines, summary table
- [ ] Full default suite green, spike skipped, **no existing test file modified**
- [ ] `tasks/tasks-parakeet-english-engine.md` with 0.1 and 1.6–1.9 checked off as completed
- [ ] §16 Decisions Log filled in
- [ ] The §15.1 handoff block printed as the last thing you do

Not applicable to this project: database migrations, seed data, hosting config. The README is
updated in Stage 3 (task 7.5), not now — there is nothing user-facing to document yet.

### 15.1 Handoff block — print this verbatim, filled in, then stop

```
STAGE 1 COMPLETE — Parakeet English Engine harness

Branch:            feat/parakeet-english-engine
FluidAudio pinned: <exact version>
Suite:             <N> tests passed, ParakeetSpikeTests skipped, 0 existing files modified

NEXT — this needs a human and a microphone:

1. Record 35 clips into mac/Fixtures/spike/ per tasks-parakeet-english-engine.md §1.1–1.4:
     - 20 short English (1-5 words), leading pause across 0.0/0.2/0.3/0.4/0.5/0.6/1.0/2.0 s
       → short-<NN>-lead<MS>.wav   (the 0.4-0.6 s band is the suspected dead zone)
     - 10 medium English (10-30 s) → medium-<NN>.wav
     -  5 with dictionary terms (Miraside, Convex, Ollama) → terms-<NN>.wav
     -  1 mixed pt/en → mixed-01.wav
   Use the real microphone and hold the hotkey as you normally would.

2. Hand-write references.json in the same folder: {"short-01-lead000": "...", ...}

3. From mac/:  VOICE_ASR_TESTS=1 xcodebuild -project Voice.xcodeproj -scheme Voice \
                 -derivedDataPath build/test-dd test

4. Read the summary table against the four thresholds. Record the verdict in docs/SPIKES.md
   EITHER WAY.

   GO    → a later agent run starts at task 2.0.
   NO-GO → remove FluidAudio from mac/project.yml, keep the harness and corpus, stop.

Open questions this run could not answer: <list, or "none">
```

## 16. Decisions log

Append one line per decision you made that this spec did not settle, in the form:
`<what you decided> — <why, in one clause>`. Also record anything you deliberately did not
build because §2 excluded it.

Record at minimum:
- The exact FluidAudio version you pinned, and what the latest available was
- Whether the real `AsrManager` API matched the shape in §9, and the true signature if not
- Any existing test that failed after adding the dependency, and what you did about it

- <your entries here>

## 17. Definition of Done

> **Stage 1 (this run) is done when AC-1 passes**: from a clean checkout on
> `feat/parakeet-english-engine`,
> `xcodegen generate && xcodebuild -project Voice.xcodeproj -scheme Voice -derivedDataPath build/test-dd -quiet test`
> run from `mac/` builds and passes with every pre-existing test unmodified,
> `ParakeetSpikeTests` reported as **skipped**, `WordErrorRate`'s unit tests passing, an exactly-pinned
> FluidAudio version in `mac/project.yml`, §16 filled in, and the §15.1 handoff block printed —
> **with no engine code written and no task past 1.9 started.**
