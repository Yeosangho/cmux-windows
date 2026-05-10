using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cmux.Core.Config;
using Cmux.Core.Models;
using Cmux.Core.Terminal;

namespace Cmux.Controls;

/// <summary>
/// WPF control that renders a TerminalBuffer and handles keyboard/mouse input.
/// Uses DrawingVisual for efficient rendering of the terminal cell grid.
/// Features: scrollback, URL detection, search highlights, mouse reporting, visual bell.
/// </summary>
public class TerminalControl : FrameworkElement
{
    private TerminalSession? _session;
    private readonly TerminalSelection _selection = new();
    private GhosttyTheme _theme;
    private DrawingVisual _visual;
    private Typeface _typeface;
    private double _cellWidth;
    private double _cellHeight;
    // Vertical offset from a row's top edge to the font baseline. GlyphRun
    // takes a baseline origin (not a top-left), so we measure once via a probe
    // FormattedText("M") to match the layout the rest of the renderer assumes.
    private double _cellBaseline;
    private double _fontSize;
    private int _cols;
    private int _rows;
    private bool _mouseDown;
    private int _scrollOffset; // Negative = scrolled into history, 0 = at bottom
    private bool _followOutput = true;
    private int _lastScrollbackCount;

    // Visibility-aware render gating. When the workspace this control belongs
    // to is not currently shown (e.g., the user switched to another workspace
    // and a different SurfaceView is in the ContentControl), this control's
    // session keeps streaming output and firing Redraw. Without gating, every
    // chunk becomes a Dispatcher.BeginInvoke that competes with the visible
    // workspace's renders and IME message processing — that's the cross-
    // workspace lag and Korean-IME breakage the user observed.
    //
    // _isVisibleSnapshot mirrors IsVisible (UI thread-only DepProp) so the
    // PTY read thread can check it without crossing threads. When we hit a
    // RequestRender while hidden we just remember a render is owed; flipping
    // back to visible flushes one catch-up render.
    private volatile bool _isVisibleSnapshot;
    private int _renderPendingWhileHidden;

    // Render coalescer. Without time-based throttling a single PTY chunk →
    // BeginInvoke → Render cycle runs at chunk arrival pace; Claude
    // streaming (50–200 tok/s) saturates the UI thread because each
    // Render() is a full-viewport repaint. The DispatcherTimer below caps
    // redraws at ~60fps (16ms) regardless of how fast chunks arrive, while
    // a "dirty" flag lets a single chunk schedule a render and any number
    // of follow-up chunks coalesce into the same tick.
    //
    // Timer is created + Started in the constructor on the UI thread and
    // never stopped, so PTY-thread RequestRenders just need to set the
    // dirty flag — no cross-thread Start() / dispatcher binding race.
    // Idle ticks (dirty=0) are essentially free (one Interlocked read).
    private System.Windows.Threading.DispatcherTimer? _renderCoalesceTimer;
    private int _renderDirty;
    private const int RenderIntervalMs = 16;

    private string _cursorStyle = "bar";
    private bool _cursorBlink = true;

    // Cursor blink timer
    private System.Windows.Threading.DispatcherTimer? _cursorTimer;
    private bool _cursorVisible = true;

    // Visual bell
    private DateTime _bellFlashUntil;
    private System.Windows.Threading.DispatcherTimer? _bellTimer;

    // URL detection
    private (int row, int startCol, int endCol, string url)? _hoveredUrl;
    private int _lastUrlRow = -1;
    private List<(int startCol, int endCol, string url)>? _cachedRowUrls;

    // Search highlights
    private List<(int row, int col, int length)> _searchMatches = [];
    private int _currentSearchMatch = -1;
    private HashSet<(int row, int col)>? _searchMatchSetCache;
    private HashSet<(int row, int col)>? _currentMatchSetCache;
    private static readonly HashSet<(int row, int col)> EmptyMatchSet = [];
    private readonly StringBuilder _inputLineBuffer = new();
    private bool _suppressNextEnterToShell;

    // Rendering caches to avoid per-frame allocations
    private readonly Dictionary<Color, SolidColorBrush> _brushCache = [];
    private Typeface? _typefaceBold;
    private Typeface? _typefaceItalic;
    private Typeface? _typefaceBoldItalic;

    // GlyphTypeface caches for the GlyphRun fast path. FormattedText, even
    // with per-span style overrides, still walks WPF's font fallback chain
    // and runs full text shaping per construction; for monospace narrow text
    // both are wasted work because we already know the typeface and the
    // advance width. GlyphRun lets us blit pre-resolved glyph indices at
    // pre-known advances, which is ~5–10× cheaper per call. One typeface per
    // (bold, italic) combo; null until first use, cleared in
    // InvalidateRenderCaches when font/theme/dpi changes.
    private GlyphTypeface? _glyphTypefaceRegular;
    private GlyphTypeface? _glyphTypefaceBold;
    private GlyphTypeface? _glyphTypefaceItalic;
    private GlyphTypeface? _glyphTypefaceBoldItalic;


