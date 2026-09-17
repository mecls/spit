# Parakeet English Engine (macOS) — Task List

Source spec: `tasks/prd-parakeet-english-engine.md`. Rule numbers below refer to its §2,
thresholds to its §5.

## Relevant Files

- `mac/Voice/ASR/Transcriber.swift` - the protocol both engines implement; gains a streaming capability (rule 17)
- `mac/Voice/ASR/WhisperKitTranscriber.swift` - existing engine; must keep behaving identically after the refactor
- `mac/Voice/ASR/ParakeetTranscriber.swift` - **new**; the FluidAudio engine, incl. silence trim and empty-decode fallback (rules 6-9)
- `mac/Voice/ASR/ParakeetStreamingSession.swift` - **new**; `SlidingWindowAsrManager` wrapper for the live bar (rule 17)
- `mac/Voice/ASR/ModelManager.swift` - hardcodes the WhisperKit folder layout, `requiredEntries` and downloader; becomes engine-tagged (rule 11)
- `mac/Voice/ASR/StreamingTranscriber.swift` - WhisperKit-specific live path; the Parakeet equivalent sits beside it
- `mac/Voice/ASR/Stitch.swift` - Whisper-path only after this work; must not be reached from Parakeet (rule 18)
- `mac/Voice/App/Coordinator.swift` - builds the engine at `:53` and `:97`; `startStreamingIfEnabled()` at `:339` downcasts to `WhisperKitTranscriber`
- `mac/Voice/Audio/AudioRecorder.swift` - `stop()` at `:178` returns an untrimmed buffer; the trim lives on the Parakeet path, not here
- `mac/Voice/Audio/EnergyGate.swift` - the speech threshold the trim reuses; the gate itself is unchanged (rule 10)
- `mac/Voice/Storage/Preferences.swift` - `Key.modelId` default stays WhisperKit (rule 2); `Key.language` is written by the lock (rule 3)
- `mac/Voice/UI/SettingsView.swift` - `ModelTab` at `:180`; model picker at `:194`, already binds `Key.language`
- `mac/Voice/UI/Strings.swift` - `modelLabelTurbo` at `:67`, `modelPickerLabel` at `:106`; new labels and lock copy go here
- `mac/Voice/Refine/RefineService.swift` - `asrModel` at `:13` sends a bare id; becomes namespaced (rule 14)
- `mac/project.yml` - `packages:` gains FluidAudio; `Fixtures` is already a folder-reference resource on VoiceTests
- `mac/Fixtures/` - holds 3 clips today; the Phase 0 corpus lands here
- `docs/SPIKES.md` - where the Phase 0 result is recorded, pass or fail
- `docs/PLAN.md` - §3.4 describes the ASR contract as Whisper-only; needs the second engine
- `README.md` - architecture diagram says "WhisperKit on the Mac"; also carries the CC-BY-4.0 attribution (rule 20)

### Test files

- `mac/VoiceTests/ParakeetSpikeTests.swift` - **new**; the Phase 0 harness. Opt-in, prints a scoreable table
- `mac/VoiceTests/WordErrorRate.swift` - **new**; test-only WER scorer shared by the spike and later checks
- `mac/VoiceTests/ParakeetTranscriberTests.swift` - **new**; trim, 0.3 s floor, empty-decode fallback, `language == "en"`
- `mac/VoiceTests/ModelManagerTests.swift` - existing Whisper assertions must pass **unmodified**; that is the refactor's regression test (rule 11)
- `mac/VoiceTests/WhisperKitTranscriberTests.swift` - the opt-in ASR test to copy the `VOICE_ASR_TESTS` / `AudioFile.load16k` pattern from
- `mac/VoiceTests/AudioFile.swift` - test-only 16 kHz mono Float32 loader; reuse it, do not write a second one
- `mac/VoiceTests/StreamingTranscriberTests.swift`, `StitchTests.swift`, `StreamTailTests.swift` - encode Whisper-segment assumptions; must still pass

### Notes

- Tests are XCTest, all in one flat bundle at `mac/VoiceTests/` (no per-module directories). Add new files there.
- Run from `mac/`: `xcodebuild -project Voice.xcodeproj -scheme Voice -derivedDataPath build/test-dd -quiet test`
- Tests that touch a real model are opt-in and skip by default via `XCTSkipUnless(...environment["VOICE_ASR_TESTS"] == "1")`. Run them with `VOICE_ASR_TESTS=1 xcodebuild ... test` — the scheme already forwards that variable into the app-hosted test bundle.
- After any `mac/project.yml` change, run `xcodegen generate` from `mac/` before building.
- `mac/Fixtures` is a **folder reference** (`{ path: Fixtures, type: folder, buildPhase: resources }`), so new `.wav` files are bundled without editing `project.yml`. Load them with `subdirectory: "Fixtures"`.
- **There is no macOS CI in this repo** — only `windows-ci.yml` and `release-windows.yml`. Every test here is run by a person, so the default suite must stay fast and network-free (rule 21).
- `package.sh` runs `xcodebuild test` before packaging; `SPIT_SKIP_TESTS=1` skips it but is refused with `--release`.

