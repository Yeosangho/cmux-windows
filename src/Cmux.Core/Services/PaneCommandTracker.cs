namespace Cmux.Core.Services;

/// <summary>
/// Pure-function decision helpers for tracking pane state from typed
/// shell commands. Extracted from SurfaceViewModel so each rule
/// (ssh-enter / ssh-exit / claude-inside-ssh / auto-restore primary)
/// is independently unit-testable without WPF or session dependencies.
///
/// The actual mutating state (Dictionary&lt;paneId, ...&gt; in
/// SurfaceViewModel) stays in the view-model; these helpers tell *what
/// to change*, the caller applies it. That keeps the pipeline a single
/// place to inspect for "command X → state Y" while the side-effect
/// surface stays explicit at the caller.
/// </summary>
public static class PaneCommandTracker
{
    /// <summary>
    /// Decides whether a typed command toggles the pane's inside-SSH
    /// state. Mirrors <c>TrackSshState</c>: <c>ssh</c>/<c>mosh</c>
    /// enter, <c>exit</c>/<c>logout</c> leave, everything else neutral.
    /// </summary>
    public enum SshTransition { None, Enter, Leave }

    public static SshTransition ClassifySshTransition(string command)
    {
        var trimmed = (command ?? string.Empty).Trim();
        if (trimmed.Length == 0) return SshTransition.None;
        var firstWord = trimmed.Split(' ', 2)[0].ToLowerInvariant();
        return firstWord switch
        {
            "ssh" or "mosh" => SshTransition.Enter,
            "exit" or "logout" => SshTransition.Leave,
            _ => SshTransition.None,
        };
    }

    /// <summary>
    /// Decides what (if anything) to capture as the pane's
    /// <c>AutoRestoreCommand</c> on receiving <paramref name="command"/>.
    /// Mirrors <c>MaybeCaptureAutoRestoreCommand</c>: first-time-only
    /// capture of <c>ssh</c>/<c>mosh</c>/<c>claude</c>/<c>tmux</c>/<c>screen</c>
    /// invocations. Subsequent commands return <see cref="CaptureKind.None"/>
    /// unless they're a <c>claude</c> typed *inside* a pane whose primary
    /// was an ssh — that flags the pane as a "ssh + claude" workflow
    /// for stage-2 soft-restore.
    /// </summary>
    public enum CaptureKind
    {
        /// <summary>No capture action — leave existing state alone.</summary>
        None,
        /// <summary>First-time capture: set AutoRestoreCommand to the trimmed
        /// command verbatim.</summary>
        CaptureAsPrimary,
        /// <summary>Primary was ssh/mosh and the user now typed claude
        /// inside — flag the pane as needing stage-2 soft-restore.</summary>
        MarkClaudeInsideSsh,
    }

    public static CaptureKind ClassifyAutoRestoreCapture(
        string command,
        string? existingPrimary)
    {
        var trimmed = (command ?? string.Empty).Trim();
        if (trimmed.Length == 0) return CaptureKind.None;
        var firstWord = trimmed.Split(' ', 2)[0].ToLowerInvariant();

        if (existingPrimary == null)
        {
            return firstWord is "ssh" or "mosh" or "claude" or "tmux" or "screen"
                ? CaptureKind.CaptureAsPrimary
                : CaptureKind.None;
        }

        var primaryFirstWord = existingPrimary.Trim()
            .Split(' ', 2)[0].ToLowerInvariant();
        if ((primaryFirstWord is "ssh" or "mosh") && firstWord == "claude")
            return CaptureKind.MarkClaudeInsideSsh;

        return CaptureKind.None;
    }
}
