# Day-0 spikes

Measurements that set the plan's numbers. Throwaway code lived in the session scratchpad; only the results are kept.

## Ollama Cloud (2026-09-09, house key from hub/miraside/.env.local)

19 models listed on the account, including `gpt-oss:120b`, `gpt-oss:20b`, `gemma4:31b`, `qwen3.5:397b`, `glm-5.3-flash`.

Five short dictation fixtures (pt, en, mixed, fillers, one word), the v1 cleanup system prompt, `temperature 0.1`, `max_tokens 512`, sequential:

| model | reasoning_effort | p50 | max | completion tokens (5 fixtures) | reasoning in `message.reasoning` | quality |
|---|---|---|---|---|---|---|
| `gemma4` (31b) | `none` | **706 ms** | 910 ms | 2–20 | none | clean on all 5; digits for numbers; honoured "new line" |
| `gpt-oss:120b` | `low` | 837 ms | 1099 ms | 30–104 (60–100 of them reasoning) | 76–336 chars | clean on all 5 |
| `gpt-oss:120b` | `none` | 779 ms | 1085 ms | 95–184 | still reasons (`none` is ignored) | clean |
| `qwen3.5` (397b) | `none` | 1498 ms | 1877 ms | 2–22 | none | clean, slower |
| `gpt-oss:20b` | `low` | 1806 ms | 2201 ms | 29–118 | yes | left "hã" in place once; slower than 120b |
| `glm-5.3-flash` | `none` | 2308 ms | 2782 ms | 55–225 | **leaked into `content` with a `</think>` marker** | unusable without the strip guard |

Consequences:
- **Default `LLM_MODEL=gemma4`, `LLM_REASONING=none`.** Fastest, smallest output, no reasoning overhead; `gpt-oss:120b` at `low` is the documented alternative. The M1 bench re-checks on 20 fixtures.
- The `<think>` strip + reject guard is not theoretical: `glm-5.3-flash` puts its whole reasoning in `content`.
- gpt-oss ignores `reasoning_effort: none` and emits 60–100 reasoning tokens per short request, which is why the token cap floors at 512.

Concurrency probe on `gpt-oss:120b`: 3 parallel → all ok, wall 1.2 s; **5 parallel → all ok, wall 1.15 s**, no rate-limit headers. The account tier allows at least 5 in flight, so `LLM_CONCURRENCY=5`.

## WhisperKit on the M2 (2026-09-09, `argmax-oss-swift` 0.18.0, model `large-v3-v20240930_turbo_632MB`)

Throwaway SwiftPM executable; `DecodingOptions(temperature: 0, temperatureFallbackCount: 0, usePrefillPrompt: true, detectLanguage: true, withoutTimestamps: true)`, default compute units, no prompt tokens.

| step | time |
|---|---|
| first run: download 624 MB + CoreML specialization + prewarm | **262 s** (one-time; the onboarding must show progress) |
| `en.wav` 12.5 s, first transcription after load | 1.72 s |
| `en.wav` 12.5 s, second transcription | **0.92 s** |
| `pt-synthetic.wav` 11.3 s (English TTS voice reading Portuguese) | 0.88 s |
| `silence.wav` 6 s | 0.46 s → hallucinated `you` |