## Instructions for Completing Tasks

As you complete each sub-task, check it off by changing `- [ ]` to `- [x]`, and save the file
then — not at the end of the parent task. Someone picking this up after an interruption can
only trust the boxes if they were ticked as the work happened.

## Tasks

- [ ] 0.0 Create a feature branch for this work
  - [ ] 0.1 Branch `feat/parakeet-english-engine` off `main`, not off the current `fix/ci-model-rate-limit` — that branch is an unrelated Hugging Face rate-limit fix and must not carry this work

- [ ] 1.0 Phase 0 — build the fixture corpus and run the go/no-go gate *(rule 1: nothing below starts until §5.1 passes)*
  - [ ] 1.1 Record 20 short English dictations, 1–5 words, on the target Mac and microphone with the hotkey held as in normal use. Vary the pause between key-press and speech across 0.0, 0.2, 0.3, 0.4, 0.5, 0.6, 1.0 and 2.0 s — the cited dead zone is 0.4–0.6 s and is non-monotonic, so sampling must straddle it. Save as `mac/Fixtures/spike/short-<NN>-lead<MS>.wav`
  - [ ] 1.2 Record 10 medium English dictations, 10–30 s of ordinary speech, as `mac/Fixtures/spike/medium-<NN>.wav`
  - [ ] 1.3 Record 5 dictations containing dictionary terms — at minimum "Miraside", "Convex" and "Ollama" — as `mac/Fixtures/spike/terms-<NN>.wav`
  - [ ] 1.4 Record 1 mixed pt/en dictation shaped like the existing bench fixture ("...confirmou a entrega, but it might slip to Friday") as `mac/Fixtures/spike/mixed-01.wav`. Not scored; it answers spec Q1
  - [ ] 1.5 Hand-write the reference transcript for every clip into `mac/Fixtures/spike/references.json` (`{"short-01-lead000": "…"}`). Without references, threshold 2 cannot be scored
  - [ ] 1.6 Add the FluidAudio SPM package to `packages:` in `mac/project.yml`, pinned to an exact version, then run `xcodegen generate` from `mac/`
  - [ ] 1.7 Add `mac/VoiceTests/WordErrorRate.swift`: lowercase, strip punctuation, split on whitespace, word-level Levenshtein → WER. Test-only, no app target
  - [ ] 1.8 Add `mac/VoiceTests/ParakeetSpikeTests.swift` following `WhisperKitTranscriberTests.swift`'s shape — `XCTSkipUnless(VOICE_ASR_TESTS == "1")`, load clips with `AudioFile.load16k(subdirectory: "Fixtures/spike")`
  - [ ] 1.9 In the spike test, transcribe every clip three ways — WhisperKit `large-v3-v20240930_turbo_632MB`, FluidAudio Parakeet v2 `.int8V2`, and FluidAudio Parakeet v2 `.int8` — printing per clip: engine, transcript, wall-clock ms, and whether the output was empty
  - [ ] 1.10 Record first-launch CoreML/ANE specialization time for the Parakeet encoder separately from steady-state transcription (spec Q3; the Whisper baseline is 262 s)
  - [ ] 1.11 Score the run against §5.1: (1) zero empty transcripts across all 35 non-silent clips, (2) WER ≤ WhisperKit on the 30 English clips, (3) dictionary terms no worse than Whisper's prompt-token path with zero false substitutions, (4) p50 wall-clock ≤ WhisperKit on the 20 short clips
  - [ ] 1.12 Write the result into `docs/SPIKES.md` in its existing format — **including on a failure**, so this is not re-proposed in three months
  - [ ] 1.13 Record the `.int8V2` vs `.int8` comparison from 1.9 as the answer to spec Q4, and confirm or overturn rule 13's choice on this corpus
  - [ ] 1.14 **GO/NO-GO.** Any §5.1 threshold missed → stop, leave 2.0 onwards unstarted, and close the spec against the `docs/SPIKES.md` entry
  - [ ] 1.15 *(independent of the gate, spec §5.2)* Run streamed vs one-pass WER for **WhisperKit** on the 10 medium clips and record it — this is the measurement `Preferences.liveTranscription`'s own code comment names as impossible today, and it decides rule 19 whether or not Parakeet ships

