using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Spit.App.Tests;

internal static class ClipboardTestSupport
{
    private static readonly nint MessageOnlyParent = new(-3);   // HWND_MESSAGE

    /// Runs `body` on a fresh STA thread that owns a message-only window, passing its handle: every
    /// clipboard open needs a real owner (rule 33), and the thread that opens the clipboard should own it.
    public static void WithOwnerWindow(Action<nint> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var source = new HwndSource(new HwndSourceParameters("SpitTestClipboardOwner") { ParentWindow = MessageOnlyParent, WindowStyle = 0 });
                body(source.Handle);
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Throw(failure);
    }

    /// Test-side opens wait up to 3 s rather than the app's 200 ms (that rule is the app's behaviour, not what
    /// these tests check), and name the process holding the clipboard when even that is not enough — which is
    /// how a CI failure was traced to two clipboard tests running in parallel (see `ClipboardCollection`).
    public static bool RunPatiently(nint owner, string purpose, Func<bool> work)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!Native.OpenClipboard(owner))
        {
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(3))
                throw new InvalidOperationException($"clipboard still busy after 3 s ({purpose}); held by {ClipboardHolder()}");
            Thread.Sleep(10);
        }
        try
        {
            return work();
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    private static string ClipboardHolder()
    {
        var window = GetOpenClipboardWindow();
        if (window == 0) return "no window";
        Native.GetWindowThreadProcessId(window, out var pid);
        try
        {
            return $"{System.Diagnostics.Process.GetProcessById((int)pid).ProcessName} (pid {pid})";
        }
        catch (ArgumentException)
        {
            return $"pid {pid} (exited)";
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetOpenClipboardWindow();

    public static byte[] Utf16(string text)
    {
        var bytes = new byte[(text.Length + 1) * sizeof(char)];
        MemoryMarshal.AsBytes(text.AsSpan()).CopyTo(bytes);
        return bytes;
    }

    /// `CF_UNICODETEXT` bytes up to the terminating NUL.
    public static string TextOf(byte[] bytes)
    {
        var chars = MemoryMarshal.Cast<byte, char>(bytes.AsSpan(0, bytes.Length / sizeof(char) * sizeof(char)));
        var end = chars.IndexOf('\0');
        return new string(end < 0 ? chars : chars[..end]);
    }

    /// A 1×1 32-bit `CF_DIB`: a BITMAPINFOHEADER followed by one BGRA pixel.
    /// A 32-bit top-down DIB of `width`×`height` with a pattern in it, the size Windows puts on the clipboard for a
    /// screenshot of that resolution.
    public static byte[] Dib(int width, int height)
    {
        var pixels = width * height * 4;
        var dib = new byte[40 + pixels];
        BitConverter.TryWriteBytes(dib.AsSpan(0), 40);          // biSize
        BitConverter.TryWriteBytes(dib.AsSpan(4), width);       // biWidth
        BitConverter.TryWriteBytes(dib.AsSpan(8), -height);     // biHeight: negative is top-down
        BitConverter.TryWriteBytes(dib.AsSpan(12), (short)1);   // biPlanes
        BitConverter.TryWriteBytes(dib.AsSpan(14), (short)32);  // biBitCount
        BitConverter.TryWriteBytes(dib.AsSpan(20), pixels);     // biSizeImage
        for (var i = 40; i < dib.Length; i++) dib[i] = (byte)(i * 7);
        return dib;
    }

    public static byte[] OnePixelDib()
    {
        var dib = new byte[44];
        BitConverter.TryWriteBytes(dib.AsSpan(0), 40);        // biSize
        BitConverter.TryWriteBytes(dib.AsSpan(4), 1);         // biWidth
        BitConverter.TryWriteBytes(dib.AsSpan(8), 1);         // biHeight
        BitConverter.TryWriteBytes(dib.AsSpan(12), (short)1); // biPlanes
        BitConverter.TryWriteBytes(dib.AsSpan(14), (short)32); // biBitCount
        BitConverter.TryWriteBytes(dib.AsSpan(20), 4);        // biSizeImage
        dib[40] = 0x20; dib[41] = 0x40; dib[42] = 0x80; dib[43] = 0xFF;
        return dib;
    }
}