Consequences:
- Steady-state ASR on this M2 is **~0.9 s** for a 10–12 s clip, better than the 1.2–2 s the plan assumed. M3 acceptance: an 8 s clip pasted within **1.3 s** of release.
- The first transcription after launch is ~2× slower even after `prewarm`; the coordinator should run one throwaway transcription of 1 s of zeros right after `prepare()`.
- Silence produced `you`: the energy gate before Whisper is required, not optional.
- Without prompt tokens, "Miraside" came out as "Miracyte" and "Ollama key" as "Olamaki" — the dictionary-as-prompt and the server-side dictionary earn their place.
- No pt-PT TTS voice is installed on this Mac, so real Portuguese detection/accuracy is verified in M3 with a live recording, not a fixture.
- WhisperKit's default `downloadBase` is `~/Documents/huggingface`, layout `models/<repo>/openai_whisper-<variant>`; the app passes its own Application Support base. The spike's copy at `~/Documents/huggingface/models/argmaxinc/whisperkit-coreml/` (624 MB) can be deleted or moved into the app's models folder to skip the first download.
- Task 7: the in-app model folder (`~/Library/Application Support/Voice/models/models/argmaxinc/whisperkit-coreml/openai_whisper-large-v3-v20240930_turbo_632MB`, 616 MB, 6 entries) was pre-seeded from this spike's copy before M3 work began, so `ModelManager.isDownloaded(...)` is true and `WhisperKitTranscriberTests` never downloads.
- Task 7 in-app test (`VOICE_ASR_TESTS=1`, `WhisperKitTranscriberTests`, real WhisperKit pipeline, en fixture): on a quiet system right after the env-var scheme fix — model load (warm CoreML cache) **9.3 s**, first transcription **1.58 s**, second transcription **1.31 s**, both well under the 3 s budget and close to this spike's 0.9 s steady state. Later runs on this same Mac, back-to-back with ~10 other CoreML loads plus a loaded Dia browser and Spotlight reindexing the new 616 MB model folder (`uptime` load average 20–36 on 8 cores), slowed to 21–38 s load / 11–12 s per transcription with identical correct output (non-empty text, `language: "en"`) — a system-contention artifact, not a regression; re-run on an idle Mac for a release-gate number. Also found: a non-nil `promptTokens` (dictionary-vocabulary biasing) combined with `temperatureFallbackCount: 0` reproducibly made the single decode attempt come back flagged `needsFallback` with empty text on real speech, independent of content/language/`usePrefillCache` — `WhisperKitTranscriber` retries once without the prompt when this happens (see `task-7-report.md`).

## Event tap under secure input (pending — needs a permission grant, run by Miguel)

## VPS (2026-09-09, `ssh -i ~/.ssh/<SSH_KEY> vps`)

`vps` = <VPS_IP>, Ubuntu 24.04.4, **2 vCPU / 7.8 GB RAM** (not the 4 vCPU / 16 GB assumed during planning), 73 GB free, Docker 29.4, root shell, passwordless sudo.

| container | ports | note |
|---|---|---|
| `<proxy-container>` | `0.0.0.0:80`, `0.0.0.0:443` | **the public reverse proxy** — docker provider, `exposedbydefault=false`, entrypoints `web` (redirects to https) / `websecure`, certresolver `mytlschallenge` (TLS-ALPN), network `<proxy-network>` |
| `n8n-n8n-1` | `127.0.0.1:5678` | routed as `n8n.miraside.co` via labels |
| `arwatches-openwa` | `127.0.0.1:2785` | loopback only, as its README says |
| `arwatches-worker` | exposed 4000 only | not published |
| `deal-pipeline` | none | `/opt/miraside/demos/deal-pipeline` |

ufw allows 22, 80, 443, 8642, 9119. Nothing needs opening. The ARwatches docs' "no inbound path" describes the ARwatches containers, not the box.

Consequences: **Caddy dropped**; `voice-api` joins `<proxy-network>` with Traefik labels; deploy dir `/opt/miraside/voice`; memory limit 512 MB is generous on 7.8 GB but keep it. The 2 vCPU figure makes the on-device Whisper decision final for this box.

Known: a `speaches` container (`arwatches-speaches`, faster-whisper CPU) is defined in the ARwatches compose but was not running at audit time.

### Post-deploy audit (2026-09-09)

First deploy of `miraside-voice` (Task 10, commit `76e5cd5`), synced by rsync (no git remote
yet) since DNS for `voice.miraside.co` wasn't live yet. `docker compose ps` showed `voice-api`
healthy and `backup` up; in-network `GET /health` returned
`{"ok":true,"version":"0.1.0","db":"ok","llm":"ok"}`. Port map afterwards, unchanged from the
pre-deploy baseline apart from the two new voice containers (neither publishes a host port):

```
miraside-voice-backup-1                                  (no ports)
miraside-voice-voice-api-1        8080/tcp
miraside-openwa      127.0.0.1:2786->2785/tcp
arwatches-worker      4000/tcp
arwatches-openwa      127.0.0.1:2785->2785/tcp
deal-pipeline                                              (no ports)
<proxy-container>          0.0.0.0:80->80/tcp, [::]:80->80/tcp, 0.0.0.0:443->443/tcp, [::]:443->443/tcp
n8n-n8n-1              127.0.0.1:5678->5678/tcp
```