- [ ] 2.0 Generalise the engine abstraction and model management, with the Whisper path unchanged
  - [ ] 2.1 Add `enum AsrEngine { case whisperKit, parakeet }` to `mac/Voice/ASR/ModelManager.swift`
  - [ ] 2.2 Add an `engine: AsrEngine` field to `ModelManager.available`'s tuple; both existing rows are `.whisperKit`
  - [ ] 2.3 Replace `ModelManager.folder(for:base:)`'s hardcoded `models/argmaxinc/whisperkit-coreml/openai_whisper-<id>` with a per-engine layout, keeping the Whisper string byte-identical
  - [ ] 2.4 Replace the static `requiredEntries` array with a per-engine value; Whisper keeps its existing four CoreML artefacts (rule 11)
  - [ ] 2.5 Replace `ensure()`'s direct `WhisperKit.download(variant:downloadBase:from:)` call with a per-engine downloader closure, leaving the Whisper branch calling exactly what it calls today
  - [ ] 2.6 Run `ModelManagerTests.swift` **without editing it**. It asserts the Whisper folder layout and is the regression test for 2.3–2.5; if it needs changing, 2.3–2.5 are wrong
  - [ ] 2.7 Add an engine factory (`Transcriber` from a `ModelManager.available` entry) and use it at `Coordinator.swift:53` and in `reloadModel()` at `:97`, replacing both direct `WhisperKitTranscriber(modelId:)` constructions
  - [ ] 2.8 Add a streaming capability to `mac/Voice/ASR/Transcriber.swift` — a method returning an optional per-engine streaming session — and implement it on `WhisperKitTranscriber` by returning its existing `whisperKit` pipe. Designed here rather than in 6.0 because retrofitting the protocol later is the expensive version of this change
  - [ ] 2.9 Confirm the full existing suite passes and that a Whisper-selected build produces byte-identical pasted output and comparable `asr_ms` to before the branch (§5.4)

- [ ] 3.0 Build the Parakeet one-pass transcriber
  - [ ] 3.1 Create `mac/Voice/ASR/ParakeetTranscriber.swift` conforming to `Transcriber`, with `modelId`, `isReady`, `prepare(progress:)` and `transcribe(_:hint:progress:)`
  - [ ] 3.2 Implement `prepare(progress:)` via `AsrModels.downloadAndLoad(to: ModelManager.modelsDir, version: .v2)` selecting the `.int8V2` encoder (rule 13), reporting progress into the existing `.modelProgress` UI
  - [ ] 3.3 Add the silence-trim helper (rule 7): find the first and last sample exceeding `EnergyGate`'s speech threshold, then re-pad to exactly 0.20 s each side. Parakeet path only — do not touch `AudioRecorder.stop()`
  - [ ] 3.4 Add the 0.3 s floor (rule 8): if the trimmed buffer is shorter, re-pad with silence rather than letting FluidAudio throw `ASRError.invalidAudioData`
  - [ ] 3.5 Implement the decode: a `TdtDecoderState` built from the manager's decoder layer count, then `transcribe(samples, decoderState:&state, language:)`
  - [ ] 3.6 Implement the empty-decode fallback (rule 9): on empty or whitespace-only output where `EnergyGate.hasSpeech` was true, retry once with the untrimmed buffer, then surface the existing "nothing heard" HUD state. Never paste an empty string
  - [ ] 3.7 Return `Transcript(text:language:"en", durationMs:)` with an inline comment stating the constant is asserted by rule 3 and that `SkipGate.reasonToClean` ignores its `language:` parameter — so nobody later "fixes" it into a nil (rule 6)
  - [ ] 3.8 Add `mac/VoiceTests/ParakeetTranscriberTests.swift` covering the trim (leading and trailing), the 0.3 s floor, the empty-decode retry path and `language == "en"`. Use synthesised sample arrays so these run in the default suite without a model download (rule 21)
  - [ ] 3.9 Add an opt-in end-to-end case to the same file, gated on `VOICE_ASR_TESTS`, transcribing `mac/Fixtures/en.wav` through the real engine

- [ ] 4.0 Add dictionary vocabulary boosting on the Parakeet path *(cuttable as a unit if 4.5 fails — spec Q5)*
  - [ ] 4.1 Extend `prepare(progress:)` to also fetch the CTC boosting model (~106 MB) into `ModelManager.modelsDir`, and make `isDownloaded` account for it so a half-fetched pair reports false (rule 12)
  - [ ] 4.2 Wire `hint.vocabulary` through `VocabularyBoostingSession.rescore(text:tokenTimings:audioSamples:)`, passing the transcript, token timings and source audio the one-pass path already holds (rule 15)
  - [ ] 4.3 Apply rule 16's two guards: a similarity floor no lower than 0.60, and skip boosting entirely for transcripts under 3 words
  - [ ] 4.4 Extend `ParakeetTranscriberTests.swift`: a term-bearing transcript gets the term corrected; a 2-word transcript is returned untouched; a transcript containing no dictionary term is never modified (the `"Hey"` → `"Codex"` case)
  - [ ] 4.5 Re-run §5.1 threshold 4 (p50 wall-clock) on the 20 short clips with boosting enabled. Boosting roughly doubles encoder work — if latency passed without it and fails with it, stop and escalate spec Q5 rather than choosing between the dictionary and the latency

