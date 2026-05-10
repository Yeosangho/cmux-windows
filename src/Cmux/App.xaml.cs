using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Cmux.Core.Config;
using Cmux.Core.IPC;
using Cmux.Core.Services;
using Cmux.Services;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Cmux;

public partial class App : Application
{
    private NamedPipeServer? _pipeServer;

    public static NotificationService NotificationService { get; } = new();
    public static NamedPipeServer? PipeServer { get; private set; }
    public static SnippetService SnippetService { get; } = new();
    public static CommandLogService CommandLogService { get; } = new();
    public static AgentConversationStoreService AgentConversationStore { get; } = new();
    public static AgentRuntimeService AgentRuntime { get; } = new();
    public static DaemonClient DaemonClient { get; } = new();
    public static Task<bool> DaemonConnectTask { get; private set; } = Task.FromResult(false);

    /// <summary>
    /// Volatile mirror of MainWindow.IsActive + WindowState — read from the
    /// PTY thread (where NotificationReceived events fire) without crossing
    /// onto the UI thread. Updated from MainWindow's Activated /
    /// Deactivated / StateChanged events. Used to suppress notifications
    /// for the focused pane when the user is already looking at it.
    /// </summary>
    private static volatile bool _isMainWindowForeground;
    public static bool IsMainWindowForeground => _isMainWindowForeground;
    public static void SetMainWindowForeground(bool foreground)
        => _isMainWindowForeground = foreground;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Defer Gen2 GC compaction as long as the runtime can manage it.
        // SustainedLowLatency tells the GC to prefer growing the heap over
        // doing blocking compactions that show up as 30–80ms typing-lag
        // spikes during heavy Claude streaming. Gen0/Gen1 still run as
        // normal (cheap, sub-ms). Trade-off: working-set may creep upward
        // over a long session — acceptable for an interactive shell that
        // is restarted occasionally; if it becomes a real problem we can
        // schedule a manual GC.Collect() during idle.
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;

