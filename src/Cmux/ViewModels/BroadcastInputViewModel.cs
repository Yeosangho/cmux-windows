using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cmux.Core.Services;
using Cmux.Core.Terminal;

namespace Cmux.ViewModels;

public enum BroadcastScope
{
    /// <summary>Send to the single currently focused pane of the active
    /// surface. Lets the user use the broadcast bar as a *low-latency
    /// prompt composer for the current pane*, sidestepping per-keystroke
    /// PTY echo lag — the whole composed text is fanned out at once.</summary>
    ActivePane,
    /// <summary>Send to every pane in the currently selected surface.</summary>
    CurrentSurface,
    /// <summary>Send to every pane across every surface in the current workspace.</summary>
    Workspace,
    /// <summary>Send only to the user-checked subset (see <see cref="SelectedPaneIds"/>).</summary>
    Selected,
    /// <summary>Send only to panes classified as either Claude-Local or
    /// Claude-SSH (i.e. every session that's running Claude in some form).
    /// Convenience scope for "send a prompt to all my Claude sessions".</summary>
    ClaudeAll,
    /// <summary>Send only to panes whose process tree shows a Claude
    /// session running locally on Windows (claude.exe / node + claude args).</summary>
    ClaudeLocal,
    /// <summary>Send only to panes whose process tree contains ssh.exe
    /// (assumed to be a remote Claude session).</summary>
    ClaudeSsh,
}

/// <summary>Static (or user-defined) preset prompts the user can drop into
/// the input box with one click. Tailored for Claude broadcasting — e.g.
/// "지금까지 한 작업을 메모리에 정리해줘".</summary>
public record BroadcastPreset(string Label, string Text);

/// <summary>
/// Backs the bottom-of-window broadcast input bar (Xshell-style "send command
/// to multiple sessions"). Resolves a target pane set from <see cref="Scope"/>
/// against the currently selected workspace, then writes the user's text + CR
/// to each session — going through TerminalSession.Write means the keystrokes
/// pass through PTY → shell exactly like a manual keystroke would.
/// </summary>
public partial class BroadcastInputViewModel : ObservableObject
{
    private readonly Func<MainViewModel?> _mainResolver;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private BroadcastScope _scope = BroadcastScope.ActivePane;

    [ObservableProperty]
    private string _text = string.Empty;

    /// <summary>
    /// Pane picker popup grouped by surface (terminal). Two-way bound
    /// checkboxes flow through <see cref="BroadcastPaneSelection.IsChecked"/>
    /// into <see cref="SelectedPaneIds"/>.
    /// </summary>
    public ObservableCollection<BroadcastSurfaceGroup> SurfaceGroups { get; } = [];

    public HashSet<string> SelectedPaneIds { get; } = [];

    /// <summary>Fires whenever the broadcast target set may have changed —
    /// scope switch, manual selection toggle, or async classification cache
    /// fill. SplitPaneContainer subscribes to repaint pane borders red.</summary>
    public event Action? SelectionChanged;

    /// <summary>
    /// Async-filled cache of per-pane process-tree classification (Claude
    /// local vs ssh). The Claude scopes' visualization and target resolution
    /// both read from here so the WMI fan-out happens once per scope-change
    /// instead of once per repaint or per Submit.
    /// </summary>
    private readonly Dictionary<string, AgentDetector.PaneAgentKind> _kindCache = [];
    private CancellationTokenSource? _kindRefreshCts;

    [ObservableProperty]
    private string _targetSummary = "";

    /// <summary>User-facing preset prompts. Loaded from
    /// %LOCALAPPDATA%\cmux\broadcast-presets.json on construction; falls
    /// back to a small set of Korean defaults so the popup is never empty.
    /// Edit the JSON file by hand to change them — no settings UI yet.</summary>
    public ObservableCollection<BroadcastPreset> Presets { get; } = [];

