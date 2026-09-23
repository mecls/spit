import Foundation

/// `.skipped` is the gate's verdict (prd-sub-second-dictation.md rules 11-14): the transcript was
/// already clean, so no cleanup engine ran and no network round trip was spent. It inserts the raw
/// text exactly like `.literal` does; the two are separate cases because they mean different things
/// — `.literal` is the user's explicit choice, `.skipped` is ours, and only the second one needs to
/// be visible (rule 24) and counted (rule 14) while the thresholds are being tuned.
enum RefineResult: Equatable { case cleaned(String), rawFallback(FallbackReason), literal, skipped }

enum MachineEvent {
    case modelReady, modelProgress(Double)
    case hotkeyDown(FrontmostApp?), hotkeyUp, cancelRequested
    case audioStopped(samples: [Float], ms: Int, speech: Bool)
    case transcribed(UUID, text: String, language: String, ms: Int), transcriptionFailed(UUID)
    case refined(UUID, RefineResult)
    case inserted(UUID, Injected), insertFailed(UUID)
}

enum HUDState: Equatable {
    case hidden, listening, transcribing(progress: Double?), cleaning
    case done(preview: String?, via: String?), message(String), modelLoading(Double)
}

enum Effect: Equatable {
    case startRecording, stopRecording, discardRecording
    case transcribe(UUID), refine(UUID), insert(UUID, String)
    case hud(HUDState), reportInjected(UUID, Injected)
}

/// Pure reducer. Owns the queue of in-flight dictations; pastes strictly in order.
struct DictationMachine {
    enum Phase: Equatable { case modelLoading(Double), ready }
    static let minimumMs = 400

    private(set) var phase: Phase = .modelLoading(0)

    /// Whether a double-tap may latch.
    ///
    /// False while the model is still loading. `.hotkeyDown` already refuses to start a dictation then,
    /// so latching would leave the bar's indicator claiming a live microphone over a session that never
    /// began, and the next press would end a dictation that does not exist. The bar's mic button checked
    /// this from the start; the key path did not. Windows has the same rule as `DictationMachine.CanLatch`,
    /// checked in `Coordinator.Apply(TapLatch.Outcome.Latch)`.
    var canLatch: Bool { phase == .ready }
    private(set) var queue: [Dictation] = []
    private var recording: UUID?      // dictation currently capturing audio
    private var cancelledRecording = false

    mutating func handle(_ e: MachineEvent) -> [Effect] {
        switch e {
        case .modelProgress(let p):
            phase = .modelLoading(p); return [.hud(.modelLoading(p))]
        case .modelReady:
            phase = .ready; return [.hud(.hidden)]

        case .hotkeyDown(let app):
            if case .modelLoading(let p) = phase { return [.hud(.modelLoading(p))] }
            guard recording == nil else { return [] }
            let d = Dictation(clientId: UUID(), startedAt: Date(), app: app)
            queue.append(d); recording = d.clientId; cancelledRecording = false
            return [.startRecording, .hud(.listening)]

        case .hotkeyUp:
            guard recording != nil, !cancelledRecording else { return [] }
            return [.stopRecording]

        case .cancelRequested:
            guard let id = recording else { return [] }
            queue.removeAll { $0.clientId == id }
            recording = nil; cancelledRecording = true
            return [.discardRecording, .hud(.hidden)]

        case .audioStopped(let samples, let ms, let speech):
            guard let id = recording, let i = index(of: id) else { return [] }
            recording = nil
            guard ms >= Self.minimumMs, speech, !samples.isEmpty else {
                queue.remove(at: i); return [.hud(.message(Strings.nothingHeard))]
            }
            queue[i].samples = samples; queue[i].audioMs = ms; queue[i].stage = .transcribing
            return [.transcribe(id), .hud(.transcribing(progress: nil))]

        case .transcribed(let id, let text, let language, let ms):
            guard let i = index(of: id) else { return [] }
            queue[i].samples = []
            let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !trimmed.isEmpty else { queue.remove(at: i); return [.hud(.message(Strings.nothingHeard))] }
            queue[i].raw = trimmed; queue[i].language = language; queue[i].asrMs = ms; queue[i].stage = .refining
            return [.refine(id), .hud(.cleaning)]

        case .transcriptionFailed(let id):
            queue.removeAll { $0.clientId == id }
            return [.hud(.message(Strings.nothingHeard))]

        case .refined(let id, let result):
            guard let i = index(of: id) else { return [] }
            switch result {
            case .cleaned(let t): queue[i].cleaned = t
            case .literal: queue[i].cleaned = nil
            case .skipped:
                queue[i].cleaned = nil
                queue[i].llmModel = CleanupEngine.skipped
            case .rawFallback(let r):
                // Said once the paste is done (`.inserted`), not now: `.done` followed and replaced it
                // before anyone could read it, and a clipboard-only paste's own message came before it
                // and was replaced by it.
                queue[i].fallback = r; queue[i].cleaned = nil
            }
            queue[i].stage = .readyToInsert
            return insertHeadIfReady()

        case .inserted(let id, let how):
            guard let i = index(of: id) else { return [] }
            let preview = queue[i].textToInsert
            // Rule 24: which path produced this text, so the gate's decisions are visible while its
            // thresholds are being tuned. Derived, not timed — the release→paste measurement lives
            // in the coordinator, which keeps this reducer pure and its effects comparable in tests.
            let via = queue[i].llmModel == CleanupEngine.skipped ? Strings.viaSkipped
                : (queue[i].cleaned != nil ? Strings.viaCleaned : nil)
            let fallback = queue[i].fallback
            queue.remove(at: i)
            var effects: [Effect] = [.reportInjected(id, how)]
            let next = insertHeadIfReady()
            effects += next
            // A raw fallback says so in place of `.done`, for as long as `.done` would have stayed.
            if next.isEmpty {
                if let r = fallback {
                    effects.append(.hud(.message(r == .unauthorized ? Strings.tokenInvalid : Strings.pastedRaw)))
                } else {
                    effects.append(.hud(.done(preview: preview, via: via)))
                }
            }
            return effects

        case .insertFailed(let id):
            queue.removeAll { $0.clientId == id }
            return [.hud(.message(Strings.secureField))] + insertHeadIfReady()
        }
    }

    private func index(of id: UUID) -> Int? { queue.firstIndex { $0.clientId == id } }

    /// Only the head of the queue may paste, and only once its text is ready and nothing else is inserting.
    private mutating func insertHeadIfReady() -> [Effect] {
        guard let head = queue.first, head.stage == .readyToInsert, let text = head.textToInsert else { return [] }
        queue[0].stage = .inserting
        return [.insert(head.clientId, text)]
    }
}
