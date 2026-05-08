namespace Cmux.Core.IPC;

public static class DaemonMessageTypes
{
    public const string SessionCreate = "SESSION_CREATE";
    public const string SessionWrite = "SESSION_WRITE";
    public const string SessionResize = "SESSION_RESIZE";
    public const string SessionClose = "SESSION_CLOSE";
    public const string SessionList = "SESSION_LIST";
    public const string SessionSnapshot = "SESSION_SNAPSHOT";
    /// <summary>
    /// Walk the daemon-side ConPTY child process tree of a given pane and
    /// return a classification ("LocalClaude" / "SshSession" / "Unknown").
    /// Lets the cmuxw broadcast bar's Claude scopes work for daemon-backed
    /// sessions — without this, cmuxw only sees `null` ProcessId for those
    /// panes (since the actual ConPTY child lives under cmux-daemon.exe)
    /// and skips them entirely.
    /// </summary>
    public const string SessionClassify = "SESSION_CLASSIFY";
    public const string Ping = "PING";

    public const string EventOutput = "OUTPUT";
    public const string EventExited = "EXITED";
    public const string EventTitleChanged = "TITLE_CHANGED";
    public const string EventCwdChanged = "CWD_CHANGED";
    public const string EventBell = "BELL";
}

public class DaemonRequest
{
    public string Type { get; set; } = "";
    public string? PaneId { get; set; }
    public int? Cols { get; set; }
    public int? Rows { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? Command { get; set; }
    public string? Data { get; set; }
}

public class DaemonResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Data { get; set; }
}

public class DaemonSessionInfo
{
    public string PaneId { get; set; } = "";
    public int Cols { get; set; }
    public int Rows { get; set; }
    public string WorkingDirectory { get; set; } = "";
    public string? Title { get; set; }
    public bool IsRunning { get; set; }
    /// <summary>True when the session already existed on the daemon (reconnect/attach).</summary>
    public bool IsExisting { get; set; }
}

public class DaemonEvent
{
    public string Type { get; set; } = "";
    public string? PaneId { get; set; }
    public string? Data { get; set; }
}
