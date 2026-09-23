import XCTest
@testable import Voice

final class DictationMachineTests: XCTestCase {
    private func ready() -> DictationMachine { var m = DictationMachine(); _ = m.handle(.modelReady); return m }

    func testIgnoresHotkeyWhileModelLoading() {
        var m = DictationMachine()
        XCTAssertEqual(m.handle(.hotkeyDown(nil)), [.hud(.modelLoading(0))])
        XCTAssertTrue(m.queue.isEmpty)
    }

    /// A double-tap during the first launch's model load must not start a latched session.
    ///
    /// The gesture itself is valid — `TapLatch` says `.latch`, as asserted below — so nothing in the
    /// key path stops it. What stops it is `canLatch`: `.hotkeyDown` refuses to start a dictation while
    /// the model is loading, and latching over a dictation that never began leaves the bar claiming a
    /// live microphone and makes the next press end a session that does not exist. Windows guarded this
    /// in `Coordinator.Apply` (no latch without a capture); the Mac's key path had not.
    func testADoubleTapWhileTheModelIsLoadingCannotLatch() {
        var m = DictationMachine()
        XCTAssertFalse(m.canLatch)
        XCTAssertEqual(m.handle(.hotkeyDown(nil)), [.hud(.modelLoading(0))])
        XCTAssertTrue(m.queue.isEmpty)

        // The gesture does reach `.latch`, so the refusal above is the machine's and not an accident
        // of the tap timing.
        let t0 = Date(timeIntervalSince1970: 1_000_000)
        var latch = TapLatch()
        XCTAssertEqual(latch.handle(.press, at: t0), [.startDictation])
        XCTAssertEqual(latch.handle(.release, at: t0.addingTimeInterval(0.1)), [.holdOpen])
        XCTAssertEqual(latch.handle(.press, at: t0.addingTimeInterval(0.2)), [.latch])

        _ = m.handle(.modelReady)
        XCTAssertTrue(m.canLatch)
        XCTAssertEqual(m.handle(.hotkeyDown(nil)), [.startRecording, .hud(.listening)])
    }

    func testHappyPath() {
        var m = ready()
        XCTAssertEqual(m.handle(.hotkeyDown(nil)), [.startRecording, .hud(.listening)])
        XCTAssertEqual(m.handle(.hotkeyUp), [.stopRecording])
        let id = m.queue[0].clientId
        XCTAssertEqual(m.handle(.audioStopped(samples: [0.1, 0.2], ms: 1200, speech: true)), [.transcribe(id), .hud(.transcribing(progress: nil))])
        XCTAssertEqual(m.handle(.transcribed(id, text: "olá", language: "pt", ms: 900)), [.refine(id), .hud(.cleaning)])
        XCTAssertEqual(m.handle(.refined(id, .cleaned("Olá."))), [.insert(id, "Olá.")])
        XCTAssertEqual(m.handle(.inserted(id, .cleaned)), [.reportInjected(id, .cleaned), .hud(.done(preview: "Olá.", via: Strings.viaCleaned))])
        XCTAssertTrue(m.queue.isEmpty)
    }

    func testShortOrSilentRecordingIsDropped() {
        var m = ready()
        _ = m.handle(.hotkeyDown(nil)); _ = m.handle(.hotkeyUp)
        XCTAssertEqual(m.handle(.audioStopped(samples: [], ms: 200, speech: true)), [.hud(.message(Strings.nothingHeard))])
        XCTAssertTrue(m.queue.isEmpty)
        _ = m.handle(.hotkeyDown(nil)); _ = m.handle(.hotkeyUp)
        XCTAssertEqual(m.handle(.audioStopped(samples: [0], ms: 3000, speech: false)), [.hud(.message(Strings.nothingHeard))])
    }

    func testCancelWhileListeningDiscards() {
        var m = ready()
        _ = m.handle(.hotkeyDown(nil))
        XCTAssertEqual(m.handle(.cancelRequested), [.discardRecording, .hud(.hidden)])
        XCTAssertTrue(m.queue.isEmpty)
        // a hotkeyUp after cancel is a no-op
        XCTAssertEqual(m.handle(.hotkeyUp), [])
    }

    func testBurstKeepsPasteOrder() {
        var m = ready()
        _ = m.handle(.hotkeyDown(nil)); _ = m.handle(.hotkeyUp)
        let a = m.queue[0].clientId
        _ = m.handle(.audioStopped(samples: [1], ms: 1000, speech: true))
        _ = m.handle(.transcribed(a, text: "a", language: "en", ms: 1))
        // second dictation starts while the first is refining
        XCTAssertEqual(m.handle(.hotkeyDown(nil)), [.startRecording, .hud(.listening)])
        _ = m.handle(.hotkeyUp)
        let b = m.queue[1].clientId
        _ = m.handle(.audioStopped(samples: [1], ms: 1000, speech: true))
        _ = m.handle(.transcribed(b, text: "b", language: "en", ms: 1))
        // b's cleanup arrives first: it must wait
        XCTAssertEqual(m.handle(.refined(b, .cleaned("B."))), [])
        XCTAssertEqual(m.handle(.refined(a, .cleaned("A."))), [.insert(a, "A.")])
        XCTAssertEqual(m.handle(.inserted(a, .cleaned)), [.reportInjected(a, .cleaned), .insert(b, "B.")])
        XCTAssertEqual(m.handle(.inserted(b, .cleaned)), [.reportInjected(b, .cleaned), .hud(.done(preview: "B.", via: Strings.viaCleaned))])
    }

    func testRawFallbackPastesRawAndReportsRaw() {
        var m = ready()
        _ = m.handle(.hotkeyDown(nil)); _ = m.handle(.hotkeyUp)
        let id = m.queue[0].clientId
        _ = m.handle(.audioStopped(samples: [1], ms: 1000, speech: true))
        _ = m.handle(.transcribed(id, text: "raw text", language: "en", ms: 1))
        XCTAssertEqual(m.handle(.refined(id, .rawFallback(.offline))), [.insert(id, "raw text")])
        // Said after the paste, in place of `.done`: said before it, `.done` replaced it within milliseconds.
        XCTAssertEqual(m.handle(.inserted(id, .raw)), [.reportInjected(id, .raw), .hud(.message(Strings.pastedRaw))])
    }

    func testAnInvalidTokenSaysSoOnceTheRawTextIsPasted() {
        var m = ready()
        _ = m.handle(.hotkeyDown(nil)); _ = m.handle(.hotkeyUp)
        let id = m.queue[0].clientId
        _ = m.handle(.audioStopped(samples: [1], ms: 1000, speech: true))
        _ = m.handle(.transcribed(id, text: "raw text", language: "en", ms: 1))
        XCTAssertEqual(m.handle(.refined(id, .rawFallback(.unauthorized))), [.insert(id, "raw text")])
        XCTAssertEqual(m.handle(.inserted(id, .raw)), [.reportInjected(id, .raw), .hud(.message(Strings.tokenInvalid))])
    }

    func testTranscriptionFailureDropsTheDictation() {
        var m = ready()
        _ = m.handle(.hotkeyDown(nil)); _ = m.handle(.hotkeyUp)
        let id = m.queue[0].clientId
        _ = m.handle(.audioStopped(samples: [1], ms: 1000, speech: true))
        XCTAssertEqual(m.handle(.transcriptionFailed(id)), [.hud(.message(Strings.nothingHeard))])
        XCTAssertTrue(m.queue.isEmpty)
    }
}
