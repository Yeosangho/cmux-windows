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
    {
        if (shellPid <= 0) return PaneAgentKind.Unknown;

        try
        {
            var visited = new HashSet<int> { shellPid };
            var queue = new Queue<int>();
            queue.Enqueue(shellPid);

            // Cap to keep the WMI fan-out bounded if a pane somehow spawned
            // a runaway tree. Real shell trees are tiny (5-10 nodes).
            int budget = 64;

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

                    if (name.Equals("ssh.exe", StringComparison.OrdinalIgnoreCase))
                        return PaneAgentKind.SshSession;

                    if (name.Contains("claude", StringComparison.OrdinalIgnoreCase) ||
                        cmd.Contains("claude", StringComparison.OrdinalIgnoreCase))
                        return PaneAgentKind.LocalClaude;

                    if (visited.Add(childPid))
                        queue.Enqueue(childPid);
                }
            }
        }
        catch { }

        return PaneAgentKind.Unknown;
    }
}
