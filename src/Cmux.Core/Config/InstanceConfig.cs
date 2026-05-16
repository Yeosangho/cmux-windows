namespace Cmux.Core.Config;

/// <summary>
/// Reads the <c>CMUX_INSTANCE_ID</c> environment variable once on startup
/// to compute pipe names + state directory suffixes. Lets us spin up a
/// completely isolated test instance ("CMUX_INSTANCE_ID=test1 cmuxw.exe")
/// in parallel with the user's production cmuxw without touching its
/// pipes, daemon, or session.json.
///
/// Empty / unset → no suffix → identical behaviour to pre-isolation
/// builds (production default).
/// </summary>
public static class InstanceConfig
{
    /// <summary>The raw value of CMUX_INSTANCE_ID, or empty string.</summary>
    public static string InstanceId { get; } = SanitizeInstanceId(
        Environment.GetEnvironmentVariable("CMUX_INSTANCE_ID"));

    /// <summary>True when running as an isolated (test) instance.</summary>
    public static bool IsIsolated => InstanceId.Length > 0;

    /// <summary>
    /// Suffix to append to anything that needs an instance-local identity.
    /// Empty string for the production default, <c>-test1</c> etc. for
    /// isolated instances.
    /// </summary>
    public static string Suffix => InstanceId.Length == 0 ? string.Empty : "-" + InstanceId;

    /// <summary>
    /// Daemon named pipe name. Production: <c>cmux-daemon</c>.
    /// Isolated: <c>cmux-daemon-test1</c>.
    /// </summary>
    public static string DaemonPipeName => "cmux-daemon" + Suffix;

    /// <summary>
    /// cmuxw GUI's own named pipe name (used by CLI / toast forwarder).
    /// Production: <c>cmux</c>. Isolated: <c>cmux-test1</c>.
    /// </summary>
    public static string AppPipeName => "cmux" + Suffix;

    /// <summary>
    /// LocalAppData state directory name. Production: <c>cmux</c>.
    /// Isolated: <c>cmux-test1</c>.
    /// </summary>
    public static string StateDirName => "cmux" + Suffix;

    private static string SanitizeInstanceId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        // Pipe / path-safe chars only — letters, digits, dash, underscore.
        // Reject anything else outright so a stray space / slash can't
        // produce an unintended path or pipe.
        var trimmed = raw.Trim();
        foreach (var ch in trimmed)
        {
            bool ok = (ch >= 'a' && ch <= 'z')
                   || (ch >= 'A' && ch <= 'Z')
                   || (ch >= '0' && ch <= '9')
                   || ch == '-' || ch == '_';
            if (!ok) return string.Empty;
        }
        return trimmed.Length > 32 ? trimmed[..32] : trimmed;
    }
}
