namespace Cmux.Core.Services;

/// <summary>
/// Pure cd / drive-switch parser. Extracted from
/// <c>SurfaceViewModel.TryUpdateCwdFromCdCommand</c> so the decision
/// logic — which is load-bearing for the "cd / drive 이동 유지" half of
/// the /goal — can be unit-tested without WPF or session-bookkeeping
/// dependencies.
///
/// Mirror semantics of the production code path 1:1. Pinned behaviour:
///   • `D:` / `d:` outside SSH → drive-root LocalCwd = "D:\".
///   • `cd &lt;abs windows&gt;` → absolute LocalCwd via Path.GetFullPath.
///   • `cd &lt;rel&gt;` → resolved against (priorLocal ?? sessionCwd
///     ?? UserProfile).
///   • Inside SSH: `cd /abs` resets remote cwd; `cd rel` accumulates
///     into priorRemote (relative path arithmetic with .. pops).
///   • `cd /work &amp;&amp; claude` reads only the first arg.
///   • Surrounding quotes stripped.
///   • Bare `cd` → no change (home is environment-dependent).
///   • `cd ~` / `cd ~/foo` → no change (remote home only resolvable
///     after a successful prompt parse; we don't fake it).
///   • Drive switch inside SSH → ignored (Unix has no drives).
/// </summary>
public static class CdCommandParser
{
    public sealed class ParseResult
    {
        /// <summary>New absolute Windows local cwd, or null if the
        /// command didn't change the local view.</summary>
        public string? LocalCwd { get; init; }

        /// <summary>New remote cwd (absolute Unix path or relative-to-home
        /// like <c>ysh/foo</c>), or null if the command didn't change
        /// the remote view.</summary>
        public string? RemoteCwd { get; init; }
    }

    public static ParseResult Parse(
        string command,
        bool insideSsh,
        string? priorLocalCwd,
        string? priorRemoteCwd,
        string? fallbackLocalBase)
    {
        var trimmed = (command ?? string.Empty).Trim();
        if (trimmed.Length == 0) return new ParseResult();

        // Drive switch: bare `D:`. Only meaningful on Windows shell —
        // ignored inside SSH (Unix has no drives).
        if (!insideSsh && trimmed.Length == 2
            && char.IsLetter(trimmed[0]) && trimmed[1] == ':')
        {
            return new ParseResult { LocalCwd = char.ToUpperInvariant(trimmed[0]) + ":\\" };
        }

        if (!trimmed.StartsWith("cd ", StringComparison.OrdinalIgnoreCase)
            && !trimmed.Equals("cd", StringComparison.OrdinalIgnoreCase))
            return new ParseResult();
        if (trimmed.Equals("cd", StringComparison.OrdinalIgnoreCase))
            return new ParseResult();

        var arg = trimmed.Substring(3).Trim();

        // Trim shell chain / redirect tails so `cd /work && claude` is
        // read as cd /work, not cd "/work && claude".
        foreach (var sep in new[] { "&&", "||", ";", "|", ">", "<" })
        {
            var idx = arg.IndexOf(sep, StringComparison.Ordinal);
            if (idx > 0)
            {
                arg = arg[..idx].TrimEnd();
                break;
            }
        }

        // Strip a single pair of surrounding quotes (single or double).
        if (arg.Length >= 2
            && ((arg[0] == '"' && arg[^1] == '"')
                || (arg[0] == '\'' && arg[^1] == '\'')))
            arg = arg[1..^1].Trim();
        if (string.IsNullOrEmpty(arg)) return new ParseResult();

        // Remote home (~) — refuse to fake-resolve since we don't know
        // the user's home on the remote.
        if (arg.StartsWith('~')) return new ParseResult();

        if (insideSsh)
        {
            return new ParseResult
            {
                RemoteCwd = CombineUnixPath(priorRemoteCwd ?? "", arg),
            };
        }

        // Local cd. Absolute Windows path: `X:\...` or `X:/...`
        bool isWindowsAbs = arg.Length >= 3 && char.IsLetter(arg[0])
                            && arg[1] == ':' && (arg[2] == '\\' || arg[2] == '/');

        string? resolved;
        if (isWindowsAbs)
        {
            try { resolved = System.IO.Path.GetFullPath(arg); }
            catch { return new ParseResult(); }
        }
        else
        {
            var basePath = priorLocalCwd
                ?? fallbackLocalBase
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            try
            {
                resolved = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(basePath, arg));
            }
            catch { return new ParseResult(); }
        }

        return string.IsNullOrEmpty(resolved)
            ? new ParseResult()
            : new ParseResult { LocalCwd = resolved };
    }

    /// <summary>
    /// Combines a current Unix-style cwd with a relative or absolute cd
    /// arg. Used inside SSH where cmuxw has no kernel-level cwd read.
    /// </summary>
    public static string CombineUnixPath(string current, string arg)
    {
        if (arg.StartsWith('/'))
            return NormalizeUnixPath(arg);

        var combined = string.IsNullOrEmpty(current)
            ? arg
            : current.TrimEnd('/') + "/" + arg;
        bool isAbsolute = combined.StartsWith('/');

        var stack = new List<string>();
        foreach (var seg in combined.Split('/'))
        {
            if (string.IsNullOrEmpty(seg) || seg == ".") continue;
            if (seg == "..")
            {
                if (stack.Count > 0 && stack[^1] != "..")
                    stack.RemoveAt(stack.Count - 1);
                else if (!isAbsolute)
                    stack.Add("..");
            }
            else
            {
                stack.Add(seg);
            }
        }

        var joined = string.Join('/', stack);
        return isAbsolute ? "/" + joined : joined;
    }

    private static string NormalizeUnixPath(string path)
    {
        var stack = new List<string>();
        foreach (var seg in path.Split('/'))
        {
            if (string.IsNullOrEmpty(seg) || seg == ".") continue;
            if (seg == "..")
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
            }
            else
            {
                stack.Add(seg);
            }
        }
        return "/" + string.Join('/', stack);
    }
}
