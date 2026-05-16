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
    /// <summary>OSC 7 carried a non-empty file://host/ authority. Data = hostname.</summary>
    public const string EventRemoteHost = "REMOTE_HOST";
    /// <summary>OSC 1338 cmux-agent announce. Data = JSON-encoded AgentAnnouncePayload.</summary>
    public const string EventAgentAnnounce = "AGENT_ANNOUNCE";
}

/// <summary>
/// Wire payload for EventAgentAnnounce. Mirrors the OscHandler 1338 signature.
/// </summary>
public class AgentAnnouncePayload
{
    public string Agent { get; set; } = "";
    public string Event { get; set; } = "";
    public string? Host { get; set; }
    public string? SessionId { get; set; }
    public long? TsEpoch { get; set; }
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
    /// <summary>Last hostname reported via OSC 7 file://host/ authority (null if never).</summary>
    public string? RemoteHost { get; set; }
    /// <summary>Currently announced agent id via OSC 1338 ("claude" etc.). Null when no
    /// announce or after a SessionEnd-style "end" announce.</summary>
    public string? AnnouncedAgent { get; set; }
    /// <summary>Unix epoch seconds of the last OSC 1338 announce for staleness GC.</summary>
    public long? AnnouncedAtEpoch { get; set; }
}

public class DaemonEvent
{
    public string Type { get; set; } = "";
    public string? PaneId { get; set; }
    public string? Data { get; set; }
}
