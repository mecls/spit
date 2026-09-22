import Foundation

/// Joins a streamed transcript to a final pass over the audio the stream did not reach.
///
/// The two overlap by an unknown amount, and **the segment timestamps cannot say by how much**.
/// That was the original assumption — take the last segment's `end` as the boundary, transcribe
/// from there, concatenate — and it produces a different bug in each direction:
///
///   - Whisper's text runs *past* the timestamp, and the tail is transcribed twice. Counting out
///     loud to twenty produced `1 … 29, 12 … 20`: the stream hallucinated `21…29` past the end of
///     its window (a predictable sequence is exactly what the language model will continue), then
///     the tail pass correctly transcribed `12…20` and it was appended wholesale.
///   - Whisper's text stops *short* of the timestamp, and the words in between are lost — the
///     sentences that came back ending mid-phrase.
///
/// So the seam is found in the text, where the evidence actually is. The tail is a fresh
/// transcription of real audio and is trusted as the more reliable of the two; where its opening
/// words reappear in the streamed transcript, that is the true seam, and everything the stream
/// claimed after it is discarded.
enum Stitch {

    /// Words compared for matching: case and punctuation carry no information about where the seam
    /// is, and `"20."` must match `"20"`.
    private static func key(_ word: String) -> String {
        word.lowercased().filter { $0.isLetter || $0.isNumber }
    }

    /// Anchor length. Long enough that a match means something, short enough to still find the seam
    /// when the tail is a few words. Capped by the tail's own length.
    private static let anchorWords = 5
    /// Below this a match is coincidence — two common words line up by chance constantly, and a
    /// false seam deletes every word between it and the real one.
    private static let minimumAnchor = 3

    /// The Mac's original rule: the stitched text, or the tail appended when no seam is found.
    static func join(streamed: String, tail: String) -> String {
        tryJoin(streamed: streamed, tail: tail).text
    }

    /// `join`, reporting whether a seam was actually found (or one side was empty).
    ///
    /// `foundSeam == false` means `text` is the tail simply appended — right when the tail is new speech,
    /// a duplication of the overlap when it is not. Text alone cannot tell those two apart, which is the
    /// whole reason the caller needs to know: appending blind is where "Hi Joel Hi Joel, quick update…"
    /// came from.
    static func tryJoin(streamed: String, tail: String) -> (text: String, foundSeam: Bool) {
        tryJoin(streamed: streamed, tail: tail, maxTailSkip: 0)
    }

    /// Leading tail words the anchor may pass over before it starts.
    ///
    /// The overlap cut can leave a fragment — "board" of "dashboard" — or a word the two passes simply
    /// heard differently, and an anchor forced to start at the tail's first word then finds no seam in
    /// ordinary speech. The skipped words lie inside the overlap, so the stream already has them.
    private static let tailSkip = 2

    /// `tryJoin` allowing the anchor to start at tail word 0, 1 or 2 — longest anchor at the earliest
    /// start first.
    static func tryJoinAllowingTailSkip(streamed: String, tail: String) -> (text: String, foundSeam: Bool) {
        tryJoin(streamed: streamed, tail: tail, maxTailSkip: tailSkip)
    }

    private static func tryJoin(streamed: String, tail: String, maxTailSkip: Int) -> (text: String, foundSeam: Bool) {
        let s = streamed.trimmingCharacters(in: .whitespacesAndNewlines)
        let t = tail.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !t.isEmpty else { return (s, true) }
        guard !s.isEmpty else { return (t, true) }

        let sWords = s.split(separator: " ").map(String.init)
        let tWords = t.split(separator: " ").map(String.init)
        let sKeys = sWords.map(key)

        for skip in 0...max(0, maxTailSkip) {
            // Capped by *both* sides. Capping only by the tail misses the seam whenever the tail is the
            // longer of the two — the case where the stream produced almost nothing but a hallucination
            // and the tail holds the real transcript.
            let longest = min(anchorWords, tWords.count - skip, sWords.count)
            guard longest >= minimumAnchor else { continue }

            // Longest anchor first: a five-word match is evidence, a three-word match is nearly so, and
            // taking the longest that matches keeps the weaker ones as a fallback rather than a
            // shortcut. The overlap is only as long as it is — the tail's opening words can run past
            // the seam — so a shorter anchor has to be tried before giving up.
            for length in stride(from: longest, through: minimumAnchor, by: -1) {
                let anchor = tWords.dropFirst(skip).prefix(length).map(key)
                // The *last* occurrence: a phrase repeated earlier in the dictation is not the seam,
                // and cutting at the earliest match would throw away everything said in between.
                for start in stride(from: sKeys.count - length, through: 0, by: -1) {
                    if Array(sKeys[start..<(start + length)]) == anchor {
                        let kept = sWords.prefix(start).joined(separator: " ")
                        let rest = tWords.dropFirst(skip).joined(separator: " ")
                        return (kept.isEmpty ? rest : kept + " " + rest, true)
                    }
                }
            }
        }

        // No seam. Appending is right only when the tail really is new speech, and this function cannot
        // know: that is what the Bool is for.
        return (s + " " + t, false)
    }
}
