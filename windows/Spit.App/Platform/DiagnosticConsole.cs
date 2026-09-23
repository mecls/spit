using System.IO;
using System.Runtime.InteropServices;

namespace Spit.App;

/// The console the spike diagnostics (`--key-log`, `--clip-log`, `--elevation-log`) print to. Spit is a GUI app
/// with no console of its own, so it opens one.
internal static class DiagnosticConsole
{
    public static (StreamWriter Output, StreamReader Input) Open(string category)
    {
        if (!Native.AllocConsole())
        {
            // Already attached to one (started from a terminal that gave us its console): print there.
            HookLog.Info(category, $"AllocConsole failed: error {Marshal.GetLastPInvokeError()}");
        }
        // Console.Out may have been bound to "no console" before AllocConsole; rebind it to the new handles.
        var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(output);
        return (output, new StreamReader(Console.OpenStandardInput()));
    }
}
