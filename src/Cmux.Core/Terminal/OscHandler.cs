namespace Cmux.Core.Terminal;

/// <summary>
/// Handles OSC (Operating System Command) terminal sequences.
/// Specifically detects notification sequences (OSC 9, 99, 777)
/// used by AI coding agents like Claude Code and Codex.
/// </summary>
public class OscHandler
{
    public event Action<string>? TitleChanged;
    public event Action<string>? WorkingDirectoryChanged;
    // host extracted from OSC 7 file://host/path payload (null when payload is
    // a plain path with no authority). Emitted *in addition to*
    // WorkingDirectoryChanged so callers that only need the path stay unchanged.
    public event Action<string>? RemoteHostReported;
    // title, subtitle, body, id?, timestamp?
    // id and timestamp are non-null only when the sender supplied them via
    // OSC 99's i= / ts= keys. NotificationService uses them for dedup and to
    // record the actual emission time instead of arrival time.
    public event Action<string, string?, string, string?, DateTime?>? NotificationReceived;
    public event Action<char, string?>? ShellPromptMarker;
    // Agent self-announce from cmux-aware hooks (OSC 1338).
    // (agent, event, host?, sessionId?, ts?)
    //   agent     "claude" / "codex" / ...
    //   event     "start" | "end" | "heartbeat"
    //   host      hostname the agent is running on (local computer name or
    //             remote hostname when emitted through SSH)
    //   sessionId agent-supplied session id (e.g. Claude Code session_id)
    //   ts        Unix epoch seconds reported by the hook
    public event Action<string, string, string?, string?, DateTime?>? AgentAnnounceReceived;

    /// <summary>
    /// Processes an OSC string (without the ESC ] prefix and BEL/ST terminator).
    /// </summary>
    public void Handle(string oscString)
    {
        if (string.IsNullOrEmpty(oscString)) return;

        // Split on first ';' to get the OSC code
        int semicolonIndex = oscString.IndexOf(';');
        string codeStr;
        string payload;

        if (semicolonIndex >= 0)
        {
            codeStr = oscString[..semicolonIndex];
            payload = oscString[(semicolonIndex + 1)..];
        }
        else
        {
            codeStr = oscString;
            payload = "";
        }

        if (!int.TryParse(codeStr, out int code))
            return;

        switch (code)
        {
            case 0: // Set icon name and window title
            case 2: // Set window title
                TitleChanged?.Invoke(payload);
                break;

            case 7: // Set working directory (file://host/path)
                HandleWorkingDirectory(payload);
                break;

            case 9: // Terminal notification (body text)
                // OSC 9 ; <body> ST
                // Used by many terminal emulators for simple notifications
                NotificationReceived?.Invoke("Terminal", null, payload, null, null);
                break;

            case 99: // Extended notification (key=value pairs)
                HandleOsc99(payload);
                break;

            case 777: // Custom notification (notify;title;body)
                HandleOsc777(payload);
                break;

            case 133: // Shell integration (prompt markers)
                if (payload.Length > 0)
                {
                    var marker = payload[0];
                    string? markerPayload = null;

                    if (payload.Length > 1)
                    {
                        markerPayload = payload[1] == ';'
                            ? payload[2..]
                            : payload[1..];
                    }

                    ShellPromptMarker?.Invoke(marker, string.IsNullOrWhiteSpace(markerPayload) ? null : markerPayload);
                }
                break;

            case 1338: // cmux agent self-announce
                HandleOsc1338(payload);
                break;
        }
    }