        // Add global exception handlers to diagnose crashes
        DispatcherUnhandledException += (s, args) =>
        {
            System.Diagnostics.Debug.WriteLine($"[CRASH] DispatcherUnhandledException: {args.Exception}");
            System.Windows.MessageBox.Show($"Unexpected error: {args.Exception.Message}\n\n{args.Exception.StackTrace}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            System.Diagnostics.Debug.WriteLine($"[CRASH] UnhandledException: {ex}");
            System.Windows.MessageBox.Show($"Fatal error: {ex?.Message}\n\n{ex?.StackTrace}", "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        // Single-instance toast forwarding. If another cmux is already running
        // (named pipe `cmux` is being served), this process is either a
        // duplicate launch or — more importantly — a forwarder spawned by the
        // Toolkit COM activator after the user clicked a toast that the
        // existing process couldn't intercept directly. Capture the toast
        // activation argument, hand it to the running cmux via the same pipe
        // CLI uses, and exit. Without this, the user's click only ever runs
        // a transient new process that has no panes / sessions to navigate to.
        //
        // Use Environment.Exit instead of WPF Shutdown(): with
        // StartupUri set to Views/MainWindow.xaml, returning from OnStartup
        // still triggers MainWindow creation, which then NullRefs because we
        // skipped pipe-server init. Hard-exit avoids that. Wrapped in
        // try/catch so any unexpected failure here can't break the primary
        // launch path — we'd rather have one running cmux than zero.
        try
        {
            if (TryForwardToastAndExit())
                Environment.Exit(0);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Forwarder] {ex}");
        }

        // Start the named pipe server for CLI communication
        _pipeServer = new NamedPipeServer();
        PipeServer = _pipeServer;
        _pipeServer.Start();

        // Daemon connect: try existing daemon first, then start one if needed.
        // Sessions wait for this task before deciding local vs daemon mode.
        DaemonConnectTask = Task.Run(() =>
        {
            DaemonLog("[App] Phase 1: Quick daemon check (300ms)...");
            if (DaemonClient.TryConnect(300))
            {
                DaemonLog("[App] Phase 1: Daemon connected!");
                DaemonClient.RaiseConnected();
                return true;
            }
            DaemonLog("[App] Phase 1: Daemon not available, starting daemon...");

            var connected = DaemonClient.StartDaemonAndConnect();
            DaemonLog(connected
                ? "[App] Phase 2: Daemon started and connected"
                : "[App] Phase 2: Daemon failed to start");
            if (connected) DaemonClient.RaiseConnected();
            return connected;
        });

        // Diagnostic log path. Every step of the toast path appends here so
        // we can tell whether clicks reach our activator. Located at
        // %LOCALAPPDATA%\cmuxw-toast.log
        var toastLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "cmuxw-toast.log");
        // Ensure the log file is UTF-8 with BOM so PowerShell's Get-Content
        // auto-detects the encoding instead of falling back to CP949 on
        // Korean Windows (which mangles Hangul to mojibake on display).
        try
        {
            if (!File.Exists(toastLogPath))
            {
                File.WriteAllBytes(toastLogPath, new byte[] { 0xEF, 0xBB, 0xBF });
            }
        }
        catch { }
        void LogToast(string msg)
        {
            try
            {
                File.AppendAllText(toastLogPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n",
                    Encoding.UTF8);
            }
            catch { }
        }
        LogToast($"App startup; pid={Environment.ProcessId}; exe={Environment.ProcessPath}");

        // NotificationService gets PTY-driven AddNotification calls from the
        // background output-processing thread. Marshal them onto the WPF
        // dispatcher so the bound ListBox doesn't blow up with
        // ItemContainerGenerator cross-thread errors when the second
        // notification of a session arrives while the panel is rendered.
        // Background priority so notification book-keeping never preempts
        // keyboard input (Render / Input priority work runs first).
        NotificationService.UIMarshal = action =>
            Current.Dispatcher.BeginInvoke(action, System.Windows.Threading.DispatcherPriority.Background);

        // Toast click activation — subscribe BEFORE any toast is shown so
        // the toolkit registers the AumId and COM activator with this
        // subscription in mind. OnActivated fires on a background thread,
        // so we marshal to the UI dispatcher before touching the window /
        // view model.
        ToastNotificationManagerCompat.OnActivated += toastArgs =>
        {
            LogToast($"OnActivated raw='{toastArgs.Argument}'");
            Current.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    var args = ToastArguments.Parse(toastArgs.Argument);
                    var action = args.Get("action");
                    LogToast($"OnActivated action='{action}'");
                    if (action != "jumpToNotification") return;
                    var notificationId = args.Get("notificationId");
                    if (string.IsNullOrEmpty(notificationId)) return;

                    // Foreground the window. The toolkit's COM activator
                    // delivers this callback even when our window was
                    // background / minimized; we have to lift it explicitly.
                    var window = Current.MainWindow;
                    if (window != null)
                    {
                        if (window.WindowState == WindowState.Minimized)
                            window.WindowState = WindowState.Normal;
                        window.Show();
                        window.Activate();
                        // Topmost flicker forces foreground when activated
                        // from a background process (Action Center is its
                        // own host window).
                        window.Topmost = true;
                        window.Topmost = false;
                    }

                    if (window?.DataContext is ViewModels.MainViewModel vm)
                    {
                        LogToast($"Dispatching JumpToNotification id={notificationId}");
                        vm.JumpToNotification(notificationId);
                    }
                }
                catch (Exception ex)
                {
                    LogToast($"OnActivated handler failed: {ex.GetType().Name}: {ex.Message}");
                }
            });
        };

        // Hard suppress: drop a notification entirely (no list entry, no
        // toast, no sound) when the user is already looking at the source
        // pane in a foreground cmuxw. Conditions match the toast-suppress
        // rule below — when toast wouldn't fire, the list entry shouldn't
        // either. Runs on the UI thread (inside NotificationService.Apply
        // via UIMarshal) so reading UI-thread DependencyProperties is safe.
        NotificationService.ShouldSuppress = notification =>
        {
            var window = Current.MainWindow;
            if (window is not { IsActive: true, WindowState: not WindowState.Minimized })
                return false;
            if (window.DataContext is not ViewModels.MainViewModel vm)
                return false;

            var ws = vm.SelectedWorkspace;
            var surface = ws?.SelectedSurface;
            if (ws == null || surface == null) return false;
            return string.Equals(ws.Workspace.Id, notification.WorkspaceId, StringComparison.Ordinal)
                && string.Equals(surface.Surface.Id, notification.SurfaceId, StringComparison.Ordinal)
                && string.Equals(surface.FocusedPaneId, notification.PaneId, StringComparison.Ordinal);
        };

        // NotificationAdded only fires for items that pass ShouldSuppress.
        // Toast skip / sound logic stays here for the "user is looking at
        // cmuxw but focus is on a sibling control" gray-zone, where we
        // still want the audible cue + an Action Center entry but maybe
        // no Windows toast.
        NotificationService.NotificationAdded += notification =>
        {
            var mainWindow = Current.MainWindow;
            var vm = mainWindow?.DataContext as ViewModels.MainViewModel;

            // "Active pane" = cmuxw is the foreground app AND the pane the
            // notification came from is the workspace/surface's focused
            // pane. Only this case suppresses toasts — when the user has a
            // different app focused (browser, IDE, etc.) we fire the toast
            // so they actually find out something arrived. IsActive is the
            // discriminator: a visible-but-not-foreground cmuxw could be
            // off-screen, on another monitor the user isn't looking at,
            // or behind their browser.
            bool isActivePane = false;
            if (mainWindow is { IsActive: true, WindowState: not WindowState.Minimized } && vm != null)
            {
                var ws = vm.SelectedWorkspace;
                var surface = ws?.SelectedSurface;
                if (ws != null && surface != null
                    && string.Equals(ws.Workspace.Id, notification.WorkspaceId, StringComparison.Ordinal)
                    && string.Equals(surface.Surface.Id, notification.SurfaceId, StringComparison.Ordinal)
                    && string.Equals(surface.FocusedPaneId, notification.PaneId, StringComparison.Ordinal))
                {
                    isActivePane = true;
                }
            }

            // Always nudge the user with an in-app audible cue, even when
            // we suppress the Windows toast (because they're already on the
            // source pane) — gives feedback that something arrived without
            // depending on Action Center / Focus Assist / notification scheme.
            PlayNotificationSound();

            if (!isActivePane)
            {
                var workspaceName = vm?.Workspaces
                    .FirstOrDefault(w => w.Workspace.Id == notification.WorkspaceId)?.Name
                    ?? "Terminal";
                Services.ToastNotificationHelper.ShowToast(notification, workspaceName);
            }
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pipeServer?.Dispose();
        DaemonClient.Dispose();
        AgentRuntime.Dispose();
        base.OnExit(e);
    }

    internal static void DaemonLog(string message) => DaemonClient.LogDaemon(message);

    /// <summary>
    /// Plays a notification sound by directly invoking winmm.dll's PlaySound
    /// API on a Windows Media .wav file. Bypasses the user's "Sounds" scheme
    /// entries (which can be mapped to "(None)" silently), the per-app
    /// notification sound setting, AND the .NET SoundPlayer abstraction
    /// layer (which has been observed to do nothing on some configurations).
    /// As long as the system audio device is on, this beeps.
    /// </summary>
    [DllImport("winmm.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);

    private const uint SND_FILENAME = 0x00020000;
    private const uint SND_ASYNC = 0x00000001;
    private const uint SND_NODEFAULT = 0x00000002;

    private static string? _resolvedSoundPath;
    private static void PlayNotificationSound()
    {
        if (_resolvedSoundPath == null)
        {
            var mediaDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Media");
            foreach (var name in new[]
            {
                "Windows Notify System Generic.wav",
                "Windows Notify Messaging.wav",
                "Windows Notify.wav",
                "Windows Notify Email.wav",
                "notify.wav",
                "Windows Ding.wav",
                "chimes.wav",
            })
            {
                var p = Path.Combine(mediaDir, name);
                if (File.Exists(p)) { _resolvedSoundPath = p; break; }
            }
            if (_resolvedSoundPath == null) _resolvedSoundPath = string.Empty;
        }

        try
        {
            if (!string.IsNullOrEmpty(_resolvedSoundPath))
            {
                bool ok = PlaySound(_resolvedSoundPath, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
                if (ok) return;
                System.Diagnostics.Debug.WriteLine($"[Sound] PlaySound failed for {_resolvedSoundPath}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Sound] {ex}");
        }
        // Last-resort fallback.
        try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
    }

    /// <summary>
    /// Returns true if another cmux instance is already running and this
    /// process should exit. When toast activation argument is captured, it is
    /// forwarded to the existing instance via the cmux named pipe so the
    /// user's click reaches the process that actually owns the panes.
    /// </summary>
    private static bool TryForwardToastAndExit()
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "cmuxw-toast.log");
        void Log(string msg)
        {
            try { File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] FWD {msg}\n"); }
            catch { }
        }

        // Probe: is the cmux pipe currently serving connections?
        bool existingInstance;
        try
        {
            using var probe = new NamedPipeClientStream(".", "cmux", PipeDirection.Out);
            probe.Connect(200);
            existingInstance = probe.IsConnected;
        }
        catch
        {
            existingInstance = false;
        }
        if (!existingInstance)
        {
            Log("no existing instance — proceeding as primary");
            return false;
        }
        Log($"existing instance detected (pid={Environment.ProcessId})");

        // Wait briefly for Toolkit to hand us a toast activation argument.
        // If none arrives in the budget window, we're a plain duplicate
        // launch — exit silently.
        var captured = new System.Threading.ManualResetEventSlim(false);
        string capturedArgument = string.Empty;
        ToastNotificationManagerCompat.OnActivated += args =>
        {
            capturedArgument = args.Argument ?? string.Empty;
            Log($"OnActivated in forwarder: '{capturedArgument}'");
            captured.Set();
        };
        captured.Wait(2000);

        if (string.IsNullOrEmpty(capturedArgument))
        {
            Log("no toast argument received — exiting as duplicate");
            return true; // Exit anyway, we're a duplicate.
        }

        try
        {
            using var pipe = new NamedPipeClientStream(".", "cmux", PipeDirection.InOut);
            pipe.Connect(2000);
            using var writer = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8);
            var escaped = capturedArgument
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"");
            writer.WriteLine($"TOAST.NAVIGATE arg=\"{escaped}\"");
            // Read the response line to make sure the server actually received it
            var response = reader.ReadLine();
            Log($"forwarded; response={response}");
        }
        catch (Exception ex)
        {
            Log($"forward failed: {ex.GetType().Name}: {ex.Message}");
        }
        return true;
    }
}
