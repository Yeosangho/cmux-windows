using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Cmux.Core.Models;
using Cmux.Core.Config;
using Cmux.Core.Terminal;
using Cmux.ViewModels;
using Cmux.Views;

namespace Cmux.Controls;

/// <summary>
/// Recursively renders a SplitNode tree as nested Grid panels with
/// GridSplitters for resizable dividers. Leaf nodes contain TerminalControl instances.
/// </summary>
public class SplitPaneContainer : ContentControl
{
    private SurfaceViewModel? _surface;
    private readonly Dictionary<string, TerminalControl> _terminalCache = [];
    // One TextBox proxy per pane, paired with the TerminalControl. Cached so
    // its IME composition state and event subscriptions persist across
    // tree rebuilds (layout change / zoom toggle).
    private readonly Dictionary<string, TextBox> _imeProxyCache = [];
    // Outer Border per pane — cached so UpdateFocusState can repaint the
    // active-pane outline without rebuilding the whole subtree.
    private readonly Dictionary<string, Border> _paneBorderCache = [];

    public event Action? SearchRequested;

    private static SolidColorBrush GetThemeBrush(string key) =>
        Application.Current.Resources[key] as SolidColorBrush ?? Brushes.Transparent;

    public SplitPaneContainer()
    {
        Background = Brushes.Transparent;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SurfaceViewModel oldSurface)
        {
            oldSurface.PropertyChanged -= OnSurfacePropertyChanged;
        }

        // Clear terminal cache when switching surfaces/workspaces
        // This prevents reusing terminals from a different workspace
        _terminalCache.Clear();
        _imeProxyCache.Clear();
        _paneBorderCache.Clear();

        _surface = e.NewValue as SurfaceViewModel;