    private void HandleWorkingDirectory(string payload)
    {
        // Format: file://hostname/path or just a path
        if (payload.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var uri = new Uri(payload);
                // AbsolutePath preserves the URI's forward-slash form
                // (Unix path semantics). Uri.LocalPath would map
                // `file://host/work` to `\\host\work` (UNC) on
                // Windows, which is wrong for the OSC 7 protocol
                // — that field always carries the *remote shell's*
                // notion of cwd, not a Windows file-system path.
                var path = Uri.UnescapeDataString(uri.AbsolutePath);
                if (!string.IsNullOrEmpty(uri.Host))
                    RemoteHostReported?.Invoke(uri.Host);
                if (!string.IsNullOrEmpty(path))
                    WorkingDirectoryChanged?.Invoke(path);
            }
            catch (UriFormatException)
            {
                // Try as plain path
                var rest = payload["file://".Length..];
                int slashIndex = rest.IndexOf('/');
                if (slashIndex > 0)
                {
                    var host = rest[..slashIndex];
                    if (!string.IsNullOrEmpty(host))
                        RemoteHostReported?.Invoke(host);
                }
                if (slashIndex >= 0)
                {
                    var path = rest[slashIndex..];
                    WorkingDirectoryChanged?.Invoke(path);
                }
            }
        }
        else if (!string.IsNullOrEmpty(payload))
        {
            WorkingDirectoryChanged?.Invoke(payload);
        }
    }

    /// <summary>
    /// OSC 1338: cmux agent self-announce. Format:
    ///   cmux-agent=<id>;event=<start|end|heartbeat>;host=<hostname>;sid=<sessionId>;ts=<epoch>
    /// Required: cmux-agent + event. Other keys optional.
    /// </summary>
    private void HandleOsc1338(string payload)
    {
        if (string.IsNullOrEmpty(payload)) return;

        string? agent = null;
        string? ev = null;
        string? host = null;
        string? sid = null;
        DateTime? ts = null;

        foreach (var pair in payload.Split(';'))
        {
            int eq = pair.IndexOf('=');
            if (eq < 0) continue;
            var key = pair[..eq].Trim();
            var value = pair[(eq + 1)..].Trim();

            switch (key)
            {
                case "cmux-agent": agent = value; break;
                case "event": ev = value; break;
                case "host":
                    host = string.IsNullOrEmpty(value) ? null : value;
                    break;
                case "sid":
                    sid = string.IsNullOrEmpty(value) ? null : value;
                    break;
                case "ts":
                    if (long.TryParse(value, out var epoch))
                        ts = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
                    break;
            }
        }

        if (!string.IsNullOrEmpty(agent) && !string.IsNullOrEmpty(ev))
            AgentAnnounceReceived?.Invoke(agent, ev, host, sid, ts);
    }

    /// <summary>
    /// OSC 99: Extended notification format.
    /// Format: key=value;key=value
    /// Keys:
    ///   t  = title
    ///   b  = body
    ///   s  = subtitle
    ///   i  = sender-supplied ID (dedup key — duplicates dropped by NotificationService)
    ///   ts = Unix epoch seconds (sender-supplied timestamp)
    /// Falls back to "OSC 99 ; body" simple form when no '=' is present.
    /// </summary>
    private void HandleOsc99(string payload)
    {
        if (payload.Contains('='))
        {
            string? title = null;
            string? body = null;
            string? subtitle = null;
            string? id = null;
            DateTime? timestamp = null;

            foreach (var pair in payload.Split(';'))
            {
                int eq = pair.IndexOf('=');
                if (eq < 0) continue;
                var key = pair[..eq].Trim();
                var value = pair[(eq + 1)..].Trim();

                switch (key)
                {
                    case "t": title = value; break;
                    case "b": body = value; break;
                    case "s": subtitle = value; break;
                    case "i": id = string.IsNullOrEmpty(value) ? null : value; break;
                    case "ts":
                        if (long.TryParse(value, out var epoch))
                            timestamp = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
                        break;
                }
            }

            if (title != null || body != null)
            {
                NotificationReceived?.Invoke(
                    title ?? "Terminal",
                    subtitle,
                    body ?? title ?? "",
                    id,
                    timestamp);
            }
        }
        else
        {
            // Simpler format: OSC 99 ; body
            NotificationReceived?.Invoke("Terminal", null, payload, null, null);
        }
    }

    /// <summary>
    /// OSC 777: notify;title;body format.
    /// Used by rxvt-unicode and adopted by other terminals.
    /// </summary>
    private void HandleOsc777(string payload)
    {
        var parts = payload.Split(';', 3);
        if (parts.Length >= 1 && parts[0] == "notify")
        {
            string title = parts.Length >= 2 ? parts[1] : "Terminal";
            string body = parts.Length >= 3 ? parts[2] : "";
            NotificationReceived?.Invoke(title, null, body, null, null);
        }
        else
        {
            NotificationReceived?.Invoke("Terminal", null, payload, null, null);
        }
    }
}
