import XCTest
@testable import Voice

/// Joining the streamed transcript to the final pass over the audio it did not reach.
///
/// Every case here is a real failure or a real recording, not an invented one. The seam cannot be
/// found from the segment timestamps: Whisper's text runs past them or stops short of them, and
/// each direction produced a bug that looked unrelated to the other.
final class StitchTests: XCTestCase {

    /// The counting test, verbatim. Counting to twenty over 13.6s: the stream covered 0-7884ms and
    /// should have produced `1…12`, but hallucinated the sequence onward to 29 — a predictable
    /// series is exactly what the language model continues past the end of its audio. The tail pass
    /// over 7884-13599ms produced `12…20`, which is correct. Appending them gave the user
    /// `1 … 29, 12 … 20`.
    func testTheHallucinatedContinuationIsCutAtTheSeam() {
        let streamed = "1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29,"
        let tail = "12, 13, 14, 15, 16, 17, 18, 19, 20."
        XCTAssertEqual(Stitch.join(streamed: streamed, tail: tail),
                       "1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20.")
    }

    /// The ordinary case: the tail is genuinely new speech the stream never reached. This is what
    /// the final pass exists for, and it must not be thrown away for lack of an overlap.
    func testGenuinelyNewSpeechIsKept() {
        XCTAssertEqual(
            Stitch.join(streamed: "Let's test the live transcription path and how well it's working,",
                        tail: "if it's cutting the chunks properly or not."),
            "Let's test the live transcription path and how well it's working, if it's cutting the chunks properly or not.")
    }

    /// A short overlap at the seam — the common shape when the timestamp lands mid-phrase.
    func testAnOverlappingSeamIsNotRepeated() {
        XCTAssertEqual(
            Stitch.join(streamed: "it has all the settings and so I think we have",
                        tail: "and so I think we have this in a very working state."),
            "it has all the settings and so I think we have this in a very working state.")
    }

    /// Punctuation and case are not evidence about where the seam is: `"20."` has to match `"20"`.
    func testPunctuationAndCaseDoNotBlockTheMatch() {
        XCTAssertEqual(Stitch.join(streamed: "Yeah, Once Again, it didn't get part",
                                   tail: "once again, it didn't get part of the text."),
                       "Yeah, once again, it didn't get part of the text.")
    }

    /// A phrase repeated earlier in the dictation is not the seam. Cutting at the first match would
    /// silently delete everything the user said in between — a worse bug than the one being fixed.
    func testARepeatedPhraseEarlierOnIsNotMistakenForTheSeam() {
        XCTAssertEqual(
            Stitch.join(streamed: "and then I said and then I said something else entirely here",
                        tail: "and then I said something different."),
            "and then I said and then I said something different.")
    }

    /// Too short to be evidence. Two common words match by chance, so a one-word tail is appended
    /// rather than used to cut the transcript.
    func testAOneWordTailIsAppendedNotMatched() {
        XCTAssertEqual(Stitch.join(streamed: "the quick brown fox the", tail: "the"),
                       "the quick brown fox the the")
    }

    func testEmptyInputs() {
        XCTAssertEqual(Stitch.join(streamed: "Hello there.", tail: ""), "Hello there.")
        XCTAssertEqual(Stitch.join(streamed: "", tail: "Hello there."), "Hello there.")
        XCTAssertEqual(Stitch.join(streamed: "   ", tail: "  Hello there. "), "Hello there.")
    }

    /// The seam at the very start: the stream produced only a hallucination, and the tail is the
    /// whole real transcript.
    func testAWhollyOverlappingTailReplacesTheStream() {
        XCTAssertEqual(Stitch.join(streamed: "one two three four", tail: "one two three four five six"),
                       "one two three four five six")
    }

    // MARK: - Back-ported from windows/Spit.Core.Tests/StreamTailCombineTests.cs

    /// The Bool is the whole point of `tryJoin`: `false` means the text is the tail appended, which is
    /// right for new speech and a duplication otherwise. The caller — not `Stitch` — decides which.
    func testTryJoinReportsWhetherItFoundTheSeam() {
        let stitched = Stitch.tryJoin(streamed: "the build on friday after the review",
                                      tail: "friday after the review and more")
        XCTAssertTrue(stitched.foundSeam)
        XCTAssertEqual(stitched.text, "the build on friday after the review and more")

        // Three words either side and no seam among them. Appended — and this exact pair pasted
        // "Hi Joel, quick Joel, quick update." in a Windows review.
        let appended = Stitch.tryJoin(streamed: "Hi Joel, quick", tail: "Joel, quick update.")
        XCTAssertFalse(appended.foundSeam)
        XCTAssertEqual(appended.text, "Hi Joel, quick Joel, quick update.")
        // `join` is `tryJoin` with the answer thrown away, and must stay so.
        XCTAssertEqual(appended.text, Stitch.join(streamed: "Hi Joel, quick", tail: "Joel, quick update."))
    }

    /// `join` and `tryJoin` keep the original rule — the anchor starts at the tail's first word.
    /// Only `tryJoinAllowingTailSkip` may step over a fragment the overlap cut in half.
    func testOnlyTheSkippingVariantStepsOverACutOffWord() {
        let streamed = "the dashboard is running on Convex now"
        let tail = "board is running on Convex now, and more"

        XCTAssertFalse(Stitch.tryJoin(streamed: streamed, tail: tail).foundSeam)
        XCTAssertTrue(Stitch.tryJoinAllowingTailSkip(streamed: streamed, tail: tail).foundSeam)
    }

    /// The fifth Windows review's case: the overlap cut "dashboard" to "board", the anchor had to start
    /// at the tail's first word, no seam was found, and ordinary speech went to a whole second pass.
    func testACutOffWordAtTheTailsStartStillFindsTheSeam() {
        let joined = Stitch.tryJoinAllowingTailSkip(
            streamed: "the MiraSite dashboard is running on Convex now",
            tail: "board is running on Convex now, and the Olamaki lives on the VPS")

        XCTAssertTrue(joined.foundSeam)
        XCTAssertEqual(joined.text,
                       "the MiraSite dashboard is running on Convex now, and the Olamaki lives on the VPS")
    }
}
