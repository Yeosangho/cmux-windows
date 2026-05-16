using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cmux.Core.Config;
using Cmux.Core.IPC;
using Cmux.Core.Models;
using Cmux.Core.Services;
using Cmux.Core.Terminal;

namespace Cmux.ViewModels;

public partial class SurfaceViewModel : ObservableObject, IDisposable
{
    public Surface Surface { get; }
    private readonly string _workspaceId;
    private readonly NotificationService _notificationService;
    private readonly Dictionary<string, TerminalSession> _sessions = [];
    private readonly Dictionary<string, List<string>> _paneCommandHistory = [];
    private readonly Dictionary<string, string?> _paneShells = [];
    /// <summary>First long-running session command typed in each pane —
    /// captured once and replayed verbatim on session restore. Mirrored
    /// to/from <c>PaneStateSnapshot.AutoRestoreCommand</c>.</summary>
    private readonly Dictionary<string, string> _paneAutoRestoreCommand = [];
    /// <summary>
    /// Local (Windows-style) cwd per pane, tracked from typed `cd` /
    /// `D:` style commands. Decoupled from <c>session.WorkingDirectory</c>
    /// because the daemon's CWD_CHANGED stream resets that field on
    /// every session start (and on every OSC 7), clobbering our writes.
    /// Authoritative for snapshot persistence — overrides session.WorkingDirectory
    /// when present.
    /// </summary>
    private readonly Dictionary<string, string> _paneLocalCwd = [];
    /// <summary>Last observed remote cwd per pane. For shells that emit
    /// OSC 7, picked up via WorkingDirectoryChanged. For shells that
    /// don't, populated from typed `cd &lt;arg&gt;` commands while the
    /// pane is inside an ssh session — stored verbatim (relative or
    /// absolute) so the soft-restore second stage can replay the same
    /// argument and arrive at the same remote dir.</summary>
    private readonly Dictionary<string, string> _paneRemoteCwd = [];
    /// <summary>Panes currently inside an ssh/mosh session, as inferred
    /// from typed commands (ssh entry, exit/logout exit). Used to route
    /// cd commands to remote vs local cwd tracking.</summary>
    private readonly HashSet<string> _paneInsideSsh = [];
    /// <summary>Per-pane captured Claude session UUID (filename of the
    /// active .jsonl under ~/.claude/projects/...). Populated ~5s after
    /// the user types `claude` — local panes via filesystem walk, remote
    /// panes via an out-of-band ssh subprocess. Mirrored to/from
    /// PaneStateSnapshot.ClaudeSessionUuid; on restore drives
    /// `claude --resume &lt;uuid&gt;` instead of bare `--continue`.</summary>
    private readonly Dictionary<string, string> _paneClaudeUuid = [];
    /// <summary>Serializes UUID claim-assignment across concurrent
    /// <see cref="MaybeCaptureClaudeUuid"/> tasks so two panes that type
    /// `claude` in the same window can't both grab the same JSONL.
    /// Without this, the global "newest mtime" race assigns each pane
    /// the UUID of whichever JSONL was last touched at poll time —
    /// shifting the mapping by one across panes.</summary>
    private readonly object _claudeUuidClaimLock = new();
    /// <summary>Panes where the user has run `claude` *inside* an SSH
    /// session (i.e. after the AutoRestoreCommand was a bare ssh). Marks
    /// the pane as a "ssh + claude" workflow target for two-stage
    /// soft-restore.</summary>
    private readonly HashSet<string> _paneClaudeInsideSsh = [];
    private readonly HashSet<string> _daemonPanes = [];
    private readonly HashSet<string> _daemonOutputLogged = [];
    private static readonly object _daemonWaitLock = new();
    private static bool _daemonWaitDone;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private SplitNode _rootNode;

    [ObservableProperty]
    private string? _focusedPaneId;

    [ObservableProperty]
    private bool _isZoomed;

    public event Action<string>? WorkingDirectoryChanged;

    /// <summary>Gets the shell process PID from the focused pane session.</summary>
    public int? ShellPid
    {
        get
        {
            if (FocusedPaneId == null) return null;
            var session = GetSession(FocusedPaneId);
            return session?.ProcessId;
        }
    }

    public SurfaceViewModel(Surface surface, string workspaceId, NotificationService notificationService)
    {
        Surface = surface;
        _workspaceId = workspaceId;
        _notificationService = notificationService;
        _name = surface.Name;
        _rootNode = surface.RootSplitNode;
        _focusedPaneId = surface.FocusedPaneId;

        // Wire daemon events for session persistence
        var daemon = App.DaemonClient;
        daemon.RawOutputReceived += OnDaemonRawOutput;
        daemon.CwdChanged += OnDaemonCwdChanged;
        daemon.TitleChanged += OnDaemonTitleChanged;
        daemon.SessionExited += OnDaemonSessionExited;
        daemon.BellReceived += OnDaemonBellReceived;
        daemon.Disconnected += OnDaemonDisconnected;

        // Start terminal sessions for all leaf nodes
        foreach (var leaf in _rootNode.GetLeaves())
        {
            if (leaf.PaneId != null)
            {
                Surface.PaneSnapshots.TryGetValue(leaf.PaneId, out var snapshot);
                if (snapshot?.CommandHistory is { Count: > 0 })
                {
                    _paneCommandHistory[leaf.PaneId] = snapshot.CommandHistory
                        .Select(App.CommandLogService.SanitizeCommandForStorage)
                        .Where(c => !string.IsNullOrWhiteSpace(c))
                        .Cast<string>()
                        .ToList();
                }

                if (!string.IsNullOrWhiteSpace(snapshot?.AutoRestoreCommand))
                {
                    _paneAutoRestoreCommand[leaf.PaneId] = snapshot.AutoRestoreCommand!;
                    // Restore inside-ssh state for ssh-rooted panes so any
                    // user typing post-restore is routed to the remote cwd
                    // tracker, not the local one.
                    var firstWord = snapshot.AutoRestoreCommand!.Trim()
                        .Split(' ', 2)[0].ToLowerInvariant();
                    if (firstWord is "ssh" or "mosh")
                        _paneInsideSsh.Add(leaf.PaneId);
                }

                if (!string.IsNullOrWhiteSpace(snapshot?.RemoteWorkingDirectory))
                    _paneRemoteCwd[leaf.PaneId] = snapshot.RemoteWorkingDirectory!;

                if (!string.IsNullOrWhiteSpace(snapshot?.WorkingDirectory)
                    && !LooksLikeRemoteCwd(snapshot.WorkingDirectory))
                    _paneLocalCwd[leaf.PaneId] = snapshot.WorkingDirectory!;

                if (snapshot?.ClaudeRunningInside == true)
                    _paneClaudeInsideSsh.Add(leaf.PaneId);

                if (!string.IsNullOrWhiteSpace(snapshot?.ClaudeSessionUuid))
                    _paneClaudeUuid[leaf.PaneId] = snapshot.ClaudeSessionUuid!;

                // For ssh-rooted panes, snapshot.WorkingDirectory typically
                // holds the *remote* cwd (Unix-style) which is invalid as a
                // local Windows shell start dir — pass null so Start() falls
                // back to the user profile. Local-rooted panes pass through.
                var startCwd = LooksLikeRemoteCwd(snapshot?.WorkingDirectory)
                    ? null
                    : snapshot?.WorkingDirectory;

                StartSession(leaf.PaneId, startCwd, snapshot, snapshot?.Shell);
            }
        }

        if (_focusedPaneId == null)
        {
            var firstLeaf = _rootNode.GetLeaves().FirstOrDefault();
            if (firstLeaf?.PaneId != null)
                FocusedPaneId = firstLeaf.PaneId;
        }
    }