    // Render diagnostics. Opt-in via CMUX_RENDER_DIAG=1; off by default so
    // there's zero hot-path cost in normal builds (one volatile bool check).
    // Captures Stopwatch ticks per phase + Gen0/1/2 counts over a rolling
    // 120-frame window, flushed asynchronously to %LOCALAPPDATA%/cmux/
    // render-diag.log so we can tell whether perceived lag is actually
    // Render() time or somewhere else (parser feed, IPC, GC pause, …).
    private static readonly bool RenderDiagEnabled =
        Environment.GetEnvironmentVariable("CMUX_RENDER_DIAG") == "1";
    private const int RenderDiagFlushFrames = 120;
    private static readonly string RenderDiagLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "cmux", "render-diag.log");
    private long _diagSumSetupTicks;
    private long _diagSumRowsTicks;
    private long _diagSumOverlayTicks;
    private long _diagSumTotalTicks;
    private long _diagMaxTotalTicks;
    private int _diagFrames;
    private int _diagGen0AtWindowStart;
    private int _diagGen1AtWindowStart;
    private int _diagGen2AtWindowStart;
    private DateTime _diagWindowStartUtc;
    private readonly StringBuilder _textRunBuffer = new();
    private bool _suppressNextEnterTextInput;

    // Cache of FormattedText for single wide CJK / Hangul / emoji cells. Each
    // wide cell is rendered individually for grid alignment; without caching,
    // every frame would allocate a fresh FormattedText AND walk WPF's font
    // fallback chain (Latin fonts don't carry Korean glyphs), which dominates
    // typing latency on screens dense with Hangul output. Cache key includes
    // glyph + foreground colour + style flags. Cleared in InvalidateRenderCaches
    // when theme / font / dpi changes.
    private readonly struct WideTextKey : IEquatable<WideTextKey>
    {
        public readonly char Character;
        public readonly uint FgArgb;
        public readonly byte Style; // bit0 bold, bit1 italic, bit2 dim
        public WideTextKey(char c, Color fg, bool bold, bool italic, bool dim)
        {
            Character = c;
            FgArgb = ((uint)fg.A << 24) | ((uint)fg.R << 16) | ((uint)fg.G << 8) | fg.B;
            Style = (byte)((bold ? 1 : 0) | (italic ? 2 : 0) | (dim ? 4 : 0));
        }
        public bool Equals(WideTextKey other) =>
            Character == other.Character && FgArgb == other.FgArgb && Style == other.Style;
        public override bool Equals(object? obj) => obj is WideTextKey k && Equals(k);
        public override int GetHashCode() => HashCode.Combine(Character, FgArgb, Style);
    }
    private readonly Dictionary<WideTextKey, FormattedText> _wideTextCache = [];

    // Per-row narrow-text batching state. Without this, every style run on
    // every row spawns a fresh FormattedText (which walks WPF's font fallback
    // chain and runs full text shaping) — a Claude-streamed markdown row
    // commonly has 5–10 style runs, so Render() was burning ~10k FormattedText
    // allocations per second under load and saturating the UI thread. With
    // batching we build the row's text into _textRunBuffer once, record style
    // boundaries into _rowSpans, then emit a single FormattedText per row and
    // apply per-range overrides via SetForegroundBrush / SetFontWeight /
    // SetFontStyle. Wide CJK cells stay on the cached single-cell path for
    // grid alignment; the row text just leaves two-space placeholders for
    // their slots so following columns line up.
    private readonly List<RowStyleSpan> _rowSpans = new();

    private readonly struct RowStyleSpan
    {
        public readonly int StartIdx;       // index into row text
        public readonly int Length;         // length in chars (= cells, since narrow only)
        public readonly int StartCol;       // grid column for underline/strikethrough x
        public readonly Color FgColor;
        public readonly bool Bold;
        public readonly bool Italic;
        public readonly bool Dim;
        public readonly bool Underline;
        public readonly bool Strikethrough;
        public RowStyleSpan(int startIdx, int length, int startCol, Color fg,
            bool bold, bool italic, bool dim, bool ul, bool st)
        {
            StartIdx = startIdx; Length = length; StartCol = startCol;
            FgColor = fg; Bold = bold; Italic = italic; Dim = dim;
            Underline = ul; Strikethrough = st;
        }
    }

    // IME preedit (composing text) overlay. Drawn on top of the buffer at
    // the cursor location with an underline, mirroring VSCode/xterm.js's
    // approach of compositing the in-progress IME string visually rather
    // than mutating the PTY. The PTY only ever receives committed text via
    // WriteFromInputProxy, so there is no backspace dance and no buffer
    // corruption. SplitPaneContainer's IME proxy calls SetPreedit during
    // TextInputUpdate, then SetPreedit("") + WriteFromInputProxy on commit.
    private string _preedit = string.Empty;

    public void SetPreedit(string text)
    {
        var newText = text ?? string.Empty;
        if (_preedit == newText) return;
        _preedit = newText;
        RequestRender(System.Windows.Threading.DispatcherPriority.Render);
    }

    // Software Hangul composer for CJK IME input on raw FrameworkElement.
    // The OS IME on a WPF FrameworkElement (no TSF text store) hands us
    // decomposed Compatibility Jamo instead of composed syllables, so we
    // re-implement the composition algorithm in user space.
    private readonly HangulComposer _hangulComposer = new();

    /// <summary>Fired when the pane wants focus.</summary>
    public event Action? FocusRequested;
    public event Action<string>? CommandSubmitted;
    public event Func<string, bool>? CommandInterceptRequested;
    public event Action? ClearRequested;
    public event Action<SplitDirection>? SplitRequested;
    public event Action? ZoomRequested;
    public event Action? ClosePaneRequested;
    public event Action? SearchRequested;

    /// <summary>Clears all event handlers (called before re-attaching to visual tree).</summary>
    public void ClearEventHandlers()
    {
        FocusRequested = null;
        CommandSubmitted = null;
        CommandInterceptRequested = null;
        ClearRequested = null;
        SplitRequested = null;
        ZoomRequested = null;
        ClosePaneRequested = null;
        SearchRequested = null;
    }

    /// <summary>Whether this pane has notification state (blue ring).</summary>
    public static readonly DependencyProperty HasNotificationProperty =
        DependencyProperty.Register(nameof(HasNotification), typeof(bool), typeof(TerminalControl),
            new PropertyMetadata(false, OnHasNotificationChanged));

    public bool HasNotification
    {
        get => (bool)GetValue(HasNotificationProperty);
        set => SetValue(HasNotificationProperty, value);
    }

    /// <summary>Whether this pane is focused.</summary>
    public static readonly DependencyProperty IsPaneFocusedProperty =
        DependencyProperty.Register(nameof(IsPaneFocused), typeof(bool), typeof(TerminalControl),
            new PropertyMetadata(false, OnIsPaneFocusedChanged));

    public bool IsPaneFocused
    {
        get => (bool)GetValue(IsPaneFocusedProperty);
        set => SetValue(IsPaneFocusedProperty, value);
    }

    /// <summary>Whether the parent surface is currently zoomed.</summary>
    public bool IsSurfaceZoomed { get; set; }

    public TerminalControl()
    {
        _theme = GhosttyConfigReader.ReadConfig();
        _visual = new DrawingVisual();
        AddVisualChild(_visual);
        AddLogicalChild(_visual);

        _fontSize = _theme.FontSize;
        _typeface = new Typeface(new FontFamily(_theme.FontFamily), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        var settings = SettingsService.Current;
        _cursorStyle = settings.CursorStyle;
        _cursorBlink = settings.CursorBlink;

        CalculateCellSize();

        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Arrow;
        AllowDrop = true;

        // Enable IME so users can type CJK languages. The IME mode (Hangul /
        // English / Hiragana / etc.) is left to the user's current setting —
        // we deliberately don't force PreferredImeState to On, otherwise
        // every focus gain would reset the user's Hangul/English toggle.
        InputMethod.SetIsInputMethodEnabled(this, true);

        _selection.SelectionChanged += () => RequestRender(System.Windows.Threading.DispatcherPriority.Render);

        _isVisibleSnapshot = IsVisible;
        IsVisibleChanged += OnIsVisibleChanged;

        // Set up the render coalescer here on the UI thread so we never
        // have to touch DispatcherTimer state from the PTY reader thread.
        // Always-on at 16ms; idle ticks just check the dirty flag, which
        // is cheap (one Interlocked.Exchange of 0).
        //
        // Priority is Render (= 7), above Input (= 5). When the user types
        // continuously, KeyDown messages keep arriving at Input priority;
        // a lower-priority render timer would *starve* — the echo bytes
        // get into the buffer (PTY thread sets dirty=1) but Render() never
        // runs because input keeps preempting it, so the user sees typed
        // characters appear with a delay. Render priority guarantees the
        // tick runs ahead of input each frame, so each 16ms cycle commits
        // whatever echoes are in. Trade-off: a single Render() call
        // (~5–10ms with GlyphRun) defers input by that much. Acceptable
        // given the alternative is "typed character is invisible until I
        // stop typing".
        _renderCoalesceTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Render,
            Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(RenderIntervalMs),
        };
        _renderCoalesceTimer.Tick += OnRenderTick;
        _renderCoalesceTimer.Start();

        // Cursor blink
        _cursorTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(530),
        };
        _cursorTimer.Tick += (_, _) =>
        {
            bool wasVisible = _cursorVisible;
            if (!_cursorBlink)
                _cursorVisible = true;
            else
                _cursorVisible = !_cursorVisible;

            if (_cursorVisible != wasVisible)
                RequestRender();
        };
        _cursorTimer.Start();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        // Deliberately NOT flushing HangulComposer here. WPF emits transient
        // LostKeyboardFocus → GotKeyboardFocus pairs during fast typing
        // (internal popups, layout updates, focus scope shuffles); flushing on
        // every loss would commit half-composed jamo prematurely AND the OS
        // IME would reset its own composition state in sync, making
        // subsequent jamo silently disappear. The composer carries state
        // forward; the next jamo / non-jamo keystroke will trigger a natural
        // commit, which preserves user-visible composition during these
        // micro-blips. Special keys (Enter / Backspace / arrows) still flush
        // via OnKeyDown because those are explicit user intents.
        base.OnLostKeyboardFocus(e);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);

        // Re-assert IME enablement on every focus gain. Across visual tree
        // rebuilds (layout change / zoom toggle), the InputMethod attached
        // properties can drift out of sync with the focused control,
        // resulting in TextInput / IME composition not routing here on the
        // first focus. Setting it again on focus gain forces re-binding
        // so Korean / Japanese / Chinese input works on the very first
        // keystroke without the user having to bounce focus elsewhere.
        //
        // Note: deliberately NOT calling SetPreferredImeState(InputMethodState.On)
        // here. That coerces the IME into Hangul mode every focus gain, which
        // under fast typing — when WPF emits transient focus blips —
        // resets the user's Hangul/English toggle and breaks active
        // composition. Letting the user's IME mode persist is the right
        // default; SetIsInputMethodEnabled alone is enough to ensure
        // TextInput routes here.
        InputMethod.SetIsInputMethodEnabled(this, true);
    }

    public void AttachSession(TerminalSession session)
    {
        if (_session != null)
        {
            _session.Redraw -= OnRedraw;
            _session.BellReceived -= OnBell;
        }

        _session = session;
        _inputLineBuffer.Clear();
        _scrollOffset = 0;
        _followOutput = true;
        _lastScrollbackCount = _session.Buffer.ScrollbackCount;
        _session.Redraw += OnRedraw;
        _session.BellReceived += OnBell;
        CalculateTerminalSize();
        Render();
    }

    private void OnRedraw()
    {
        if (_session == null)
            return;

        var currentScrollback = _session.Buffer.ScrollbackCount;
        var scrollbackDelta = currentScrollback - _lastScrollbackCount;

        if (_followOutput || _scrollOffset == 0)
        {
            // Live mode: always stick to bottom.
            _scrollOffset = 0;
            _followOutput = true;
        }
        else if (_scrollOffset < 0 && scrollbackDelta > 0)
        {
            // Freeze viewport while output is streaming.
            _scrollOffset -= scrollbackDelta;
        }

        _scrollOffset = Math.Clamp(_scrollOffset, -currentScrollback, 0);
        if (_scrollOffset == 0)
            _followOutput = true;

        _lastScrollbackCount = currentScrollback;
        RequestRender();
    }

    private void OnBell()
    {
        _bellFlashUntil = DateTime.UtcNow.AddMilliseconds(150);
        RequestRender(System.Windows.Threading.DispatcherPriority.Render);

        _bellTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(170),
        };
        // Restart the timer (handles rapid bell sequences)
        _bellTimer.Stop();
        _bellTimer.Tick -= OnBellTimerTick;
        _bellTimer.Tick += OnBellTimerTick;
        _bellTimer.Start();
    }

    private void OnBellTimerTick(object? sender, EventArgs e)
    {
        _bellTimer?.Stop();
        RequestRender();
    }

    // --- Search support ---

    public void SetSearchHighlights(List<(int row, int col, int length)> matches, int currentIndex)
    {
        _searchMatches = matches;
        _currentSearchMatch = currentIndex;
        RebuildSearchMatchCache();
        RequestRender(System.Windows.Threading.DispatcherPriority.Render);
    }

    public void ClearSearchHighlights()
    {
        _searchMatches = [];
        _currentSearchMatch = -1;
        _searchMatchSetCache = null;
        _currentMatchSetCache = null;
        RequestRender(System.Windows.Threading.DispatcherPriority.Render);
    }

    private void RebuildSearchMatchCache()
    {
        var matchSet = new HashSet<(int row, int col)>();
        foreach (var (mRow, mCol, mLen) in _searchMatches)
        {
            for (int i = 0; i < mLen; i++)
                matchSet.Add((mRow, mCol + i));
        }
        _searchMatchSetCache = matchSet;

        if (_currentSearchMatch >= 0 && _currentSearchMatch < _searchMatches.Count)
        {
            var curSet = new HashSet<(int row, int col)>();
            var (cmRow, cmCol, cmLen) = _searchMatches[_currentSearchMatch];
            for (int i = 0; i < cmLen; i++)
                curSet.Add((cmRow, cmCol + i));
            _currentMatchSetCache = curSet;
        }
        else
        {
            _currentMatchSetCache = null;
        }
    }

    private void RequestRender(System.Windows.Threading.DispatcherPriority priority = System.Windows.Threading.DispatcherPriority.Render)
    {
        // priority kept for source compat with existing callers; the
        // always-on render timer ticks at Render priority anyway.
        _ = priority;

        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        if (!_isVisibleSnapshot)
        {
            // Workspace not on screen — skip; flushed via OnIsVisibleChanged
            // when the control becomes visible again.
            Interlocked.Exchange(ref _renderPendingWhileHidden, 1);
            return;
        }

        // Just mark dirty. The constructor-started timer wakes up at
        // 16ms cadence on the UI thread and dispatches Render() if
        // dirty=1. No cross-thread DispatcherTimer state mutation, so
        // PTY-thread calls are safe and freeze-free.
        Interlocked.Exchange(ref _renderDirty, 1);
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            _renderCoalesceTimer?.Stop();
            return;
        }
        if (Interlocked.Exchange(ref _renderDirty, 0) == 1)
            Render();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var nowVisible = e.NewValue is bool b && b;
        _isVisibleSnapshot = nowVisible;

        if (nowVisible && Interlocked.Exchange(ref _renderPendingWhileHidden, 0) == 1)
        {
            // Catch up — buffer may have advanced significantly while hidden.
            if (_session != null)
                _lastScrollbackCount = _session.Buffer.ScrollbackCount;
            RequestRender(System.Windows.Threading.DispatcherPriority.Render);
        }
    }

    // --- Layout ---

    private void CalculateCellSize()
    {
        var formattedText = new FormattedText(
            "M",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _typeface,
            _fontSize,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        _cellWidth = formattedText.WidthIncludingTrailingWhitespace;
        _cellHeight = formattedText.Height;
        _cellBaseline = formattedText.Baseline;
    }

    private void CalculateTerminalSize()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || _cellWidth <= 0 || _cellHeight <= 0) return;

        int cols = Math.Max(1, (int)(ActualWidth / _cellWidth));
        int rows = Math.Max(1, (int)(ActualHeight / _cellHeight));

        if (cols != _cols || rows != _rows)
        {
            _cols = cols;
            _rows = rows;
            _session?.Resize(cols, rows);
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        CalculateTerminalSize();
        RequestRender(System.Windows.Threading.DispatcherPriority.Render);
    }

    // --- Rendering ---

    private SolidColorBrush GetCachedBrush(Color color)
    {
        if (_brushCache.TryGetValue(color, out var brush))
            return brush;

        brush = new SolidColorBrush(color);
        brush.Freeze();
        _brushCache[color] = brush;
        return brush;
    }

    private void InvalidateRenderCaches()
    {
        _brushCache.Clear();
        _typefaceBold = null;
        _typefaceItalic = null;
        _typefaceBoldItalic = null;
        _glyphTypefaceRegular = null;
        _glyphTypefaceBold = null;
        _glyphTypefaceItalic = null;
        _glyphTypefaceBoldItalic = null;
        _wideTextCache.Clear();
    }

    private Typeface GetTypeface(bool bold, bool italic)
    {
        if (!bold && !italic) return _typeface;
        if (bold && !italic) return _typefaceBold ??= new Typeface(new FontFamily(_theme.FontFamily), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        if (!bold && italic) return _typefaceItalic ??= new Typeface(new FontFamily(_theme.FontFamily), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
        return _typefaceBoldItalic ??= new Typeface(new FontFamily(_theme.FontFamily), FontStyles.Italic, FontWeights.Bold, FontStretches.Normal);
    }

    /// <summary>
    /// Resolves the GlyphTypeface for a (bold, italic) variant. Returns null
    /// when the font family doesn't expose a concrete face (e.g. composite/
    /// fallback fonts) — callers must fall back to FormattedText in that case.
    /// Caches per variant, invalidated alongside the regular Typeface caches.
    /// </summary>
    private GlyphTypeface? GetGlyphTypeface(bool bold, bool italic)
    {
        ref GlyphTypeface? slot = ref _glyphTypefaceRegular;
        if (bold && !italic) slot = ref _glyphTypefaceBold;
        else if (!bold && italic) slot = ref _glyphTypefaceItalic;
        else if (bold && italic) slot = ref _glyphTypefaceBoldItalic;

        if (slot != null) return slot;

        var tf = GetTypeface(bold, italic);
        if (tf.TryGetGlyphTypeface(out var gtf))
        {
            slot = gtf;
            return gtf;
        }
        return null;
    }

    private void Render()
    {
        if (_session == null) return;

        long tStart = 0, tAfterSetup = 0, tAfterRows = 0;
        if (RenderDiagEnabled)
        {
            if (_diagFrames == 0 && _diagWindowStartUtc == default)
                ResetRenderDiagWindow();
            tStart = Stopwatch.GetTimestamp();
        }

        try
        {
            var buffer = _session.Buffer;
            using var dc = _visual.RenderOpen();
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            // Background
            var bgColor = ToWpfColor(_theme.Background);
            dc.DrawRectangle(GetCachedBrush(bgColor), null, new Rect(0, 0, ActualWidth, ActualHeight));

            // Visual bell flash
            if (DateTime.UtcNow < _bellFlashUntil)
            {
                dc.DrawRectangle(GetCachedBrush(Color.FromArgb(25, 255, 255, 255)), null,
                    new Rect(0, 0, ActualWidth, ActualHeight));
            }

            // Notification ring
            if (HasNotification)
            {
                var notifPen = new Pen(GetCachedBrush(Color.FromArgb(180, 0x63, 0x66, 0xF1)), 2);
                notifPen.Freeze();
                dc.DrawRoundedRectangle(null, notifPen, new Rect(1, 1, ActualWidth - 2, ActualHeight - 2), 4, 4);
            }

            // Focused pane indicator
            if (IsPaneFocused)
            {
                var focusPen = new Pen(GetCachedBrush(Color.FromArgb(50, 0x81, 0x8C, 0xF8)), 1);
                focusPen.Freeze();
                dc.DrawRectangle(null, focusPen, new Rect(0, 0, ActualWidth, ActualHeight));
            }

            // Calculate scrollback offset
            int scrollbackCount = buffer.ScrollbackCount;
            bool isScrolledBack = _scrollOffset < 0;
            int viewStartLine = scrollbackCount + _scrollOffset;

            // Use cached search match sets (built once in SetSearchHighlights)
            var searchMatchSet = _searchMatchSetCache ?? EmptyMatchSet;
            var currentMatchSet = _currentMatchSetCache ?? EmptyMatchSet;
            var searchMatchBrush = searchMatchSet.Count > 0 ? GetCachedBrush(Color.FromArgb(100, 0xFB, 0xBF, 0x24)) : null;
            var currentMatchBrush = currentMatchSet.Count > 0 ? GetCachedBrush(Color.FromArgb(180, 0xFB, 0x92, 0x3C)) : null;

            if (RenderDiagEnabled) tAfterSetup = Stopwatch.GetTimestamp();

            // Render visible rows with batched text
            for (int visRow = 0; visRow < _rows; visRow++)
            {
                int virtualLine = viewStartLine + visRow;
                bool isScrollback = virtualLine < scrollbackCount;
                int bufferRow = virtualLine - scrollbackCount;

                TerminalCell[]? scrollbackLine = null;
                if (isScrollback)
                    scrollbackLine = buffer.GetScrollbackLine(virtualLine);

                double y = visRow * _cellHeight;

                // Per-row text + style-span accumulation. The whole row's
                // narrow text goes into _textRunBuffer (with two-space pads
                // for wide-cell slots so column alignment is preserved), and
                // any cell whose style differs from the default starts /
                // continues / ends a span in _rowSpans. After the inner loop
                // FlushRowText emits a GlyphRun per span instead of one
                // FormattedText per style run.
                _textRunBuffer.Clear();
                _rowSpans.Clear();

                int runStartIdx = -1;
                int runStartCol = -1;
                Color runFgColor = default;
                bool runBold = false, runItalic = false, runDim = false;
                bool runUnderline = false, runStrikethrough = false;

                for (int c = 0; c < _cols; c++)
                {
                    TerminalCell cell;
                    if (isScrollback)
                    {
                        cell = (scrollbackLine != null && c < scrollbackLine.Length)
                            ? scrollbackLine[c]
                            : TerminalCell.Empty;
                    }
                    else if (bufferRow >= 0 && bufferRow < buffer.Rows && c < buffer.Cols)
                    {
                        cell = buffer.CellAt(bufferRow, c);
                    }
                    else
                    {
                        cell = TerminalCell.Empty;
                    }

                    if (cell.Width == 0)
                        continue;

                    double x = c * _cellWidth;
                    double cellRenderWidth = cell.Width >= 2 ? _cellWidth * 2 : _cellWidth;
                    var attr = cell.Attribute;
                    bool isSelected = _selection.IsSelected(visRow, c);
                    bool isInverse = attr.Flags.HasFlag(CellFlags.Inverse) != isSelected;

                    TerminalColor cellBg, cellFg;
                    if (isInverse)
                    {
                        cellBg = attr.Foreground.IsDefault ? _theme.Foreground : attr.Foreground;
                        cellFg = attr.Background.IsDefault ? _theme.Background : attr.Background;
                    }
                    else
                    {
                        cellBg = attr.Background;
                        cellFg = attr.Foreground;
                    }

                    if (isSelected && _theme.SelectionBackground.HasValue)
                        cellBg = _theme.SelectionBackground.Value;

                    if (!cellBg.IsDefault)
                    {
                        dc.DrawRectangle(GetCachedBrush(ToWpfColor(cellBg)), null,
                            new Rect(x, y, cellRenderWidth, _cellHeight));
                    }

                    bool isSearchMatch = searchMatchSet.Contains((visRow, c));
                    bool isCurrentMatch = currentMatchSet.Contains((visRow, c));
                    if (isCurrentMatch)
                        dc.DrawRectangle(currentMatchBrush, null, new Rect(x, y, cellRenderWidth, _cellHeight));
                    else if (isSearchMatch)
                        dc.DrawRectangle(searchMatchBrush, null, new Rect(x, y, cellRenderWidth, _cellHeight));

                    if (_hoveredUrl is { } url && visRow == url.row && c >= url.startCol && c <= url.endCol)
                    {
                        var urlPen = new Pen(GetCachedBrush(Color.FromRgb(0x81, 0x8C, 0xF8)), 1);
                        urlPen.Freeze();
                        dc.DrawLine(urlPen, new Point(x, y + _cellHeight - 1), new Point(x + cellRenderWidth, y + _cellHeight - 1));
                    }

                    bool hasChar = cell.Character != '\0' && cell.Character != ' ';
                    bool isWide = cell.Width >= 2;

                    if (isWide)
                    {
                        if (runStartIdx >= 0)
                        {
                            _rowSpans.Add(new RowStyleSpan(runStartIdx, _textRunBuffer.Length - runStartIdx,
                                runStartCol, runFgColor, runBold, runItalic, runDim, runUnderline, runStrikethrough));
                            runStartIdx = -1;
                        }

                        _textRunBuffer.Append("  ");

                        if (hasChar)
                        {
                            var fgColor = cellFg.IsDefault ? ToWpfColor(_theme.Foreground) : ToWpfColor(cellFg);
                            bool bold = attr.Flags.HasFlag(CellFlags.Bold);
                            bool italic = attr.Flags.HasFlag(CellFlags.Italic);
                            bool dim = attr.Flags.HasFlag(CellFlags.Dim);
                            bool underline = attr.Flags.HasFlag(CellFlags.Underline);
                            bool strikethrough = attr.Flags.HasFlag(CellFlags.Strikethrough);

                            var brush = dim
                                ? GetCachedBrush(Color.FromArgb(128, fgColor.R, fgColor.G, fgColor.B))
                                : GetCachedBrush(fgColor);
                            var key = new WideTextKey(cell.Character, fgColor, bold, italic, dim);
                            if (!_wideTextCache.TryGetValue(key, out var charText))
                            {
                                var tf = GetTypeface(bold, italic);
                                charText = new FormattedText(
                                    cell.Character.ToString(),
                                    CultureInfo.CurrentCulture,
                                    FlowDirection.LeftToRight,
                                    tf,
                                    _fontSize,
                                    brush,
                                    dpi);
                                _wideTextCache[key] = charText;
                            }
                            double glyphOffset = (cellRenderWidth - charText.WidthIncludingTrailingWhitespace) / 2;
                            dc.DrawText(charText, new Point(x + Math.Max(0, glyphOffset), y));

                            if (underline)
                            {
                                var pen = new Pen(brush, 1);
                                pen.Freeze();
                                dc.DrawLine(pen, new Point(x, y + _cellHeight - 1), new Point(x + cellRenderWidth, y + _cellHeight - 1));
                            }
                            if (strikethrough)
                            {
                                var pen = new Pen(brush, 1);
                                pen.Freeze();
                                dc.DrawLine(pen, new Point(x, y + _cellHeight / 2), new Point(x + cellRenderWidth, y + _cellHeight / 2));
                            }
                        }
                    }
                    else if (hasChar)
                    {
                        var fgColor = cellFg.IsDefault ? ToWpfColor(_theme.Foreground) : ToWpfColor(cellFg);
                        bool bold = attr.Flags.HasFlag(CellFlags.Bold);
                        bool italic = attr.Flags.HasFlag(CellFlags.Italic);
                        bool dim = attr.Flags.HasFlag(CellFlags.Dim);
                        bool underline = attr.Flags.HasFlag(CellFlags.Underline);
                        bool strikethrough = attr.Flags.HasFlag(CellFlags.Strikethrough);

                        if (runStartIdx >= 0 && (fgColor != runFgColor || bold != runBold ||
                            italic != runItalic || dim != runDim ||
                            underline != runUnderline || strikethrough != runStrikethrough))
                        {
                            _rowSpans.Add(new RowStyleSpan(runStartIdx, _textRunBuffer.Length - runStartIdx,
                                runStartCol, runFgColor, runBold, runItalic, runDim, runUnderline, runStrikethrough));
                            runStartIdx = -1;
                        }

                        if (runStartIdx < 0)
                        {
                            runStartIdx = _textRunBuffer.Length;
                            runStartCol = c;
                            runFgColor = fgColor;
                            runBold = bold;
                            runItalic = italic;
                            runDim = dim;
                            runUnderline = underline;
                            runStrikethrough = strikethrough;
                        }

                        _textRunBuffer.Append(cell.Character);
                    }
                    else
                    {
                        if (runStartIdx >= 0)
                        {
                            _rowSpans.Add(new RowStyleSpan(runStartIdx, _textRunBuffer.Length - runStartIdx,
                                runStartCol, runFgColor, runBold, runItalic, runDim, runUnderline, runStrikethrough));
                            runStartIdx = -1;
                        }
                        _textRunBuffer.Append(' ');
                    }
                }

                if (runStartIdx >= 0)
                {
                    _rowSpans.Add(new RowStyleSpan(runStartIdx, _textRunBuffer.Length - runStartIdx,
                        runStartCol, runFgColor, runBold, runItalic, runDim, runUnderline, runStrikethrough));
                }

                FlushRowText(dc, dpi, y, _textRunBuffer, _rowSpans);
            }

            if (RenderDiagEnabled) tAfterRows = Stopwatch.GetTimestamp();

            // IME preedit overlay (composing string).
            int preeditCols = 0;
            if (!isScrolledBack && _preedit.Length > 0)
            {
                int row = buffer.CursorRow;
                int col = buffer.CursorCol;
                double pY = row * _cellHeight;
                double pX = col * _cellWidth;
                var fgPreedit = ToWpfColor(_theme.Foreground);
                var bgPreedit = ToWpfColor(_theme.Background);
                var fgBrush = GetCachedBrush(fgPreedit);
                var bgBrush = GetCachedBrush(bgPreedit);

                foreach (var ch in _preedit)
                {
                    int w = UnicodeWidth.GetWidth(ch);
                    double cellRender = w >= 2 ? _cellWidth * 2 : _cellWidth;
                    dc.DrawRectangle(bgBrush, null, new Rect(pX, pY, cellRender, _cellHeight));
                    var t = new FormattedText(
                        ch.ToString(),
                        CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        _typeface,
                        _fontSize,
                        fgBrush,
                        dpi);
                    double glyphOffset = (cellRender - t.WidthIncludingTrailingWhitespace) / 2;
                    dc.DrawText(t, new Point(pX + Math.Max(0, glyphOffset), pY));
                    var underlinePen = new Pen(fgBrush, 1);
                    underlinePen.Freeze();
                    dc.DrawLine(underlinePen,
                        new Point(pX, pY + _cellHeight - 1),
                        new Point(pX + cellRender, pY + _cellHeight - 1));
                    pX += cellRender;
                    preeditCols += w;
                }
            }

            // Cursor (only when viewing live buffer)
            if (!isScrolledBack && buffer.CursorVisible && IsPaneFocused && (_cursorVisible || !_cursorBlink))
            {
                double cx = (buffer.CursorCol + preeditCols) * _cellWidth;
                double cy = buffer.CursorRow * _cellHeight;
                var cursorColor = _theme.CursorColor.HasValue
                    ? ToWpfColor(_theme.CursorColor.Value)
                    : ToWpfColor(_theme.Foreground);
                var cursorBrush = GetCachedBrush(Color.FromArgb(200, cursorColor.R, cursorColor.G, cursorColor.B));

                switch ((_cursorStyle ?? "bar").ToLowerInvariant())
                {
                    case "block":
                        dc.DrawRectangle(cursorBrush, null, new Rect(cx, cy, _cellWidth, _cellHeight));
                        break;
                    case "underline":
                        dc.DrawRectangle(cursorBrush, null, new Rect(cx, cy + _cellHeight - 2, _cellWidth, 2));
                        break;
                    default:
                        dc.DrawRectangle(cursorBrush, null, new Rect(cx, cy, 2, _cellHeight));
                        break;
                }
            }

            // Scrollback indicator
            if (isScrolledBack)
            {
                int linesBack = -_scrollOffset;
                string indicator = $"[{linesBack}/{scrollbackCount}]";
                var indicatorText = new FormattedText(
                    indicator,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    _typeface,
                    10,
                    GetCachedBrush(Color.FromArgb(160, 0x81, 0x8C, 0xF8)),
                    dpi);
                double iw = indicatorText.WidthIncludingTrailingWhitespace + 12;
                double ih = indicatorText.Height + 4;
                double ix = ActualWidth - iw - 8;
                dc.DrawRoundedRectangle(
                    GetCachedBrush(Color.FromArgb(200, 0x14, 0x14, 0x14)), null,
                    new Rect(ix, 6, iw, ih), 4, 4);
                dc.DrawText(indicatorText, new Point(ix + 6, 8));
            }

            if (RenderDiagEnabled)
            {
                long tEnd = Stopwatch.GetTimestamp();
                RecordRenderDiag(tStart, tAfterSetup, tAfterRows, tEnd);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TerminalControl] Render failed: {ex}");
        }
    }

    private void ResetRenderDiagWindow()
    {
        _diagSumSetupTicks = 0;
        _diagSumRowsTicks = 0;
        _diagSumOverlayTicks = 0;
        _diagSumTotalTicks = 0;
        _diagMaxTotalTicks = 0;
        _diagFrames = 0;
        _diagGen0AtWindowStart = GC.CollectionCount(0);
        _diagGen1AtWindowStart = GC.CollectionCount(1);
        _diagGen2AtWindowStart = GC.CollectionCount(2);
        _diagWindowStartUtc = DateTime.UtcNow;
    }

    private void RecordRenderDiag(long tStart, long tAfterSetup, long tAfterRows, long tEnd)
    {
        long setup = tAfterSetup - tStart;
        long rows = tAfterRows - tAfterSetup;
        long overlays = tEnd - tAfterRows;
        long total = tEnd - tStart;

        _diagSumSetupTicks += setup;
        _diagSumRowsTicks += rows;
        _diagSumOverlayTicks += overlays;
        _diagSumTotalTicks += total;
        if (total > _diagMaxTotalTicks) _diagMaxTotalTicks = total;
        _diagFrames++;

        if (_diagFrames < RenderDiagFlushFrames) return;

        // Snapshot window stats and reset for the next window. We keep the
        // formatting + file write off the UI thread so render diagnostics
        // never become their own perf problem (the very thing 909e577 fixed
        // for ToastNotificationHelper).
        int frames = _diagFrames;
        long sumTotal = _diagSumTotalTicks;
        long sumSetup = _diagSumSetupTicks;
        long sumRows = _diagSumRowsTicks;
        long sumOverlays = _diagSumOverlayTicks;
        long maxTotal = _diagMaxTotalTicks;
        int gen0 = GC.CollectionCount(0) - _diagGen0AtWindowStart;
        int gen1 = GC.CollectionCount(1) - _diagGen1AtWindowStart;
        int gen2 = GC.CollectionCount(2) - _diagGen2AtWindowStart;
        var windowStart = _diagWindowStartUtc;
        var windowEnd = DateTime.UtcNow;
        string paneId = _session?.PaneId ?? "?";
        int cols = _cols;
        int rowsCount = _rows;
        ResetRenderDiagWindow();

        double freq = Stopwatch.Frequency;
        double avgTotalMs = sumTotal * 1000.0 / freq / frames;
        double avgSetupMs = sumSetup * 1000.0 / freq / frames;
        double avgRowsMs = sumRows * 1000.0 / freq / frames;
        double avgOverlaysMs = sumOverlays * 1000.0 / freq / frames;
        double maxTotalMs = maxTotal * 1000.0 / freq;
        double windowSec = (windowEnd - windowStart).TotalSeconds;
        double rendersPerSec = windowSec > 0 ? frames / windowSec : 0;

        string line =
            $"{windowEnd:o} pane={paneId} grid={cols}x{rowsCount} " +
            $"frames={frames} renders/s={rendersPerSec:F1} " +
            $"avg_total={avgTotalMs:F2}ms max_total={maxTotalMs:F2}ms " +
            $"avg_setup={avgSetupMs:F2}ms avg_rows={avgRowsMs:F2}ms avg_overlays={avgOverlaysMs:F2}ms " +
            $"gc0={gen0} gc1={gen1} gc2={gen2}";

        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RenderDiagLogPath)!);
                File.AppendAllText(RenderDiagLogPath, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RenderDiag] flush failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Emits each non-default style span as a GlyphRun (low-level glyph blit,
    /// no font fallback / no shaping — the row's monospace advance is fixed
    /// at <c>_cellWidth</c>) and draws underline / strikethrough lines for
    /// spans that need them. Spans between/around styled ranges are spaces
    /// (placeholders for grid alignment) which have no ink, so we don't draw
    /// them at all. Wide CJK cells stay on their cached single-cell
    /// FormattedText path, drawn at the inner-loop call site.
    /// </summary>
    private void FlushRowText(DrawingContext dc, double dpi, double y, StringBuilder rowText, List<RowStyleSpan> spans)
    {
        if (spans.Count == 0 || rowText.Length == 0) return;

        float pixelsPerDip = (float)dpi;
        double baselineY = y + _cellBaseline;

        for (int i = 0; i < spans.Count; i++)
        {
            var span = spans[i];
            var brush = span.Dim
                ? GetCachedBrush(Color.FromArgb(128, span.FgColor.R, span.FgColor.G, span.FgColor.B))
                : GetCachedBrush(span.FgColor);

            var gtf = GetGlyphTypeface(span.Bold, span.Italic);

            // GlyphRun fast path requires every char in the span to have a
            // glyph in our monospace GlyphTypeface (no font fallback at
            // this layer). Three failure modes route the span to the slow
            // FormattedText path instead, which DOES walk WPF's font-
            // fallback chain:
            //
            //   1. Surrogate pairs — emoji like 😀 (U+1F600) decompose to
            //      two UTF-16 chars in the buffer, neither of which is a
            //      valid codepoint by itself.
            //   2. BMP chars missing from the monospace font — TUI glyphs
            //      like ⏵ (U+23F5), box-drawing oddballs, etc. Many
            //      monospace fonts skip these ranges; on a miss, GlyphRun
            //      would emit glyph 0 (.notdef → empty box) instead of
            //      letting Windows substitute Segoe UI Symbol / Emoji.
            //   3. gtf == null (composite font with no concrete face).
            //
            // We try to fill the GlyphRun arrays in one pass and bail to
            // fallback the moment we see a char that can't be resolved.
            ushort[]? glyphIndices = null;
            double[]? advanceWidths = null;
            bool needsFallback = gtf == null;
            if (gtf != null)
            {
                glyphIndices = new ushort[span.Length];
                advanceWidths = new double[span.Length];
                var charMap = gtf.CharacterToGlyphMap;
                for (int j = 0; j < span.Length; j++)
                {
                    char ch = rowText[span.StartIdx + j];
                    if (char.IsSurrogate(ch) || !charMap.TryGetValue(ch, out var idx))
                    {
                        needsFallback = true;
                        break;
                    }
                    glyphIndices[j] = idx;
                    advanceWidths[j] = _cellWidth;
                }
            }

            if (!needsFallback)
            {
                // Fast path: GlyphRun with pre-resolved glyph indices and
                // uniform advance widths. We allocate fresh ushort[] /
                // double[] of exact length per span — WPF GlyphRun reads
                // these via interface (IList<T>), and an earlier attempt to
                // pool via a class-based IList<T> wrapper measurably
                // *regressed* render time on heavier panes (8ms → 13ms),
                // suggesting WPF has a T[] fast path that the wrapper
                // bypassed. Per-span alloc is fine given GlyphRun fast path.
                var run = new GlyphRun(
                    gtf!,
                    bidiLevel: 0,
                    isSideways: false,
                    renderingEmSize: _fontSize,
                    pixelsPerDip: pixelsPerDip,
                    glyphIndices: glyphIndices!,
                    baselineOrigin: new Point(span.StartCol * _cellWidth, baselineY),
                    advanceWidths: advanceWidths!,
                    glyphOffsets: null,
                    characters: null,
                    deviceFontName: null,
                    clusterMap: null,
                    caretStops: null,
                    language: null);

                dc.DrawGlyphRun(brush, run);
            }
            else
            {
                // Composite/fallback font that doesn't expose a concrete
                // GlyphTypeface — fall back to FormattedText for this span.
                var tf = GetTypeface(span.Bold, span.Italic);
                var text = new FormattedText(
                    rowText.ToString(span.StartIdx, span.Length),
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    tf,
                    _fontSize,
                    brush,
                    dpi);
                dc.DrawText(text, new Point(span.StartCol * _cellWidth, y));
            }

            // Decorations are separate primitives rather than via
            // GlyphRun/FormattedText decorations because we want them at
            // exactly y + _cellHeight - 1 / y + _cellHeight / 2 to match
            // the grid, not at the font's natural underline metric.
            if (span.Underline || span.Strikethrough)
            {
                double sx = span.StartCol * _cellWidth;
                double sw = span.Length * _cellWidth;
                if (span.Underline)
                {
                    var pen = new Pen(brush, 1);
                    pen.Freeze();
                    dc.DrawLine(pen, new Point(sx, y + _cellHeight - 1), new Point(sx + sw, y + _cellHeight - 1));
                }
                if (span.Strikethrough)
                {
                    var pen = new Pen(brush, 1);
                    pen.Freeze();
                    dc.DrawLine(pen, new Point(sx, y + _cellHeight / 2), new Point(sx + sw, y + _cellHeight / 2));
                }
            }
        }
    }

    private static Color ToWpfColor(TerminalColor c) =>
        c.IsDefault ? Colors.Transparent : Color.FromRgb(c.R, c.G, c.B);

    // --- Mouse reporting ---

    private bool IsMouseTrackingActive =>
        _session?.Buffer.MouseEnabled == true;

    private void SendMouseReport(int button, int col, int row, bool press)
    {
        if (_session == null) return;
        var buf = _session.Buffer;
        if (!buf.MouseEnabled) return;

        col = Math.Clamp(col, 0, buf.Cols - 1);
        row = Math.Clamp(row, 0, buf.Rows - 1);

        if (buf.MouseSgrExtended)
        {
            char suffix = press ? 'M' : 'm';
            _session.Write($"\x1b[<{button};{col + 1};{row + 1}{suffix}");
        }
        else if (press)
        {
            char cb = (char)(button + 32);
            char cx = (char)(col + 33);
            char cy = (char)(row + 33);
            _session.Write($"\x1b[M{cb}{cx}{cy}");
        }
    }

    // --- Keyboard input ---

    private void EnsureLiveView()
    {
        if (_session == null)
            return;

        if (_scrollOffset == 0 && _followOutput)
            return;

        _scrollOffset = 0;
        _followOutput = true;
        _lastScrollbackCount = _session.Buffer.ScrollbackCount;
        RequestRender(System.Windows.Threading.DispatcherPriority.Render);
    }

    private void TrackInputText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\b':
                    if (_inputLineBuffer.Length > 0)
                        _inputLineBuffer.Length--;
                    break;

                case '\r':
                case '\n':
                    SubmitBufferedCommand(allowInterception: false);
                    break;

                default:
                    if (!char.IsControl(ch))
                    {
                        _inputLineBuffer.Append(ch);

                        if (_inputLineBuffer.Length > 4096)
                            _inputLineBuffer.Remove(0, _inputLineBuffer.Length - 4096);
                    }
                    break;
            }
        }
    }

    private void SubmitBufferedCommand(bool allowInterception)
    {
        // Prefer reading the actual visible command line from the
        // terminal buffer — that captures shell-side Tab completions /
        // history-recall expansions that the user never typed key-by-key
        // (e.g., `cd rax_<Tab>` becomes `cd rax_shared/` on screen but the
        // _inputLineBuffer only sees `cd rax_`). Falls back to the typed
        // buffer when reading the visible line fails (no session, empty
        // line, or the prompt prefix can't be located).
        var bufferLine = TryReadCommandFromBufferLine();
        var rawCommand = bufferLine ?? _inputLineBuffer.ToString();
        var command = rawCommand.Trim();
        _inputLineBuffer.Clear();

        if (string.IsNullOrWhiteSpace(command))
            return;

        if (allowInterception && TryInterceptCommand(command))
        {
            _suppressNextEnterToShell = true;
            _suppressNextEnterTextInput = true;

            // The command text has already been sent character-by-character to the shell.
            // Cancel the current input line so a subsequent newline from agent output
            // cannot execute the intercepted handler command.
            if (_session != null)
                _session.Write("\x03");
            return;
        }

        CommandSubmitted?.Invoke(command);
    }

    /// <summary>
    /// Reads the visible cursor row from the terminal buffer and tries to
    /// extract the command portion (everything after the shell prompt).
    /// Returns null when no session is attached, the line is empty, or
    /// no recognizable prompt boundary is found — caller falls back to
    /// the typed input buffer in that case. Captures shell-side Tab
    /// completion and history recall, which the keystroke-based
    /// _inputLineBuffer can't see.
    /// </summary>
    private string? TryReadCommandFromBufferLine()
    {
        if (_session == null) return null;
        var buffer = _session.Buffer;

        try
        {
            var line = buffer.GetLine(buffer.CursorRow);
            if (line.Length == 0) return null;

            // Take cells up to the cursor column (chars typed beyond the
            // cursor — e.g. user moved left mid-edit — would be on the
            // line too, so cap at the cursor for accuracy). Trim trailing
            // empty cells (rest of line is blank space).
            int upTo = Math.Min(buffer.CursorCol, line.Length);
            var sb = new StringBuilder(upTo);
            for (int i = 0; i < upTo; i++)
            {
                var ch = line[i].Character;
                if (ch == '\0') ch = ' ';
                sb.Append(ch);
            }
            var rendered = sb.ToString().TrimEnd();
            if (string.IsNullOrEmpty(rendered)) return null;

            // Strip the shell prompt prefix. Common boundaries:
            //   bash / zsh:  "user@host:dir$ "  or  "...# " for root
            //   cmd.exe:      "C:\path>"
            //   PowerShell:   "PS C:\path> "
            //   fish / heavy themes: vary
            // Heuristic: find the LAST occurrence of one of these markers
            // and take everything after. Falls back to the whole line if
            // no marker is found (rare — caller will then prefer the
            // typed buffer if both look bad).
            int idx = LastPromptBoundary(rendered);
            if (idx < 0) return null;
            var afterPrompt = rendered[(idx + 1)..].TrimStart();
            return string.IsNullOrEmpty(afterPrompt) ? null : afterPrompt;
        }
        catch
        {
            return null;
        }
    }

    private static int LastPromptBoundary(string line)
    {
        // Walk right-to-left for the last `$ `, `# `, `> ` (with trailing
        // space) — these are the canonical end-of-prompt markers across
        // bash / zsh / cmd / PowerShell. Returns the index of the marker
        // character; caller takes line[idx+1..].
        for (int i = line.Length - 2; i >= 0; i--)
        {
            char c = line[i];
            if ((c == '$' || c == '#' || c == '>' || c == '%') && line[i + 1] == ' ')
                return i + 1;
        }
        // cmd.exe prompt has no trailing space after `>`: `C:\path>cmd`.
        for (int i = line.Length - 1; i >= 0; i--)
        {
            if (line[i] == '>') return i;
        }
        return -1;
    }

    private bool TryInterceptCommand(string command)
    {
        var handlers = CommandInterceptRequested;
        if (handlers == null)
            return false;

        foreach (var callback in handlers.GetInvocationList().OfType<Func<string, bool>>())
        {
            try
            {
                if (callback(command))
                    return true;
            }
            catch
            {
                // Ignore handler failures to avoid breaking terminal input.
            }
        }

        return false;
    }

    private bool CopySelectionToClipboard()
    {
        if (_session == null || !_selection.HasSelection)
            return false;

        var text = _selection.GetSelectedText(_session.Buffer, _scrollOffset);
        if (string.IsNullOrEmpty(text))
            return false;

        Clipboard.SetText(text);
        _selection.ClearSelection();
        return true;
    }

    /// <summary>
    /// Public entry point used by the IME-proxy TextBox in SplitPaneContainer
    /// to forward keystrokes that don't belong to text composition (special
    /// keys, shortcuts, control combos). Routes through the same logic as
    /// when this control has focus directly.
    /// </summary>
    public void HandleInputProxyKeyDown(KeyEventArgs e) => OnKeyDown(e);

    /// <summary>
    /// Public entry point used by the IME-proxy TextBox to deliver text
    /// committed by Windows TSF (post-IME composition). Bypasses the
    /// per-control HangulComposer entirely — TSF already handed us a
    /// finalized character sequence so we just stream it to the PTY.
    /// </summary>
    public void WriteFromInputProxy(string text)
    {
        if (_session == null || string.IsNullOrEmpty(text)) return;
        EnsureLiveView();
        TrackInputText(text);
        _session.Write(text);
        _selection.ClearSelection();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_session == null) return;

        // Let the IME consume keystrokes that belong to an active composition.
        // Without this, Korean/Japanese/Chinese jamo would leak into the
        // OnKeyDown -> KeyToVtSequence path while still composing.
        if (e.Key == Key.ImeProcessed)
            return;

        var modifiers = Keyboard.Modifiers;
        bool ctrl = modifiers.HasFlag(ModifierKeys.Control);
        bool shift = modifiers.HasFlag(ModifierKeys.Shift);
        bool alt = modifiers.HasFlag(ModifierKeys.Alt);

        // Let application-level shortcuts bubble to MainWindow.
        // Ctrl+Alt combos (pane focus), Ctrl+Tab (surface cycling),
        // and Ctrl+Shift combos (split, zoom, search, etc.) are app-level.
        if (ctrl && alt) return;
        if (ctrl && shift) return;
        if (ctrl && e.Key == Key.Tab) return;

        // Ctrl+Backspace: delete previous word (send Ctrl+W / unix-word-rubout)
        if (ctrl && e.Key == Key.Back)
        {
            _inputLineBuffer.Clear();
            EnsureLiveView();
            _session.Write("\x17");
            e.Handled = true;
            return;
        }

        // Terminal shortcuts
        if (ctrl && e.Key == Key.C)
        {
            if (!CopySelectionToClipboard())
            {
                // Forward Ctrl+C to shell as interrupt when no selection is active.
                _inputLineBuffer.Clear();
                EnsureLiveView();
                _session.Write("\x03");
            }

            e.Handled = true;
            return;
        }

        if ((ctrl && e.Key == Key.V) || (shift && e.Key == Key.Insert))
        {
            PasteFromClipboard();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.Insert)
        {
            _ = CopySelectionToClipboard();
            e.Handled = true;
            return;
        }

        // Forward Ctrl+letter as control bytes (e.g. Ctrl+X => 0x18) for TUI apps like nano.
        if (ctrl && !modifiers.HasFlag(ModifierKeys.Alt) && TryGetCtrlLetterSequence(e.Key, out var ctrlSequence))
        {
            _inputLineBuffer.Clear();
            EnsureLiveView();
            _session.Write(ctrlSequence);
            e.Handled = true;
            return;
        }

        // Flush any pending Hangul composition before processing special keys
        // (Enter, arrows, Backspace, etc.) — they should commit whatever
        // syllable is currently being built.
        if (_hangulComposer.IsComposing)
        {
            var flushed = _hangulComposer.Flush();
            if (!string.IsNullOrEmpty(flushed))
            {
                EnsureLiveView();
                TrackInputText(flushed);
                _session.Write(flushed);
            }
        }

        bool appCursor = _session.Buffer.ApplicationCursorKeys;
        string? sequence = KeyToVtSequence(e.Key, modifiers, appCursor);
        if (sequence != null)
        {
            if (e.Key == Key.Back)
                TrackInputText("\b");
            else if (e.Key == Key.Enter && !modifiers.HasFlag(ModifierKeys.Shift))
            {
                // Plain Enter submits — track for command history / interception.
                // Shift+Enter is a newline-within-input (multi-line agent prompt)
                // and must not trigger command submission.
                SubmitBufferedCommand(allowInterception: true);
                if (_suppressNextEnterToShell)
                {
                    _suppressNextEnterToShell = false;
                    e.Handled = true;
                    return;
                }
            }

            EnsureLiveView();
            _session.Write(sequence);
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        if (_session == null || string.IsNullOrEmpty(e.Text)) return;

        // KeyDown handles Enter; suppress the trailing TextInput CR/LF when
        // an intercepted command consumed the shell submission.
        if (_suppressNextEnterTextInput && (e.Text.Contains('\r') || e.Text.Contains('\n')))
        {
            _suppressNextEnterTextInput = false;
            e.Handled = true;
            return;
        }

        // Prevent duplicate newline writes from TextInput path.
        if (e.Text.Contains('\r') || e.Text.Contains('\n'))
        {
            e.Handled = true;
            return;
        }

        // Handle Ctrl+C (copy when selection exists)
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Text == "\x03")
        {
            if (_selection.HasSelection)
            {
                var text = _selection.GetSelectedText(_session.Buffer, _scrollOffset);
                if (!string.IsNullOrEmpty(text))
                    Clipboard.SetText(text);
                _selection.ClearSelection();
                return;
            }
        }

        // Handle Ctrl+V (paste)
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Text == "\x16")
        {
            PasteFromClipboard();
            return;
        }

        // Feed each character through the Hangul composer. On a raw
        // FrameworkElement the OS IME delivers decomposed Compatibility Jamo
        // (e.g. ㅇ + ㅏ + ㄴ) instead of composed syllables; the composer
        // reassembles them into Hangul Syllables (e.g. 안) before they hit
        // the PTY. Non-Hangul characters pass through unchanged.
        foreach (char c in e.Text)
        {
            var composed = _hangulComposer.Feed(c);
            if (!string.IsNullOrEmpty(composed))
            {
                EnsureLiveView();
                TrackInputText(composed);
                _session.Write(composed);
            }
        }
        _selection.ClearSelection();
    }

    private void PasteFromClipboard()
    {
        if (_session == null) return;
        if (!TryGetClipboardPasteText(out var text)) return;

        PasteText(text);
    }

    private void PasteText(string text)
    {
        if (_session == null || string.IsNullOrEmpty(text)) return;

        EnsureLiveView();
        TrackInputText(text);

        if (_session.Buffer.BracketedPasteMode)
            _session.Write("\x1b[200~" + text + "\x1b[201~");
        else
            _session.Write(text);
    }

    private static bool HasClipboardPasteContent()
    {
        try
        {
            return Clipboard.ContainsText()
                || Clipboard.ContainsFileDropList()
                || Clipboard.ContainsImage();
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetClipboardPasteText(out string text)
    {
        text = string.Empty;

        try
        {
            if (Clipboard.ContainsText())
            {
                text = Clipboard.GetText();
                return !string.IsNullOrEmpty(text);
            }

            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                var paths = files.Cast<string>()
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .ToArray();

                if (paths.Length > 0)
                {
                    text = string.Join(" ", paths.Select(QuotePathForShell));
                    return true;
                }
            }

            if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage();
                if (image != null)
                {
                    var tempPath = SaveBitmapSourceToTempFile(image);
                    if (!string.IsNullOrWhiteSpace(tempPath))
                    {
                        text = QuotePathForShell(tempPath);
                        return true;
                    }
                }
            }
        }
        catch
        {
            // Ignore clipboard race/format exceptions and treat as unavailable.
        }

        return false;
    }

    private static string? SaveBitmapSourceToTempFile(BitmapSource image)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "cmux", "clipboard-images");
            Directory.CreateDirectory(dir);

            var fileName = $"cmux-clipboard-{DateTime.Now:yyyyMMdd-HHmmssfff}.png";
            var fullPath = Path.Combine(dir, fileName);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));

            using var stream = File.Create(fullPath);
            encoder.Save(stream);

            return fullPath;
        }
        catch
        {
            return null;
        }
    }

    private static string QuotePathForShell(string path)
    {
        if (path.IndexOfAny([' ', '\t', '\n', '\r', '"']) < 0)
            return path;

        return "\"" + path.Replace("\"", "\\\"") + "\"";
    }

    private static bool HasDropContent(IDataObject? data)
    {
        if (data == null)
            return false;

        try
        {
            return data.GetDataPresent(DataFormats.FileDrop)
                || data.GetDataPresent(DataFormats.UnicodeText)
                || data.GetDataPresent(DataFormats.Text)
                || data.GetDataPresent(DataFormats.Bitmap)
                || data.GetDataPresent("PNG");
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetDropPasteText(IDataObject? data, out string text)
    {
        text = string.Empty;
        if (data == null)
            return false;

        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop) &&
                data.GetData(DataFormats.FileDrop) is string[] files &&
                files.Length > 0)
            {
                text = string.Join(" ", files
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(QuotePathForShell));
                return !string.IsNullOrWhiteSpace(text);
            }

            if (data.GetDataPresent(DataFormats.UnicodeText) &&
                data.GetData(DataFormats.UnicodeText) is string unicodeText &&
                !string.IsNullOrEmpty(unicodeText))
            {
                text = unicodeText;
                return true;
            }

            if (data.GetDataPresent(DataFormats.Text) &&
                data.GetData(DataFormats.Text) is string plainText &&
                !string.IsNullOrEmpty(plainText))
            {
                text = plainText;
                return true;
            }

            if (TryGetDropBitmapSource(data, out var bitmap))
            {
                var tempPath = SaveBitmapSourceToTempFile(bitmap);
                if (!string.IsNullOrWhiteSpace(tempPath))
                {
                    text = QuotePathForShell(tempPath);
                    return true;
                }
            }
        }
        catch
        {
            // Ignore drag-data conversion failures.
        }

        return false;
    }

    private static bool TryGetDropBitmapSource(IDataObject data, out BitmapSource bitmap)
    {
        bitmap = null!;

        if (data.GetDataPresent(DataFormats.Bitmap))
        {
            var value = data.GetData(DataFormats.Bitmap);
            if (value is BitmapSource bitmapSource)
            {
                bitmap = bitmapSource;
                return true;
            }
        }

        if (data.GetDataPresent("PNG"))
        {
            var value = data.GetData("PNG");
            if (value is MemoryStream memoryStream)
            {
                memoryStream.Position = 0;
                var frame = BitmapFrame.Create(memoryStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                frame.Freeze();
                bitmap = frame;
                return true;
            }

            if (value is byte[] bytes && bytes.Length > 0)
            {
                using var stream = new MemoryStream(bytes, writable: false);
                var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                frame.Freeze();
                bitmap = frame;
                return true;
            }
        }

        return false;
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        e.Effects = HasDropContent(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effects = HasDropContent(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        Focus();
        FocusRequested?.Invoke();

        if (_session != null && TryGetDropPasteText(e.Data, out var text))
        {
            PasteText(text);
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    // --- Mouse input ---

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        FocusRequested?.Invoke();

        if (_cols <= 0 || _rows <= 0) return;

        var pos = e.GetPosition(this);
        int col = Math.Clamp((int)(pos.X / _cellWidth), 0, _cols - 1);
        int row = Math.Clamp((int)(pos.Y / _cellHeight), 0, _rows - 1);

        // Ctrl+Click for URL opening
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && _hoveredUrl.HasValue)
        {
            try
            {
                Process.Start(new ProcessStartInfo(_hoveredUrl.Value.url) { UseShellExecute = true });
            }
            catch { }
            e.Handled = true;
            return;
        }

        // Mouse reporting (bypass selection when app requests mouse)
        if (IsMouseTrackingActive)
        {
            SendMouseReport(0, col, row, true);
            _mouseDown = true;
            CaptureMouse();
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 2 && _session != null)
        {
            _selection.SelectWord(_session.Buffer, row, col, _scrollOffset);
        }
        else if (e.ClickCount == 3 && _session != null)
        {
            _selection.SelectLine(row, _session.Buffer.Cols);
        }
        else
        {
            _selection.StartSelection(row, col);
            _mouseDown = true;
            CaptureMouse();
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_cols <= 0 || _rows <= 0) return;

        var pos = e.GetPosition(this);
        int col = Math.Clamp((int)(pos.X / _cellWidth), 0, _cols - 1);
        int row = Math.Clamp((int)(pos.Y / _cellHeight), 0, _rows - 1);

        // URL detection (Ctrl held) — cache scanned URLs per row
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && _session != null && row < _session.Buffer.Rows)
        {
            // Only re-scan when the row changes
            if (row != _lastUrlRow)
            {
                _lastUrlRow = row;
                var lineText = UrlDetector.GetRowText(_session.Buffer, row);
                _cachedRowUrls = UrlDetector.FindUrls(lineText);
            }

            // Check cached URLs for hit at current column
            var oldHover = _hoveredUrl;
            _hoveredUrl = null;
            if (_cachedRowUrls != null)
            {
                foreach (var (startCol, endCol, url) in _cachedRowUrls)
                {
                    if (col >= startCol && col <= endCol)
                    {
                        _hoveredUrl = (row, startCol, endCol, url);
                        break;
                    }
                }
            }

            Cursor = _hoveredUrl.HasValue ? Cursors.Hand : Cursors.Arrow;
            if (_hoveredUrl != oldHover)
                RequestRender(System.Windows.Threading.DispatcherPriority.Render);
        }
        else if (_hoveredUrl.HasValue)
        {
            _hoveredUrl = null;
            _lastUrlRow = -1;
            _cachedRowUrls = null;
            Cursor = Cursors.Arrow;
            RequestRender(System.Windows.Threading.DispatcherPriority.Render);
        }

        // Mouse reporting (motion events)
        if (IsMouseTrackingActive && _mouseDown)
        {
            var buf = _session!.Buffer;
            if (buf.MouseTrackingButton || buf.MouseTrackingAny)
            {
                SendMouseReport(32, col, row, true); // 32 = motion flag
            }
            return;
        }
        if (IsMouseTrackingActive && _session!.Buffer.MouseTrackingAny)
        {
            SendMouseReport(35, col, row, true); // 35 = no-button motion
            return;
        }

        // Selection drag
        if (_mouseDown && !IsMouseTrackingActive)
        {
            _selection.ExtendSelection(row, col);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (IsMouseTrackingActive && _mouseDown && _cols > 0 && _rows > 0)
        {
            var pos = e.GetPosition(this);
            int col = Math.Clamp((int)(pos.X / _cellWidth), 0, _cols - 1);
            int row = Math.Clamp((int)(pos.Y / _cellHeight), 0, _rows - 1);
            SendMouseReport(0, col, row, false);
        }

        if (_mouseDown)
        {
            _mouseDown = false;
            ReleaseMouseCapture();
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);

        if (IsMouseTrackingActive)
        {
            if (_cols <= 0 || _rows <= 0) return;

            var pos = e.GetPosition(this);
            int col = Math.Clamp((int)(pos.X / _cellWidth), 0, _cols - 1);
            int row = Math.Clamp((int)(pos.Y / _cellHeight), 0, _rows - 1);
            SendMouseReport(2, col, row, true);
            return;
        }

        var menu = new ContextMenu
        {
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x20)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x3C)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
        };

        var menuItemStyle = new Style(typeof(MenuItem));
        menuItemStyle.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xE2, 0xE2, 0xE9))));
        menuItemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        menuItemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
        menuItemStyle.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));

        var separatorStyle = new Style(typeof(Separator));
        separatorStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x3C))));
        separatorStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4, 2, 4, 2)));

        menu.Resources.Add(typeof(MenuItem), menuItemStyle);
        menu.Resources.Add(typeof(Separator), separatorStyle);

        // Copy
        var copyItem = new MenuItem { Header = "Copy", InputGestureText = "Ctrl+C" };
        copyItem.Icon = MakeIcon("\uE8C8");
        copyItem.IsEnabled = _selection.HasSelection;
        copyItem.Click += (_, _) =>
        {
            if (_session != null)
            {
                var text = _selection.GetSelectedText(_session.Buffer, _scrollOffset);
                if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
                _selection.ClearSelection();
            }
        };
        menu.Items.Add(copyItem);

        // Paste
        var pasteItem = new MenuItem { Header = "Paste", InputGestureText = "Ctrl+V" };
        pasteItem.Icon = MakeIcon("\uE77F");
        pasteItem.IsEnabled = HasClipboardPasteContent();
        pasteItem.Click += (_, _) => PasteFromClipboard();
        menu.Items.Add(pasteItem);

        // Select All
        var selectAllItem = new MenuItem { Header = "Select All" };
        selectAllItem.Icon = MakeIcon("\uE8B3");
        selectAllItem.Click += (_, _) =>
        {
            if (_session != null)
                _selection.SelectAll(_session.Buffer.Rows, _session.Buffer.Cols);
        };
        menu.Items.Add(selectAllItem);

        menu.Items.Add(new Separator());

        // Split Right
        var splitRight = new MenuItem { Header = "Split Right", InputGestureText = "Ctrl+D" };
        splitRight.Icon = MakeIcon("\uE745");
        splitRight.Click += (_, _) => SplitRequested?.Invoke(SplitDirection.Vertical);
        menu.Items.Add(splitRight);

        // Split Down
        var splitDown = new MenuItem { Header = "Split Down", InputGestureText = "Ctrl+Shift+D" };
        splitDown.Icon = MakeIcon("\uE74B");
        splitDown.Click += (_, _) => SplitRequested?.Invoke(SplitDirection.Horizontal);
        menu.Items.Add(splitDown);

        menu.Items.Add(new Separator());

        // Zoom
        var isZoomed = IsSurfaceZoomed;
        var zoom = new MenuItem
        {
            Header = isZoomed ? "Unzoom Pane" : "Zoom Pane",
            InputGestureText = "Ctrl+Shift+Z",
            IsCheckable = true,
            IsChecked = isZoomed,
        };
        zoom.Icon = MakeIcon(isZoomed ? "\uE73F" : "\uE740");
        zoom.Click += (_, _) => ZoomRequested?.Invoke();
        menu.Items.Add(zoom);

        // Close Pane
        var closePane = new MenuItem { Header = "Close Pane" };
        closePane.Icon = MakeIcon("\uE711");
        closePane.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        closePane.Click += (_, _) => ClosePaneRequested?.Invoke();
        menu.Items.Add(closePane);

        menu.Items.Add(new Separator());

        // Clear Terminal
        var clear = new MenuItem { Header = "Clear Terminal" };
        clear.Icon = MakeIcon("\uE894");
        clear.Click += (_, _) =>
        {
            ClearRequested?.Invoke();
            ClearTerminalView();
        };
        menu.Items.Add(clear);

        // Search
        var search = new MenuItem { Header = "Search", InputGestureText = "Ctrl+Shift+F" };
        search.Icon = MakeIcon("\uE721");
        search.Click += (_, _) => SearchRequested?.Invoke();
        menu.Items.Add(search);

        menu.IsOpen = true;
        e.Handled = true;
    }

    private static TextBlock MakeIcon(string glyph) =>
        new() { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12 };

    private void ClearTerminalView()
    {
        if (_session == null) return;

        _session.Buffer.EraseInDisplay(3);
        _session.Buffer.MoveCursorTo(0, 0);
        _scrollOffset = 0;
        _followOutput = true;
        _lastScrollbackCount = _session.Buffer.ScrollbackCount;
        RequestRender(System.Windows.Threading.DispatcherPriority.Render);

        // Ask shell to repaint prompt where supported.
        _session.Write("\x0c");
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_session == null) return;

        // Mouse wheel reporting
        if (IsMouseTrackingActive)
        {
            if (_cols <= 0 || _rows <= 0) return;

            var pos = e.GetPosition(this);
            int col = Math.Clamp((int)(pos.X / _cellWidth), 0, _cols - 1);
            int row = Math.Clamp((int)(pos.Y / _cellHeight), 0, _rows - 1);
            int button = e.Delta > 0 ? 64 : 65; // 64 = scroll up, 65 = scroll down
            SendMouseReport(button, col, row, true);
            e.Handled = true;
            return;
        }

        // Scrollback navigation
        int lines = e.Delta > 0 ? -3 : 3;
        _scrollOffset = Math.Clamp(_scrollOffset + lines, -_session.Buffer.ScrollbackCount, 0);
        _followOutput = _scrollOffset == 0;
        _lastScrollbackCount = _session.Buffer.ScrollbackCount;
        RequestRender(System.Windows.Threading.DispatcherPriority.Render);
        e.Handled = true;
    }

    // --- Visual tree ---

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _visual;

    private static bool TryGetCtrlLetterSequence(Key key, out string sequence)
    {
        sequence = "";
        if (key < Key.A || key > Key.Z)
            return false;

        var controlCode = (char)(key - Key.A + 1);
        sequence = controlCode.ToString();
        return true;
    }

    private static string? KeyToVtSequence(Key key, ModifierKeys modifiers, bool appCursor)
    {
        if (appCursor)
        {
            var appSeq = key switch
            {
                Key.Up => "\x1bOA",
                Key.Down => "\x1bOB",
                Key.Right => "\x1bOC",
                Key.Left => "\x1bOD",
                Key.Home => "\x1bOH",
                Key.End => "\x1bOF",
                _ => (string?)null,
            };
            if (appSeq != null) return appSeq;
        }

        return key switch
        {
            // Shift+Enter sends ESC+CR (alacritty / wezterm convention) so
            // multi-line agent CLIs like Claude Code can distinguish it from
            // a plain Enter (which submits). Bare Enter still sends CR.
            Key.Enter => modifiers.HasFlag(ModifierKeys.Shift) ? "\x1b\r" : "\r",
            Key.Escape => "\x1b",
            Key.Back => "\x7f",
            Key.Tab => modifiers.HasFlag(ModifierKeys.Shift) ? "\x1b[Z" : "\t",
            Key.Up => "\x1b[A",
            Key.Down => "\x1b[B",
            Key.Right => "\x1b[C",
            Key.Left => "\x1b[D",
            Key.Home => "\x1b[H",
            Key.End => "\x1b[F",
            Key.Insert => "\x1b[2~",
            Key.Delete => "\x1b[3~",
            Key.PageUp => "\x1b[5~",
            Key.PageDown => "\x1b[6~",
            Key.F1 => "\x1bOP",
            Key.F2 => "\x1bOQ",
            Key.F3 => "\x1bOR",
            Key.F4 => "\x1bOS",
            Key.F5 => "\x1b[15~",
            Key.F6 => "\x1b[17~",
            Key.F7 => "\x1b[18~",
            Key.F8 => "\x1b[19~",
            Key.F9 => "\x1b[20~",
            Key.F10 => "\x1b[21~",
            Key.F11 => "\x1b[23~",
            Key.F12 => "\x1b[24~",
            _ => null,
        };
    }

    public void UpdateTheme(GhosttyTheme theme)
    {
        _theme = theme;
        _typeface = new Typeface(new FontFamily(theme.FontFamily), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _fontSize = theme.FontSize;
        InvalidateRenderCaches();
        CalculateCellSize();
        CalculateTerminalSize();
        RequestRender(System.Windows.Threading.DispatcherPriority.Render);
    }

    public void UpdateSettings(TerminalTheme theme, string fontFamily, int fontSize)
    {
        // Convert TerminalTheme to GhosttyTheme
        var ghosttyTheme = new GhosttyTheme
        {
            Background = theme.Background,
            Foreground = theme.Foreground,
            Palette = theme.Palette,
            SelectionBackground = theme.SelectionBg,
            CursorColor = theme.CursorColor,
            FontFamily = fontFamily,
            FontSize = fontSize
        };
        UpdateSettings(ghosttyTheme, fontFamily, fontSize);
    }

    public void UpdateSettings(GhosttyTheme theme, string fontFamily, int fontSize)
    {
        _theme = theme;
        _fontSize = fontSize;

        var settings = SettingsService.Current;
        _cursorStyle = settings.CursorStyle;
        _cursorBlink = settings.CursorBlink;

        _typeface = new Typeface(new FontFamily(fontFamily), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        InvalidateRenderCaches();
        CalculateCellSize();
        CalculateTerminalSize();
        RequestRender(System.Windows.Threading.DispatcherPriority.Render);
    }

    private static void OnHasNotificationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((TerminalControl)d).RequestRender(System.Windows.Threading.DispatcherPriority.Render);
    }

    private static void OnIsPaneFocusedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (TerminalControl)d;
        if ((bool)e.NewValue)
        {
            ctrl._cursorVisible = true;
            if (ctrl._cursorBlink)
                ctrl._cursorTimer?.Start();
        }
        ctrl.RequestRender(System.Windows.Threading.DispatcherPriority.Render);
    }
}
