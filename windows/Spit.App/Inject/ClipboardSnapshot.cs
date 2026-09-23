using System.Runtime.InteropServices;

namespace Spit.App;

/// A restorable copy of the clipboard, the Windows form of `PasteboardSnapshot`. Memory formats on an
/// allow list only, each ≤ 5 MB and bitmaps ≤ 128 MB (rule 33): GDI-handle formats such as `CF_BITMAP`
/// and `CF_ENHMETAFILE` can't be copied as bytes, and a format that fails to read is left out rather than
/// blocking the paste. Windows re-synthesises what it derives (`CF_BITMAP` from a DIB, `CF_TEXT` from Unicode text).
public sealed class ClipboardSnapshot
{
    /// Mac `PasteboardSnapshot.maxBytes`, for every format but bitmaps.
    public const int MaxBytes = 5_000_000;

    /// Bitmaps are the exception. A Mac screenshot is compressed PNG and fits in 5 MB; a Windows one is an
    /// uncompressed DIB — 8.3 MB at 1920×1080, 33 MB at 4K — and leaving it out made the restore empty the
    /// clipboard and destroy the user's image (prd-windows-parity.md rule 10: the clipboard always comes back).
    /// 128 MB covers Print Screen across three 4K displays; the copy lives only until the restore, 1.5 s later.
    public const int MaxBitmapBytes = 128_000_000;

    public static readonly IReadOnlyList<uint> AllowedStandardFormats =
        [Native.CF_UNICODETEXT, Native.CF_DIBV5, Native.CF_DIB, Native.CF_HDROP];

    public static readonly IReadOnlyList<string> AllowedRegisteredFormats = ["HTML Format", "Rich Text Format", "PNG"];

    private ClipboardSnapshot(IReadOnlyList<KeyValuePair<uint, byte[]>> items) => Items = items;

    /// Format id and bytes, in the order the clipboard listed them.
    public IReadOnlyList<KeyValuePair<uint, byte[]>> Items { get; }

    /// Opens the clipboard with `owner`, Spit's own window, and copies it; null if it stayed busy.
    public static ClipboardSnapshot? Capture(nint owner)
    {
        ClipboardSnapshot? snapshot = null;
        return ClipboardSession.Run(owner, "snapshot", () =>
        {
            snapshot = CaptureOpen();
            return true;
        }) ? snapshot : null;
    }

    /// Opens the clipboard with `owner` and puts the copy back.
    public bool Restore(nint owner) => ClipboardSession.Run(owner, "restore", RestoreOpen);

    public Task<bool> RestoreAsync(nint owner) => ClipboardSession.RunAsync(owner, "restore", RestoreOpen);

    /// Copies the clipboard the caller already has open.
    internal static ClipboardSnapshot CaptureOpen()
    {
        var allowed = new HashSet<uint>(AllowedStandardFormats);
        foreach (var name in AllowedRegisteredFormats)
        {
            var format = ClipboardSession.Register(name);
            if (format != 0) allowed.Add(format);
        }

        var items = new List<KeyValuePair<uint, byte[]>>();
        uint current = 0;
        while ((current = Native.EnumClipboardFormats(current)) != 0)
        {
            if (!allowed.Contains(current)) continue;
            var data = ClipboardSession.ReadData(current, MaxBytesFor(current));
            if (data is not null) items.Add(new KeyValuePair<uint, byte[]>(current, data));
        }
        var error = Marshal.GetLastPInvokeError();
        if (error != 0) Log.Win32Failure("clipboard", "EnumClipboardFormats", error);

        return new ClipboardSnapshot(items);
    }

    internal static int MaxBytesFor(uint format) => format is Native.CF_DIB or Native.CF_DIBV5 ? MaxBitmapBytes : MaxBytes;

    /// Puts the copy back on the clipboard the caller already has open. An empty snapshot empties the
    /// clipboard, as `PasteboardSnapshot.restore` clears it. Restored content is excluded from history
    /// too: the user's copy is already in Clipboard History, and the restore must not add it twice.
    internal bool RestoreOpen()
    {
        if (!ClipboardSession.Empty()) return false;
        if (Items.Count == 0) return true;

        var restored = ClipboardSession.MarkExcludedFromHistory();
        foreach (var (format, data) in Items)
            restored &= ClipboardSession.SetData(format, data);
        return restored;
    }
}