- [ ] 5.0 Add the Settings model entry and the English-only language lock
  - [ ] 5.1 Add `modelLabelParakeetEN` to `mac/Voice/UI/Strings.swift` carrying "English only" **in the label itself**, not only in helper text (§4.1)
  - [ ] 5.2 Add the `parakeet-tdt-0.6b-v2` / `.parakeet` / 483 MB row to `ModelManager.available`. `Preferences.defaults[Key.modelId]` stays the Whisper turbo id — no install changes engine on update (rule 2)
  - [ ] 5.3 Add the lock copy from §4.2 to `Strings.swift` — it must say the model cannot detect which language was spoken, not just that it is English-only
  - [ ] 5.4 In `ModelTab` (`SettingsView.swift:180`), render the language control disabled with that copy when the active entry's engine is `.parakeet`. The view already binds `@AppStorage(Preferences.Key.language)`
  - [ ] 5.5 On selecting a `.parakeet` entry, write `language = "en"` to `Preferences` and to the server via the existing settings sync (`PUT /v1/settings`, enum `['auto','pt','en']` at `server/src/routes/settings.ts:12`). Selecting a Whisper model does **not** restore the previous value (rule 3)
  - [ ] 5.6 Add a test asserting the lock writes `"en"` and that switching back to Whisper leaves it at `"en"` — the non-restore is deliberate and needs pinning so it is not "fixed"

- [ ] 6.0 Add live transcription on the Parakeet path
  - [ ] 6.1 Create `mac/Voice/ASR/ParakeetStreamingSession.swift` wrapping `SlidingWindowAsrManager`: `startStreaming`, `streamAudio(AVAudioPCMBuffer)` per capture buffer, `finish()`
  - [ ] 6.2 Consume `transcriptionUpdates`, rendering `isConfirmed` text as settled and the remainder as hypothesis — the same two-tier display the bar shows today
  - [ ] 6.3 Implement 2.8's streaming capability on `ParakeetTranscriber` to return this session
  - [ ] 6.4 Replace the `guard … let pipe = (transcriber as? WhisperKitTranscriber)?.whisperKit` downcast at `Coordinator.swift:339` with the protocol capability. An engine that cannot stream returns nil and the bar stays off — same visible outcome as today, but stated rather than implied (rule 17)
  - [ ] 6.5 Confirm `Stitch.swift` and the tail pass are unreachable from the Parakeet path; `SlidingWindowAsrManager` confirms internally and a second stitch would disagree with it (rule 18)
  - [ ] 6.6 Confirm the final pasted text still comes from the one-pass transcription in §3.2 — live text stays display-only, exactly as today
  - [ ] 6.7 Leave `Preferences.defaults[Key.liveTranscription]` at `false` (rule 19); revisit only against 1.15's measurement
  - [ ] 6.8 Run `StreamingTranscriberTests.swift`, `StitchTests.swift` and `StreamTailTests.swift` unmodified — they encode Whisper-segment assumptions and must still pass

- [ ] 7.0 Validation, telemetry namespacing, docs and licence attribution
  - [ ] 7.1 Change `RefineService.asrModel` (`:13`) from the bare `Preferences.modelId` to a namespaced value — `"whisperkit/<id>"` and `"fluidaudio/parakeet-tdt-0.6b-v2"` — matching the Windows client's existing `"whisper.cpp/<file>"` shape. Do not backfill existing rows (rule 14)
  - [ ] 7.2 Add the §5.3 query to `docs/PLAN.md` as the post-ship check, counting empty decodes via `word_count = 0` so no transcript column is ever selected
  - [ ] 7.3 Run the full regression in §5.4: existing 143 tests pass, `ModelManagerTests` unmodified, a Whisper-selected build unchanged, and the language lock reflected in `GET /v1/me`
  - [ ] 7.4 Update `docs/PLAN.md` §3.4 — it describes the ASR contract as Whisper-only and now has two engines with different capabilities
  - [ ] 7.5 Update the `README.md` architecture diagram ("WhisperKit on the Mac") and the Status table to name the second engine
  - [ ] 7.6 Add NVIDIA's CC-BY-4.0 attribution to `README.md` and the app's acknowledgements. FluidAudio's own model card contradicts itself here (frontmatter `cc-by-4.0`, body "Apache 2.0") — the upstream NVIDIA licence governs, so follow the stricter reading (rule 20)
  - [ ] 7.7 Pin the FluidAudio version in `mac/project.yml` and add a comment that §5.1 must be re-verified on any bump — the shipped quantisation is not the one upstream treats as canonical (rule 13)
