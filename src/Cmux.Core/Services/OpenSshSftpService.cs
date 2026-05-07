using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Cmux.Core.Models;

namespace Cmux.Core.Services;

public record RemoteEntry(string Name, string FullPath, bool IsDirectory, long Size, DateTime LastModified);

/// <summary>
/// Drives the bundled Windows OpenSSH client (sftp.exe) as a transport for the
/// Editor view. Each call spawns a short-lived sftp.exe in batch mode (-b -)
/// so that ~/.ssh/config is honoured for ProxyJump, ProxyCommand, Match,
/// Include, IdentityAgent, etc. Authentication must be non-interactive
/// (ssh-agent / Pageant / IdentityFile); password prompts will fail.
/// </summary>
public class OpenSshSftpService : IDisposable
{
    private readonly Func<string, string?> _passwordResolver;

    // Matches the leading mode token of a POSIX `ls -la` line, e.g. "drwxr-xr-x".
    // Ten chars: type + 9 perm bits, with optional trailing extended-attr marker.
    private static readonly Regex _modeRegex = new(
        @"^[\-dlbcps][rwx\-tTsS]{9}[\+\.@]?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public OpenSshSftpService(Func<string, string?> passwordResolver)
    {
        // Resolver is kept for backwards compatibility with the previous
        // SSH.NET-based service. sftp.exe in BatchMode cannot accept passwords
        // from us, so the resolver is intentionally not consulted.
        _passwordResolver = passwordResolver;
    }

    public IReadOnlyList<RemoteEntry> ListDirectory(EditorFolder folder, string remotePath)
    {
        if (folder.Kind != EditorFolderKind.RemoteSsh)
            throw new InvalidOperationException("Folder is not a remote SSH folder.");

        var dir = NormalizeRemoteDir(remotePath);
        var batch = $"cd \"{EscapeQuotes(dir)}\"\nls -la\n";
        var (stdout, stderr, exit) = RunSftp(folder, batch);
        if (exit != 0)
            throw new InvalidOperationException(BuildSftpError(folder, stderr, exit));

        var entries = ParseLsLa(stdout, dir);
        return entries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string ReadAllText(EditorFolder folder, string remotePath)
    {
        if (folder.Kind != EditorFolderKind.RemoteSsh)
            throw new InvalidOperationException("Folder is not a remote SSH folder.");

        var temp = Path.Combine(Path.GetTempPath(), "cmux-sftp-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var batch = $"get -P \"{EscapeQuotes(remotePath)}\" \"{EscapeQuotes(temp)}\"\n";
            var (_, stderr, exit) = RunSftp(folder, batch);
            if (exit != 0 || !File.Exists(temp))
                throw new InvalidOperationException(BuildSftpError(folder, stderr, exit));

            return File.ReadAllText(temp);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    public void WriteAllText(EditorFolder folder, string remotePath, string contents)
    {
        if (folder.Kind != EditorFolderKind.RemoteSsh)
            throw new InvalidOperationException("Folder is not a remote SSH folder.");

        var temp = Path.Combine(Path.GetTempPath(), "cmux-sftp-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temp, contents);
            var batch = $"put -P \"{EscapeQuotes(temp)}\" \"{EscapeQuotes(remotePath)}\"\n";
            var (_, stderr, exit) = RunSftp(folder, batch);
            if (exit != 0)
                throw new InvalidOperationException(BuildSftpError(folder, stderr, exit));
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    /// <summary>No-op. Each call spawns its own sftp.exe; nothing to disconnect.</summary>
    public void Disconnect(string folderId) { _ = folderId; }

    public void Dispose() { GC.SuppressFinalize(this); }

    // ----- internals -----

    private static string ResolveSftpTarget(EditorFolder folder)
    {
        if (string.IsNullOrWhiteSpace(folder.Host))
            throw new InvalidOperationException("Remote folder is missing host.");

        if (folder.UseSshConfig || string.IsNullOrWhiteSpace(folder.Username))
            return folder.Host!;

        return $"{folder.Username}@{folder.Host}";
    }

    private (string Stdout, string Stderr, int ExitCode) RunSftp(EditorFolder folder, string stdinScript)
    {
        var target = ResolveSftpTarget(folder);

        // Auth strategy: any saved secret (password OR key passphrase) flips on
        // SSH_ASKPASS. We don't lock the auth method, so OpenSSH tries the
        // ssh-agent / IdentityFile first; if that needs a passphrase, ASKPASS
        // answers it. If the host wants password auth, ASKPASS answers that
        // too. No saved secret -> BatchMode=yes (agent / unencrypted key only).
        var password = _passwordResolver($"editor-folder:{folder.Id}:password");
        bool useAskpassPassword = !string.IsNullOrEmpty(password);

        var psi = new ProcessStartInfo
        {
            FileName = "sftp.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-b");
        psi.ArgumentList.Add("-");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(useAskpassPassword ? "BatchMode=no" : "BatchMode=yes");
        // ControlMaster connection multiplexing was tried here to amortize the
        // SSH handshake across calls, but Microsoft's port of OpenSSH does not
        // honour ControlMaster on Windows — it fails with "get socket name
        // failed / Not a socket" because the named-pipe ControlPath isn't a
        // real Unix socket. Removed; users who want multiplexing can set
        // ControlMaster in their own ssh_config (works on WSL/cygwin builds
        // of OpenSSH but not the in-box Microsoft one).
        if (useAskpassPassword)
        {
            // One prompt is enough for either a key passphrase OR a password.
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("NumberOfPasswordPrompts=1");
        }
        // Manual entry private key, if present.
        if (!folder.UseSshConfig
            && !folder.UsePasswordAuth
            && !string.IsNullOrWhiteSpace(folder.PrivateKeyPath)
            && File.Exists(folder.PrivateKeyPath))
        {
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("IdentitiesOnly=yes");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(folder.PrivateKeyPath!);
        }
        // Honour an explicit non-default port from manual-entry folders.
        if (!folder.UseSshConfig && folder.Port != 22 && folder.Port > 0)
        {
            psi.ArgumentList.Add("-P");
            psi.ArgumentList.Add(folder.Port.ToString(CultureInfo.InvariantCulture));
        }
        psi.ArgumentList.Add(target);

        if (useAskpassPassword)
        {
            // SSH_ASKPASS_REQUIRE=force makes OpenSSH 8.4+ run the helper even
            // without a controlling tty (Windows GUI process spawning ssh.exe
            // is exactly that). The helper just echoes whatever's in
            // CMUX_SFTP_PW back on stdout, which OpenSSH consumes as the
            // password reply.
            psi.Environment["SSH_ASKPASS"] = GetOrCreateAskpassHelper();
            psi.Environment["SSH_ASKPASS_REQUIRE"] = "force";
            psi.Environment["CMUX_SFTP_PW"] = password!;
            // Some OpenSSH builds also gate askpass on DISPLAY being set.
            if (!psi.Environment.ContainsKey("DISPLAY"))
                psi.Environment["DISPLAY"] = "cmux:0";
        }

        Process proc;
        try
        {
            proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start sftp.exe.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException(
                "OpenSSH client (sftp.exe) not found on PATH. " +
                "Install Windows OpenSSH Client (Settings -> Apps -> Optional features) " +
                "or add it to PATH.", ex);
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            proc.StandardInput.Write(stdinScript);
            proc.StandardInput.Flush();
            proc.StandardInput.Close();
        }
        catch
        {
            // sftp.exe may have already exited (e.g. auth failure); ignore.
        }

        // Bound the wait so a stalled sftp.exe does not hang the UI thread's
        // background task forever. 60 seconds is generous for typical edits.
        if (!proc.WaitForExit(60_000))
        {
            try { proc.Kill(true); } catch { }
            throw new InvalidOperationException(
                $"sftp.exe for {target} did not finish within 60 seconds and was terminated.");
        }
        // Drain async readers.
        proc.WaitForExit();

        return (stdout.ToString(), stderr.ToString(), proc.ExitCode);
    }

    private static string BuildSftpError(EditorFolder folder, string stderr, int exitCode)
    {
        var target = ResolveSftpTarget(folder);
        var trimmed = (stderr ?? "").Trim();
        var hint = (folder.UseSshConfig || !folder.UsePasswordAuth)
            ? "Auth must be non-interactive: ssh-agent / Pageant / IdentityFile via ssh_config."
            : "Password auth uses SSH_ASKPASS — verify the password and that the host accepts password auth.";
        return $"sftp.exe failed for {target} (exit code {exitCode}): {trimmed}. {hint}";
    }

    private static string? _askpassHelper;
    private static readonly object _askpassLock = new();

    /// <summary>
    /// Writes (once per process) a tiny .cmd script that echoes the
    /// CMUX_SFTP_PW environment variable to stdout. OpenSSH spawns this script
    /// via SSH_ASKPASS when it needs a password and reads its stdout.
    /// </summary>
    private static string GetOrCreateAskpassHelper()
    {
        if (_askpassHelper != null && File.Exists(_askpassHelper)) return _askpassHelper;
        lock (_askpassLock)
        {
            if (_askpassHelper != null && File.Exists(_askpassHelper)) return _askpassHelper;
            var path = Path.Combine(Path.GetTempPath(), "cmux-askpass.cmd");
            File.WriteAllText(path, "@echo off\r\necho %CMUX_SFTP_PW%\r\n");
            _askpassHelper = path;
            return path;
        }
    }

    private static string EscapeQuotes(string s) => s.Replace("\"", "\\\"");

    private static string NormalizeRemoteDir(string remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath)) return ".";
        return remotePath;
    }

    /// <summary>
    /// Parses the stdout of `ls -la` issued inside an sftp session. sftp emits
    /// the prompt and echoed commands too; we filter those out by only
    /// accepting lines whose first token matches the POSIX mode pattern.
    /// </summary>
    private static List<RemoteEntry> ParseLsLa(string stdout, string baseDir)
    {
        var entries = new List<RemoteEntry>();
        if (string.IsNullOrWhiteSpace(stdout)) return entries;

        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').TrimEnd();
            if (line.Length == 0) continue;

            // Tokenize on runs of whitespace; require at least 9 tokens before
            // the name (mode, links, owner, group, size, month, day, time/year, name...).
            var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 9) continue;
            if (!_modeRegex.IsMatch(tokens[0])) continue;

            var mode = tokens[0];
            if (!long.TryParse(tokens[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
                continue;

            // Reconstruct the trailing name segment from the original line,
            // because file names may legitimately contain runs of spaces.
            var name = ExtractName(line, tokens);
            if (string.IsNullOrEmpty(name)) continue;

            // Symbolic links: "linkname -> target".
            if (mode[0] == 'l')
            {
                int arrow = name.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow > 0) name = name[..arrow];
            }

            if (name == "." || name == "..") continue;

            bool isDir = mode[0] == 'd';
            var lastModified = TryParseTimestamp(tokens[5], tokens[6], tokens[7]);
            var fullPath = JoinRemotePath(baseDir, name);
            entries.Add(new RemoteEntry(name, fullPath, isDir, size, lastModified));
        }

        return entries;
    }

    /// <summary>
    /// Returns the substring of <paramref name="line"/> that begins with the 9th
    /// whitespace-separated token. Preserves embedded spaces inside the name.
    /// </summary>
    private static string ExtractName(string line, string[] tokens)
    {
        int idx = 0;
        int tokenIdx = 0;
        while (idx < line.Length && tokenIdx < 8)
        {
            // Skip whitespace.
            while (idx < line.Length && char.IsWhiteSpace(line[idx])) idx++;
            // Skip token characters.
            while (idx < line.Length && !char.IsWhiteSpace(line[idx])) idx++;
            tokenIdx++;
        }
        // Skip trailing whitespace before the name.
        while (idx < line.Length && char.IsWhiteSpace(line[idx])) idx++;
        if (idx >= line.Length)
        {
            // Fall back to the last token if structural parsing fails.
            return tokens[^1];
        }
        return line[idx..];
    }

    private static DateTime TryParseTimestamp(string month, string day, string timeOrYear)
    {
        // POSIX `ls -la` uses either "MMM d HH:mm" (recent files) or "MMM d  yyyy" (older).
        // Best-effort parse against invariant culture; fall back to MinValue.
        var combined = $"{month} {day} {timeOrYear}";
        var formats = new[]
        {
            "MMM d HH:mm",
            "MMM d  HH:mm",
            "MMM dd HH:mm",
            "MMM d yyyy",
            "MMM d  yyyy",
            "MMM dd yyyy",
        };
        if (DateTime.TryParseExact(combined, formats,
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
            return dt;
        return DateTime.MinValue;
    }

    private static string JoinRemotePath(string baseDir, string name)
    {
        if (string.IsNullOrEmpty(baseDir) || baseDir == ".") return name;
        if (baseDir.EndsWith('/')) return baseDir + name;
        return baseDir + "/" + name;
    }
}
