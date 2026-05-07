using System.IO;

namespace Cmux.Core.Services;

/// <summary>
/// One Host block parsed from ~/.ssh/config.
/// </summary>
public class SshConfigEntry
{
    public string Alias { get; set; } = "";
    public string? HostName { get; set; }
    public int? Port { get; set; }
    public string? User { get; set; }
    public string? IdentityFile { get; set; }

    /// <summary>
    /// True when the host block declared a ProxyJump or ProxyCommand.
    /// Informational only - the OpenSSH client handles both directives
    /// natively when sftp.exe is used as the transport.
    /// </summary>
    public bool RequiresProxy { get; set; }
}

/// <summary>
/// Minimal parser for the user's OpenSSH client config file.
/// Supports Host alias blocks plus HostName / Port / User / IdentityFile keys.
/// Match, Include and other directives are ignored.
/// </summary>
public static class SshConfigParser
{
    public static IReadOnlyList<SshConfigEntry> ParseDefaultConfig()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ssh",
            "config");

        if (!File.Exists(path))
            return Array.Empty<SshConfigEntry>();

        try
        {
            return Parse(File.ReadAllLines(path));
        }
        catch
        {
            return Array.Empty<SshConfigEntry>();
        }
    }

    public static IReadOnlyList<SshConfigEntry> Parse(IEnumerable<string> lines)
    {
        var entries = new List<SshConfigEntry>();
        SshConfigEntry? current = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#')) continue;

            // Strip inline comments.
            int hashIdx = line.IndexOf('#');
            if (hashIdx >= 0) line = line[..hashIdx].Trim();
            if (line.Length == 0) continue;

            // Split on '=' or whitespace; key + remaining value.
            string key, value;
            int eqIdx = line.IndexOf('=');
            int wsIdx = FindFirstWhitespace(line);
            int splitIdx;
            if (eqIdx >= 0 && (wsIdx < 0 || eqIdx < wsIdx))
                splitIdx = eqIdx;
            else
                splitIdx = wsIdx;

            if (splitIdx < 0) continue;

            key = line[..splitIdx].Trim();
            value = line[(splitIdx + 1)..].Trim().Trim('"');

            if (string.Equals(key, "Host", StringComparison.OrdinalIgnoreCase))
            {
                // First alias only; ignore wildcards.
                var firstAlias = value.Split(' ', '\t')
                    .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a) && !a.Contains('*') && !a.Contains('?'));
                if (string.IsNullOrWhiteSpace(firstAlias))
                {
                    current = null;
                    continue;
                }
                current = new SshConfigEntry { Alias = firstAlias };
                entries.Add(current);
            }
            else if (string.Equals(key, "Match", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(key, "Include", StringComparison.OrdinalIgnoreCase))
            {
                current = null;
            }
            else if (current != null)
            {
                if (string.Equals(key, "HostName", StringComparison.OrdinalIgnoreCase))
                    current.HostName = value;
                else if (string.Equals(key, "Port", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var p))
                    current.Port = p;
                else if (string.Equals(key, "User", StringComparison.OrdinalIgnoreCase))
                    current.User = value;
                else if (string.Equals(key, "IdentityFile", StringComparison.OrdinalIgnoreCase))
                    current.IdentityFile = ExpandPath(value);
                else if (string.Equals(key, "ProxyJump", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(key, "ProxyCommand", StringComparison.OrdinalIgnoreCase))
                    current.RequiresProxy = true;
            }
        }

        return entries;
    }

    private static int FindFirstWhitespace(string s)
    {
        for (int i = 0; i < s.Length; i++)
            if (char.IsWhiteSpace(s[i])) return i;
        return -1;
    }

    private static string ExpandPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.StartsWith("~/", StringComparison.Ordinal) || path == "~")
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return path == "~" ? home : Path.Combine(home, path[2..]);
        }
        return path;
    }
}
