using System.Collections.Concurrent;
using Cmux.Core.IPC;
using Cmux.Core.Terminal;
using static Cmux.Core.IPC.DaemonClient;

namespace Cmux.Daemon;

public sealed class DaemonSessionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, TerminalSession> _sessions = new();

    public event Action<string>? SessionCreated;
    public event Action<string, int>? SessionExited;
    public event Action<string, string>? TitleChanged;
    public event Action<string, string>? CwdChanged;
    public event Action<string, string>? RemoteHostChanged;
    // (paneId, agent, event, host?, sessionId?, tsEpoch?)
    public event Action<string, string, string, string?, string?, long?>? AgentAnnounced;
    public event Action<string>? BellReceived;
    public event Action<string, byte[]>? RawOutput;

    public int ActiveSessionCount => _sessions.Count;

    public DaemonSessionInfo CreateSession(string paneId, int cols, int rows, string? workingDirectory, string? command)
    {
        // If session already exists, return its info (attach/reconnect semantics).
        // Includes the OSC-derived state cached on the daemon side so a fresh
        // cmuxw attaching to a long-running pane sees the correct host / agent
        // immediately — the original OSC announce/cwd events were consumed by
        // a previous cmuxw instance and won't be replayed on the wire.
        if (_sessions.TryGetValue(paneId, out var existing))
        {
            LogDaemon($"[SessionMgr] Reconnecting to existing session: {paneId} (running={existing.IsRunning}, cwd={existing.WorkingDirectory}, host={existing.RemoteHost}, agent={existing.AnnouncedAgent})");
            return new DaemonSessionInfo
            {
                PaneId = paneId,
                Cols = existing.Buffer.Cols,
                Rows = existing.Buffer.Rows,
                WorkingDirectory = existing.WorkingDirectory ?? "",
                Title = existing.Title,
                IsRunning = existing.IsRunning,
                IsExisting = true,
                RemoteHost = existing.RemoteHost,
                AnnouncedAgent = existing.AnnouncedAgent,
                AnnouncedAtEpoch = existing.AnnouncedAt.HasValue
                    ? new DateTimeOffset(existing.AnnouncedAt.Value, TimeSpan.Zero).ToUnixTimeSeconds()
                    : null,
            };
        }

        LogDaemon($"[SessionMgr] Creating NEW session: {paneId} ({cols}x{rows}) cwd={workingDirectory} cmd={command}");
        var session = new TerminalSession(paneId, cols, rows);

        session.RawOutputReceived += data =>
        {
            RawOutput?.Invoke(paneId, data);
        };

        session.ProcessExited += () =>
        {
            SessionExited?.Invoke(paneId, 0);
        };

        session.TitleChanged += title =>
        {
            TitleChanged?.Invoke(paneId, title);
        };

        session.WorkingDirectoryChanged += dir =>
        {
            CwdChanged?.Invoke(paneId, dir);
        };

        session.RemoteHostReported += host =>
        {
            RemoteHostChanged?.Invoke(paneId, host);
        };

        session.AgentAnnounceReceived += (agent, ev, host, sid, ts) =>
        {
            long? epoch = ts.HasValue
                ? new DateTimeOffset(ts.Value, TimeSpan.Zero).ToUnixTimeSeconds()
                : null;
            AgentAnnounced?.Invoke(paneId, agent, ev, host, sid, epoch);
        };

        session.BellReceived += () =>
        {
            BellReceived?.Invoke(paneId);
        };

        _sessions[paneId] = session;
        session.Start(command: command, workingDirectory: workingDirectory);

        SessionCreated?.Invoke(paneId);

        return new DaemonSessionInfo
        {
            PaneId = paneId,
            Cols = cols,
            Rows = rows,
            WorkingDirectory = session.WorkingDirectory ?? "",
            IsRunning = session.IsRunning,
        };
    }

    public void WriteToSession(string paneId, byte[] data)
    {
        if (_sessions.TryGetValue(paneId, out var session))
            session.Write(data);
    }

    public void ResizeSession(string paneId, int cols, int rows)
    {
        if (_sessions.TryGetValue(paneId, out var session))
            session.Resize(cols, rows);
    }

    public void CloseSession(string paneId)
    {
        if (_sessions.TryRemove(paneId, out var session))
            session.Dispose();
    }

    public List<DaemonSessionInfo> ListSessions()
    {
        return _sessions.Select(kvp => new DaemonSessionInfo
        {
            PaneId = kvp.Key,
            Cols = kvp.Value.Buffer.Cols,
            Rows = kvp.Value.Buffer.Rows,
            WorkingDirectory = kvp.Value.WorkingDirectory ?? "",
            Title = kvp.Value.Title,
            IsRunning = kvp.Value.IsRunning,
            RemoteHost = kvp.Value.RemoteHost,
            AnnouncedAgent = kvp.Value.AnnouncedAgent,
            AnnouncedAtEpoch = kvp.Value.AnnouncedAt.HasValue
                ? new DateTimeOffset(kvp.Value.AnnouncedAt.Value, TimeSpan.Zero).ToUnixTimeSeconds()
                : null,
        }).ToList();
    }

    public string? GetSnapshot(string paneId)
    {
        if (!_sessions.TryGetValue(paneId, out var session))
            return null;

        var snapshot = session.CreateBufferSnapshot(maxScrollbackLines: 3000);
        return System.Text.Json.JsonSerializer.Serialize(snapshot);
    }

    public TerminalSession? GetSession(string paneId)
    {
        return _sessions.GetValueOrDefault(paneId);
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
            session.Dispose();
        _sessions.Clear();
    }
}
