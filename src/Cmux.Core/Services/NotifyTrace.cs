using System.IO;
using System.Threading;

namespace Cmux.Core.Services;

/// <summary>
/// Append-only diagnostic trace for the NamedPipe NOTIFY → toast pipeline.
/// Every step in the chain (server accept, dispatcher invoke, AddNotification,
/// NotificationAdded handler, ShowToast) writes one line so we can pinpoint
/// where the pipeline stalls when the hook hangs.
///
/// Disabled when the trace file's directory cannot be created. Best-effort
/// only — never throws back into the caller.
/// </summary>
public static class NotifyTrace
{
    private static readonly object _gate = new();
    private static readonly string _path = ResolvePath();

    public static void Log(string stage, string? detail = null)
    {
        try
        {
            var ts = DateTime.Now.ToString("HH:mm:ss.fff");
            var tid = Thread.CurrentThread.ManagedThreadId;
            var line = detail == null
                ? $"[{ts}] tid={tid} {stage}"
                : $"[{ts}] tid={tid} {stage} | {detail}";
            lock (_gate)
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch
        {
            // Never break the caller because of a trace failure.
        }
    }

    private static string ResolvePath()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "cmux");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "notify-trace.log");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "cmux-notify-trace.log");
        }
    }
}
