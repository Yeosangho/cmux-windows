namespace Cmux.Core.Services;

/// <summary>
/// Pure decision logic for transforming a saved <c>AutoRestoreCommand</c>
/// right before it's re-issued on session restart. Extracted from
/// SurfaceViewModel so the logic can be unit-tested without the WPF /
/// SettingsService dependency stack — every branch of the
/// claude-resume / --continue rewrite path is load-bearing for the
/// "Claude session 이어서 할 수 있게" half of our /goal.
///
/// The production code path in SurfaceViewModel.TransformAutoRestoreCommand
/// reduces to a single call into <see cref="Transform"/>. Behavioral
/// contract:
///   • ssh / mosh / arbitrary commands → returned verbatim (trimmed).
///   • claude commands with <see cref="resumeClaude"/> = false → verbatim.
///   • claude with an explicit <c>--resume &lt;uuid&gt;</c> → verbatim
///     (user pinned a specific session; never clobber).
///   • claude (any form) + captured UUID → strip any existing
///     <c>--continue</c>/<c>-c</c>/standalone <c>--resume</c>, append
///     <c>--resume &lt;uuid&gt;</c>.
///   • claude (no captured UUID) with <c>--continue</c>/<c>-c</c>/
///     <c>--resume</c> already present → verbatim.
///   • bare claude (no flags, no UUID) → append <c>--continue</c>.
/// </summary>
public static class AutoRestoreCommandTransformer
{
    public static string Transform(string command, string? capturedClaudeUuid, bool resumeClaude)
    {
        var trimmed = (command ?? string.Empty).Trim();
        if (trimmed.Length == 0) return trimmed;

        var firstWord = trimmed.Split(' ', 2)[0].ToLowerInvariant();

        if (firstWord != "claude" || !resumeClaude)
            return trimmed;

        if (HasExplicitResumeUuid(trimmed))
            return trimmed;

        if (!string.IsNullOrEmpty(capturedClaudeUuid))
        {
            var cleaned = StripStandaloneResumeFlag(trimmed);
            return $"{cleaned} --resume {capturedClaudeUuid}";
        }

        bool hasContinue = trimmed.Contains("--continue")
            || System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"(\s|^)-c(\s|$)");
        bool hasResumeFlag = trimmed.Contains("--resume")
            || System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"(\s|^)-r(\s|$)");
        if (hasContinue || hasResumeFlag) return trimmed;

        return $"{trimmed} --continue";
    }

    public static bool HasExplicitResumeUuid(string command)
    {
        var parts = command.Split(' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            if (!parts[i].Equals("--resume", StringComparison.OrdinalIgnoreCase)
                && parts[i] != "-r")
                continue;
            if (i + 1 >= parts.Length) return false;
            return Guid.TryParseExact(parts[i + 1], "D", out _);
        }
        return false;
    }

    public static string StripStandaloneResumeFlag(string command)
    {
        var parts = command.Split(' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        for (int i = 0; i < parts.Count; i++)
        {
            var t = parts[i];
            if (t.Equals("--continue", StringComparison.OrdinalIgnoreCase) || t == "-c")
            {
                parts.RemoveAt(i--);
                continue;
            }
            if (t.Equals("--resume", StringComparison.OrdinalIgnoreCase) || t == "-r")
            {
                if (i + 1 < parts.Count && Guid.TryParseExact(parts[i + 1], "D", out _))
                    continue;
                parts.RemoveAt(i--);
                continue;
            }
        }
        return string.Join(' ', parts);
    }
}
