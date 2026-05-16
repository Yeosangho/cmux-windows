namespace Cmux.Core.Services;

public enum AgentType
{
    None, ClaudeCode, Codex, Aider, GithubCopilot, Cursor, Cline, Windsurf
}

public static class AgentDetector
{
    private static readonly (string pattern, AgentType type)[] Patterns =
    [
        ("claude", AgentType.ClaudeCode),
        ("codex", AgentType.Codex),
        ("aider", AgentType.Aider),
        ("copilot", AgentType.GithubCopilot),
        ("cursor", AgentType.Cursor),
        ("cline", AgentType.Cline),
        ("windsurf", AgentType.Windsurf),
    ];

    public static AgentType DetectFromProcessId(int shellPid)
    {
        try
        {
            var names = GetChildProcessNames(shellPid);
            foreach (var name in names)
                foreach (var (pattern, type) in Patterns)
                    if (name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        return type;
        }
        catch { }
        return AgentType.None;
    }

    public static string GetLabel(AgentType t) => t switch
    {
        AgentType.ClaudeCode => "Claude Code",
        AgentType.Codex => "Codex",
        AgentType.Aider => "Aider",
        AgentType.GithubCopilot => "Copilot",
        AgentType.Cursor => "Cursor",
        AgentType.Cline => "Cline",
        AgentType.Windsurf => "Windsurf",
        _ => "",
    };

    public static string GetIcon(AgentType t) => t switch
    {
        AgentType.ClaudeCode => "\uE99A",
        AgentType.Codex => "\uE943",
        AgentType.Aider => "\uE8D4",
        AgentType.GithubCopilot => "\uE774",
        AgentType.Cursor => "\uE7C8",
        AgentType.Cline => "\uE8D4",
        AgentType.Windsurf => "\uE774",
        _ => "",
    };

    private static List<string> GetChildProcessNames(int parentPid)
    {
        var names = new List<string>();
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT Name FROM Win32_Process WHERE ParentProcessId = {parentPid}");
            foreach (var obj in searcher.Get())
                names.Add(obj["Name"]?.ToString() ?? "");
        }
        catch { }
        return names;
    }

    /// <summary>
    /// Coarse classification of what kind of agent session is running inside
    /// a pane's shell. Used by the broadcast bar to limit a Send to either
    /// ssh-tunnelled Claude sessions or local Windows Claude sessions.
    /// </summary>
    public enum PaneAgentKind
    {
        Unknown = 0,
        /// <summary>Claude running directly in the pane's process tree on
        /// Windows (typically launched as `claude.cmd` → `node.exe`).</summary>
        LocalClaude,
        /// <summary>The pane has an `ssh.exe` descendant. Claude is assumed
        /// to be running on the remote side — we can't see remote processes
        /// from here, so ssh presence is the signal.</summary>
        SshSession,
    }

    /// <summary>
    /// Walks the descendant process tree of <paramref name="shellPid"/> and
    /// returns the first match: SshSession if any descendant is ssh.exe,
    /// LocalClaude if any descendant's name or command line contains
    /// "claude". WMI calls are expensive so the walk is depth-limited.
    /// </summary>
    public static PaneAgentKind ClassifyPane(int shellPid)
        => ClassifyPane(shellPid, bufferSnapshot: null, announcedAgent: null);

    public static PaneAgentKind ClassifyPane(int shellPid, string? bufferSnapshot)
        => ClassifyPane(shellPid, bufferSnapshot, announcedAgent: null);

