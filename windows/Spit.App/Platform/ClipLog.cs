using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Spit.App;

/// <summary>
/// `Spit.exe --clip-log`: the console diagnostic for spike S2. Puts a marker on the clipboard the way rule 32's
/// read-triggered restore would need it — `CF_UNICODETEXT` promised by delayed rendering, beside
/// `ExcludeClipboardContentFromMonitorProcessing` — and prints every `WM_RENDERFORMAT` that follows: which process
/// asked, how long after the marker went up, and what was in front. Rule 32 may shrink the 1.5 s restore only if
/// the first reader is always the paste target, never Clipboard History or another monitor.
///
/// The marker is text this tool makes up (`Spit S2 round 3 14:05:22`), never anything the user wrote, so it is
/// printed freely and can be looked for in Win+V afterwards (rule 9, task 2.9).
/// </summary>
public static class ClipLog
{
    private const string Category = "clip-log";
    private const uint WmRenderFormat = 0x0305;
    private const uint WmRenderAllFormats = 0x0306;
    private const uint WmDestroyClipboard = 0x0307;
    private static readonly nint MessageOnlyParent = new(-3);   // HWND_MESSAGE

    private static StreamWriter output = StreamWriter.Null;
    private static nint owner;
    private static int round;
    private static string marker = "";
    private static long placedAt;
    private static int renders;
    private static bool emptyingOurselves;

    public static int Run()
    {
        (output, var input) = DiagnosticConsole.Open(Category);
        output.WriteLine("Spit clipboard log (spike S2).");
        output.WriteLine($"Clipboard History: {ClipboardHistoryState()}. S2 needs it on (Settings > System > Clipboard).");
        output.WriteLine("Each round puts a marker on the clipboard. Paste it once with Ctrl+V into the target (Notepad, Chrome,");
        output.WriteLine("Word), then press Win+V and look for the marker: it must not be there. A render before you pasted");
        output.WriteLine("came from something other than the target.");

        using var source = new HwndSource(new HwndSourceParameters("SpitClipLog") { ParentWindow = MessageOnlyParent, WindowStyle = 0 });
        source.AddHook(WndProc);
        owner = source.Handle;

        var dispatcher = Dispatcher.CurrentDispatcher;
        var reader = new Thread(() =>
        {
            // Enter starts the next round; q (or a closed console) quits.
            string? line;
            while ((line = input.ReadLine()) is not null && !line.Trim().Equals("q", StringComparison.OrdinalIgnoreCase))
            {
                dispatcher.BeginInvoke(Place);
            }
            dispatcher.BeginInvoke(() => Quit(dispatcher));
        })
        { IsBackground = true };

        Place();
        reader.Start();
        Dispatcher.Run();
        return 0;
    }

    private static void Place()
    {
        round++;
        renders = 0;
        marker = string.Create(CultureInfo.InvariantCulture, $"Spit S2 round {round} {DateTime.Now:HH:mm:ss}");
        var placed = ClipboardSession.Run(owner, "clip-log", () =>
        {
            emptyingOurselves = true;
            try
            {
                if (!ClipboardSession.Empty()) return false;
            }
            finally
            {
                emptyingOurselves = false;
            }
            return ClipboardSession.MarkExcludedFromHistory() && PromiseText();
        });
        placedAt = Stopwatch.GetTimestamp();
        output.WriteLine();
        output.WriteLine(placed
            ? $"[round {round}] promised, not yet rendered: \"{marker}\". Paste it now."
            : $"[round {round}] could not put the marker on the clipboard (the Win32 error is in the log).");
        output.WriteLine("Enter: next round   q: quit");
    }

    /// `SetClipboardData(CF_UNICODETEXT, NULL)`: the text is only made when someone asks, by `WM_RENDERFORMAT`.
    /// A delayed render returns NULL on success too, so success is a zero last error (the generated stub clears it).
    private static bool PromiseText()
    {
        if (Native.SetClipboardData(Native.CF_UNICODETEXT, 0) != 0) return true;
        var error = Marshal.GetLastPInvokeError();
        if (error == 0) return true;
        Log.Win32Failure(Category, "SetClipboardData(CF_UNICODETEXT, NULL)", error);
        return false;
    }

    private static nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch ((uint)msg)
        {
            case WmRenderFormat:
            {
                handled = true;
                // The reader holds the clipboard open while it waits for us, so the open window is the reader's.
                var reader = Native.GetOpenClipboardWindow();
                var from = reader == 0 ? "a reader with no window (it opened the clipboard with NULL)" : Describe(ProcessOf(reader));
                var ms = Stopwatch.GetElapsedTime(placedAt).TotalMilliseconds;
                renders++;
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[round {round}] WM_RENDERFORMAT #{renders}{(renders == 1 ? " (FIRST)" : "")}: format {(uint)wParam} after {ms:F0} ms from {from}; in front: {Describe(ForegroundContext.ForegroundProcessId())}"));
                // Inside WM_RENDERFORMAT the clipboard is already open (by the reader): set the data, never open it.
                if ((uint)wParam == Native.CF_UNICODETEXT) SetText();
                return 0;
            }
            case WmRenderAllFormats:
                handled = true;
                RenderBeforeLeaving();
                return 0;
            case WmDestroyClipboard:
                if (!emptyingOurselves)
                {
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"[round {round}] another app replaced the clipboard after {Stopwatch.GetElapsedTime(placedAt).TotalMilliseconds:F0} ms; in front: {Describe(ForegroundContext.ForegroundProcessId())}"));
                }
                return 0;
        }
        return 0;
    }

    private static void SetText()
    {
        var bytes = new byte[(marker.Length + 1) * sizeof(char)];
        MemoryMarshal.AsBytes(marker.AsSpan()).CopyTo(bytes);
        ClipboardSession.SetData(Native.CF_UNICODETEXT, bytes);
    }

    /// Leaving while still the owner: keep the promise, or the marker vanishes from the clipboard with the window. Done
    /// before the dispatcher shuts down, because the hook may not run for `WM_RENDERALLFORMATS` after it has.
    private static void RenderBeforeLeaving()
    {
        if (Native.GetClipboardOwner() != owner) return;
        // If a reader already had it rendered, this replaces the text with the same text.
        ClipboardSession.Run(owner, "clip-log", () =>
        {
            SetText();
            return true;
        });
    }

    private static void Quit(Dispatcher dispatcher)
    {
        RenderBeforeLeaving();
        output.WriteLine("Done. Copy this window's text into docs/SPIKES.md (S2).");
        dispatcher.InvokeShutdown();
    }

    private static int? ProcessOf(nint window)
    {
        _ = Native.GetWindowThreadProcessId(window, out var processId);
        return processId == 0 ? null : (int)processId;
    }

    private static string Describe(int? processId) => processId is { } id
        ? $"{ForegroundContext.ForProcess(id)?.BundleId ?? "(unreadable)"} (pid {id})"
        : "(none)";

    /// HKCU\Software\Microsoft\Clipboard\EnableClipboardHistory: 1 on, 0 off, absent means never turned on.
    private static string ClipboardHistoryState()
    {
        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Clipboard", "EnableClipboardHistory", null) switch
            {
                1 => "on",
                0 => "OFF",
                null => "OFF (never turned on)",
                var other => $"unknown ({other})",
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Failure(Category, "read the Clipboard History setting", e);
            return "unreadable";
        }
    }
}