    private static readonly string PresetsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "cmux", "broadcast-presets.json");

    // In-memory shell-style prompt history. Each Submit appends Text here
    // (deduplicating consecutive duplicates). The view binds Up/Down at the
    // top/bottom caret line into TryNavigateHistory{Up,Down} so the user
    // can recall and re-fire a previous broadcast prompt without retyping.
    private readonly List<string> _history = new();
    private int _historyIndex = -1;  // -1 = not currently navigating; otherwise 0=newest, 1=older, ...
    private string _historyDraft = string.Empty;  // saved on entry into navigation, restored at the newest edge
    private const int HistoryCap = 200;

    public BroadcastInputViewModel(Func<MainViewModel?> mainResolver)
    {
        _mainResolver = mainResolver;
        LoadPresets();
        RefreshTargetSummary();

        // OSC 1338 announce / OSC 7 host arriving on the wire is *new* state
        // that invalidates whatever classification we already cached for that
        // pane. The cleanest reaction is to drop the cache entry and rerun
        // MaybeStartKindRefresh — if the bar is visible & on a Claude scope
        // the visualization repaints in the next tick; otherwise the next
        // OnIsVisibleChanged / OnScopeChanged will pick it up naturally.
        var daemon = App.DaemonClient;
        daemon.AgentAnnounced += (paneId, agent, ev, host, sid, tsEpoch) =>
            InvalidatePane(paneId);
        daemon.RemoteHostChanged += (paneId, host) =>
            InvalidatePane(paneId);
    }

    /// <summary>
    /// Drops any cached classification for <paramref name="paneId"/> and (if
    /// the broadcast bar is currently driving a Claude scope) schedules a
    /// background refresh so the visualization stays current. Public so
    /// SurfaceViewModel's local-side OSC wire can call it for in-process
    /// (non-daemon) sessions.
    /// </summary>
    public void InvalidatePane(string paneId)
    {
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            _kindCache.Remove(paneId);
            if (IsVisible &&
                (Scope == BroadcastScope.ClaudeLocal
                 || Scope == BroadcastScope.ClaudeSsh
                 || Scope == BroadcastScope.ClaudeAll))
            {
                MaybeStartKindRefresh();
            }
        }));
    }

    [RelayCommand]
    public void ApplyPreset(BroadcastPreset? preset)
    {
        if (preset == null) return;
        // Drop the prompt into the input box rather than auto-sending — lets
        // the user review / tweak before fanning the keystroke out to many
        // Claude sessions, where a typo would be expensive to rollback.
        Text = preset.Text;
    }

    private void LoadPresets()
    {
        try
        {
            if (File.Exists(PresetsPath))
            {
                var json = File.ReadAllText(PresetsPath);
                var list = JsonSerializer.Deserialize<List<BroadcastPreset>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (list is { Count: > 0 })
                {
                    Presets.Clear();
                    foreach (var p in list) Presets.Add(p);
                    return;
                }
            }
        }
        catch { /* fall through to defaults */ }

        Presets.Clear();
        Presets.Add(new("기억 정리", "지금까지 한 작업을 메모리에 정리해줘"));
        Presets.Add(new("작업 요약", "지금까지 변경한 코드를 짧게 요약해줘"));
        Presets.Add(new("Git 상태", "git status, git diff를 보고 변경사항을 요약해줘"));
        Presets.Add(new("커밋 작성", "지금 변경된 내용으로 commit message를 작성하고 커밋해줘"));
    }

    [RelayCommand]
    public void Toggle()
    {
        IsVisible = !IsVisible;
        if (IsVisible)
        {
            RefreshAvailablePanes();
            MaybeStartKindRefresh();
        }
    }

    [RelayCommand]
    public void Hide() => IsVisible = false;

    [RelayCommand]
    public async Task Submit()
    {
        var text = Text;
        if (string.IsNullOrEmpty(text)) return;

        var sessions = ResolveTargetSessions().ToList();
        if (sessions.Count == 0) return;

        // Normalize CRLF / LF to a single `\n` so multi-line composition
        // works the same regardless of the user's keyboard or paste source.
        var normalized = text.Replace("\r\n", "\n");
        bool isMultiLine = normalized.Contains('\n');

        // Stage 1: send the body only (no trailing \r). When the target is
        // a multi-line TUI like Claude Code that's already mid-edit (the
        // user typed something in the pane before broadcasting), a single
        // write that ends in `…\x1b[201~\r` or `…text\r` can have its
        // trailing `\r` swallowed by the input handler while it's still
        // finalizing the paste-end / input buffer — the symptom users
        // see is "broadcast arrives but doesn't submit; I have to press
        // Enter manually in the Claude Code pane". Splitting body and
        // Enter into two writes with a short gap makes the Enter arrive
        // as a fresh, unambiguous keystroke.
        foreach (var session in sessions)
        {
            try
            {
                string payload;
                if (isMultiLine && session.Buffer.BracketedPasteMode)
                {
                    // Multi-line + bracketed-paste-aware target (Claude Code
                    // TUI, modern shells with readline, etc.). Wrapping the
                    // body in DECSET 2004 brackets tells the receiver "this
                    // is pasted text — keep newlines as input, don't
                    // execute each line".
                    payload = "\x1b[200~" + normalized + "\x1b[201~";
                }
                else
                {
                    // Single-line OR target without bracketed-paste support.
                    // For a multi-line payload here each \n becomes its own
                    // shell command (same as pasting into a non-bracketing
                    // terminal), which is what users on those shells
                    // already expect.
                    payload = normalized;
                }
                session.Write(payload);
            }
            catch { /* one bad session shouldn't block the others */ }
        }

        // Append to in-memory history (deduplicate consecutive duplicates),
        // cap size to avoid runaway growth, and reset the navigation cursor
        // so the next Up brings back the just-sent prompt.
        if (_history.Count == 0 || _history[^1] != text)
            _history.Add(text);
        if (_history.Count > HistoryCap)
            _history.RemoveRange(0, _history.Count - HistoryCap);
        _historyIndex = -1;
        _historyDraft = string.Empty;

        // Clear the input box — deferred to the next dispatcher cycle so
        // it lands AFTER the current Enter keystroke's TSF / IME composition
        // commit has fully drained. Mutating the bound Text property
        // synchronously inside the key-event message frame while WPF's
        // TextStore is mid-`OnEndComposition` leaves TSF state inconsistent
        // and trips a `MS.Internal.Invariant.FailFast` later — even after
        // focus has moved to another control (pane) — taking the whole
        // cmuxw.exe down with .NET Runtime Id=1025.
        Application.Current?.Dispatcher.BeginInvoke(new Action(() => { Text = string.Empty; }));

        // Stage 2: wait long enough for the receiver to finalize its
        // paste-end / input-buffer state before delivering Enter as its
        // own keystroke. ~40 ms is below human perception for a manual
        // broadcast but well above the worst-case TUI render frame.
        await Task.Delay(40).ConfigureAwait(true);

        // Stage 3: deliver the submit keystroke as a separate write.
        foreach (var session in sessions)
        {
            try { session.Write("\r"); }
            catch { /* one bad session shouldn't block the others */ }
        }
    }

    /// <summary>
    /// True if Up would advance to an older history entry (caret-line
    /// check is the view's job). Lets the view decide whether to Handle
    /// the key without actually performing the mutation synchronously.
    /// </summary>
    public bool CanNavigateHistoryUp =>
        _history.Count > 0
        && (_historyIndex < 0 || _historyIndex + 1 < _history.Count);

    /// <summary>
    /// True if Down would step toward newer (or restore draft).
    /// </summary>
    public bool CanNavigateHistoryDown => _historyIndex >= 0;

    /// <summary>
    /// Step backward in submission history (older). Returns true if Text
    /// changed (caller should reposition the caret). Saves the current
    /// draft on the first navigation step so that stepping past the newest
    /// entry restores what the user was typing.
    /// </summary>
    /// <remarks>
    /// Must be invoked OUTSIDE a TextBox key event handler (post via
    /// Dispatcher.BeginInvoke) — mutating the bound Text property while
    /// WPF's TextStore holds the composition lock for the current key has
    /// caused .NET Runtime FailFast crashes (TextStore.OnEndComposition
    /// invariant violation) during Korean / IME input.
    /// </remarks>
    public bool TryNavigateHistoryUp()
    {
        if (_history.Count == 0) return false;

        if (_historyIndex < 0)
            _historyDraft = Text ?? string.Empty;

        int newIndex = _historyIndex < 0 ? 0 : _historyIndex + 1;
        if (newIndex >= _history.Count) return false;  // already at the oldest entry

        _historyIndex = newIndex;
        Text = _history[_history.Count - 1 - _historyIndex];
        return true;
    }

    /// <summary>
    /// Step forward in submission history (newer). Returns true if Text
    /// changed. At the newest entry, one more press restores the draft
    /// captured on history entry and exits navigation mode (matches the
    /// readline / bash history-down behavior). Same IME-deferral caveat as
    /// <see cref="TryNavigateHistoryUp"/>.
    /// </summary>
    public bool TryNavigateHistoryDown()
    {
        if (_historyIndex < 0) return false;  // not currently navigating; nothing to step toward

        if (_historyIndex == 0)
        {
            _historyIndex = -1;
            Text = _historyDraft;
            return true;
        }

        _historyIndex--;
        Text = _history[_history.Count - 1 - _historyIndex];
        return true;
    }

    partial void OnScopeChanged(BroadcastScope value)
    {
        RefreshAvailablePanes();
        RefreshTargetSummary();
        MaybeStartKindRefresh();
        SelectionChanged?.Invoke();
    }

    partial void OnIsVisibleChanged(bool value)
    {
        if (value)
        {
            RefreshAvailablePanes();
            MaybeStartKindRefresh();
        }
        SelectionChanged?.Invoke();
    }

    /// <summary>
    /// Returns true if <paramref name="paneId"/> would be hit by a Send right
    /// now. Used by SplitPaneContainer to draw a red ring around every
    /// targeted pane — not just manually-checked ones, so changing the scope
    /// ComboBox visibly changes which panes will receive input.
    /// </summary>
    public bool IsPaneTargeted(string paneId)
    {
        if (string.IsNullOrEmpty(paneId)) return false;
        var workspace = _mainResolver()?.SelectedWorkspace;
        if (workspace == null) return false;

        return Scope switch
        {
            BroadcastScope.ActivePane
                => workspace.SelectedSurface?.FocusedPaneId == paneId,
            BroadcastScope.Selected
                => SelectedPaneIds.Contains(paneId),
            BroadcastScope.CurrentSurface
                => workspace.SelectedSurface != null
                    && workspace.SelectedSurface.RootNode
                        .GetLeaves().Any(l => l.PaneId == paneId),
            BroadcastScope.Workspace
                => workspace.Surfaces.Any(s =>
                    s.RootNode.GetLeaves().Any(l => l.PaneId == paneId)),
            BroadcastScope.ClaudeLocal
                => _kindCache.GetValueOrDefault(paneId)
                    == AgentDetector.PaneAgentKind.LocalClaude,
            BroadcastScope.ClaudeSsh
                => _kindCache.GetValueOrDefault(paneId)
                    == AgentDetector.PaneAgentKind.SshSession,
            BroadcastScope.ClaudeAll
                => _kindCache.GetValueOrDefault(paneId)
                    is AgentDetector.PaneAgentKind.LocalClaude
                    or AgentDetector.PaneAgentKind.SshSession,
            _ => false,
        };
    }

    /// <summary>
    /// Rebuilds <see cref="SurfaceGroups"/> from the current workspace's
    /// surfaces. Keeps the manual selection set pruned so closed panes
    /// don't keep their broadcast subscription.
    /// </summary>
    public void RefreshAvailablePanes()
    {
        var main = _mainResolver();
        var workspace = main?.SelectedWorkspace;

        var keepIds = new HashSet<string>();
        SurfaceGroups.Clear();

        if (workspace != null)
        {
            int surfaceIndex = 1;
            foreach (var surface in workspace.Surfaces)
            {
                var group = new BroadcastSurfaceGroup(surface.Name);

                int paneIndex = 1;
                foreach (var leaf in surface.RootNode.GetLeaves())
                {
                    if (string.IsNullOrEmpty(leaf.PaneId)) continue;
                    var paneId = leaf.PaneId;

                    surface.Surface.PaneCustomNames.TryGetValue(paneId, out var custom);
                    var displayName = string.IsNullOrWhiteSpace(custom)
                        ? $"Pane {paneIndex}"
                        : custom!;

                    group.Panes.Add(new BroadcastPaneSelection(this, paneId, displayName));
                    keepIds.Add(paneId);
                    paneIndex++;
                }

                SurfaceGroups.Add(group);
                surfaceIndex++;
            }
        }

        bool prunedAny = SelectedPaneIds.RemoveWhere(id => !keepIds.Contains(id)) > 0;

        // Drop classification entries for panes that vanished too.
        var toRemoveKinds = _kindCache.Keys.Where(id => !keepIds.Contains(id)).ToList();
        foreach (var k in toRemoveKinds) _kindCache.Remove(k);

        foreach (var entry in SurfaceGroups.SelectMany(g => g.Panes))
            entry.RaiseIsCheckedChanged();

        RefreshTargetSummary();
        if (prunedAny)
            SelectionChanged?.Invoke();
    }

    public void RefreshTargetSummary()
    {
        var count = ResolveTargetSessions().Count();
        TargetSummary = count == 0 ? "no targets" : $"{count} pane{(count == 1 ? "" : "s")}";
    }

    internal void OnSelectionToggled()
    {
        RefreshTargetSummary();
        SelectionChanged?.Invoke();
    }

    /// <summary>
    /// Kicks off a background WMI walk of every pane's process tree to
    /// classify Claude-local vs SSH-session. Only runs when the bar is
    /// visible AND a Claude scope is active — WMI per-pane is expensive,
    /// no point burning it when the result wouldn't change anything visible.
    /// </summary>
    private void MaybeStartKindRefresh()
    {
        if (!IsVisible) return;
        if (Scope != BroadcastScope.ClaudeLocal
            && Scope != BroadcastScope.ClaudeSsh
            && Scope != BroadcastScope.ClaudeAll)
            return;

        _kindRefreshCts?.Cancel();
        _kindRefreshCts = new CancellationTokenSource();
        var ct = _kindRefreshCts.Token;

        var workspace = _mainResolver()?.SelectedWorkspace;
        if (workspace == null) return;

        // Capture per-pane info to classify outside the UI lock. For each
        // pane: pid is nonzero when the ConPTY child runs locally inside
        // cmuxw; null/0 means daemon-backed (the actual child PID lives in
        // cmux-daemon.exe and we have to ask the daemon for the kind).
        // bufferText is captured for local panes only — it lets
        // AgentDetector disambiguate "SSH carrying Claude" from "bare SSH"
        // by looking at the rendered output for Claude TUI markers.
        var snapshots = new List<(string paneId, int localPid, string? bufferText, string? announcedAgent)>();
        foreach (var surface in workspace.Surfaces)
        {
            foreach (var leaf in surface.RootNode.GetLeaves())
            {
                if (string.IsNullOrEmpty(leaf.PaneId)) continue;
                var session = surface.GetSession(leaf.PaneId);
                if (session == null) continue;

                string? bufferText = null;
                if ((session.ProcessId ?? 0) > 0)
                {
                    try
                    {
                        var snap = session.Buffer.CreateSnapshot(maxScrollbackLines: 500);
                        bufferText = string.Join('\n',
                            snap.ScrollbackLines.Concat(snap.ScreenLines));
                    }
                    catch { /* snapshot failure is non-fatal */ }
                }

                snapshots.Add((leaf.PaneId, session.ProcessId ?? 0, bufferText, session.AnnouncedAgent));
            }
        }

        Task.Run(async () =>
        {
            foreach (var (paneId, localPid, bufferText, announcedAgent) in snapshots)
            {
                if (ct.IsCancellationRequested) return;

                AgentDetector.PaneAgentKind kind;
                if (localPid > 0)
                {
                    kind = AgentDetector.ClassifyPane(localPid, bufferText, announcedAgent);
                }
                else if (!string.IsNullOrEmpty(announcedAgent))
                {
                    // Daemon-backed pane that already got an OSC 1338 announce
                    // forwarded back through the daemon → cmuxw event path
                    // (or replayed via DaemonSessionInfo on reconnect). The
                    // announce alone is enough — no need to RPC the daemon.
                    kind = AgentDetector.ClassifyPane(0, null, announcedAgent);
                }
                else
                {
                    // Daemon-backed pane — ask the daemon. Failures
                    // (disconnect, timeout, missing) fall through to
                    // Unknown so the visualization simply leaves the pane
                    // un-targeted instead of hiding the bar entirely.
                    kind = AgentDetector.PaneAgentKind.Unknown;
                    try
                    {
                        var daemon = App.DaemonClient;
                        if (daemon.IsConnected)
                        {
                            var raw = await daemon.ClassifyPaneAsync(paneId).ConfigureAwait(false);
                            if (!string.IsNullOrEmpty(raw)
                                && Enum.TryParse<AgentDetector.PaneAgentKind>(raw, out var parsed))
                            {
                                kind = parsed;
                            }
                        }
                    }
                    catch { /* swallow — Unknown is the safe default */ }
                }

                if (ct.IsCancellationRequested) return;
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    _kindCache[paneId] = kind;
                    RefreshTargetSummary();
                    SelectionChanged?.Invoke();
                }));
            }
        }, ct);
    }

    private IEnumerable<TerminalSession> ResolveTargetSessions()
    {
        var main = _mainResolver();
        var workspace = main?.SelectedWorkspace;
        if (workspace == null) yield break;

        switch (Scope)
        {
            case BroadcastScope.ActivePane:
                {
                    var surface = workspace.SelectedSurface;
                    var paneId = surface?.FocusedPaneId;
                    if (surface == null || string.IsNullOrEmpty(paneId)) yield break;
                    var session = surface.GetSession(paneId);
                    if (session != null) yield return session;
                    break;
                }
            case BroadcastScope.CurrentSurface:
                {
                    var surface = workspace.SelectedSurface;
                    if (surface == null) yield break;
                    foreach (var leaf in surface.RootNode.GetLeaves())
                    {
                        if (string.IsNullOrEmpty(leaf.PaneId)) continue;
                        var session = surface.GetSession(leaf.PaneId);
                        if (session != null) yield return session;
                    }
                    break;
                }
            case BroadcastScope.Workspace:
                {
                    foreach (var surface in workspace.Surfaces)
                    {
                        foreach (var leaf in surface.RootNode.GetLeaves())
                        {
                            if (string.IsNullOrEmpty(leaf.PaneId)) continue;
                            var session = surface.GetSession(leaf.PaneId);
                            if (session != null) yield return session;
                        }
                    }
                    break;
                }
            case BroadcastScope.Selected:
                {
                    if (SelectedPaneIds.Count == 0) yield break;
                    foreach (var surface in workspace.Surfaces)
                    {
                        foreach (var leaf in surface.RootNode.GetLeaves())
                        {
                            if (string.IsNullOrEmpty(leaf.PaneId)) continue;
                            if (!SelectedPaneIds.Contains(leaf.PaneId)) continue;
                            var session = surface.GetSession(leaf.PaneId);
                            if (session != null) yield return session;
                        }
                    }
                    break;
                }
            case BroadcastScope.ClaudeLocal:
            case BroadcastScope.ClaudeSsh:
            case BroadcastScope.ClaudeAll:
                {
                    foreach (var surface in workspace.Surfaces)
                    {
                        foreach (var leaf in surface.RootNode.GetLeaves())
                        {
                            if (string.IsNullOrEmpty(leaf.PaneId)) continue;
                            var session = surface.GetSession(leaf.PaneId);
                            if (session == null) continue;

                            // Prefer the cached classification when the
                            // background refresh has already populated it
                            // (visualization stays in sync with Submit).
                            // Falls back to a synchronous classify when the
                            // cache is empty AND we have a local PID. For
                            // daemon-backed panes (no local PID) we have to
                            // wait for MaybeStartKindRefresh's async daemon
                            // RPC to fill the cache — Submit on a freshly
                            // opened bar will simply skip them on the first
                            // Send and pick them up on the next.
                            var cached = _kindCache.GetValueOrDefault(
                                leaf.PaneId, AgentDetector.PaneAgentKind.Unknown);
                            if (cached == AgentDetector.PaneAgentKind.Unknown)
                            {
                                // Take whatever signals are cheaply available
                                // for a synchronous reclassify. The OSC 1338
                                // announce (if any) is on the session object
                                // itself and is authoritative when present.
                                var pid = session.ProcessId ?? 0;
                                var announce = session.AnnouncedAgent;
                                if (pid > 0 || !string.IsNullOrEmpty(announce))
                                {
                                    cached = AgentDetector.ClassifyPane(pid, bufferSnapshot: null, announcedAgent: announce);
                                    _kindCache[leaf.PaneId] = cached;
                                }
                            }

                            bool match = Scope switch
                            {
                                BroadcastScope.ClaudeLocal
                                    => cached == AgentDetector.PaneAgentKind.LocalClaude,
                                BroadcastScope.ClaudeSsh
                                    => cached == AgentDetector.PaneAgentKind.SshSession,
                                BroadcastScope.ClaudeAll
                                    => cached is AgentDetector.PaneAgentKind.LocalClaude
                                              or AgentDetector.PaneAgentKind.SshSession,
                                _ => false,
                            };
                            if (match) yield return session;
                        }
                    }
                    break;
                }
        }
    }
}