    private void OnDaemonRawOutput(string paneId, byte[] data)
    {
        if (!_daemonPanes.Contains(paneId)) return;
        if (_sessions.TryGetValue(paneId, out var session))
            session.FeedOutput(data);
    }

    private void OnDaemonCwdChanged(string paneId, string dir)
    {
        if (!_daemonPanes.Contains(paneId)) return;
        // Update the session's WorkingDirectory so it's captured in snapshots
        if (_sessions.TryGetValue(paneId, out var session))
            session.WorkingDirectory = dir;
        if (paneId == FocusedPaneId)
            WorkingDirectoryChanged?.Invoke(dir);
    }

    private void OnDaemonTitleChanged(string paneId, string title)
    {
        if (!_daemonPanes.Contains(paneId)) return;
    }

    private void OnDaemonSessionExited(string paneId, int exitCode)
    {
        if (!_daemonPanes.Contains(paneId)) return;
        _daemonPanes.Remove(paneId);
    }

    private void OnDaemonBellReceived(string paneId)
    {
        if (!_daemonPanes.Contains(paneId)) return;
    }

    private void OnDaemonDisconnected()
    {
        // Daemon died — fall back all daemon sessions to local ConPTY
        var paneIds = _daemonPanes.ToList();
        if (paneIds.Count == 0) return;

        DaemonLog($"[DaemonDisconnected] Falling back {paneIds.Count} sessions to local ConPTY");

        foreach (var paneId in paneIds)
        {
            if (!_sessions.TryGetValue(paneId, out var session)) continue;

            var cwd = session.WorkingDirectory;
            session.DaemonWrite = null;
            session.DaemonResize = null;

            try
            {
                session.Start(command: _paneShells.GetValueOrDefault(paneId) ?? GetConfiguredShell(), workingDirectory: cwd);
                DaemonLog($"[DaemonDisconnected] {paneId} → local session started");
            }
            catch (Exception ex)
            {
                DaemonLog($"[DaemonDisconnected] {paneId} → local start failed: {ex.Message}");
            }
        }

        _daemonPanes.Clear();
    }

    public TerminalSession? GetSession(string paneId)
    {
        return _sessions.GetValueOrDefault(paneId);
    }

    public string GetPaneTitle(string paneId, string? fallbackTitle)
    {
        if (Surface.PaneCustomNames.TryGetValue(paneId, out var custom) && !string.IsNullOrWhiteSpace(custom))
            return custom;

        return fallbackTitle ?? "Terminal";
    }

    public void SetPaneCustomName(string paneId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            Surface.PaneCustomNames.Remove(paneId);
        else
            Surface.PaneCustomNames[paneId] = name.Trim();

        OnPropertyChanged(nameof(RootNode));
    }

    public IReadOnlyList<string> GetCommandHistory(string paneId)
    {
        return _paneCommandHistory.TryGetValue(paneId, out var history)
            ? history.AsReadOnly()
            : [];
    }

    private static bool ShouldCaptureTranscript(string reason)
    {
        var settings = SettingsService.Current;

        if (string.Equals(reason, "clear-terminal", StringComparison.OrdinalIgnoreCase))
            return settings.CaptureTranscriptsOnClear;

        return settings.CaptureTranscriptsOnClose;
    }

    public string? CapturePaneTranscript(string paneId, string reason)
    {
        if (!ShouldCaptureTranscript(reason))
            return null;

        if (!_sessions.TryGetValue(paneId, out var session))
            return null;

        var text = session.Buffer.ExportPlainText(maxScrollbackLines: 20000);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return App.CommandLogService.SaveTerminalTranscript(
            _workspaceId,
            Surface.Id,
            paneId,
            session.WorkingDirectory,
            text,
            reason);
    }

    public int CaptureAllPaneTranscripts(string reason)
    {
        if (!ShouldCaptureTranscript(reason))
            return 0;

        int captured = 0;

        var paneIds = RootNode.GetLeaves()
            .Select(l => l.PaneId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var paneId in paneIds)
        {
            if (CapturePaneTranscript(paneId, reason) != null)
                captured++;
        }

        return captured;
    }

