using Cmux.Core.Terminal;

namespace Cmux.Core.Models;

public class PaneStateSnapshot
{
    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;
    public string? WorkingDirectory { get; set; }
    public string? Shell { get; set; }
    public List<string> CommandHistory { get; set; } = [];
    public TerminalBufferSnapshot? BufferSnapshot { get; set; }

    /// <summary>
    /// First long-running session command the user typed in this pane —
    /// captured on first <c>ssh</c>/<c>mosh</c>/<c>claude</c>/<c>tmux</c>
    /// invocation. On the next launch (cmuxw / daemon / OS restart) the
    /// pane is respawned and this command is re-issued so the user lands
    /// back where they left off. Persisted via session.json.
    /// </summary>
    public string? AutoRestoreCommand { get; set; }

    /// <summary>
    /// Last observed remote (Unix-style) cwd while the user was inside
    /// the SSH session. Only populated for ssh-rooted panes; the local
    /// shell cwd lives in <see cref="WorkingDirectory"/>. Drives the
    /// two-stage soft-restore: after replaying the ssh command, cmuxw
    /// sends <c>cd '&lt;RemoteWorkingDirectory&gt;'</c> on the remote so
    /// the user lands in the same directory.
    /// </summary>
    public string? RemoteWorkingDirectory { get; set; }

    /// <summary>
    /// True if the user ran <c>claude</c> *inside* this pane's SSH
    /// session at any point. Set on first such command, never cleared.
    /// On restore, combined with <see cref="RemoteWorkingDirectory"/>
    /// this triggers <c>claude --continue</c> on the remote side after
    /// the ssh handshake completes.
    /// </summary>
    public bool ClaudeRunningInside { get; set; }

    /// <summary>
    /// Captured Claude Code session UUID (filename of the active
    /// .jsonl under <c>~/.claude/projects/&lt;encoded-cwd&gt;/</c>).
    /// Allows soft-restore to use <c>claude --resume &lt;uuid&gt;</c> for
    /// pinpoint conversation recovery — disambiguates the case where
    /// the user has multiple parallel claude sessions in the same
    /// directory (where bare <c>--continue</c> would always pick the
    /// most recent and may resume the wrong one).
    /// </summary>
    public string? ClaudeSessionUuid { get; set; }
}
