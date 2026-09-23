using System.Collections.Concurrent;
using Spit.Core;

namespace Spit.App;

/// <summary>
/// `Spit.exe --key-log`: the console diagnostic for spike S4. Prints what the hook reports for every key —
/// virtual key, scan code, flags, direction — so rules 27, 28 and 35 can be checked against a real keyboard
/// (Right Ctrl, Right Alt, AltGr+2 on pt-PT, Right Ctrl+C, the vkE8 mask).
///
/// The only place key identities are ever written anywhere, and only to a console the user opened. Nothing
/// goes to the log file.
/// </summary>
public static class KeyLog
{
    private const int VkEscape = 0x1B;

    public static int Run()
    {
        var (output, _) = DiagnosticConsole.Open("key-log");

        using var queue = new BlockingCollection<(SendOrPostCallback Callback, object? State)>();
        using var hook = new KeyboardHook(new QueueContext(queue));

        var escapes = 0;
        var escapeHeld = false;
        hook.KeyEvent += e =>
        {
            var injected = (e.Flags & HotkeyTranslator.LlkhfInjected) != 0;
            var extended = (e.Flags & HotkeyTranslator.LlkhfExtended) != 0;
            output.WriteLine($"vk=0x{e.VkCode:X2} sc=0x{e.ScanCode:X3} flags=0x{e.Flags:X2} up={e.IsKeyUp} injected={injected} extended={extended}");

            if (injected) return;
            if (e.VkCode != VkEscape)
            {
                if (!e.IsKeyUp) escapes = 0;
                return;
            }
            if (e.IsKeyUp)
            {
                escapeHeld = false;
            }
            else if (!escapeHeld)
            {
                // Autorepeat is not a second press.
                escapeHeld = true;
                if (++escapes == 2) queue.CompleteAdding();
            }
        };

        if (!hook.Start())
        {
            output.WriteLine("The keyboard hook could not be installed.");
            return 1;
        }
        output.WriteLine("Spit key log. Press keys to see what the hook reports; press Esc twice to quit.");

        foreach (var (callback, state) in queue.GetConsumingEnumerable()) callback(state);
        hook.Stop();
        return 0;
    }

    /// A synchronisation context for a thread with no dispatcher: callbacks queue up and `Run` executes them.
    private sealed class QueueContext(BlockingCollection<(SendOrPostCallback Callback, object? State)> queue) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            // After the second Esc the queue is closed; late keys have nowhere to go and are dropped.
            if (queue.IsAddingCompleted) return;
            try
            {
                queue.Add((d, state));
            }
            catch (InvalidOperationException)
            {
                // Closed or disposed between the check and the add (ObjectDisposedException is one of these).
            }
        }

        public override void Send(SendOrPostCallback d, object? state) => Post(d, state);

        public override SynchronizationContext CreateCopy() => this;
    }
}
