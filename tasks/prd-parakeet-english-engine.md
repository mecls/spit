# Parakeet English Engine (macOS) — Implementation Spec

Status: draft, not approved. Phase 0 (§3.1) is a go/no-go gate — no engine code is written until it passes.

## 1. Objective

Add **NVIDIA Parakeet TDT 0.6B v2** as an opt-in, English-only on-device ASR engine on macOS,
running beside the existing WhisperKit engine rather than replacing it. WhisperKit stays the
default and remains the only engine for Portuguese and for mixed pt/en speech.

Why now: on the HF Open ASR Leaderboard (11-09-2026 run, 1×H200, same harness), Parakeet beats
`whisper-large-v3-turbo` by **32% relative WER on AMI** (9.42 vs 13.88) and 28% on Earnings22 —
spontaneous, conversational speech, which is the closest available proxy for someone talking off
the cuff into a laptop mic. Read-speech benchmarks show a much smaller gap, so the win is
specifically in the register this app is used in.

Why v2 and not the v3 that prompted this: v3 bought 25 languages by giving up English accuracy
(leaderboard average 4.86 vs v2's 4.70; SPGISpeech 1.94 → 3.63). Because WhisperKit keeps
Portuguese and mixed input under rule 3, v3's multilingual coverage buys nothing this app would
use, and costs English accuracy it would.

Why gated: two independently reported, currently-open bugs produce **silent empty transcripts**
on audio shaped exactly like push-to-talk dictation (§2 rule 7). Neither is theoretical, and
neither is visible to the user as an error — they look like the app not hearing you. Phase 0
exists to find out whether they bite on real recordings before any engine code is written.

This spec covers macOS only. Windows is out of scope and §6 says why.

## 2. Business rules (invariants — never violate)

1. **Phase 0 is a hard gate.** No engine code, no `ModelManager` refactor, no Settings change is
   written until the fixture corpus in §3.1 passes every threshold in §5.1. If it fails, the
   outcome is a recorded result in `docs/SPIKES.md` and this spec is closed unbuilt. The gate
   exists because the two failure modes in rule 7 are silent: shipping into them produces
   "the app randomly doesn't hear me" bug reports that are very expensive to diagnose after the fact.

2. **WhisperKit stays the default and nothing migrates onto Parakeet.** `Preferences.defaults`
   keeps `Key.modelId: "large-v3-v20240930_turbo_632MB"`. No existing install changes engine on
   update. Parakeet is reached only by a deliberate selection in Settings › Model.

3. **Selecting Parakeet pins the language to English.** When the selected model's engine is
   `.parakeet`, the app writes `language = "en"` to both `Preferences.language` and the server
   (`PUT /v1/settings`, whose enum is `['auto','pt','en']` at `server/src/routes/settings.ts:12`),
   and the language picker in Settings renders disabled with the copy in §4.2.
   Selecting a Whisper model does **not** restore the previous language — the user sets it back
   themselves, because silently changing a server-side setting on a model swap is worse than
   making them do it once.

4. **Engine selection is manual and can never be automatic.** Do not build language-based routing.
   Parakeet emits no language identifier at all: NVIDIA's maintainer, asked whether a language can
   be forced or the detected one read back, answered *"No. Only with canary model"*. Routing by
   detected language is impossible by construction, not merely unimplemented, and an attempt
   would have to invent a language detector this app does not have.

5. **Parakeet is English-only, and the app says so rather than discovering it.** v2 is trained on
   English alone. Portuguese fed to it produces confident, plausible, wrong English — the worst
   possible failure shape. Rule 3 is the mitigation; there is no runtime check, because there is
   nothing to check against (rule 4).

6. **`Transcript.language` is `"en"` for every Parakeet transcription.** Not `nil`. It is a
   constant asserted by rule 3, not a detection result. This is safe because the field is
   **telemetry only**: `SkipGate.reasonToClean(raw:mode:language:segments:)` at
   `mac/Voice/Refine/SkipGate.swift:68` accepts `language:` and never reads it in its body — both
   filler lists always apply — and the server's `buildSystemPrompt` does not use
   `languageDetected` either. Its only consumer is the `dictations.language_detected` column.
   Record this in the code as a comment, so nobody later "fixes" it into a nil.

7. **Trim silence from both ends of the buffer before handing it to Parakeet.** Two open upstream
   bugs both produce an empty string from real speech:
   - [NeMo #15757](https://github.com/NVIDIA-NeMo/Speech/issues/15757) — a 2.2 s clip decodes
     correctly; the same clip plus 400 ms of appended zeros decodes to `""`.
   - [FluidAudio #746](https://github.com/FluidInference/FluidAudio/issues/746) — 0.4–0.6 s of
     *leading* silence yields zero tokens, deterministically and non-monotonically (0.3 s fine,
     0.5 s empty, 1.0 s fine).

   `AudioRecorder.stop()` (`mac/Voice/Audio/AudioRecorder.swift:178`) returns `buffer.drain()` with
   no trimming, so every dictation today carries whatever pause the speaker left before and after
   speaking. Trim to the first and last sample exceeding the `EnergyGate` speech threshold, then
   re-pad to exactly 0.20 s on each side. Trimming is applied on the Parakeet path only; the
   WhisperKit path is unchanged, because Whisper does not have this failure and changing its input
   would invalidate the existing 143 tests.

8. **Never send Parakeet a buffer shorter than 0.3 s.** FluidAudio throws
   `ASRError.invalidAudioData` below `minAudioLength`. After the trim in rule 7 a very short
   dictation can land under it. Below 0.3 s, re-pad with silence to 0.3 s rather than throwing —
   `EnergyGate.hasSpeech` has already confirmed there is speech in the buffer, so an error here
   would be the app refusing audio it just agreed was speech.

9. **An empty decode from Parakeet is a fallback, not a result.** If the trimmed buffer passed
   `EnergyGate.hasSpeech` and Parakeet returns an empty or whitespace-only string, retry once
   against the same engine with the untrimmed buffer; if that is also empty, surface the existing
   "nothing heard" HUD state. Never paste an empty string and never fail silently. Record the
   occurrence so §5.3's query can count it. This is the runtime guard for rule 7 — the trim is the
   prevention, this is the net.

10. **`EnergyGate` runs before the transcriber, unchanged.** `Coordinator.swift:248` already calls
    `EnergyGate.hasSpeech(samples)` before any engine sees audio, so pure silence never reaches
    Parakeet. Do not add a second gate inside the Parakeet transcriber. Parakeet returns `""` for
    non-speech rather than Whisper's `[BLANK_AUDIO]`, so the `NonSpeechTag()` regex is simply
    unused on this path — leave it in place for the Whisper path.

11. **`ModelManager` becomes engine-tagged; its Whisper behaviour must not change.** Today
    `folder(for:base:)` hardcodes `base/models/argmaxinc/whisperkit-coreml/openai_whisper-<id>`,
    `requiredEntries` names four Whisper CoreML artefacts, and `ensure()` calls
    `WhisperKit.download(variant:downloadBase:from:)`. Each becomes a per-engine value:
    `{ folder, requiredEntries, download }`. `ModelManager.available` gains an `engine` field.
    Every existing assertion in `ModelManagerTests.swift` must still pass unmodified for Whisper
    ids — that is the regression test for this refactor.

12. **Parakeet models live under the app's own directory, like Whisper's.** Download via
    `AsrModels.downloadAndLoad(to:version:)`, passing `ModelManager.modelsDir`
    (`~/Library/Application Support/Voice/models`). Do not accept FluidAudio's default location.
    A partially-written folder must report `isDownloaded == false`, matching rule 11's existing
    contract — an interrupted download that reports success is the bug this rule prevents.

13. **Ship the `.int8V2` encoder, not the default `.int8`.** FluidAudio's default `.int8` encoder
    is 6-bit palettized and corrupts words in the first ~5.5 s of a window under specific
    right-context ([#760](https://github.com/FluidInference/FluidAudio/issues/760)); NeMo PyTorch
    and parakeet-mlx transcribe the same audio correctly, so it is a CoreML export artefact, not a
    model property. `.int8V2` is the fix and is opt-in. Pin the FluidAudio version in
    `project.yml` and re-verify §5.1 on any bump — this is a fast-moving library and the
    quantisation you are shipping is not the one upstream treats as canonical.

14. **`asrModel` is namespaced on both engines.** `RefineService.asrModel` currently sends the bare
    `Preferences.modelId` (`mac/Voice/Refine/RefineService.swift:13`), e.g.
    `"large-v3-v20240930_turbo_632MB"`, while the Windows client already sends
    `"whisper.cpp/ggml-small-q8_0"`. Mac adopts the same shape: `"whisperkit/<id>"` and
    `"fluidaudio/parakeet-tdt-0.6b-v2"`. Without this the server cannot separate the three
    populations in `dictations.asr_model`, which is what every query in §5 depends on.
    This changes existing rows' successors, not existing rows — do not backfill.

15. **The dictionary keeps working on Parakeet.** `hint.vocabulary` is applied through FluidAudio's
    `VocabularyBoostingSession.rescore(text:tokenTimings:audioSamples:)`, which is documented as
    engine-agnostic and needs exactly the transcript, token timings and source audio that the
    one-pass path already holds. This is not optional polish: `docs/SPIKES.md:43` measured that
    without prompt tokens *"Miraside" came out as "Miracyte" and "Ollama key" as "Olamaki"*, and
    the server-side LLM safety net does not cover it — `literal` mode returns before the API call,
    and `SkipGate` short-circuits exactly the short, well-formed utterances where a mangled proper
    noun would otherwise be repaired.

16. **Vocabulary boosting must not fire on short utterances, and must never invent a term.**
    FluidAudio's boosting has two open issues — [#899](https://github.com/FluidInference/FluidAudio/issues/899)
    (false positives: `"Hey"` → `"Codex"` at similarity 0.12) and
    [#912](https://github.com/FluidInference/FluidAudio/issues/912) (streaming boosting silently
    never fires below a 10 s context). Set a similarity floor no lower than 0.60 and skip boosting
    entirely for transcripts under 3 words. A wrong proper noun substituted into a two-word
    dictation is worse than the un-boosted transcript, which is at least honestly wrong.

17. **Live transcription works on Parakeet, and the Whisper-only guard goes.**
    `Coordinator.startStreamingIfEnabled()` at `mac/Voice/App/Coordinator.swift:339` reads
    `guard Preferences.liveTranscription, let pipe = (transcriber as? WhisperKitTranscriber)?.whisperKit`
    — so today any non-Whisper engine gets no live bar and no error. Replace the downcast with a
    capability on the `Transcriber` protocol so each engine supplies its own streaming session
    (WhisperKit's `AudioStreamTranscriber`, FluidAudio's `SlidingWindowAsrManager`). An engine that
    cannot stream returns nil and the bar stays off — the same visible outcome as today, but stated
    rather than implied by a downcast.

18. **`Stitch.swift` stays on the Whisper path only.** `SlidingWindowAsrManager` confirms segments
    internally across its own 15 s window with 2 s overlap, so the app-level stitch and tail pass
    would be a second, disagreeing implementation over the same audio. Parakeet's streaming path
    consumes `transcriptionUpdates` and its `isConfirmed` flag directly.

19. **`liveTranscription` stays off by default for Parakeet too.** `Preferences.defaults` keeps
    `Key.liveTranscription: false`. The accuracy gate that would justify defaulting it on —
    streamed versus one-pass WER — is still unmeasured, and §3.1's corpus is the first real
    opportunity to measure it (see §5.2).

20. **Attribute the model.** Parakeet weights are **CC-BY-4.0**, unlike Whisper's MIT. Attribution
    is required in a shipped app: add NVIDIA's attribution to the app's acknowledgements and to
    `README.md`. FluidAudio's own HF card contradicts itself on this (frontmatter says
    `cc-by-4.0`, body text says Apache 2.0); the upstream NVIDIA licence governs, so follow the
    stricter reading.

21. **Both engines stay verifiable by hand.** There is no macOS CI in this repo — only
    `windows-ci.yml` and `release-windows.yml` — so every Mac test is run by a person. Any test
    added here runs in the same `xcodebuild test` invocation as the existing 143, with no new
    network dependency in the default suite (model downloads stay behind the existing opt-in ASR
    test flag). A suite that needs a 480 MB download to go green will stop being run.

## 3. Flows

### 3.1 Phase 0 — the fixture spike (gate, ~half a day)

Runs before any engine code. Produces a result in `docs/SPIKES.md` and a yes or no.

1. **Record the corpus.** `mac/Fixtures/` today holds `en.wav`, `pt-synthetic.wav` and
   `silence.wav` — one real English recording, one synthetic Portuguese, and no short dictations
   at all. Record, on the target hardware and microphone, with the hotkey held as in normal use:
   - **20 short English dictations**, 1–5 words each, deliberately varying the pause between
     pressing the key and starting to speak across 0.0, 0.2, 0.3, 0.4, 0.5, 0.6, 1.0 and 2.0 s.
     Rule 7's cited dead zone is 0.4–0.6 s and is non-monotonic, so the sampling must straddle it.
   - **10 medium English dictations**, 10–30 s, ordinary speech.
   - **5 dictations containing dictionary terms** — at minimum "Miraside", "Convex" and
     "Ollama", the three `docs/SPIKES.md:43` already has a measured failure for.
   - **1 mixed pt/en dictation** in the shape of the existing bench fixture
     (`"...confirmou a entrega, but it might slip to Friday"`). Not a pass/fail input — it is the
     recording that settles, for this codebase, a question no published benchmark answers
     (§7 Q1).

   Commit the corpus. It outlives this spec: rule 19's unmeasured live-transcription gate and
   `Preferences.liveTranscription`'s own code comment both name the absence of real audio fixtures
   as the blocker.

2. **Transcribe each clip three ways** — WhisperKit `large-v3-turbo_632MB` (the current default),
   FluidAudio Parakeet v2 `.int8V2`, and FluidAudio Parakeet v2 `.int8` (to confirm rule 13's
   choice on this corpus rather than on an issue report). Record per clip: transcript, wall-clock
   ms, and whether the output was empty.
3. **Score** against §5.1. Any threshold missed is a no.
4. **Write up** in `docs/SPIKES.md` in the existing format — including on a no, because a recorded
   negative is what stops this being re-proposed in three months.

### 3.2 Runtime — one-pass dictation (the 99% path)

Unchanged up to the engine. Hotkey release → `AudioRecorder.stop()` → `EnergyGate.hasSpeech`
(rule 10) → `Transcriber.transcribe(_:hint:progress:)`.

On the Parakeet path only:

1. Trim and re-pad the buffer per rule 7; floor at 0.3 s per rule 8.
2. `AsrManager.transcribe(samples, decoderState:&state, language:)` with a `TdtDecoderState`.
3. If the result is empty and `hasSpeech` was true, apply rule 9's single retry, then the
   "nothing heard" HUD state.
4. Apply vocabulary boosting per rules 15 and 16, skipping it under 3 words.
5. Return `Transcript(text:language:"en", durationMs:)` per rule 6.

Downstream — `SkipGate`, `RefineService`, `TextInjector` — is untouched. It already receives a
`Transcript` and does not care which engine produced it.

### 3.3 Runtime — live transcription (opt-in, off by default)

`startStreamingIfEnabled()` asks the active engine for a streaming session (rule 17) instead of
downcasting. On Parakeet this is `SlidingWindowAsrManager`: `startStreaming`, then
`streamAudio(AVAudioPCMBuffer)` per capture buffer, consuming `transcriptionUpdates` and rendering
`isConfirmed` text as settled and the rest as hypothesis — the same two-tier display the bar shows
today. `finish()` on hotkey release. `Stitch.swift` and the tail pass are not used here (rule 18).

The final pasted text still comes from the one-pass transcription in §3.2. Live text is display
only, exactly as today.

### 3.4 Model download

Settings › Model → select a Parakeet entry → **Download**. The existing progress UI is reused
(`send(.modelProgress(_:))`). ~483 MB for the `.int8V2` set. On completion, `reloadModel()`
constructs the engine from the entry's `engine` field rather than always
`WhisperKitTranscriber(modelId:)` (`Coordinator.swift:53` and `:97`). First load also pays a CoreML
ANE specialization cost, unmeasured for this encoder (§7 Q3); the existing progress UI must not
appear hung during it.

## 4. Surfaces

### 4.1 Settings › Model — picker

`ModelManager.available` gains an `engine` field, so the existing `Picker` over it (`ModelTab`,
`mac/Voice/UI/SettingsView.swift:194`) keeps working with a label change only:

| Entry | Engine | Size | Label |
|---|---|---|---|
| `large-v3-v20240930_turbo_632MB` | `.whisperKit` | 632 MB | existing `Strings.modelLabelTurbo` (default) |
| `large-v3-v20240930_turbo` | `.whisperKit` | 1.6 GB | existing `Strings.modelLabelLarge` |
| `parakeet-tdt-0.6b-v2` | `.parakeet` | 483 MB | new — must carry "English only" in the label itself |

"English only" belongs in the picker label, not only in helper text below it, because the picker
label is the only string guaranteed to be read.

### 4.2 Settings › Model — language lock

`ModelTab` already binds `@AppStorage(Preferences.Key.language)`, so the lock is local to this view.
When a `.parakeet` entry is active, the language control renders disabled with:

> **English only.** This model does not recognise Portuguese and cannot detect which language you
> spoke. Switch back to Whisper for Portuguese or mixed speech.

Naming the specific consequence — including that it cannot *tell* — is the point; "English only"
alone reads like a recommendation rather than a hard limit.

### 4.3 No new server surface

`/v1/refine`, `/v1/settings` and `/v1/dictations` are unchanged. `asr_model` already exists and
already accepts free text; rule 14 only changes what the Mac writes into it.

## 5. Validation

### 5.1 Phase 0 thresholds (the gate)

All four must hold on the §3.1 corpus, comparing Parakeet `.int8V2` against the current WhisperKit
default:

1. **Zero empty transcripts across all 35 non-silent clips.** Not "few" — one silent empty decode
   on a clip a human can hear is a no, because rule 9's net turns it into a visible "nothing heard"
   and the user experiences it as the app being broken. This is the threshold most likely to fail.
2. **Word error rate on the 30 English clips at or below WhisperKit's**, scored against
   hand-written references. The leaderboard predicts a large margin; anything worse than parity on
   real recordings from this microphone contradicts the premise of the feature.
3. **Dictionary terms**: on the 5 term-bearing clips, boosted Parakeet gets "Miraside", "Convex"
   and "Ollama" right at least as often as WhisperKit's prompt-token path, and rule 16's floor
   produces zero substitutions into clips that contain no dictionary term.
4. **p50 wall-clock at or below WhisperKit's** on the same machine for the 20 short clips. Published
   RTFx figures (6076) are H200 batch throughput and predict nothing here; for a 3 s utterance fixed
   per-call overhead may dominate. This is the only latency number that means anything.

Record all four in `docs/SPIKES.md` whichever way they land.

### 5.2 Secondary output of the corpus (not a gate)

With real audio fixtures in hand, run streamed versus one-pass WER for **WhisperKit** on the 10
medium clips. That is the measurement `Preferences.liveTranscription`'s own comment names as
impossible today, and it decides rule 19 independently of whether Parakeet ships.

### 5.3 Post-ship, from the server

Both engines are namespaced per rule 14, so populations separate cleanly. Empty decodes are
counted via `word_count`, never by reading transcript text — `dictations.word_count` exists
precisely so insights never selects a transcript column:

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

Expected after one week of real use: `empty_decodes` for `fluidaudio/%` is **0**, and `avg_asr_ms`
is at or below the `whisperkit/%` row. A non-zero empty count means rule 7's trim is not covering
a case the corpus missed — investigate before recommending the engine to anyone else.

### 5.4 Regression

- The existing 143 Mac tests pass unmodified, with `ModelManagerTests` asserting the unchanged
  Whisper folder layout (rule 11).
- With a Whisper model selected, `asr_ms` and pasted output are unchanged from before the branch —
  the refactor must be invisible on the default path.
- Selecting Parakeet writes `language = "en"` to the server; `GET /v1/me` reflects it (rule 3).

## 6. Out of scope

- **Windows, entirely.** Two independent implementations refuse the app's decode contract for
  Parakeet. sherpa-onnx's hotwords require `modified_beam_search`, which on this exact model
  returns hallucinated or empty text ~20% of the time
  ([#3267](https://github.com/k2-fsa/sherpa-onnx/issues/3267), open). Whisper.net's merged Parakeet
  bindings throw `NotSupportedException` for initial prompts, language selection, language
  detection, temperature and temperature fallback — which is every line of
  `windows/Spit.App/Asr/WhisperTranscriber.cs:204-208`. Nothing is published to NuGet in any case
  (`whisper.net.runtime.parakeet` → 404; latest Whisper.net is 1.9.2-preview1, predating the
  merge). Revisit only if `Whisper.net.Runtime.Parakeet` publishes *and* offers a biasing
  mechanism; without one, Windows Parakeet means losing the dictionary, which rule 15 establishes
  is not an acceptable trade.
- **Parakeet v3.** Worse English than v2 and worse Portuguese than Whisper (§1). It would only
  make sense as a single engine for both languages, which rule 3 rules out.
- **Replacing WhisperKit.** Costs the same work minus a picker entry, plus deleting the fallback
  that rules 7–9 exist to fall back to.
- **Automatic engine routing.** Impossible, not deferred — rule 4.
- **Portuguese or mixed-language support on Parakeet.** Rule 5.
- **Restoring the previous language setting when switching back to Whisper.** Rule 3.
- **macOS CI.** Real, and out of scope here; this branch must not be the thing that first requires
  it (rule 21).

## 7. Open questions

1. **Does Parakeet actually break on mixed pt/en, in this codebase, on this microphone?** The
   evidence is an en/ru anecdote plus NVIDIA's *"not fully tuned for codeswitching capabilities"*.
   No benchmark exists for any of these models on code-switched speech. §3.1's mixed fixture
   answers it for us; the answer changes nothing in this spec (rule 3 holds either way) but decides
   whether a future "Parakeet for everything" proposal is worth hearing. **Decided by: the spike.**
2. **pt-PT accuracy for any of these models is unpublished.** FLEURS and MLS Portuguese are
   Brazilian; NVIDIA claims its training data is European Portuguese, which could mean the
   published gap understates *or* overstates real pt-PT performance. Affects any future
   reconsideration of rule 3, not this build. **Decided by: recording a real pt-PT corpus, not by
   reading.**
3. **First-launch ANE specialization time for the 446 MB Parakeet encoder.** Unpublished; the
   "~20 s" in FluidAudio's docs is a Kokoro TTS figure. The Whisper baseline is 262 s for download
   + specialization + prewarm (`docs/SPIKES.md`). If Parakeet is materially worse, §3.4 needs
   explicit first-run copy. **Decided by: measuring during the spike.**
4. **Does `.int8V2` fully resolve #760's right-context corruption, or only the reported cases?**
   Rule 13 picks it on the issue thread's evidence. §3.1 step 2 transcribes both quantisations so
   the choice rests on this corpus. **Decided by: the spike.**
5. **Whether vocabulary boosting's second encoder pass is affordable on the oldest supported Mac.**
   It roughly doubles encoder work. Rule 15 makes it mandatory, so if §5.1 threshold 4 fails *only*
   with boosting enabled, the choice is between the dictionary and the latency — and that is a
   product decision, not an engineering one. **Decided by: Miguel, if the spike forces it.**