/// <summary>Surface-grouped pane list shown in the Panes popup. Each
/// surface (terminal tab) is rendered as a header followed by its
/// pane checkboxes.</summary>
public partial class BroadcastSurfaceGroup : ObservableObject
{
    public string SurfaceName { get; }
    public ObservableCollection<BroadcastPaneSelection> Panes { get; } = [];

    public BroadcastSurfaceGroup(string surfaceName)
    {
        SurfaceName = surfaceName;
    }
}

public partial class BroadcastPaneSelection : ObservableObject
{
    private readonly BroadcastInputViewModel _owner;
    public string PaneId { get; }
    public string Label { get; }

    public BroadcastPaneSelection(BroadcastInputViewModel owner, string paneId, string label)
    {
        _owner = owner;
        PaneId = paneId;
        Label = label;
    }

    public bool IsChecked
    {
        get => _owner.SelectedPaneIds.Contains(PaneId);
        set
        {
            bool current = _owner.SelectedPaneIds.Contains(PaneId);
            if (current == value) return;

            if (value) _owner.SelectedPaneIds.Add(PaneId);
            else _owner.SelectedPaneIds.Remove(PaneId);

            OnPropertyChanged();
            _owner.OnSelectionToggled();
        }
    }

    /// <summary>Forces a refresh after SelectedPaneIds is mutated externally
    /// (e.g. RefreshAvailablePanes prunes a stale id).</summary>
    public void RaiseIsCheckedChanged() => OnPropertyChanged(nameof(IsChecked));
}