    public void CapturePaneSnapshotsForPersistence()
    {
        var activePaneIds = RootNode.GetLeaves()
            .Select(l => l.PaneId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToHashSet();

        foreach (var paneId in activePaneIds)
        {
            if (!_sessions.TryGetValue(paneId, out var session))
                continue;

            var state = Surface.PaneSnapshots.TryGetValue(paneId, out var existing)
                ? existing
                : new PaneStateSnapshot();

            state.CapturedAt = DateTime.UtcNow;
            // Local cwd: prefer our cd-parsed value (authoritative when
            // populated). Fall back to session.WorkingDirectory (which is
            // valid when OSC 7 / daemon CWD events fire — modern bash with
            // VTE integration, daemon-side ConPTY start dir, etc.).
            state.WorkingDirectory = _paneLocalCwd.TryGetValue(paneId, out var localCwd)
                ? localCwd
                : session.WorkingDirectory;
            state.Shell = _paneShells.GetValueOrDefault(paneId);
            state.BufferSnapshot = session.CreateBufferSnapshot(maxScrollbackLines: 3000);

            if (_paneCommandHistory.TryGetValue(paneId, out var history))
                state.CommandHistory = history.TakeLast(500).ToList();

            if (_paneAutoRestoreCommand.TryGetValue(paneId, out var restoreCmd))
                state.AutoRestoreCommand = restoreCmd;

            if (_paneRemoteCwd.TryGetValue(paneId, out var remoteCwd))
                state.RemoteWorkingDirectory = remoteCwd;

            state.ClaudeRunningInside = _paneClaudeInsideSsh.Contains(paneId);

            if (_paneClaudeUuid.TryGetValue(paneId, out var uuid))
                state.ClaudeSessionUuid = uuid;

            Surface.PaneSnapshots[paneId] = state;
        }

        var stalePaneIds = Surface.PaneSnapshots.Keys.Where(id => !activePaneIds.Contains(id)).ToList();
        foreach (var paneId in stalePaneIds)
            Surface.PaneSnapshots.Remove(paneId);
    }

    public void RegisterCommandSubmission(string paneId, string command)
    {
        var sanitized = App.CommandLogService.SanitizeCommandForStorage(command);
        if (string.IsNullOrWhiteSpace(sanitized))
            return;

        AppendToCommandHistory(paneId, sanitized);
        TrackSshState(paneId, sanitized!);
        MaybeCaptureAutoRestoreCommand(paneId, sanitized!);
        MaybeCaptureClaudeUuid(paneId, sanitized!);

        // Cwd tracking strategy:
        //   1. typed-command parser (TryUpdateCwdFromCdCommand) — primary
        //      signal. Catches `cd X`, `D:`, `pushd`, `popd`, `chdir`
        //      immediately on Enter, regardless of shell prompt format,
        //      OSC 7 availability, or color escapes. Works for SSH cwd
        //      too (remote-side cd) via `_paneInsideSsh` toggle.
        //   2. SchedulePromptCwdRefresh (prompt-line regex parse) —
        //      secondary correction. Re-reads the rendered prompt line
        //      ~500ms post-Enter to recover from Tab completion /
        //      history recall / failed cd (no-such-directory) that the
        //      typed-command parser couldn't see. Only used when its
        //      result *agrees* with the typed value or when the typed
        //      parser had no opinion.
        // Earlier (pre-v107) the comment here claimed prompt-parse was
        // the *only* signal — that was the source of the "cd doesn't
        // update in my pane" complaint. Custom PS1, Starship/oh-my-posh,
        // and unprinted color escapes all break the regex; meanwhile the
        // typed-command path is deterministic and free.
        TryUpdateCwdFromCdCommand(paneId, sanitized!);
        PushCwdToSession(paneId);

        SchedulePromptCwdRefresh(paneId, delayMs: 500);
        var firstWord = sanitized!.Trim().Split(' ', 2)[0].ToLowerInvariant();
        if (firstWord is "ssh" or "mosh" or "telnet")
            SchedulePromptCwdRefresh(paneId, delayMs: 3000);

        var cwd = _sessions.TryGetValue(paneId, out var session)
            ? session.WorkingDirectory
            : null;

        App.CommandLogService.RecordManualCommandSubmission(
            paneId,
            _workspaceId,
            Surface.Id,
            sanitized,
            cwd);
    }

    public bool TryHandlePaneCommand(string paneId, string command)
    {
        if (!_sessions.TryGetValue(paneId, out var session))
            return false;

        return App.AgentRuntime.TryHandlePaneCommand(
            command,
            new Cmux.Services.AgentPaneContext
            {
                WorkspaceId = _workspaceId,
                SurfaceId = Surface.Id,
                PaneId = paneId,
                WorkingDirectory = session.WorkingDirectory,
                WriteToPane = text =>
                {
                    if (!string.IsNullOrEmpty(text))
                        session.Write(text);
                },
            });
    }

    /// <summary>
    /// First-time-only capture: the very first ssh / mosh / claude / tmux
    /// invocation a user types in a pane is what they'll want re-issued on
    /// next launch. Subsequent commands (cd, ls, git status …) are
    /// transient and shouldn't replace the saved primary intent.
    /// </summary>
    private void MaybeCaptureAutoRestoreCommand(string paneId, string command)
    {
        var existingPrimary = _paneAutoRestoreCommand.GetValueOrDefault(paneId);
        var kind = Cmux.Core.Services.PaneCommandTracker.ClassifyAutoRestoreCapture(
            command, existingPrimary);
        switch (kind)
        {
            case Cmux.Core.Services.PaneCommandTracker.CaptureKind.CaptureAsPrimary:
                _paneAutoRestoreCommand[paneId] = command.Trim();
                break;
            case Cmux.Core.Services.PaneCommandTracker.CaptureKind.MarkClaudeInsideSsh:
                _paneClaudeInsideSsh.Add(paneId);
                break;
        }
    }

    /// <summary>
    /// When the user types <c>claude</c> (without an explicit
    /// <c>--resume &lt;uuid&gt;</c>), schedule a delayed UUID capture so
    /// soft-restore can replay <c>claude --resume &lt;captured-uuid&gt;</c>
    /// instead of bare <c>--continue</c>. This disambiguates the case
    /// where multiple parallel claude sessions live in the same cwd.
    /// </summary>
    private void MaybeCaptureClaudeUuid(string paneId, string command)
    {
        var trimmed = command.Trim();
        var firstWord = trimmed.Split(' ', 2)[0].ToLowerInvariant();
        if (firstWord != "claude") return;
        // Explicit `--resume <uuid>` means the user already pinned a
        // specific session — trust them, no need to recapture. Bare
        // `--resume` (picker form) DOES still need capture: claude shows
        // a list, user picks one, the chosen session's .jsonl mtime
        // updates → we want that UUID for next restore.
        if (HasExplicitResumeUuid(trimmed)) return;

        bool insideSsh = _paneInsideSsh.Contains(paneId);
        var sshHost = insideSsh
            ? ExtractSshHost(_paneAutoRestoreCommand.GetValueOrDefault(paneId, ""))
            : null;
        // Snapshot the wall-clock so the file walk can filter to JSONLs
        // created after this point — guards against picking up another
        // pane's older session that just happens to be the newest mtime.
        var capturedAt = DateTime.UtcNow;

        _ = Task.Run(async () =>
        {
            // Snapshot existing jsonls BEFORE the polling loop so we can
            // diff against them later. Anything that's new (or whose
            // mtime advances) after this point is a candidate for THIS
            // pane's claude. Without this baseline, two panes typing
            // claude near-simultaneously both pick "the globally
            // newest" JSONL and end up cross-mapped.
            var beforeSnapshot = insideSsh
                ? await SnapshotClaudeJsonlsViaSshAsync(sshHost).ConfigureAwait(false)
                : SnapshotClaudeJsonlsLocal();

            string? uuid = null;
            for (int attempt = 0; attempt < 6 && uuid == null; attempt++)
            {
                await Task.Delay(2500).ConfigureAwait(false); // 2.5s … 15s
                try
                {
                    var after = insideSsh
                        ? await SnapshotClaudeJsonlsViaSshAsync(sshHost).ConfigureAwait(false)
                        : SnapshotClaudeJsonlsLocal();

                    // Claim under a lock so concurrent pane captures
                    // can't both pick the same UUID (and so we honor
                    // already-claimed UUIDs from peer panes).
                    lock (_claudeUuidClaimLock)
                    {
                        var claimed = _paneClaudeUuid.Values.ToHashSet();
                        var candidate = after
                            .Where(kv =>
                                !beforeSnapshot.TryGetValue(kv.Key, out var bm)
                                || kv.Value > bm)
                            .Where(kv => !claimed.Contains(kv.Key))
                            .OrderByDescending(kv => kv.Value)
                            .Select(kv => kv.Key)
                            .FirstOrDefault();

                        if (candidate != null)
                        {
                            uuid = candidate;
                            // Claim immediately under the same lock so a
                            // racing pane's task sees this UUID as taken.
                            _paneClaudeUuid[paneId] = uuid;
                        }
                    }
                }
                catch { /* retry on next tick */ }
            }
        });
    }

    /// <summary>
    /// Walks <c>%USERPROFILE%\.claude\projects\</c> for the most-recently
    /// created .jsonl file. Filters to files created after
    /// <paramref name="since"/> (= when the user typed `claude`) so we
    /// don't grab an older session's JSONL whose mtime happens to be
    /// fresher because of background interaction.
    /// </summary>
    /// <summary>
    /// Builds a uuid → mtime map of every Claude session JSONL on the
    /// local machine. Subagent jsonls (nested under <c>subagents/</c>)
    /// are excluded — those belong to internal helper agents, not the
    /// parent session we want to track.
    /// </summary>
    private static Dictionary<string, DateTime> SnapshotClaudeJsonlsLocal()
    {
        var map = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var root = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "projects");
            if (!System.IO.Directory.Exists(root)) return map;

            var sep = System.IO.Path.DirectorySeparatorChar;
            foreach (var path in System.IO.Directory.EnumerateFiles(
                root, "*.jsonl", System.IO.SearchOption.AllDirectories))
            {
                if (path.Contains($"{sep}subagents{sep}")) continue;
                try
                {
                    var fi = new System.IO.FileInfo(path);
                    var uuid = System.IO.Path.GetFileNameWithoutExtension(fi.Name);
                    map[uuid] = fi.LastWriteTimeUtc;
                }
                catch { }
            }
        }
        catch { }
        return map;
    }

    /// <summary>
    /// Out-of-band ssh subprocess that asks the remote machine for the
    /// most recently created .jsonl under <c>~/.claude/projects/</c>.
    /// Uses the same host string the user typed (key/agent auth required
    /// — interactive password prompts can't complete here). Stays out
    /// of the pane's primary ssh pipe (which is busy running claude).
    /// </summary>
    /// <summary>
    /// Out-of-band ssh subprocess listing every <c>~/.claude/projects/&lt;cwd&gt;/*.jsonl</c>
    /// on the remote with its mtime, returned as a uuid → mtime map. Used
    /// for snapshot-diff so two near-simultaneous remote claudes can't
    /// claim each other's UUID. Stays out of the pane's primary ssh
    /// pipe (which is busy running claude).
    /// </summary>
    private static async Task<Dictionary<string, DateTime>> SnapshotClaudeJsonlsViaSshAsync(string? sshHost)
    {
        var map = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(sshHost)) return map;

        // Portable form: list one-level-deep jsonls + their mtimes (epoch
        // seconds via stat). bash / zsh / sh / busybox compatible. Output
        // lines: "<epoch> <full-path>". Empty when no jsonls exist.
        var query = "for f in ~/.claude/projects/*/*.jsonl; do "
                  + "  [ -f \"$f\" ] && printf '%s %s\\n' \"$(stat -c %Y \"$f\" 2>/dev/null)\" \"$f\"; "
                  + "done 2>/dev/null";

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ssh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("BatchMode=yes");
        psi.ArgumentList.Add(sshHost);
        psi.ArgumentList.Add(query);

        try
        {
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return map;
            var stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await proc.WaitForExitAsync(
                new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token)
                .ConfigureAwait(false);

            foreach (var raw in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.TrimEnd('\r');
                var sp = line.IndexOf(' ');
                if (sp <= 0) continue;
                if (!long.TryParse(line[..sp], out var epoch)) continue;
                var path = line[(sp + 1)..].Trim();
                if (path.Contains("/subagents/")) continue;
                var slash = path.LastIndexOf('/');
                var baseName = slash >= 0 ? path[(slash + 1)..] : path;
                if (baseName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                    baseName = baseName[..^".jsonl".Length];
                if (string.IsNullOrEmpty(baseName)) continue;
                map[baseName] = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
            }
        }
        catch { }
        return map;
    }

    /// <summary>
    /// True if the claude command has <c>--resume</c> (or <c>-r</c>)
    /// followed by a UUID-shaped token. <c>claude --resume</c> alone
    /// (picker form) returns false — we still want to capture the
    /// eventually-selected session's UUID in that case.
    /// </summary>
    private static bool HasExplicitResumeUuid(string command)
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

    /// <summary>
    /// Removes any resume/continue flag that we'll be overriding with our
    /// captured UUID. Keeps explicit <c>--resume &lt;uuid&gt;</c> alone so
    /// user-pinned sessions aren't clobbered. Strips:
    ///   • standalone <c>--resume</c> / <c>-r</c> (picker form)
    ///   • <c>--continue</c> / <c>-c</c> (most-recent shortcut)
    /// </summary>
    private static string StripStandaloneResumeFlag(string command)
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
                // Explicit UUID after — leave it (caller path won't reach
                // here for that case, but defense-in-depth).
                if (i + 1 < parts.Count && Guid.TryParseExact(parts[i + 1], "D", out _))
                    continue;
                parts.RemoveAt(i--);
                continue;
            }
        }
        return string.Join(' ', parts);
    }

    private static string? ExtractSshHost(string sshCommand)
    {
        var trimmed = sshCommand.Trim();
        if (!trimmed.StartsWith("ssh ", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("mosh ", StringComparison.OrdinalIgnoreCase))
            return null;
        var skipFirst = trimmed.IndexOf(' ');
        if (skipFirst < 0) return null;
        var args = trimmed[(skipFirst + 1)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Walk forward, skipping flag values for known pair-flags. The
        // first non-flag, non-pair-value token is the host (or user@host).
        var pairFlags = new HashSet<string>
        {
            "-p", "-i", "-l", "-o", "-J", "-L", "-R", "-D", "-F", "-W"
        };
        bool skipNext = false;
        foreach (var arg in args)
        {
            if (skipNext) { skipNext = false; continue; }
            if (arg.StartsWith('-'))
            {
                if (pairFlags.Contains(arg)) skipNext = true;
                continue;
            }
            return arg;
        }
        return null;
    }

    /// <summary>
    /// Toggles the per-pane "inside ssh" flag based on commands the user
    /// types. Anchors cd routing: while inside ssh, cd targets are stored
    /// verbatim as the remote cwd; outside ssh, they resolve against the
    /// local cwd. <c>exit</c> / <c>logout</c> heuristically bring us
    /// back to the local shell — false positives (typing exit inside
    /// vim, etc.) are harmless because the next ssh entry re-toggles.
    /// </summary>
    private void TrackSshState(string paneId, string command)
    {
        switch (Cmux.Core.Services.PaneCommandTracker.ClassifySshTransition(command))
        {
            case Cmux.Core.Services.PaneCommandTracker.SshTransition.Enter:
                _paneInsideSsh.Add(paneId);
                break;
            case Cmux.Core.Services.PaneCommandTracker.SshTransition.Leave:
                _paneInsideSsh.Remove(paneId);
                break;
        }
    }

    /// <summary>
    /// Mirrors the typed-command-derived cwd into <c>session.WorkingDirectory</c>
    /// and fires <see cref="WorkingDirectoryChanged"/> so the workspace's
    /// status bar / pane label refresh immediately on Enter. Without this,
    /// the typed cwd would live only in <c>_paneLocalCwd</c> / <c>_paneRemoteCwd</c>
    /// (used for snapshot persistence) but the UI would stay stale until
    /// the next OSC 7 / daemon CWD_CHANGED arrived — and in environments
    /// where neither ever fires (cmd.exe, bare PowerShell, busybox over
    /// ssh) that means never.
    /// </summary>
    private void PushCwdToSession(string paneId)
    {
        if (!_sessions.TryGetValue(paneId, out var session)) return;

        // Pick the cwd that's actually meaningful for the current pane
        // state. Inside SSH we expose the remote-side path; outside, the
        // tracked local cwd.
        string? cwd;
        if (_paneInsideSsh.Contains(paneId)
            && _paneRemoteCwd.TryGetValue(paneId, out var remote)
            && !string.IsNullOrEmpty(remote))
        {
            // Render as a Unix-style absolute path for the UI. Stored form
            // may be empty (= remote home) or relative-to-home — coerce
            // to a displayable shape without inventing absolute roots
            // we don't actually know.
            cwd = remote.StartsWith('/') ? remote : "~/" + remote;
        }
        else
        {
            cwd = _paneLocalCwd.GetValueOrDefault(paneId);
        }

        if (string.IsNullOrEmpty(cwd)) return;
        if (string.Equals(session.WorkingDirectory, cwd, StringComparison.Ordinal))
            return;

        session.WorkingDirectory = cwd;
        if (paneId == FocusedPaneId)
            WorkingDirectoryChanged?.Invoke(cwd);
    }

    private static bool IsLikelyCwdChange(string command)
    {
        var t = command.Trim();
        // Drive switch in cmd: `D:` or `D:\...`. Letter+colon at start.
        if (t.Length >= 2 && char.IsLetter(t[0]) && t[1] == ':') return true;
        var first = t.Split(' ', 2)[0].ToLowerInvariant();
        return first is "cd" or "pushd" or "popd" or "chdir";
    }

    /// <summary>
    /// Reads the actual shell-side cwd from the new prompt line after a
    /// cd-like command has been processed, ~500ms post-Enter. Source of
    /// truth — handles failed cd (prompt unchanged → cwd unchanged),
    /// Tab-completion fallout we didn't see in the typed text, and
    /// shell-side ~ expansion. Updates _paneRemoteCwd or _paneLocalCwd
    /// based on the in-ssh state and parsed path style. Best effort: a
    /// failed parse leaves whatever the typed-command path captured.
    /// </summary>
    private void SchedulePromptCwdRefresh(string paneId, int delayMs = 500)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
            try
            {
                if (!_sessions.TryGetValue(paneId, out var session)) return;
                var line = ReadCursorPromptLine(session.Buffer);
                if (string.IsNullOrEmpty(line)) return;

                var cwd = ParseCwdFromPromptLine(line);
                if (string.IsNullOrEmpty(cwd)) return;

                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    bool insideSsh = _paneInsideSsh.Contains(paneId);
                    if (insideSsh)
                    {
                        // Strip ~/ from bash's home-relative display so
                        // replay (`cd '<saved>'` from ~) lands correctly.
                        // Bare `~` becomes "" (= stay at home).
                        var stored = cwd;
                        if (stored == "~") stored = "";
                        else if (stored.StartsWith("~/")) stored = stored[2..];
                        _paneRemoteCwd[paneId] = stored;
                    }
                    else
                    {
                        // Local Windows: cmd / PowerShell prompt contains
                        // the full path. Use it as-is.
                        if (!LooksLikeRemoteCwd(cwd))
                            _paneLocalCwd[paneId] = cwd;
                    }
                }));
            }
            catch { /* best-effort */ }
        });
    }

    private static string? ReadCursorPromptLine(Cmux.Core.Terminal.TerminalBuffer buffer)
    {
        try
        {
            var line = buffer.GetLine(buffer.CursorRow);
            if (line.Length == 0) return null;
            var sb = new System.Text.StringBuilder(line.Length);
            foreach (var cell in line)
            {
                var ch = cell.Character;
                if (ch == '\0') ch = ' ';
                sb.Append(ch);
            }
            return sb.ToString().TrimEnd();
        }
        catch { return null; }
    }

    /// <summary>
    /// Parses the cwd portion out of common shell prompt formats:
    ///   bash / zsh:  "user@host:CWD$ " or "user@host:CWD# "
    ///   PowerShell:  "PS CWD>"
    ///   cmd.exe:     "CWD>"
    /// Returns null when no recognizable shape matches — caller keeps
    /// whatever was previously captured rather than corrupting it.
    /// </summary>
    private static string? ParseCwdFromPromptLine(string line)
    {
        var trimmed = line.TrimEnd();
        if (string.IsNullOrEmpty(trimmed)) return null;

        // bash / zsh: ...:CWD$  or  ...:CWD#
        var bash = System.Text.RegularExpressions.Regex.Match(
            trimmed, @"[^@\s]+@[^:\s]+:([^$#]*?)\s*[$#]");
        if (bash.Success)
        {
            var cwd = bash.Groups[1].Value.Trim();
            if (LooksLikeUnixPath(cwd)) return cwd;
        }

        // PowerShell: "PS C:\path>"
        var pwsh = System.Text.RegularExpressions.Regex.Match(
            trimmed, @"^PS\s+([A-Za-z]:[^>]*?)\s*>");
        if (pwsh.Success)
        {
            var cwd = pwsh.Groups[1].Value.Trim();
            if (LooksLikeWindowsPath(cwd)) return cwd;
        }

        // cmd.exe: "C:\path>"
        var cmd = System.Text.RegularExpressions.Regex.Match(
            trimmed, @"^([A-Za-z]:[^>]*?)>");
        if (cmd.Success)
        {
            var cwd = cmd.Groups[1].Value.Trim();
            if (LooksLikeWindowsPath(cwd)) return cwd;
        }

        return null;
    }

    /// <summary>True if the captured group looks like a Unix shell cwd —
    /// absolute (<c>/foo</c>), tilde (<c>~</c>, <c>~/foo</c>), or a single
    /// dot. Rejects random text that happened to match a regex anchor
    /// (typing "user@host:hello world$ " into Claude TUI as a prompt).</summary>
    private static bool LooksLikeUnixPath(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        return s == "~" || s.StartsWith("~/") || s.StartsWith('/')
               || s == "." || s == "..";
    }

    /// <summary>True if the captured group looks like a Windows shell cwd —
    /// drive letter + colon + (optional rest). Rejects unrelated regex hits
    /// where the prompt syntax happens to resemble cmd/PowerShell.</summary>
    private static bool LooksLikeWindowsPath(string s)
    {
        return s.Length >= 2 && char.IsLetter(s[0]) && s[1] == ':';
    }

    /// <summary>
    /// True if <paramref name="path"/> looks like a Unix-style absolute
    /// path (e.g. <c>/home/user</c>) — used to differentiate remote cwd
    /// (received via OSC 7 inside an SSH session) from a local Windows
    /// cwd, since both flow through the single <c>session.WorkingDirectory</c>
    /// channel.
    /// </summary>
    private static bool LooksLikeRemoteCwd(string? path)
    {
        return !string.IsNullOrEmpty(path)
            && path.StartsWith('/')
            && !path.Contains('\\');
    }

    /// <summary>
    /// Fallback cwd tracker for shells that don't emit OSC 7 (cmd.exe,
    /// stock PowerShell, plain bash without VTE integration). Updates
    /// per-pane local/remote cwd from typed <c>cd &lt;path&gt;</c> and
    /// drive-switch (<c>D:</c>) commands. Keeps a separate state from
    /// <c>session.WorkingDirectory</c> so the daemon's CWD_CHANGED
    /// stream — which mirrors the daemon-side ConPTY's start dir on
    /// startup and on every OSC 7 — doesn't clobber our writes for
    /// shells that never emit an authoritative cwd signal.
    /// </summary>
    private void TryUpdateCwdFromCdCommand(string paneId, string command)
    {
        var insideSsh = _paneInsideSsh.Contains(paneId);
        var priorLocal = _paneLocalCwd.GetValueOrDefault(paneId);
        var priorRemote = _paneRemoteCwd.GetValueOrDefault(paneId);
        var fallbackBase = _sessions.GetValueOrDefault(paneId)?.WorkingDirectory;

        var result = Cmux.Core.Services.CdCommandParser.Parse(
            command, insideSsh, priorLocal, priorRemote, fallbackBase);

        if (!string.IsNullOrEmpty(result.LocalCwd))
            _paneLocalCwd[paneId] = result.LocalCwd!;
        if (result.RemoteCwd != null)
            _paneRemoteCwd[paneId] = result.RemoteCwd;
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

    /// <summary>
    /// Combines a Unix-style cwd with a cd argument and normalizes
    /// <c>..</c> / <c>.</c>. Returns absolute (<c>/foo/bar</c>) or
    /// relative-to-home (<c>foo/bar</c>) form depending on the inputs.
    /// Used for sequential remote cd accumulation when the shell doesn't
    /// emit OSC 7 — without this, two consecutive relatives would clobber
    /// instead of stacking.
    /// </summary>
    private static string CombineUnixPath(string current, string arg)
    {
        if (arg.StartsWith('/'))
        {
            // Absolute resets — ignore current.
            return NormalizeUnixPath(arg);
        }

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
                // Pop last real segment; if at the root of a relative
                // path keep the .. so users walking up from home (~)
                // are at least represented. Absolute root just stops.
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

    /// <summary>
    /// Applies the optional ssh→tmux wrap and claude→--continue rewrites to
    /// a saved AutoRestoreCommand right before re-issuing it on session
    /// startup. Each rewrite is gated by its <see cref="CmuxSettings"/>
    /// flag and skipped in obviously-unsafe cases (existing -t, quoted
    /// args, claude already has --continue).
    /// </summary>
    private string TransformAutoRestoreCommand(string command, string paneId)
    {
        // Delegates to Cmux.Core's pure transformer so the decision logic
        // is unit-testable without the WPF dependency stack. SurfaceViewModel
        // just supplies the runtime context (the captured UUID from
        // _paneClaudeUuid and the SettingsService flag).
        var capturedUuid = _paneClaudeUuid.GetValueOrDefault(paneId);
        return Cmux.Core.Services.AutoRestoreCommandTransformer.Transform(
            command,
            capturedUuid,
            SettingsService.Current.ResumeClaudeOnRestore);
    }

    /// <summary>
    /// Schedules the saved <c>AutoRestoreCommand</c> for <paramref name="paneId"/>
    /// to be sent into the pane's PTY ~1.5s after session start. The delay is
    /// a deliberate fallback: shells that emit OSC 133 prompt markers fire
    /// almost immediately, but cmd.exe / unconfigured PowerShell / remote
    /// daemons that lack shell integration would never fire one — this way
    /// the keystrokes still queue into stdin and execute when the prompt
    /// is ready. Skipped if AutoRestorePaneCommands is disabled.
    /// </summary>
    private void ScheduleAutoRestore(string paneId)
    {
        if (!SettingsService.Current.AutoRestorePaneCommands) return;
        if (!_paneAutoRestoreCommand.TryGetValue(paneId, out var rawCommand))
            return;

        var transformed = TransformAutoRestoreCommand(rawCommand, paneId);
        var firstStagePayload = transformed + "\r";

        // Plan a second-stage `cd <remote> && claude --continue` only when
        // (a) primary was an ssh-style command, (b) the user previously ran
        // claude inside that ssh, (c) we have a remote cwd to restore to.
        string? secondStagePayload = null;
        var firstWord = rawCommand.Trim().Split(' ', 2)[0].ToLowerInvariant();
        bool isSshPrimary = firstWord is "ssh" or "mosh";
        if (isSshPrimary
            && _paneClaudeInsideSsh.Contains(paneId)
            && SettingsService.Current.ResumeClaudeOnRestore
            && _paneRemoteCwd.TryGetValue(paneId, out var remoteCwd)
            && !string.IsNullOrEmpty(remoteCwd))
        {
            // Single-quote the remote path; escape embedded apostrophes
            // by closing-quoting around them: `it's` → `it'\''s`.
            var safeCwd = remoteCwd.Replace("'", "'\\''");
            // Prefer captured UUID for pinpoint resume; fall back to the
            // most-recent in-cwd conversation via --continue when we
            // never managed to capture a UUID (no .jsonl access, ssh
            // subprocess auth failed, etc.).
            var claudeArgs = _paneClaudeUuid.TryGetValue(paneId, out var uuid)
                             && !string.IsNullOrEmpty(uuid)
                ? $"--resume {uuid}"
                : "--continue";
            secondStagePayload = $"cd '{safeCwd}' && claude {claudeArgs}\r";
        }

        _ = Task.Run(async () =>
        {
            // Stage 1: primary command. The 1.5s delay covers shells that
            // don't emit OSC 133 prompt markers — keystrokes still queue
            // in the PTY's stdin and run when the prompt is ready.
            await Task.Delay(1500).ConfigureAwait(false);
            try
            {
                if (_sessions.TryGetValue(paneId, out var session))
                    session.Write(firstStagePayload);
            }
            catch { /* best-effort */ }

            if (secondStagePayload == null) return;

            // Stage 2: wait for the SSH handshake + remote prompt to
            // settle. 4s is empirically enough for keyed SSH; passworded
            // / hardware-token SSH may need more — user can disable
            // ResumeClaudeOnRestore if their flow is slower.
            await Task.Delay(4000).ConfigureAwait(false);
            try
            {
                if (_sessions.TryGetValue(paneId, out var session))
                    session.Write(secondStagePayload);
            }
            catch { /* best-effort */ }
        });
    }

    private void AppendToCommandHistory(string paneId, string command)
    {
        if (!_paneCommandHistory.TryGetValue(paneId, out var history))
        {
            history = [];
            _paneCommandHistory[paneId] = history;
        }

        var trimmed = command.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return;

        if (history.Count == 0 || !string.Equals(history[^1], trimmed, StringComparison.Ordinal))
            history.Add(trimmed);

        while (history.Count > 500)
            history.RemoveAt(0);
    }

    private TerminalSession StartSession(string paneId, string? workingDirectory = null, PaneStateSnapshot? restoredState = null, string? shell = null)
    {
        var effectiveShell = shell ?? GetConfiguredShell();
        // Store the explicit override (null = use default shell from settings)
        _paneShells[paneId] = shell;

        // Seed the local-cwd tracker with the ConPTY's start directory so
        // a brand-new pane has a meaningful cwd *before* the user types
        // anything. Skip when the seed value looks remote (Unix path
        // restored from an ssh-rooted snapshot) — that lives in
        // _paneRemoteCwd, not _paneLocalCwd. Don't overwrite an existing
        // tracked value (could be a restored snapshot path).
        var seedCwd = workingDirectory ?? restoredState?.WorkingDirectory;
        if (!string.IsNullOrWhiteSpace(seedCwd)
            && !LooksLikeRemoteCwd(seedCwd)
            && !_paneLocalCwd.ContainsKey(paneId))
        {
            _paneLocalCwd[paneId] = seedCwd!;
        }

        // Wait for daemon connect task (includes starting daemon if needed).
        // First pane blocks up to 5s; subsequent panes get the cached result instantly.
        lock (_daemonWaitLock)
        {
            if (!_daemonWaitDone)
            {
                DaemonLog($"[StartSession:{paneId}] Waiting for daemon connect task...");
                try { App.DaemonConnectTask.Wait(5000); }
                catch { /* timeout or connect failure — proceed with local */ }
                _daemonWaitDone = true;
            }
        }

        var daemonReady = App.DaemonConnectTask.IsCompletedSuccessfully
                          && App.DaemonConnectTask.Result;

        DaemonLog($"[StartSession:{paneId}] daemonReady={daemonReady}, IsConnected={App.DaemonClient.IsConnected}, TaskStatus={App.DaemonConnectTask.Status}");

        // Try daemon-backed session first
        if (daemonReady)
        {
            try
            {
                return StartDaemonSession(paneId, workingDirectory, restoredState, effectiveShell);
            }
            catch (Exception ex)
            {
                DaemonLog($"[StartSession:{paneId}] Daemon session failed: {ex.Message}");
            }
        }

        DaemonLog($"[StartSession:{paneId}] Using LOCAL session");
        return StartLocalSession(paneId, workingDirectory, restoredState, effectiveShell);
    }

    private static void DaemonLog(string message) => App.DaemonLog(message);

    private TerminalSession StartDaemonSession(string paneId, string? workingDirectory, PaneStateSnapshot? restoredState, string? shell)
    {
        // Use saved snapshot dimensions if available (avoids spurious resize on reconnect)
        var initCols = restoredState?.BufferSnapshot?.Cols ?? 120;
        var initRows = restoredState?.BufferSnapshot?.Rows ?? 30;
        var session = new TerminalSession(paneId, initCols, initRows);
        WireSessionEvents(session, paneId);

        // Set daemon delegates so Write/Resize route through daemon
        var daemon = App.DaemonClient;
        session.DaemonWrite = data => daemon.WriteAsync(paneId, data);
        session.DaemonResize = (cols, rows) => daemon.ResizeAsync(paneId, cols, rows);

        _sessions[paneId] = session;
        _daemonPanes.Add(paneId);

        var effectiveCwd = workingDirectory ?? restoredState?.WorkingDirectory;

        // Create/attach session on daemon asynchronously
        _ = Task.Run(async () =>
        {
            try
            {
                DaemonLog($"[DaemonSession:{paneId}] Calling CreateSessionAsync ({initCols}x{initRows}) shell={shell ?? "<auto>"}...");
                // Pass the user-chosen shell through to the daemon — without
                // this, the daemon's TerminalProcess.DetectShell() picks
                // pwsh > powershell > cmd by its own logic and ignores the
                // SettingsService.DefaultShell, so users who switch to cmd
                // see PowerShell anyway in daemon-backed panes.
                var result = await daemon.CreateSessionAsync(
                    paneId, initCols, initRows, effectiveCwd, shell);

                if (result == null)
                {
                    DaemonLog($"[DaemonSession:{paneId}] CreateSessionAsync returned NULL — falling back to local");
                    _daemonPanes.Remove(paneId);
                    session.DaemonWrite = null;
                    session.DaemonResize = null;
                    session.Start(command: shell, workingDirectory: effectiveCwd);
                    return;
                }

                DaemonLog($"[DaemonSession:{paneId}] CreateSessionAsync OK: IsExisting={result.IsExisting}, IsRunning={result.IsRunning}, Cwd={result.WorkingDirectory}");

                // Set working directory from daemon response
                if (!string.IsNullOrEmpty(result.WorkingDirectory))
                    session.WorkingDirectory = result.WorkingDirectory;

                // Auto-restore the saved primary command (ssh / claude /
                // tmux / etc.) only when this is a *new* daemon session.
                // Reconnecting to an existing one means the command is
                // already running over there — re-issuing would launch a
                // duplicate alongside the live process.
                if (!result.IsExisting)
                    ScheduleAutoRestore(paneId);

                // If reconnecting to an existing daemon session, get the live buffer snapshot
                if (result.IsExisting && result.IsRunning)
                {
                    DaemonLog($"[DaemonSession:{paneId}] Reconnecting — fetching daemon snapshot...");
                    var snapshotJson = await daemon.GetSnapshotAsync(paneId);
                    if (snapshotJson != null)
                    {
                        try
                        {
                            var snapshot = System.Text.Json.JsonSerializer.Deserialize<TerminalBufferSnapshot>(snapshotJson);
                            if (snapshot != null)
                            {
                                session.RestoreBufferSnapshot(snapshot);
                                DaemonLog($"[DaemonSession:{paneId}] Snapshot restored ({snapshotJson.Length} chars)");
                            }
                        }
                        catch (Exception ex)
                        {
                            DaemonLog($"[DaemonSession:{paneId}] Snapshot restore error: {ex.Message}");
                        }
                    }
                    else
                    {
                        DaemonLog($"[DaemonSession:{paneId}] GetSnapshotAsync returned null");
                    }

                    // NOTE: do NOT inject CR/Enter here. The snapshot already
                    // contains the visible prompt line, and an injected Enter
                    // submits whatever was sitting in the shell readline / TUI
                    // input buffer at disconnect time — e.g. it auto-runs the
                    // suggested-next-prompt that Claude Code shows pre-filled,
                    // or re-submits a half-typed shell command. If a fresh
                    // prompt redraw is ever needed it should be triggered by
                    // a non-submitting signal (resize / Ctrl+L) rather than CR.
                }
            }
            catch (Exception ex)
            {
                DaemonLog($"[DaemonSession:{paneId}] Exception — falling back to local: {ex.Message}");
                _daemonPanes.Remove(paneId);
                session.DaemonWrite = null;
                session.DaemonResize = null;
                session.Start(command: shell, workingDirectory: effectiveCwd);
            }
        });

        if (restoredState?.BufferSnapshot != null)
            session.RestoreBufferSnapshot(restoredState.BufferSnapshot);

        return session;
    }

    private static string? GetConfiguredShell()
    {
        var shell = SettingsService.Current.DefaultShell;
        return string.IsNullOrWhiteSpace(shell) ? null : shell;
    }

    private TerminalSession StartLocalSession(string paneId, string? workingDirectory, PaneStateSnapshot? restoredState, string? shell)
    {
        var session = new TerminalSession(paneId);
        WireSessionEvents(session, paneId);

        _sessions[paneId] = session;
        session.Start(command: shell, workingDirectory: workingDirectory ?? restoredState?.WorkingDirectory);

        if (restoredState?.BufferSnapshot != null)
            session.RestoreBufferSnapshot(restoredState.BufferSnapshot);

        // Local sessions are always fresh — no concept of a pre-existing
        // process to reattach to. Replay the saved primary command if any.
        ScheduleAutoRestore(paneId);

        return session;
    }

    private void WireSessionEvents(TerminalSession session, string paneId)
    {
        session.WorkingDirectoryChanged += dir =>
        {
            // OSC 7 from inside an SSH session reports the *remote* cwd —
            // detect by Unix-style absolute path with no backslash. Saved
            // separately from local cwd so the soft-restore second stage
            // can `cd '<remote>'` after the ssh handshake completes.
            if (LooksLikeRemoteCwd(dir))
                _paneRemoteCwd[paneId] = dir;

            if (paneId == FocusedPaneId)
                WorkingDirectoryChanged?.Invoke(dir);
        };

        session.NotificationReceived += (title, subtitle, body, senderId, senderTs) =>
        {
            // Always go through AddNotification; the active-pane drop is
            // decided by NotificationService.ShouldSuppress (set in
            // App.xaml.cs) which runs on the UI thread and can read
            // Window.IsActive / WindowState directly — much more reliable
            // than the volatile foreground flag we used before.
            _notificationService.AddNotification(
                _workspaceId, Surface.Id, paneId,
                title, subtitle, body, source: NotificationSource.Osc9,
                senderId: senderId, senderTimestamp: senderTs);
        };

        session.ShellPromptMarker += (marker, payload) =>
        {
            App.CommandLogService.HandlePromptMarker(
                paneId,
                _workspaceId,
                Surface.Id,
                marker,
                payload,
                session.WorkingDirectory);

            if (marker == 'B')
            {
                var sanitized = App.CommandLogService.SanitizeCommandForStorage(payload);
                if (!string.IsNullOrWhiteSpace(sanitized))
                    AppendToCommandHistory(paneId, sanitized);
            }
        };
    }

    [RelayCommand]
    public void SplitRight()
    {
        SplitFocused(SplitDirection.Vertical);
    }

    [RelayCommand]
    public void SplitDown()
    {
        SplitFocused(SplitDirection.Horizontal);
    }

    public void SplitFocused(SplitDirection direction, string? shell = null)
    {
        if (FocusedPaneId == null) return;

        var node = RootNode.FindNode(FocusedPaneId);
        if (node == null || !node.IsLeaf) return;

        var newChild = node.Split(direction);
        if (newChild.PaneId != null)
        {
            var currentSession = GetSession(FocusedPaneId);
            var cwd = currentSession?.WorkingDirectory;
            var effectiveShell = shell ?? _paneShells.GetValueOrDefault(FocusedPaneId);
            StartSession(newChild.PaneId, cwd, null, effectiveShell);
            FocusedPaneId = newChild.PaneId;
        }

        // Trigger UI update
        OnPropertyChanged(nameof(RootNode));
    }

    public void OpenPaneWithShell(string shellPath)
    {
        SplitFocused(SplitDirection.Vertical, shellPath);
    }

    [RelayCommand]
    public void ClosePane()
    {
        ClosePane(FocusedPaneId);
    }

    public void ClosePane(string? paneId)
    {
        if (paneId == null) return;

        CapturePaneTranscript(paneId, "pane-close");

        // Get adjacent pane before removal
        var nextLeaf = RootNode.GetNextLeaf(paneId) ?? RootNode.GetPreviousLeaf(paneId);
        string? nextPaneId = nextLeaf?.PaneId;

        // Stop and remove the session
        if (_sessions.TryGetValue(paneId, out var session))
        {
            if (_daemonPanes.Remove(paneId))
                _ = App.DaemonClient.CloseSessionAsync(paneId);
            session.Dispose();
            _sessions.Remove(paneId);
        }

        Surface.PaneCustomNames.Remove(paneId);
        Surface.PaneSnapshots.Remove(paneId);
        _paneCommandHistory.Remove(paneId);
        _paneShells.Remove(paneId);

        // If this is the only pane, don't remove it
        var leaves = RootNode.GetLeaves().ToList();
        if (leaves.Count <= 1) return;

        RootNode.Remove(paneId);

        if (paneId == FocusedPaneId)
            FocusedPaneId = nextPaneId;

        OnPropertyChanged(nameof(RootNode));
    }

    public void FocusPane(string paneId)
    {
        FocusedPaneId = paneId;
        Surface.FocusedPaneId = paneId;

        // Always mark read here — not only via OnFocusedPaneIdChanged. The
        // CommunityToolkit-generated property setter skips the change
        // notification (and thus the partial method) when the value is
        // unchanged, so clicking the already-focused pane while it keeps
        // emitting notifications would never clear them otherwise.
        _notificationService.MarkPaneAsRead(paneId);
    }

    [RelayCommand]
    public void FocusNextPane()
    {
        if (FocusedPaneId == null) return;
        var next = RootNode.GetNextLeaf(FocusedPaneId);
        if (next?.PaneId != null)
            FocusPane(next.PaneId);
    }

    [RelayCommand]
    public void FocusPreviousPane()
    {
        if (FocusedPaneId == null) return;
        var prev = RootNode.GetPreviousLeaf(FocusedPaneId);
        if (prev?.PaneId != null)
            FocusPane(prev.PaneId);
    }


    [RelayCommand]
    public void ToggleZoom() => IsZoomed = !IsZoomed;

    public void EqualizePanes()
    {
        RootNode.Equalize();
        OnPropertyChanged(nameof(RootNode));
    }

    /// <summary>
    /// Replaces the current pane tree with <paramref name="targetTree"/>,
    /// preserving every running session by reassigning existing pane IDs
    /// to the new tree's leaves in traversal order. Surplus existing panes
    /// (more sessions than the target geometry has slots) are nested as
    /// extra horizontal splits inside the last leaf rather than closed,
    /// so layouts can be cycled without losing Claude / SSH / etc. sessions.
    /// Empty target slots get fresh sessions.
    /// </summary>
    public void ApplyLayoutTree(SplitNode targetTree)
    {
        if (targetTree == null) return;

        // Snapshot existing pane IDs in tree-traversal order — these are the
        // sessions we MUST keep alive across the layout change.
        var existingPaneIds = RootNode.GetLeaves()
            .Select(l => l.PaneId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToList();

        // Phase 1: reuse existing pane IDs for the first N target leaves.
        var targetLeaves = targetTree.GetLeaves().ToList();
        int reuseCount = Math.Min(existingPaneIds.Count, targetLeaves.Count);
        for (int i = 0; i < reuseCount; i++)
            targetLeaves[i].PaneId = existingPaneIds[i];

        // Phase 2: surplus existing panes (more sessions than target slots).
        // Nest them as extra horizontal splits under the last target leaf so
        // every running session is still represented by a leaf in the new
        // tree — no ClosePane / Dispose calls.
        if (existingPaneIds.Count > targetLeaves.Count && targetLeaves.Count > 0)
        {
            var nestLeaf = targetLeaves[^1];
            for (int i = targetLeaves.Count; i < existingPaneIds.Count; i++)
            {
                var newRight = nestLeaf.Split(SplitDirection.Horizontal);
                newRight.PaneId = existingPaneIds[i];
                nestLeaf = newRight;
            }
        }

        // Phase 3: empty target slots (more leaves than existing sessions).
        // Start fresh sessions for those, inheriting cwd from a sibling so the
        // new shell starts in a sensible place.
        string? inheritedCwd = _sessions.Values.FirstOrDefault()?.WorkingDirectory;
        foreach (var leaf in targetTree.GetLeaves())
        {
            if (string.IsNullOrWhiteSpace(leaf.PaneId)) continue;
            if (_sessions.ContainsKey(leaf.PaneId)) continue;
            StartSession(leaf.PaneId, inheritedCwd);
        }

        // Phase 4: swap in the new tree. Setting RootNode (auto-generated by
        // [ObservableProperty]) raises the change notification that
        // SplitPaneContainer listens for; its terminal cache is keyed by
        // paneId so reused TerminalControls stay attached to their sessions.
        Surface.RootSplitNode = targetTree;
        RootNode = targetTree;

        // Make sure focus lands on a real leaf in the new tree.
        if (FocusedPaneId == null || RootNode.FindNode(FocusedPaneId) == null)
        {
            var first = RootNode.GetLeaves().FirstOrDefault();
            if (first?.PaneId != null)
                FocusedPaneId = first.PaneId;
        }

        EqualizePanes();
    }

    partial void OnFocusedPaneIdChanged(string? value)
    {
        Surface.FocusedPaneId = value;

        // Visiting a pane clears its unread notifications. Pane ids are GUIDs
        // and unique app-wide, so matching on paneId alone is enough — no
        // workspace/surface scoping needed. Safe on construction: panes that
        // have never emitted notifications are a no-op inside MarkPaneAsRead.
        if (!string.IsNullOrEmpty(value))
            _notificationService.MarkPaneAsRead(value);
    }

    partial void OnNameChanged(string value)
    {
        Surface.Name = value;
    }

    public void Dispose()
    {
        CapturePaneSnapshotsForPersistence();

        // Unwire daemon events
        var daemon = App.DaemonClient;
        daemon.RawOutputReceived -= OnDaemonRawOutput;
        daemon.CwdChanged -= OnDaemonCwdChanged;
        daemon.TitleChanged -= OnDaemonTitleChanged;
        daemon.SessionExited -= OnDaemonSessionExited;
        daemon.BellReceived -= OnDaemonBellReceived;
        daemon.Disconnected -= OnDaemonDisconnected;

        foreach (var session in _sessions.Values)
            session.Dispose();
        _sessions.Clear();
        _daemonPanes.Clear();
        _paneShells.Clear();
    }
}
