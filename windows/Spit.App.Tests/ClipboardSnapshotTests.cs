using static Spit.App.Tests.ClipboardTestSupport;

namespace Spit.App.Tests;

/// Rule 33: the snapshot copies allowed memory formats and puts them back byte for byte.
[Collection(ClipboardCollection.Name)]
public sealed class ClipboardSnapshotTests
{
    [WindowsFact]
    public void CaptureThenRestore_BringsBackTextAndABitmap()
    {
        WithOwnerWindow(owner =>
        {
            var text = Utf16("Spit clipboard round-trip");
            var dib = OnePixelDib();
            Assert.True(RunPatiently(owner, "test setup", () =>
                ClipboardSession.Empty()
                && ClipboardSession.SetData(Native.CF_UNICODETEXT, text)
                && ClipboardSession.SetData(Native.CF_DIB, dib)));

            var snapshot = ClipboardSnapshot.Capture(owner);
            Assert.NotNull(snapshot);
            Assert.Contains(snapshot.Items, item => item.Key == Native.CF_UNICODETEXT);
            Assert.Contains(snapshot.Items, item => item.Key == Native.CF_DIB);

            Assert.True(RunPatiently(owner, "test overwrite", () => ClipboardSession.PlaceText("dictated text")));
            Assert.True(snapshot.Restore(owner));

            byte[]? restoredText = null;
            byte[]? restoredDib = null;
            Assert.True(RunPatiently(owner, "test read", () =>
            {
                restoredText = ClipboardSession.ReadData(Native.CF_UNICODETEXT, ClipboardSnapshot.MaxBytes);
                restoredDib = ClipboardSession.ReadData(Native.CF_DIB, ClipboardSnapshot.MaxBytes);
                return true;
            }));

            // GlobalSize may round a block up, so compare the bytes that were written.
            Assert.NotNull(restoredText);
            Assert.NotNull(restoredDib);
            Assert.Equal(text, restoredText[..text.Length]);
            Assert.Equal(dib, restoredDib[..dib.Length]);
            Assert.True(Native.IsClipboardFormatAvailable(ClipboardSession.Register(ClipboardSession.ExcludeFromMonitorFormatName)));
        });
    }

    /// Checklist item 3 with a real screenshot's size: a 1920×1080 DIB is 8.3 MB, over the 5 MB every other format
    /// gets. Left out, the restore emptied the clipboard and the user's screenshot was gone.
    [WindowsFact]
    public void AFullHdScreenshot_ComesBackAfterTheDictation()
    {
        WithOwnerWindow(owner =>
        {
            var dib = Dib(1920, 1080);
            Assert.True(dib.Length > ClipboardSnapshot.MaxBytes);
            Assert.True(RunPatiently(owner, "test setup", () => ClipboardSession.Empty() && ClipboardSession.SetData(Native.CF_DIB, dib)));

            var snapshot = ClipboardSnapshot.Capture(owner);
            Assert.NotNull(snapshot);
            Assert.Contains(snapshot.Items, item => item.Key == Native.CF_DIB);

            Assert.True(RunPatiently(owner, "test overwrite", () => ClipboardSession.PlaceText("dictated text")));
            Assert.True(snapshot.Restore(owner));

            byte[]? restored = null;
            Assert.True(RunPatiently(owner, "test read", () =>
            {
                restored = ClipboardSession.ReadData(Native.CF_DIB, ClipboardSnapshot.MaxBitmapBytes);
                return true;
            }));
            Assert.NotNull(restored);
            Assert.Equal(dib, restored[..dib.Length]);
        });
    }

    [WindowsFact]
    public void FormatsOverFiveMegabytes_AreLeftOut()
    {
        WithOwnerWindow(owner =>
        {
            var png = ClipboardSession.Register("PNG");
            Assert.NotEqual(0u, png);
            Assert.True(RunPatiently(owner, "test setup", () =>
                ClipboardSession.Empty()
                && ClipboardSession.SetData(Native.CF_UNICODETEXT, Utf16("small"))
                && ClipboardSession.SetData(png, new byte[ClipboardSnapshot.MaxBytes + 1])));

            var snapshot = ClipboardSnapshot.Capture(owner);

            Assert.NotNull(snapshot);
            Assert.Contains(snapshot.Items, item => item.Key == Native.CF_UNICODETEXT);
            Assert.DoesNotContain(snapshot.Items, item => item.Key == png);
        });
    }
}