    /// <summary>
    /// Decides what kind of agent is running in a pane using three signal
    /// channels, in descending order of confidence:
    ///
    ///   1. <paramref name="announcedAgent"/> — the agent's own self-announce
    ///      via OSC 1338 (e.g. Claude Code SessionStart hook). When this is
    ///      non-null we treat it as ground truth and only consult the
    ///      process tree to disambiguate LocalClaude vs SshSession (Claude
    ///      running through SSH).
    ///
    ///   2. process-tree walk — explicit signals only: a descendant named
    ///      <c>claude.cmd/exe</c>, or an <c>ssh.exe</c> whose command line
    ///      itself spells <c>claude</c>.
    ///
    ///   3. <paramref name="bufferSnapshot"/> Claude-TUI marker matching —
    ///      last-ditch fallback when ssh.exe is in the tree but we have
    ///      no explicit Claude evidence from (1) or the ssh command line.
    ///      Version-string-dependent, kept only for SSH targets that lack
    ///      the cmux-announce hook.
    /// </summary>
    public static PaneAgentKind ClassifyPane(int shellPid, string? bufferSnapshot, string? announcedAgent)
    {
        if (shellPid <= 0)
        {
            // Even without a PID we can honor a fresh announce (this happens
            // for daemon-backed panes after a reconnect when the local view
            // hasn't yet learned the daemon-side child PID).
            if (string.Equals(announcedAgent, "claude", StringComparison.OrdinalIgnoreCase))
                return PaneAgentKind.LocalClaude;
            return PaneAgentKind.Unknown;
        }

        try
        {
            var visited = new HashSet<int> { shellPid };
            var queue = new Queue<int>();
            queue.Enqueue(shellPid);

            // Cap to keep the WMI fan-out bounded if a pane somehow spawned
            // a runaway tree. Real shell trees are tiny (5-10 nodes).
            int budget = 64;
            bool sawSsh = false;
            bool sawClaudeProcess = false;
            string? sshCmdLine = null;

            while (queue.Count > 0 && budget-- > 0)
            {
                int pid = queue.Dequeue();
                using var searcher = new System.Management.ManagementObjectSearcher(
                    $"SELECT ProcessId, Name, CommandLine FROM Win32_Process WHERE ParentProcessId = {pid}");

                foreach (var obj in searcher.Get())
                {
                    int childPid;
                    try { childPid = Convert.ToInt32(obj["ProcessId"]); }
                    catch { continue; }

                    var name = obj["Name"]?.ToString() ?? string.Empty;
                    var cmd = obj["CommandLine"]?.ToString() ?? string.Empty;

                    if (name.Contains("claude", StringComparison.OrdinalIgnoreCase) ||
                        cmd.Contains("claude", StringComparison.OrdinalIgnoreCase))
                        sawClaudeProcess = true;

                    if (name.Equals("ssh.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        sawSsh = true;
                        sshCmdLine = cmd;
                    }

                    if (visited.Add(childPid))
                        queue.Enqueue(childPid);
                }
            }

            // Signal 1 (highest): explicit agent announce via OSC 1338.
            // ssh.exe presence in the same tree → it's Claude running through
            // SSH (remote claude → PTS → OSC 1338 traversed SSH stream → us).
            // Otherwise it's local Claude.
            if (string.Equals(announcedAgent, "claude", StringComparison.OrdinalIgnoreCase))
                return sawSsh ? PaneAgentKind.SshSession : PaneAgentKind.LocalClaude;

            // Signal 2: explicit process-tree evidence.
            if (sawClaudeProcess && !sawSsh)
                return PaneAgentKind.LocalClaude;

            if (sawSsh)
            {
                // ssh command line itself spells claude — explicit, no buffer
                // matching needed (`ssh host claude` style invocation).
                if (sshCmdLine != null &&
                    sshCmdLine.Contains("claude", StringComparison.OrdinalIgnoreCase))
                    return PaneAgentKind.SshSession;

                // claude.exe + ssh.exe coexist in the tree (rare but possible
                // when local claude is the parent and ssh is a transient git
                // helper). Prefer LocalClaude in that case.
                if (sawClaudeProcess)
                    return PaneAgentKind.LocalClaude;

                // Signal 3 (lowest confidence): buffer-text Claude-TUI marker
                // matching. Only used when none of the above signals fired —
                // SSH-tunnelled Claude where the remote has no cmux-announce
                // hook installed and didn't bake "claude" into the ssh
                // command line. Version-string-dependent, kept solely so
                // first-time remotes still get some classification.
                if (LooksLikeClaudeUI(bufferSnapshot))
                    return PaneAgentKind.SshSession;

                // Bare SSH — not a Claude target. Returning Unknown keeps it
                // out of ClaudeAll / ClaudeSsh broadcast scopes (v102 fix).
                return PaneAgentKind.Unknown;
            }
        }
        catch { }

        return PaneAgentKind.Unknown;
    }

    /// <summary>
    /// Heuristic: does the supplied buffer snapshot look like it's rendering
    /// the Claude Code TUI? Used to verify SSH-tunnelled Claude sessions
    /// (we can't see remote processes, so we look at the local pixels).
    /// Combines very-specific marker strings with a (Claude keyword + box
    /// drawing) co-occurrence signal to keep false positives low against
    /// other TUI tools (lazygit, fzf, htop, etc.) that also use box chars.
    /// </summary>
    public static bool LooksLikeClaudeUI(string? snapshot)
    {
        if (string.IsNullOrEmpty(snapshot)) return false;

        // Definite Claude Code strings — any one is enough.
        if (snapshot.Contains("Welcome to Claude", StringComparison.OrdinalIgnoreCase)
            || snapshot.Contains("? for shortcuts", StringComparison.OrdinalIgnoreCase)
            || snapshot.Contains("(esc to interrupt", StringComparison.OrdinalIgnoreCase))
            return true;

        // Combined signal: the word "claude" present AND TUI input-box
        // border drawing chars present in the same snapshot. The boxes
        // alone are too weak (lazygit/fzf use them); the keyword alone
        // is too weak (could be one history line). Together they're a
        // reliable Claude-TUI-rendering tell.
        bool hasClaude = snapshot.Contains("claude", StringComparison.OrdinalIgnoreCase);
        bool hasBox = snapshot.Contains('╭') || snapshot.Contains('╰');
        return hasClaude && hasBox;
    }
}