Traefik registered the router on first request, confirmed via
`docker run --rm --network <proxy-network> curlimages/curl:8.10.1 -s http://<proxy-container>:8080/api/http/routers`:
`"name":"voice@docker"`, `"rule":"Host(\`voice.miraside.co\`)"`, `"service":"voice"`. The
router won't get a TLS cert until the Namecheap A record for `voice.miraside.co` exists and
resolves (Ruling 3 in Task 10 — expected, not yet Miguel's turn at audit time).

## Apple FoundationModels as the cleanup engine (2026-09-11, macOS 26.6.2, M2)

Run for `tasks/prd-sub-second-dictation.md` §7 open question 1, which made this measurement the
first task and set the bar: local must beat the 706 ms gemma4 p50 "by a clear margin", re-check the
approach if p90 exceeds ~400 ms. Same 20 fixtures as `server/src/cli/bench.ts`
(`BENCH_FIXTURES`), same cleanup rules as `buildSystemPrompt`, fresh `LanguageModelSession` per
fixture (a reused session accumulates transcript context and grows unboundedly).

| attempt | p50 | p90 | max | preamble/markdown leak | translated | content loss |
|---|---|---|---|---|---|---|
| v1: server prompt verbatim, plain string output, temp 0.1 | 583 ms | 906 ms | 1089 ms | 2/20 | 2/20 | 4/20 |
| v2: `@Generable` structured output, few-shot, temp 0 | 718 ms | 879 ms | 995 ms | 0/20 | 2/20 | 2/20 |

Model warmup (session + first respond) is **2.7 s**, so the app would need a launch-time prewarm
exactly like the WhisperKit one.

**Neither attempt is shippable, and latency is the lesser problem.**

- v1 inverted meaning and translated: `preciso que envies o invoice para o cliente` →
  `**Invoice sent to client today.**` (English, summarized, markdown, and the opposite of what was
  said). `vinte e cinco euros às três e meia` → `Twenty-five euros at three and a half`.
  `um so the the client wants to move the meeting to, uh, Thursday afternoon` → `Thursday afternoon`.
  One response opened with `Sure, I can help with that. Here's the cleaned text:`.
- v2 fixed the leakage (0/20) and got `25 euros às 3:30` exactly right, but **regurgitates the
  few-shot examples on short inputs**: `o que achas disto` and `sim` both returned an unrelated
  example sentence verbatim. Pasting text the user never said is worse than doing nothing, and
  short utterances are the common case. It also left fillers untouched in 7/20 and stripped
  Portuguese accents (`está` → `esta`, `números` → `numeros`).
- Both attempts translate across languages despite an explicit instruction not to, which answers
  §7 open question 2 (pt-PT quality) in the negative without needing a separate pt fixture.

Consequence: **the on-device cleanup engine is not Apple FoundationModels.** The 3B on-device model
does not follow transform-only instructions reliably enough to sit between a user's speech and their
document. The skip gate, the `totalMs` instrumentation and the connection pre-warm from the same
spec are independent of this and stand unchanged; the engine choice reverts to the server
(`gemma4`, quality already validated) pending a decision on a local MLX model.

Benchmarks kept at `scratchpad/fmbench.swift` and `fmbench2.swift` for re-running against a future
OS model revision.

## whisper.cpp on a GitHub Windows runner (2026-09-13, Whisper.net 1.9.1, `windows-latest`)

Windows Server 2025, 4 vCPU, **no GPU**. `Spit.exe --smoke-test mac/Fixtures/en.wav` (12,522 ms of audio), one pass
after a 1 s warm-up, then the live path fed 100 ms at a time in real time. The runtime order is Vulkan → CPU → CPU
without AVX; with no GPU, whisper.cpp logs "no GPU found" and runs the Vulkan build on the CPU device.

| Model | Runtime | One pass | Live path | CI run |
|---|---|---|---|---|
| `ggml-large-v3-turbo-q5_0` (574 MB) | Vulkan build | 85,137 ms | 157,657 ms | 34769047187 |
| `ggml-large-v3-turbo-q5_0` | CPU | 85,098 ms | 155,968 ms | 34769047187 |
| `ggml-large-v3-turbo-q5_0` | Vulkan build | 53,114 ms | 89,868 ms | 34773209592 (a faster runner) |
| `ggml-large-v3-turbo-q5_0` | CPU | 53,577 ms | 98,838 ms | 34773209592 |
| **`ggml-small-q8_0` (264 MB)** | Vulkan build | **12,881 ms** | 36,904 ms | 34774293255 |
| **`ggml-small-q8_0`** | CPU | **12,896 ms** | 37,024 ms | 34774293255 |

- **large-v3-turbo text:** "Hi Joel, quick update. The MiraSite dashboard is running on Convex now, and the Olamaki lives
  on the VPS. Can you send me the deck before Friday? Thanks."
- **small text:** "Hi Joe, quick update. The MiraSite dashboard is running on Convex now, and the Olomac he lives on
  the VPS. Can you send me the deck before Friday? Thanks." Miguel judged it good enough; small is the Windows default.
- **CPU and Vulkan runtimes are identical without a GPU,** so trying Vulkan first costs nothing on such a PC.
- **The live path is slower than one pass on a slow CPU.** Its passes queue behind each other, and on the small model it
  found no seam between stream and tail, so it re-transcribed the whole clip (task 9.1).
- **Still to measure on a real PC (S1):** a machine with a GPU Vulkan can use, `pt-synthetic.wav`, and 20 real
  dictations through the latency SQL.

---

# The five PC spikes (S1–S5) — not yet run

Specified in `tasks/prd-spit-mac-windows.md` §5 before the Windows UI existed, never run, because until now there
was no physical PC. Five design decisions are therefore sitting on their documented fallbacks rather than on
evidence. `tasks/prd-windows-parity.md` governs how they are run; the rule numbers below are its §2.

Three things that apply to all five:

- **Run them in this order: S4 → S3 → S2 → S1 → S5** (rule 3). S4 can remove a hotkey and S1 can change the
  default model, and the §5 checklist assumes both are settled.
- **The failure branch is already decided** (rule 2). A spike that fails and then gets argued with is not a
  spike.
- **Fill these sections with raw output** — the actual `vkCode`s, the actual milliseconds, the actual log
  lines — not a sentence saying it passed (rule 1). A bare "it worked" cannot be re-checked in six months
  when the rule it justifies looks arbitrary and someone deletes it.

Each heading gets the date and the machine when it is run, matching the sections above.

## S4 — Right Ctrl / Right Alt / AltGr on a pt-PT keyboard (pending)

Harness: `Spit.exe --key-log` (`windows/Spit.App/Program.cs:8`). Subjects: `KeyboardHook.cs`, `MenuMask.cs`.

**Passes if** all four gestures produce the `vkCode`/`scanCode`/flags the hotkey code assumes, **and** `vkE8`
masking suppresses the menu bar in *both* Notepad and File Explorer (rule 12 — they use different menu
implementations), **and** nothing is swallowed: Right Ctrl+C still copies and AltGr+2 still types `@` (rule 11).

**On failure: drop `rightAlt`, Right Ctrl only.** Apply it immediately, before any other spike runs against a
hotkey that is going away (task 2.5).

| gesture | vkCode | scanCode | flags | repeats | menu opened? | passed through? |
|---|---|---|---|---|---|---|
| hold Right Ctrl | | | | | n/a | |
| hold Right Alt | | | | | Notepad: / Explorer: | |
| AltGr+2 (pt-PT) | | | | | | typed `@`? |
| Right Ctrl+C | | | | | n/a | copied? |

## S3 — reading an elevated foreground window (pending)

Harness: `Spit.exe --elevation-log` (`windows/Spit.App/Platform/ElevationLog.cs`). It prints, for each process
that comes to the front, its integrity level, `IsElevated`, `BlocksInputFromSpit` and the paste route that answer
picks — paste that output here. Subject: `windows/Spit.App/Platform/ElevationProbe.cs`. Spit runs non-elevated
throughout; the harness's first line says whether it is.

**Passes if** the probe answers correctly for both cases below. **On failure:** keep rule 30's
"access-denied counts as elevated", which is the safe direction — it degrades to clipboard-only rather than
pasting into a window it cannot reach.

| foreground window | ElevationProbe says | correct? | what the user saw |
|---|---|---|---|
| Notepad as administrator | | | |
| Notepad as normal user | | | |

## S2 — which process renders the clipboard first under Clipboard History (pending)

Harness: `Spit.exe --clip-log` (`windows/Spit.App/Platform/ClipLog.cs`). Each round promises a marker line by
delayed rendering beside `ExcludeClipboardContentFromMonitorProcessing`, then prints every `WM_RENDERFORMAT`: the
process that asked (the window holding the clipboard open), the milliseconds since the marker went up, and what was
in front. Its first line says whether Clipboard History is on. One round per paste target; the marker is made-up
text, so look for it in Win+V. Subjects: `windows/Spit.App/Inject/ClipboardSession.cs`, `ClipboardSnapshot.cs`.

**Passes if** the target app triggers the first `WM_RENDERFORMAT` in all three hosts, which is what would let
the 1.5 s restore shrink. **On failure:** keep rule 32's 1.5 s.

**Rule 9 is unconditional and outranks the spike:** press Win+V after each paste. If the dictation is in
history, stop the session and fix it — it does not become a documented limitation (task 2.9).

| paste target | first WM_RENDERFORMAT from | ms until it arrived | in Win+V history? |
|---|---|---|---|
| Notepad | | | must be **no** |
| Chrome | | | must be **no** |
| Word | | | must be **no** |

## S1 — one-pass latency: 2 runtimes × 3 models × 2 fixtures (pending)

Driver: `pwsh windows/scripts/spike-s1.ps1`. It runs the matrix, discards the first repetition of each cell
(rule 4), prints the table below filled in, and prints rule 6's verdict sentence ready to paste.

**Rule 5: a Vulkan row is only valid if `BackendLog` named the device.** Whisper.net's runtime order falls
through `Vulkan → Cpu → CpuNoAvx` in silence, so a Vulkan run that quietly loaded the CPU library looks like a
slow GPU rather than a missing one. The driver marks such rows **VOID**; void is not slow, and a void row is
re-run, not recorded.

**Rule 6:** the Windows default becomes `ggml-large-v3-turbo-q5_0.bin` **iff** its median Vulkan time on
`en.wav` is ≤ 0.25 × audio duration = **≤ 3,150 ms**. That bound is not invented here: it is the Mac's own
shipped assertion (`mac/VoiceTests/WhisperKitTranscriberTests.swift:35` fails at 3.0 s), and it is measured
the same way — a cold first transcription in a fresh process, which is what every smoke run is.

Machine: (CPU, RAM, GPU — fill in)

| fixture | model | runtime asked | runtime loaded | audio ms | kept timings | median ms | median / audio | Vulkan device |
|---|---|---|---|---|---|---|---|---|
| | | | | | | | | |

Verdict sentence (paste the driver's output verbatim):

## S5 — installing 0.2.1 over 0.2.0 (pending)

Build both first — the Mac cannot do it, because Velopack's `vpk` only packs for its host OS:

```
pwsh windows/scripts/pack.ps1 -PackVersion 0.2.0-test -OutputDir windows/Releases/s5-0.2.0
pwsh windows/scripts/pack.ps1 -PackVersion 0.2.1-test -OutputDir windows/Releases/s5-0.2.1
```

Then `pwsh windows/scripts/spike-s5.ps1 -Snapshot before` once 0.2.0-test is installed with a token, Launch at
login on and one dictation done (it warns if any is missing — survival of something never there proves nothing),
and `-Snapshot after` once 0.2.1-test is installed over it. The second run prints the table below filled in.

**Passes if,** after installing 0.2.0-test and then 0.2.1-test: exactly one entry in Installed Apps, and the
data directory, the stored token and the Run entry all survive. **On failure:** the `/spit` page tells users to
uninstall first.

| after installing 0.2.1-test over 0.2.0-test | result |
|---|---|
| entries in Installed Apps | must be exactly 1 |
| `%LOCALAPPDATA%\Miraside\Spit` intact | |
| stored token survives | |
| Run registry entry survives | |
| version the app reports | expect `0.2.1-test` |

### On a GitHub runner (2026-09-23, `windows-latest`, run 35922307917) — not the PC

`.github/workflows/spike-s5.yml`: both builds packed by `pack.ps1`, 0.2.0-test installed with `--silent`, the token
and Run value seeded in the app's own formats (Launch at login is off by default, so nothing else writes them),
Spit left running, 0.2.1-test installed over it with `--silent`. `spike-s5.ps1`'s output, verbatim:

```
setup 0.2.0-test exit 0
  Installed Apps entries: 1  app version: 0.2.0-test  data files: 2  token targets: 1  Run entry: "C:\Users\runneradmin\AppData\Local\Spit\Spit.exe"
Spit processes before the upgrade: 1
setup 0.2.1-test exit 0
  Installed Apps entries: 1  app version: 0.2.1-test  data files: 3  token targets: 1  Run entry: "C:\Users\runneradmin\AppData\Local\Spit\Spit.exe"
```

| check | result | what was seen |
|---|---|---|
| exactly one entry in Installed Apps | pass | 1 (Spit 0.2.1) |
| the app is the newer build | pass | before 0.2.0-test, after 0.2.1-test |
| the data directory survived | pass | 2 files before, 3 after |
| the stored token survived | pass | 1 target(s) before, 1 after |
| the Run entry survived and still points at a file | pass | before and after `"C:\Users\runneradmin\AppData\Local\Spit\Spit.exe"` (target exists: True) |

Worth knowing: Installed Apps shows `DisplayVersion` **0.2.1**, without `-test` — Velopack drops the pre-release
label there; the app itself reports 0.2.1-test. The two files before the upgrade are the day's log and
`settings.json`, the latter written by the first launch alone (task 3.7's pin, seen working in a real install).

What this does not cover, and the PC run (task 2.14) still must: a desktop Windows 11 rather than Windows Server, a
non-elevated user, the installer downloaded through Edge and run by double-click rather than `--silent`, and a token
and Run entry written by Spit's own Settings page rather than seeded.

## Live transcription on real clips — session 3 (pending)

Record, then replay (prd-windows-parity.md §3.3; rules 14-16):

```
windows/publish/Spit.exe --record-clips C:\spit-clips
pwsh windows/scripts/spike-live.ps1 -Clips C:\spit-clips
```

`--record-clips` writes numbered WAVs through the same `AudioCapture` a dictation uses (Enter starts, Enter stops),
so the replay is the recording sample for sample. `spike-live.ps1` runs each through the smoke test's live path with
`--warm-pass` and prints the table below, rule 15's verdict, and `live-texts.md`: for every clip that missed, the
stream's own text, the tail pass's text, the stitched result and the one-pass. The texts stay in that report folder,
never in the app log, which holds no dictated text (rules 25-26).

**Why a warm one-pass:** the smoke test's `transcribeMs` is a cold first transcription (right for S1), and the stream
runs after it, warm. Rule 15's "≤ 1.3 × the one-pass time" against the cold number would flatter streaming by the
~2× first-inference cost, so the comparison is against `warmTranscribeMs`, a second one-pass after the stream.

**Holds if**, over at least 10 clips of 10-30 s: live text character-identical to one-pass on ≥ 8 of 10, and live
time ≤ 1.3 × warm one-pass on all of them. **Otherwise** `LiveTranscription` stays `false` and the logs go to a
numbered follow-up (rule 14). Run it once before tuning (task 5.1-5.2) and once after (task 5.6), into separate
`-OutputDir`s.

Before tuning:

| clip | audio s | one-pass ms (warm) | live ms | live / one-pass | identical | same words | whole-pass fallback | segments | counted |
|---|---|---|---|---|---|---|---|---|---|
| | | | | | | | | | |

Why each miss missed (task 5.3, from `live-texts.md`, written before any constant changes):

After tuning:

| clip | audio s | one-pass ms (warm) | live ms | live / one-pass | identical | same words | whole-pass fallback | segments | counted |
|---|---|---|---|---|---|---|---|---|---|
| | | | | | | | | | |