        if (_surface != null)
        {
            _surface.PropertyChanged += OnSurfacePropertyChanged;
            Rebuild();
        }
        else
        {
            Content = null;
        }
    }

    private void OnSurfacePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SurfaceViewModel.RootNode)
            or nameof(SurfaceViewModel.IsZoomed))
        {
            Dispatcher.BeginInvoke(Rebuild);
        }
        else if (e.PropertyName is nameof(SurfaceViewModel.FocusedPaneId))
        {
            Dispatcher.BeginInvoke(UpdateFocusState);
        }
    }

    /// <summary>
    /// Updates only focus-related visual state on cached terminals without
    /// rebuilding the entire UI tree.
    /// </summary>
    private void UpdateFocusState()
    {
        if (_surface == null) return;

        // In zoom mode, focus change may require rebuild if the zoomed pane changed
        if (_surface.IsZoomed)
        {
            Rebuild();
            return;
        }

        foreach (var (paneId, terminal) in _terminalCache)
        {
            var focused = paneId == _surface.FocusedPaneId;
            terminal.IsPaneFocused = focused;
            if (_paneBorderCache.TryGetValue(paneId, out var border))
            {
                border.BorderBrush = focused
                    ? GetThemeBrush("FocusedPaneBorderBrush")
                    : GetThemeBrush("BorderBrush");
            }
        }
    }

    private void Rebuild()
    {
        if (_surface == null) return;

        // Zoom mode: show only the focused pane full-size
        if (_surface.IsZoomed && _surface.FocusedPaneId != null)
        {
            var focusedNode = _surface.RootNode.FindNode(_surface.FocusedPaneId);
            if (focusedNode != null)
            {
                Content = BuildLeaf(focusedNode);
                RestoreKeyboardFocusToFocusedPane();
                return;
            }
        }

        Content = BuildNode(_surface.RootNode);
        RestoreKeyboardFocusToFocusedPane();
    }

    /// <summary>
    /// After a tree rebuild (e.g. layout change), the previously-focused
    /// TerminalControl is reused but its visual parent has been replaced, so
    /// WPF keyboard focus is on whatever element triggered the rebuild
    /// (typically the layout toolbar button). Without explicit restoration
    /// the user has to click the pane again before typing — and even after
    /// a single Focus() call the WPF IME context can stay un-rebound, so
    /// English passes through but Korean / Japanese / Chinese silently
    /// drops the first keystrokes. The two-stage focus dance below is the
    /// programmatic equivalent of the workaround users discover (click
    /// another pane, then click back) and forces IME re-association.
    /// </summary>
    private void RestoreKeyboardFocusToFocusedPane()
    {
        var focusedPaneId = _surface?.FocusedPaneId;
        if (string.IsNullOrEmpty(focusedPaneId)) return;
        if (!_terminalCache.TryGetValue(focusedPaneId, out var terminal)) return;

        // The IME-eligible target is the hidden TextBox proxy paired with
        // the terminal — that's where Windows TSF actually composes Korean.
        _imeProxyCache.TryGetValue(focusedPaneId, out var imeProxy);

        // Both stages run at Background priority — fires immediately after
        // the current input batch drains, but is below Input priority so
        // it does not share the input pump's queue. Earlier code used
        // DispatcherPriority.Input and produced a TextStore.GrantLockWorker
        // FailFast on the very first proxy focus after a rebuild (the
        // staging area would still pump pending IME events into a
        // mid-attach TextStore). ApplicationIdle was tested too but caused
        // visible focus lag, so Background is the right tradeoff.
        Dispatcher.BeginInvoke(() =>
        {
            if (!terminal.IsLoaded) return;

            // Stage 1: park focus on the container so any stale IME binding
            // from before the rebuild is broken.
            Focusable = true;
            Focus();

            // Stage 2: keyboard focus to the IME proxy if we have one,
            // otherwise the terminal control as a fallback.
            Dispatcher.BeginInvoke(() =>
            {
                if (imeProxy != null && imeProxy.IsLoaded && !imeProxy.IsKeyboardFocused)
                    Keyboard.Focus(imeProxy);
                else if (terminal.IsLoaded && !terminal.IsKeyboardFocused)
                    Keyboard.Focus(terminal);
            }, System.Windows.Threading.DispatcherPriority.Background);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private UIElement BuildNode(SplitNode node)
    {
        if (node.IsLeaf)
        {
            return BuildLeaf(node);
        }

        return BuildSplit(node);
    }

    private UIElement BuildLeaf(SplitNode node)
    {
        if (node.PaneId == null)
            return new Border { Background = Brushes.Transparent };

        var paneId = node.PaneId; // Capture for closures

        // Reuse cached terminal if available (preserves session and scroll position)
        if (!_terminalCache.TryGetValue(paneId, out var terminal))
        {
            terminal = new TerminalControl();
            _terminalCache[paneId] = terminal;
        }
        else
        {
            // Detach from old parent before reusing.
            // Parent could be Grid (current layout pairs terminal with IME
            // proxy in a Grid), DockPanel (older layout), or Border.
            var oldParent = System.Windows.Media.VisualTreeHelper.GetParent(terminal) as FrameworkElement;

            if (oldParent is Grid oldGrid)
            {
                oldGrid.Children.Remove(terminal);
            }
            else if (oldParent is DockPanel dockPanel)
            {
                dockPanel.Children.Remove(terminal);
            }
            else if (oldParent is Border border)
            {
                border.Child = null;
            }
            
            // Clear old event handlers to prevent memory leaks and wrong callbacks
            terminal.ClearEventHandlers();
        }

        // Wire up event handlers with closures capturing the current pane ID
        terminal.FocusRequested += () => _surface?.FocusPane(paneId);
        terminal.CommandInterceptRequested += command => _surface?.TryHandlePaneCommand(paneId, command) == true;
        terminal.CommandSubmitted += command => _surface?.RegisterCommandSubmission(paneId, command);
        terminal.ClearRequested += () => _surface?.CapturePaneTranscript(paneId, "clear-terminal");
        terminal.SplitRequested += dir =>
        {
            _surface?.FocusPane(paneId);
            _surface?.SplitFocused(dir);
        };
        terminal.ZoomRequested += () => _surface?.ToggleZoom();
        terminal.ClosePaneRequested += () => _surface?.ClosePane(paneId);
        terminal.SearchRequested += () => SearchRequested?.Invoke();
        terminal.IsPaneFocused = paneId == _surface?.FocusedPaneId;
        terminal.IsSurfaceZoomed = _surface?.IsZoomed == true;

        // Attach the terminal session
        var session = _surface?.GetSession(paneId);
        if (session != null)
            terminal.AttachSession(session);

        // Get pane title (custom name takes precedence over shell title)
        var title = _surface?.GetPaneTitle(paneId, session?.Title) ?? "Terminal";

        // Create panel with header
        var panel = new DockPanel { LastChildFill = true };

        // Header bar with title and close button
        var header = new Border
        {
            Background = GetThemeBrush("SidebarItemHoverBrush"),
            Height = 22,
            Padding = new Thickness(8, 2, 8, 2),
        };

        var headerMenu = new ContextMenu();
        var renamePane = new MenuItem { Header = "Rename Pane" };
        renamePane.Click += (_, _) =>
        {
            var currentName = _surface?.GetPaneTitle(paneId, session?.Title) ?? "Terminal";
            var prompt = new TextPromptWindow(
                title: "Rename Pane",
                message: "Set a custom name for this pane.",
                defaultValue: currentName)
            {
                Owner = Window.GetWindow(this),
            };

            if (prompt.ShowDialog() == true && !string.IsNullOrWhiteSpace(prompt.ResponseText))
                _surface?.SetPaneCustomName(paneId, prompt.ResponseText);
        };
        headerMenu.Items.Add(renamePane);

        var resetPaneName = new MenuItem { Header = "Reset Pane Name" };
        resetPaneName.Click += (_, _) => _surface?.SetPaneCustomName(paneId, string.Empty);
        headerMenu.Items.Add(resetPaneName);

        header.ContextMenu = headerMenu;

        DockPanel.SetDock(header, Dock.Top);

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) }); // Focus indicator
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Title
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) }); // Close button

        // Focus indicator (shows which pane is focused)
        var focusIndicator = new Border
        {
            Width = 3,
            Height = 12,
            CornerRadius = new CornerRadius(1.5),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = terminal.IsPaneFocused
                ? GetThemeBrush("AccentBrush")
                : GetThemeBrush("DividerBrush"),
        };
        Grid.SetColumn(focusIndicator, 0);

        // Title text
        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 11,
            Foreground = GetThemeBrush("ForegroundBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(titleText, 1);

        // Close button
        var closeButton = new Button
        {
            Content = "\u2715",
            FontSize = 10,
            Width = 18,
            Height = 18,
            Background = Brushes.Transparent,
            Foreground = GetThemeBrush("ForegroundDimBrush"),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Close pane",
        };
        closeButton.Click += (s, e) => _surface?.ClosePane(paneId);
        Grid.SetColumn(closeButton, 2);

        headerGrid.Children.Add(focusIndicator);
        headerGrid.Children.Add(titleText);
        headerGrid.Children.Add(closeButton);
        header.Child = headerGrid;

        panel.Children.Add(header);

        // Hidden TextBox proxy for Windows TSF (Text Services Framework)
        // integration. WPF's TextBox is the canonical IME-eligible surface,
        // so the OS Korean / Japanese / Chinese IMEs compose syllables
        // internally and only deliver the finalized result via TextChanged.
        // The TerminalControl itself is a custom-drawn FrameworkElement
        // without a TSF text store, which is why the previous custom
        // HangulComposer approach kept hitting edge cases. We offload
        // composition to TextBox and treat TerminalControl as pure render.
        //
        // Layout: TextBox sits in a Grid alongside TerminalControl, sized to
        // 1×1 with Opacity=0 and IsHitTestVisible=false so it is invisible
        // and mouse passes through to TerminalControl for selection / focus
        // clicks. Keyboard focus is forwarded TerminalControl → proxy on
        // FocusRequested.
        if (!_imeProxyCache.TryGetValue(paneId, out var imeProxy))
        {
            imeProxy = new TextBox
            {
                // Real size + on-screen position is mandatory: a 1×1 or
                // off-screen TextBox makes TSF think there's no valid input
                // target, and Korean IME falls back to committing the first
                // jamo immediately ("ㄱ" + "ㅏ" → "ㄱㅏ" instead of "가").
                // We keep it on-screen at the top-left of the pane,
                // visually invisible via Opacity / transparent brushes,
                // and IsHitTestVisible=false so mouse passes through.
                Width = 200,
                Height = 28,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 0, 0),
                Opacity = 0,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = Brushes.Transparent,
                CaretBrush = Brushes.Transparent,
                Padding = new Thickness(0),
                AcceptsReturn = false,
                AcceptsTab = false,
                Focusable = true,
                IsTabStop = false,
                IsHitTestVisible = false,
            };
            _imeProxyCache[paneId] = imeProxy;

            var capturedTerminal = terminal;

            // Diagnostic — surface what IME events actually fire so we can
            // tell whether TextInputUpdate is the right hook on this Windows
            // / IME / TextBox combo. Logs to %LOCALAPPDATA%\cmuxw-toast.log.
            var imeLogPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "cmuxw-toast.log");
            void LogIme(string msg)
            {
                try { System.IO.File.AppendAllText(imeLogPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] IME {msg}\n",
                    System.Text.Encoding.UTF8); }
                catch { }
            }

            // IME handling, mirroring xterm.js / VSCode terminal, following
            // the strict spec:
            //   1. Preedit (composing) NEVER goes to PTY — only commit does.
            //   2. Preedit is shown via TerminalControl's overlay layer
            //      (NOT by writing to TextBox.Text — that corrupts TSF and
            //      causes first-jamo separation).
            //   3. Two flags: isComposing (set Start, cleared on commit),
            //      suppressNextTextChanged (set on commit, cleared exactly
            //      once on the next TextChanged that fires from the Clear()).
            //   4. TextChanged is ONLY a fallback path for paste / legacy
            //      bulk commits — never the primary commit signal.
            InputMethod.SetIsInputMethodEnabled(imeProxy, true);
            InputMethod.SetPreferredImeConversionMode(imeProxy, ImeConversionModeValues.Native);

            bool isComposing = false;
            bool suppressNextTextChanged = false;

            imeProxy.AddHandler(TextCompositionManager.TextInputStartEvent,
                new TextCompositionEventHandler((_, _) =>
                {
                    LogIme("Start");
                    isComposing = true;
                    capturedTerminal.SetPreedit(string.Empty);
                }),
                handledEventsToo: true);

            imeProxy.AddHandler(TextCompositionManager.TextInputUpdateEvent,
                new TextCompositionEventHandler((_, e) =>
                {
                    var composing = e.TextComposition?.CompositionText
                                    ?? e.Text
                                    ?? string.Empty;
                    LogIme($"Update composing='{composing}'");
                    capturedTerminal.SetPreedit(composing);
                    // PTY write 절대 금지
                }),
                handledEventsToo: true);

            // PreviewTextInputUpdate as a fallback — some Windows builds
            // route IME composition through the tunneling preview only.
            imeProxy.AddHandler(TextCompositionManager.PreviewTextInputUpdateEvent,
                new TextCompositionEventHandler((_, e) =>
                {
                    var composing = e.TextComposition?.CompositionText
                                    ?? e.Text
                                    ?? string.Empty;
                    LogIme($"PreviewUpdate composing='{composing}'");
                    capturedTerminal.SetPreedit(composing);
                }),
                handledEventsToo: true);

            // commit 시점 — 유일한 PTY write 경로
            imeProxy.AddHandler(TextCompositionManager.TextInputEvent,
                new TextCompositionEventHandler((_, e) =>
                {
                    var committed = e.Text ?? e.TextComposition?.Text ?? string.Empty;
                    LogIme($"TextInput commit='{committed}'");
                    capturedTerminal.SetPreedit(string.Empty);
                    isComposing = false;
                    // 빈 commit은 TSF가 split / abort 시 phantom으로 발사
                    // 하는 것 — write 안 함.
                    if (string.IsNullOrEmpty(committed)) return;
                    capturedTerminal.WriteFromInputProxy(committed);
                    // ★ TextBox.Text는 절대 건드리지 않음. Clear()를 호출
                    // 하면 (동기든 deferred든) TSF가 진행 중인 다음 음절
                    // composition을 abort/split하여 빈 commit이나 자모
                    // 단독 commit이 발생함 ("안녕하세요 → 안ㅕ하요" 증상).
                    // Text가 누적되어도 우리는 e.Text만 사용하므로 PTY
                    // write 동작에 영향 없음.
                }),
                handledEventsToo: true);

            // TextChanged는 PTY write 경로에서 완전히 제외 + Text 건드리지
            // 않음. 그저 IME가 자체 관리하도록 둠.
            imeProxy.TextChanged += (s, e) =>
            {
                // 진단 로그만, 동작은 없음.
                if (!isComposing && !suppressNextTextChanged && imeProxy.Text.Length > 0)
                    LogIme($"TextChanged ignored len={imeProxy.Text.Length}");
                suppressNextTextChanged = false;
            };

            imeProxy.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.ImeProcessed) return;

                // Space: TextInput으로 안 오는 케이스가 있어 (한글 IME 환경)
                // 직접 PTY로 write. modifier 없는 순수 Space만.
                if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None)
                {
                    capturedTerminal.SetPreedit(string.Empty);
                    capturedTerminal.WriteFromInputProxy(" ");
                    e.Handled = true;
                    return;
                }

                // Ctrl+V (paste): TextChanged write 제거로 인한 보완.
                if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    try
                    {
                        var clip = Clipboard.GetText();
                        if (!string.IsNullOrEmpty(clip))
                        {
                            capturedTerminal.SetPreedit(string.Empty);
                            capturedTerminal.WriteFromInputProxy(clip);
                        }
                    }
                    catch { }
                    e.Handled = true;
                    return;
                }

                if (e.Key is Key.Enter or Key.Tab or Key.Up or Key.Down
                    or Key.Left or Key.Right or Key.Home or Key.End
                    or Key.PageUp or Key.PageDown or Key.Escape or Key.Back)
                {
                    capturedTerminal.SetPreedit(string.Empty);
                }
                capturedTerminal.HandleInputProxyKeyDown(e);
            };

            imeProxy.LostKeyboardFocus += (_, _) =>
            {
                isComposing = false;
                capturedTerminal.SetPreedit(string.Empty);
            };
        }
        else
        {
            // Detach from previous parent grid before re-adding.
            if (System.Windows.Media.VisualTreeHelper.GetParent(imeProxy) is Grid prevGrid)
                prevGrid.Children.Remove(imeProxy);
        }

        // Mouse / pane focus on the terminal moves keyboard focus to the
        // proxy so the IME composition target stays in sync with what the
        // user is "looking at". (Re-wired each BuildLeaf because
        // TerminalControl.ClearEventHandlers wipes its event subscribers.)
        //
        // Critical: defer the Focus() to Background priority. Calling it
        // synchronously while the input staging area is still draining
        // can recursively re-enter TextStore lock acquisition during the
        // proxy's first TextStore attach and trigger a WPF FailFast.
        var proxyForFocus = imeProxy;
        terminal.FocusRequested += () =>
        {
            if (proxyForFocus.IsKeyboardFocused) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (proxyForFocus.IsLoaded && !proxyForFocus.IsKeyboardFocused)
                    proxyForFocus.Focus();
            }, System.Windows.Threading.DispatcherPriority.Background);
        };

        var contentGrid = new Grid();
        contentGrid.Children.Add(terminal);
        contentGrid.Children.Add(imeProxy);
        panel.Children.Add(contentGrid);

        // Constant 2px on both states — varying the thickness would shift
        // the inner content by 1px every focus change, which is visually
        // jarring. UpdateFocusState only swaps the brush, never the layout.
        var paneBorder = new Border
        {
            Child = panel,
            BorderBrush = terminal.IsPaneFocused
                ? GetThemeBrush("FocusedPaneBorderBrush")
                : GetThemeBrush("BorderBrush"),
            BorderThickness = new Thickness(2),
        };
        _paneBorderCache[paneId] = paneBorder;
        return paneBorder;
    }


    private UIElement BuildSplit(SplitNode node)
    {
        var grid = new Grid();

        if (node.Direction == SplitDirection.Vertical)
        {
            // Left | Right
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(node.SplitRatio, GridUnitType.Star),
            });
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(4, GridUnitType.Pixel),
            });
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1 - node.SplitRatio, GridUnitType.Star),
            });

            if (node.First != null)
            {
                var first = BuildNode(node.First);
                Grid.SetColumn(first, 0);
                grid.Children.Add(first);
            }

            var splitter = new GridSplitter
            {
                Width = 4,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = FindResource("DividerBrush") as Brush ?? Brushes.Gray,
                Cursor = System.Windows.Input.Cursors.SizeWE,
            };
            Grid.SetColumn(splitter, 1);
            grid.Children.Add(splitter);

            if (node.Second != null)
            {
                var second = BuildNode(node.Second);
                Grid.SetColumn(second, 2);
                grid.Children.Add(second);
            }
        }
        else
        {
            // Top / Bottom
            grid.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(node.SplitRatio, GridUnitType.Star),
            });
            grid.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(4, GridUnitType.Pixel),
            });
            grid.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(1 - node.SplitRatio, GridUnitType.Star),
            });

            if (node.First != null)
            {
                var first = BuildNode(node.First);
                Grid.SetRow(first, 0);
                grid.Children.Add(first);
            }

            var splitter = new GridSplitter
            {
                Height = 4,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = FindResource("DividerBrush") as Brush ?? Brushes.Gray,
                Cursor = System.Windows.Input.Cursors.SizeNS,
            };
            Grid.SetRow(splitter, 1);
            grid.Children.Add(splitter);

            if (node.Second != null)
            {
                var second = BuildNode(node.Second);
                Grid.SetRow(second, 2);
                grid.Children.Add(second);
            }
        }

        return grid;
    }

    /// <summary>
    /// Updates settings for all cached terminal controls.
    /// </summary>
    public void UpdateAllTerminals(TerminalTheme theme, string fontFamily, int fontSize)
    {
        foreach (var terminal in _terminalCache.Values)
        {
            terminal.UpdateSettings(theme, fontFamily, fontSize);
        }
    }
}
