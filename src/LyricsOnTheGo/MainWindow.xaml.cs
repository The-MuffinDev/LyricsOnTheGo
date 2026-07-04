using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LyricsOnTheGo.Glass;
using LyricsOnTheGo.Interop;
using LyricsOnTheGo.Models;
using LyricsOnTheGo.Services;

namespace LyricsOnTheGo;

public partial class MainWindow : Window
{
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_WINDOWPOSCHANGED = 0x0047;
    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
                      HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private const int ResizeBorder = 8;

    // Lyric rendering tokens, derived from settings by ApplySettings(). All lines share the
    // text colour; the active line is fully opaque, the rest use the configured opacity.
    private Brush _textBrush = new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xED));
    private double _fontSize = 24;
    private double _inactiveOpacity = 0.55;
    private double _offsetMs;
    private TextAlignment _align = TextAlignment.Left;

    private readonly Settings _settings = Settings.Load();
    private bool _settingsOpen;

    private GlassWindow? _glass;
    private IntPtr _hwnd;

    private readonly SmtcReader _smtc = new();
    private readonly LyricsController _lyrics = new();
    private DispatcherTimer? _pollTimer;
    private DispatcherTimer? _interpTimer;

    private IReadOnlyList<LyricLine>? _lines;
    private readonly List<System.Windows.Controls.TextBlock> _lineBlocks = new();
    private int _activeIndex = -1;
    private double _lastScrollTarget = double.NaN;

    private NowPlaying _lastNp = NowPlaying.None;
    private DateTime _lastNpAt = DateTime.UtcNow;
    private string _lastShownKey = "";

    private DispatcherTimer? _headerTimer;   // single-shot auto-hide countdown
    private bool _headerHovered;             // hovering the header keeps it open
    private bool _headerShown = true;        // current header visibility (avoids re-animating)
    private int _lastHitX = int.MinValue;    // last cursor pos (real screen coords) to detect movement
    private int _lastHitY = int.MinValue;

    private TrayIcon? _tray;
#if DEBUG
    private Diagnostics.DiagnosticsWindow? _diagWindow;
#endif
    private bool _pinned = true;   // window starts always-on-top
    private bool _exiting;         // set only by the tray "Quit" path (the real exit)

    private bool _userScrolling;          // user manually scrolled the synced lyrics away
    private bool _dragScrolling;          // mid click-drag over the lyrics body
    private bool _suppressBar;            // guards programmatic scrollbar updates from re-entrancy
    private double _dragStartScreenY;
    private double _dragStartTransY;

    private bool _karaoke;                 // karaoke borderless-fullscreen mode
    private Native.RECT _preKaraokeRect;   // window bounds to restore when leaving karaoke

    private DispatcherTimer? _searchTimer; // animates the "Searching…" trailing dots
    private System.Windows.Documents.Run? _searchRun;
    private string _searchBase = "";
    private int _searchDots;

    public MainWindow()
    {
        InitializeComponent();

        SourceInitialized += OnSourceInitialized;
        LocationChanged += (_, _) => SyncGlass();
        SizeChanged += (_, _) => { SyncGlass(); QueueRecenter(); };
        Activated += (_, _) => SyncGlass();
        SizeChanged += (_, _) => UpdateEdgeFade();
        Loaded += OnLoaded;
        Closing += (_, e) =>
        {
            // Anything but the tray "Quit" (incl. Alt+F4) hides to tray instead of exiting.
            if (_exiting) { SaveWindowPlacement(); return; }
            e.Cancel = true;
            HideToTray();
        };
        Closed += (_, _) =>
        {
#if DEBUG
            _diagWindow?.Close();
#endif
            _tray?.Dispose();
            _glass?.Close();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (_settingsOpen) CloseSettings();
            else HideToTray();
        };

        // Clicking the overlay reveals the header. Mouse-move reveal is handled in the WM_NCHITTEST
        // hook keyed off the REAL cursor position — NOT WPF's MouseMove, which also fires when the
        // lyrics animate under a stationary cursor (which would keep the header from auto-hiding).
        PreviewMouseDown += (_, _) => RevealHeader();
    }

    // ---- Glass backdrop glue ----------------------------------------------------

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProcHook);

        RestoreWindowPlacement();   // apply last size/position BEFORE the glass is placed behind it

        var (x, y, w, h) = GetPhysicalRect();
        _glass = new GlassWindow();
        _glass.Start(x, y, w, h);
        SyncGlass();
    }

    /// <summary>Restore the last window bounds, if any and still on a connected monitor.</summary>
    private void RestoreWindowPlacement()
    {
        var p = WindowPlacementStore.Load();
        if (p is null || p.Width <= 0 || p.Height <= 0)
            return;

        var rect = new Native.RECT { left = p.X, top = p.Y, right = p.X + p.Width, bottom = p.Y + p.Height };
        if (IsOnScreen(rect))
            Native.SetBounds(_hwnd, p.X, p.Y, p.Width, p.Height);
    }

    /// <summary>Persist the current window bounds (using the pre-mode rect when in karaoke/fullscreen,
    /// so we never restore to a full-screen size).</summary>
    private void SaveWindowPlacement()
    {
        if (_hwnd == IntPtr.Zero)
            return;

        Native.RECT r;
        if (_karaoke) r = _preKaraokeRect;
        else if (_fullscreen) r = _preFsRect;
        else Native.GetWindowRect(_hwnd, out r);

        int w = r.right - r.left, h = r.bottom - r.top;
        if (w > 0 && h > 0)
            WindowPlacementStore.Save(new WindowPlacement(r.left, r.top, w, h));
    }

    /// <summary>True if the rectangle overlaps any connected monitor (guards against off-screen restores).</summary>
    private static bool IsOnScreen(Native.RECT r)
    {
        var rect = new System.Drawing.Rectangle(
            r.left, r.top, Math.Max(1, r.right - r.left), Math.Max(1, r.bottom - r.top));
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            if (screen.Bounds.IntersectsWith(rect))
                return true;
        return false;
    }

    private void SyncGlass()
    {
        if (_glass is null || _glass.Hwnd == IntPtr.Zero || _hwnd == IntPtr.Zero)
            return;
        var (x, y, w, h) = GetPhysicalRect();
        Native.PositionBehind(_glass.Hwnd, _hwnd, x, y, w, h);
        _glass.Resize(w, h);
    }

    private (int x, int y, int w, int h) GetPhysicalRect()
    {
        Native.GetWindowRect(_hwnd, out var r);
        return (r.left, r.top, r.right - r.left, r.bottom - r.top);
    }

    private bool IsOverInteractive(int screenX, int screenY)
    {
        try
        {
            Point p = PointFromScreen(new Point(screenX, screenY));
            if (InputHitTest(p) is not DependencyObject d)
                return false;
            for (DependencyObject? cur = d; cur is not null; cur = VisualTreeHelper.GetParent(cur))
            {
                if (cur is System.Windows.Controls.Primitives.ButtonBase
                    or System.Windows.Controls.Primitives.RangeBase    // Slider
                    or System.Windows.Controls.Primitives.TextBoxBase
                    or System.Windows.Controls.Primitives.ScrollBar
                    or Controls.SettingsPanel)
                    return true;
            }
        }
        catch { /* hit-test can fail mid-layout */ }
        return false;
    }

    // ---- Settings ---------------------------------------------------------------

    private void ApplySettings()
    {
        Color textColor = ParseColor(_settings.TextColor, Color.FromRgb(0xED, 0xED, 0xED));
        _textBrush = new SolidColorBrush(textColor);

        // Karaoke mode overrides size + alignment: large, centred lyrics.
        _fontSize = _karaoke ? 64 : _settings.FontSize;
        // Inverted: higher "Dim" = more transparent (lower opacity).
        _inactiveOpacity = (100 - _settings.Dim) / 100.0;
        _offsetMs = _settings.Offset;
        _align = (_karaoke || _settings.Align == "center") ? TextAlignment.Center : TextAlignment.Left;

        foreach (var tb in _lineBlocks)
        {
            tb.FontSize = _fontSize;
            tb.LineHeight = _fontSize * 1.28;
            tb.TextAlignment = _align;
            tb.Foreground = _textBrush;
        }
        PlainTextBlock.FontSize = _fontSize;
        PlainTextBlock.LineHeight = _fontSize * 1.28;
        PlainTextBlock.Foreground = _textBrush;
        PlainTextBlock.TextAlignment = _align;

        if (_lineBlocks.Count > 0)
        {
            UpdateHighlight();
            Dispatcher.BeginInvoke(new Action(RecenterInstant), DispatcherPriority.Loaded);
        }

        Color bg = ParseColor(_settings.BgColor, Color.FromRgb(0x08, 0x08, 0x08));
        _glass?.UpdateTint(bg.R, bg.G, bg.B, _settings.BgOpacity / 100f);

        _lyrics.PlainFallback = _settings.PlainFallback;
        Native.SetClickThrough(_hwnd, _settings.ClickThrough);
        _tray?.SetClickThroughChecked(_settings.ClickThrough);

        // The progress fill + lyrics scrollbar thumb track the chosen lyrics text colour.
        ProgressFill.Background = _textBrush;
        LyricsScroll.Foreground = new SolidColorBrush(Color.FromArgb(0x66, textColor.R, textColor.G, textColor.B));

        UpdateFooterLayout();
        ApplyHeaderMode();
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        ApplySettings();
        _settings.Save();

        // A user-driven offset change is saved per-song; loading a song's offset is not (guarded).
        if (e.PropertyName == nameof(Settings.Offset) && !_loadingOffset)
            OffsetStore.Set(_offsetKey, _settings.Offset);
    }

    // ---- Footer: progress bar + times (README §3.6) -----------------------------

    private void UpdateFooterLayout()
    {
        bool p = _settings.ShowProgress;
        bool t = _settings.ShowTimes;
        FooterBar.Visibility = (p || t) ? Visibility.Visible : Visibility.Collapsed;
        // Collapse (not Hide) the track when off: the middle column stays * so the times,
        // when shown, are pushed to the left/right edges as if the bar were still there.
        ProgressTrack.Visibility = p ? Visibility.Visible : Visibility.Collapsed;
        TimeCurrent.Visibility = t ? Visibility.Visible : Visibility.Collapsed;
        TimeTotal.Visibility = t ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress();
        UpdateEdgeFade();   // bottom fade grows/shrinks with the footer's presence
    }

    private void UpdateProgress()
    {
        if (FooterBar.Visibility != Visibility.Visible)
            return;

        double dur = _lastNp.DurationMs;
        double pos = _lastNp.PositionMs;
        if (_lastNp.IsPlaying)
            pos += (DateTime.UtcNow - _lastNpAt).TotalMilliseconds;
        if (dur > 0)
            pos = Math.Clamp(pos, 0, dur);

        if (_settings.ShowProgress)
        {
            double frac = dur > 0 ? pos / dur : 0;
            ProgressFill.Width = Math.Max(0, frac * ProgressTrack.ActualWidth);
        }
        if (_settings.ShowTimes)
        {
            TimeCurrent.Text = FormatTime(pos);
            TimeTotal.Text = FormatTime(dur);
        }
    }

    private static string FormatTime(double ms)
    {
        if (ms < 0 || double.IsNaN(ms))
            ms = 0;
        int total = (int)(ms / 1000);
        return $"{total / 60}:{total % 60:00}";
    }

    // ---- Header auto-hide (README §3.1) -----------------------------------------

    /// <summary>Apply the auto-hide setting: when off, the header is always shown.</summary>
    private void ApplyHeaderMode()
    {
        if (_settings.AutoHideHeader)
            RevealHeader();           // visible now, then fades out after the timer
        else
        {
            _headerTimer?.Stop();
            SetHeaderShown(true);
        }
    }

    /// <summary>Reveal the header (fade + slide down into place) and restart the 2 s hide timer.
    /// Driven by WM_NCHITTEST, which fires reliably on any mouse movement over the whole window.</summary>
    private void RevealHeader()
    {
        RestoreCursor();   // any mouse activity brings the cursor back (karaoke)
        if (!_headerShown)
            SetHeaderShown(true);
        RestartHeaderTimer();
    }

    private void RestartHeaderTimer()
    {
        if (_headerTimer is null)
            return;
        _headerTimer.Stop();
        if (_settings.AutoHideHeader && !_headerHovered)
            _headerTimer.Start();
    }

    private void HideHeaderIfAllowed()
    {
        if (!_settings.AutoHideHeader || _headerHovered)
            return;
        if (_headerShown)
            SetHeaderShown(false);
        // In karaoke, also hide the mouse cursor (YouTube-fullscreen feel).
        HideCursorIfKaraoke();
    }

    /// <summary>Animate the header in/out with a "fade up" feel: opacity + an 8 px vertical slide.</summary>
    private void SetHeaderShown(bool shown)
    {
        _headerShown = shown;
        HeaderBar.IsHitTestVisible = shown;

        var dur = TimeSpan.FromMilliseconds(220);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        HeaderBar.BeginAnimation(OpacityProperty,
            new DoubleAnimation(shown ? 1 : 0, dur) { EasingFunction = ease });
        HeaderTranslate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(shown ? 0 : -8, dur) { EasingFunction = ease });

        // The lyrics scrollbar hides together with the header.
        UpdateScrollBar();
    }

    private static Color ParseColor(string hex, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex))
                return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch { /* invalid hex while typing */ }
        return fallback;
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        _settingsOpen = true;
        SettingsOverlay.OnOpened();
        SettingsOverlay.Visibility = Visibility.Visible;
        UpdateResync();   // hide the resync pill behind the settings overlay
    }

    private void CloseSettings()
    {
        _settingsOpen = false;
        SettingsOverlay.Visibility = Visibility.Collapsed;
        UpdateResync();   // restore it if the lyrics are still hand-scrolled
    }

    private void OnCloseToTray(object sender, RoutedEventArgs e) => HideToTray();

    // ---- Tray / visibility / pin -----------------------------------------------

    /// <summary>Hide the overlay to the tray: both the WPF UI and the glass backdrop. The app
    /// keeps running; only the tray "Quit" item actually exits.</summary>
    private void HideToTray()
    {
        SaveWindowPlacement();   // remember where/what size we were when the overlay is dismissed
        if (_settingsOpen)
            CloseSettings();
        Hide();
        _glass?.SetVisible(false);
        _tray?.SetVisibleState(false);
    }

    private void ShowFromTray()
    {
        Show();
        _glass?.SetVisible(true);
        SyncGlass();
        Activate();
        _tray?.SetVisibleState(true);
    }

    private void ToggleVisibility()
    {
        if (IsVisible)
            HideToTray();
        else
            ShowFromTray();
    }

    /// <summary>The only real exit (tray "Quit"). Closing the window then shuts down the app.</summary>
    private void Quit()
    {
        _exiting = true;
        Close();
    }

#if DEBUG
    /// <summary>Open the diagnostics window (single instance: reuse + focus if already open).
    /// Debug builds only — excluded from Release/the MSI so end users never see it.</summary>
    private void ShowDiagnostics()
    {
        if (_diagWindow is null)
        {
            _diagWindow = new Diagnostics.DiagnosticsWindow();
            _diagWindow.Closed += (_, _) => _diagWindow = null;
            _diagWindow.Show();
            return;
        }

        if (_diagWindow.WindowState == WindowState.Minimized)
            _diagWindow.WindowState = WindowState.Normal;
        _diagWindow.Activate();
    }
#endif

    private void OnTogglePin(object sender, RoutedEventArgs e)
    {
        _pinned = !_pinned;
        Topmost = _pinned;
        // Re-glue the glass directly behind the UI window. PositionBehind inherits the UI's
        // topmost band, so the backdrop never floats above (and hides) the UI — the bug that
        // SetTopmost(glass, ...) caused by raising the glass to the top of the topmost band.
        SyncGlass();
        UpdatePinVisual();
    }

    private void UpdatePinVisual()
    {
        // Accent stroke + full opacity when pinned; dim white when free-floating.
        PinIcon.Stroke = _pinned
            ? new SolidColorBrush(Color.FromRgb(0x17, 0xD3, 0x46))
            : Brushes.White;
        PinButton.Opacity = _pinned ? 1.0 : 0.5;
    }

    // ---- Header drag / now-playing title ----------------------------------------

    private static readonly SolidColorBrush AccentBrush = new(Color.FromRgb(0x17, 0xD3, 0x46));

    /// <summary>The window is dragged only from the header (the body is reserved for scrolling).
    /// A double-click on the header toggles borderless fullscreen.</summary>
    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        // Ignore clicks that land on a header button.
        for (DependencyObject? cur = e.OriginalSource as DependencyObject; cur is not null;
             cur = VisualTreeHelper.GetParent(cur))
        {
            if (cur is System.Windows.Controls.Primitives.ButtonBase)
                return;
        }

        if (e.ClickCount == 2)
        {
            ToggleFullscreen();
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { /* DragMove throws if the button was already released */ }
        }
    }

    private bool _fullscreen;
    private Native.RECT _preFsRect;

    private string _offsetKey = "";   // song key the current offset belongs to
    private bool _loadingOffset;      // guards offset-load from being saved back as a user edit

    /// <summary>Toggle borderless windowed-fullscreen (covers the current monitor; not exclusive).</summary>
    internal void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;
        if (_fullscreen)
        {
            Native.GetWindowRect(_hwnd, out _preFsRect);
            var s = System.Windows.Forms.Screen.FromHandle(_hwnd).Bounds;
            Native.SetBounds(_hwnd, s.Left, s.Top, s.Width, s.Height);
        }
        else
        {
            var r = _preFsRect;
            Native.SetBounds(_hwnd, r.left, r.top, r.right - r.left, r.bottom - r.top);
        }
    }

    private void UpdateHeaderTrack()
    {
        if (_lastNp.HasSession && !string.IsNullOrWhiteSpace(_lastNp.Title))
        {
            HeaderTitle.Text = _lastNp.Title;
            HeaderArtist.Text = _lastNp.Artist;
            HeaderArtist.Visibility = string.IsNullOrWhiteSpace(_lastNp.Artist)
                ? Visibility.Collapsed : Visibility.Visible;
            HeaderInfo.Visibility = Visibility.Visible;
        }
        else
        {
            HeaderInfo.Visibility = Visibility.Collapsed;
        }
    }

    // ---- Manual lyrics scroll + resync ------------------------------------------

    private double _scrollTarget;
    private bool _smoothScrolling;   // per-frame eased glide is running

    private void OnLyricsWheel(object sender, MouseWheelEventArgs e)
    {
        if (_lines is null || LyricsViewport.Visibility != Visibility.Visible)
            return;

        // Ctrl + wheel resizes the lyrics text directly (2px per notch, clamped 14–72).
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            _settings.FontSize += e.Delta > 0 ? 2 : -2;
            e.Handled = true;
            return;
        }

        var (minY, maxY) = ScrollBounds();
        double basis = _smoothScrolling ? _scrollTarget : LyricsTranslate.Y;
        // Distance per wheel notch (e.Delta is ~120/notch). Lower = shorter jumps.
        _scrollTarget = Math.Clamp(basis + e.Delta * 0.7, minY, maxY);
        _userScrolling = true;
        UpdateResync();
        StartSmoothScroll();
        e.Handled = true;
    }

    /// <summary>Frame-synced eased glide toward <see cref="_scrollTarget"/>. Driving Y per-frame
    /// (CompositionTarget.Rendering) keeps BOTH the lyrics AND the scrollbar thumb moving smoothly,
    /// and re-targets continuously while the wheel keeps spinning.</summary>
    private void StartSmoothScroll()
    {
        if (_smoothScrolling)
            return;
        double cur = LyricsTranslate.Y;
        LyricsTranslate.BeginAnimation(TranslateTransform.YProperty, null); // release any auto hold
        LyricsTranslate.Y = cur;
        _smoothScrolling = true;
        CompositionTarget.Rendering += OnScrollFrame;
    }

    private void OnScrollFrame(object? sender, EventArgs e)
    {
        double cur = LyricsTranslate.Y;
        double diff = _scrollTarget - cur;
        if (Math.Abs(diff) < 0.3)
        {
            LyricsTranslate.Y = _scrollTarget;
            StopSmoothScroll();
            UpdateScrollBar();
            return;
        }
        // Lower factor = longer, momentum-like glide (Chrome-style inertia).
        LyricsTranslate.Y = cur + diff * 0.12;
        UpdateScrollBar();
    }

    private void StopSmoothScroll()
    {
        if (!_smoothScrolling)
            return;
        _smoothScrolling = false;
        CompositionTarget.Rendering -= OnScrollFrame;
    }

    private void OnLyricsDragStart(object sender, MouseButtonEventArgs e)
    {
        if (_lines is null || LyricsViewport.Visibility != Visibility.Visible)
            return;
        StopSmoothScroll();

        // Freeze at the CURRENTLY VISIBLE position: reading Y while an animation (e.g. from Resync)
        // is holding it gives the on-screen value, but clearing the animation reverts Y to its base.
        // So capture first, clear, then pin the base to that value — the drag then starts from the
        // view you actually see, not the last drag's end.
        double cur = LyricsTranslate.Y;
        LyricsTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        LyricsTranslate.Y = cur;

        _dragScrolling = true;
        _dragStartScreenY = e.GetPosition(this).Y;
        _dragStartTransY = cur;
        LyricsViewport.CaptureMouse();
    }

    private void UpdateResync()
        => ResyncButton.Visibility = (_userScrolling && !_settingsOpen)
            ? Visibility.Visible : Visibility.Collapsed;

    private void OnLyricsDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragScrolling)
            return;
        double dy = e.GetPosition(this).Y - _dragStartScreenY;
        SetManualScroll(_dragStartTransY + dy);
    }

    private void OnLyricsDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_dragScrolling)
            return;
        _dragScrolling = false;
        LyricsViewport.ReleaseMouseCapture();
    }

    private void SetManualScroll(double y)
    {
        _userScrolling = true;
        LyricsTranslate.BeginAnimation(TranslateTransform.YProperty, null); // stop any auto animation
        var (minY, maxY) = ScrollBounds();
        LyricsTranslate.Y = Math.Clamp(y, minY, maxY);
        UpdateResync();
        UpdateScrollBar();
    }

    private void OnResync(object sender, RoutedEventArgs e)
    {
        StopSmoothScroll();
        _userScrolling = false;
        UpdateResync();
        LyricsScroll.Visibility = Visibility.Collapsed;
        _lastScrollTarget = double.NaN;
        ScrollToIndex(Math.Max(_activeIndex, 0), animate: true);
    }

    /// <summary>Drag the lyrics scrollbar — follows the cursor instantly (fluid) and pauses auto-scroll.</summary>
    private void OnLyricsBarScroll(object sender, System.Windows.Controls.Primitives.ScrollEventArgs e)
    {
        if (_suppressBar)
            return;
        StopSmoothScroll();
        _userScrolling = true;
        LyricsTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        LyricsTranslate.Y = -e.NewValue;
        UpdateResync();
    }

    /// <summary>Sync the lyrics scrollbar's range + thumb to the current scroll offset. The bar is
    /// only shown while the user is manually scrolling (desynced) AND the header is visible — it
    /// hides together with the auto-hidden header.</summary>
    private void UpdateScrollBar()
    {
        double max = ScrollMax();
        bool show = _userScrolling && _headerShown && max > 0
                    && _lines is not null && LyricsViewport.Visibility == Visibility.Visible;
        if (!show)
        {
            LyricsScroll.Visibility = Visibility.Collapsed;
            return;
        }

        _suppressBar = true;
        LyricsScroll.Minimum = 0;
        LyricsScroll.Maximum = max;
        LyricsScroll.ViewportSize = LyricsViewport.ActualHeight;
        LyricsScroll.Value = Math.Clamp(-LyricsTranslate.Y, 0, max);
        _suppressBar = false;
        LyricsScroll.Visibility = Visibility.Visible;
    }

    /// <summary>Total scrollable distance of the lyric content past the viewport (0 if it fits).</summary>
    private double ScrollMax()
    {
        if (_lines is null)
            return 0;
        double content = 10;
        foreach (var tb in _lineBlocks)
            content += tb.ActualHeight + 10;
        return Math.Max(0, content - LyricsViewport.ActualHeight);
    }

    private (double minY, double maxY) ScrollBounds()
    {
        double content = 10;
        foreach (var tb in _lineBlocks)
            content += tb.ActualHeight + 10;
        double h = LyricsViewport.ActualHeight;
        return (Math.Min(0, h / 2 - content), h / 2);
    }

    // ---- Karaoke (borderless fullscreen) ----------------------------------------

    private void OnToggleKaraoke(object sender, RoutedEventArgs e)
    {
        _karaoke = !_karaoke;
        if (_karaoke)
        {
            Native.GetWindowRect(_hwnd, out _preKaraokeRect);
            var screen = System.Windows.Forms.Screen.FromHandle(_hwnd).Bounds;
            Native.SetBounds(_hwnd, screen.Left, screen.Top, screen.Width, screen.Height);
        }
        else
        {
            var r = _preKaraokeRect;
            Native.SetBounds(_hwnd, r.left, r.top, r.right - r.left, r.bottom - r.top);
            RestoreCursor();
        }
        UpdateKaraokeVisual();
        ApplySettings();   // re-applies font/centre (karaoke override) + recenters
    }

    private void UpdateKaraokeVisual()
    {
        KaraokeIcon.Stroke = _karaoke ? AccentBrush : Brushes.White;
    }

    private void HideCursorIfKaraoke()
    {
        if (_karaoke)
            Cursor = Cursors.None;
    }

    private void RestoreCursor()
    {
        if (Cursor == Cursors.None)
            Cursor = null;
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Re-glue the glass backdrop on ANY position/size/z change — far more reliable
        // than WPF's LocationChanged/SizeChanged (which miss maximize, Snap, etc.).
        if (msg == WM_WINDOWPOSCHANGED)
        {
            SyncGlass();
            return IntPtr.Zero; // not handled — let WPF process it too
        }

        if (msg != WM_NCHITTEST)
            return IntPtr.Zero;

        handled = true;

        // When click-through is on, the UI window carries WS_EX_LAYERED | WS_EX_TRANSPARENT, so
        // the OS excludes it from hit-testing entirely and this hook isn't even called — the mouse
        // falls straight through to whatever is behind. Nothing to do here for that case.

        long lp = lParam.ToInt64();
        int px = (short)(lp & 0xFFFF);
        int py = (short)((lp >> 16) & 0xFFFF);

        // Reveal the header ONLY when the cursor actually MOVES (real screen coords from lParam).
        // A stationary mouse inside the window then lets the header — and, in karaoke, the cursor —
        // auto-hide; it comes back as soon as the mouse moves.
        if (px != _lastHitX || py != _lastHitY)
        {
            _lastHitX = px;
            _lastHitY = py;
            RevealHeader();
        }

        // Interactive controls (header buttons, settings panel) must receive clicks
        // instead of dragging/resizing the window.
        if (IsOverInteractive(px, py))
            return (IntPtr)HTCLIENT;

        Native.GetWindowRect(_hwnd, out var r);

        bool left = px < r.left + ResizeBorder;
        bool right = px >= r.right - ResizeBorder;
        bool top = py < r.top + ResizeBorder;
        bool bottom = py >= r.bottom - ResizeBorder;

        int code = (top, bottom, left, right) switch
        {
            (true, _, true, _) => HTTOPLEFT,
            (true, _, _, true) => HTTOPRIGHT,
            (_, true, true, _) => HTBOTTOMLEFT,
            (_, true, _, true) => HTBOTTOMRIGHT,
            (true, _, _, _) => HTTOP,
            (_, true, _, _) => HTBOTTOM,
            (_, _, true, _) => HTLEFT,
            (_, _, _, true) => HTRIGHT,
            // Interior is HTCLIENT (not HTCAPTION): the window is dragged ONLY from the header
            // (via DragMove in OnHeaderDrag); the body keeps the mouse for scroll/drag-scroll.
            _ => HTCLIENT,
        };
        return (IntPtr)code;
    }

    // ---- Now-playing + lyrics ---------------------------------------------------

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        LyricsCache.EnsureVersion();
        _lyrics.Changed += OnLyricsState;

        I18n.SetLanguage(_settings.Lang);
        I18n.Changed += Localize;

        _tray = new TrayIcon();
        _tray.ToggleVisibilityRequested += ToggleVisibility;
        _tray.ClickThroughToggled += on => _settings.ClickThrough = on;
#if DEBUG
        _tray.DiagnosticsRequested += ShowDiagnostics;
#endif
        _tray.QuitRequested += Quit;
        _tray.SetVisibleState(IsVisible);
        _tray.SetClickThroughChecked(_settings.ClickThrough);

        SettingsOverlay.Initialize(_settings);
        SettingsOverlay.CloseRequested += CloseSettings;
        _settings.PropertyChanged += OnSettingChanged;
        _headerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
        _headerTimer.Tick += (_, _) => { _headerTimer!.Stop(); HideHeaderIfAllowed(); };
        HeaderBar.MouseEnter += (_, _) => { _headerHovered = true; _headerTimer?.Stop(); RevealHeader(); };
        HeaderBar.MouseLeave += (_, _) => { _headerHovered = false; RestartHeaderTimer(); };

        // Recompute the bottom edge fade when the footer appears/disappears or changes height,
        // so its height is included once layout has settled.
        FooterBar.SizeChanged += (_, _) => UpdateEdgeFade();

        // Manual scroll over the synced lyrics (wheel + click-drag), which pauses auto-scroll
        // and shows the resync pill.
        LyricsViewport.MouseWheel += OnLyricsWheel;
        LyricsViewport.MouseLeftButtonDown += OnLyricsDragStart;
        LyricsViewport.MouseMove += OnLyricsDragMove;
        LyricsViewport.MouseLeftButtonUp += OnLyricsDragEnd;

        ApplySettings();
        Localize();
        UpdatePinVisual();
        UpdateEdgeFade();

        await _smtc.InitializeAsync();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollTimer.Tick += async (_, _) => await PollAsync();
        _pollTimer.Start();

        _interpTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _interpTimer.Tick += OnInterpTick;
        _interpTimer.Start();

        await PollAsync();
    }

    private async System.Threading.Tasks.Task PollAsync()
    {
        NowPlaying np = await _smtc.GetNowPlayingAsync();
        _lastNp = np;
        _lastNpAt = DateTime.UtcNow;

        // Keep the diagnostics header aware of which app feeds the lookups (and if it's a browser).
        DiagLog.SetSource(np.SourceAppId, np.IsBrowser, np.DurationMs);

        UpdateHeaderTrack();

        // Per-song lyrics offset: on a song change (or no track) load that song's saved offset,
        // resetting to 0 for songs never adjusted — so a tuned song doesn't shift the rest.
        string offsetKey = np.HasSession ? np.Key : "";
        if (offsetKey != _offsetKey)
        {
            _offsetKey = offsetKey;
            _loadingOffset = true;
            _settings.Offset = string.IsNullOrEmpty(offsetKey) ? 0 : OffsetStore.Get(offsetKey);
            _loadingOffset = false;
        }

        // Briefly reveal the header on a song change so the new title is visible (README §3.1).
        if (np.HasSession && np.Key != _lastShownKey)
        {
            _lastShownKey = np.Key;
            RevealHeader();
        }

        _lyrics.OnTrack(np);
    }

    private LyricsState _lastState = new(LyricsKind.NoTrack);

    private void OnLyricsState(LyricsState state)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => OnLyricsState(state));
            return;
        }

        _lastState = state;

        switch (state.Kind)
        {
            case LyricsKind.Synced when state.Lines is { Count: > 0 }:
                ShowSynced(state.Lines);
                break;
            case LyricsKind.Plain when state.PlainText is not null:
                ShowPlain(state.PlainText);
                break;
            case LyricsKind.Searching:
                ShowSearching();
                break;
            case LyricsKind.Instrumental:
                ShowStatus(I18n.T("instrumental"));
                break;
            case LyricsKind.NotFound:
                ShowStatus(I18n.T("notfound"));
                break;
            case LyricsKind.OnlyUnsynced:
                ShowStatus(I18n.T("onlyUnsynced"));
                break;
            default:
                ShowStatus(I18n.T("openSpotify"));
                break;
        }
    }

    /// <summary>Re-render whatever status is currently shown in the new language. Synced/plain
    /// lyrics are content, not localized, so they are left untouched.</summary>
    private void Localize()
    {
        KaraokeButton.ToolTip = I18n.T("karaoke");
        PinButton.ToolTip = I18n.T("pin");
        SettingsButton.ToolTip = I18n.T("settingsTitle");
        CloseButton.ToolTip = I18n.T("close");
        ResyncButton.Content = I18n.T("resync");

        switch (_lastState.Kind)
        {
            case LyricsKind.Searching:
            case LyricsKind.Instrumental:
            case LyricsKind.NotFound:
            case LyricsKind.OnlyUnsynced:
            case LyricsKind.NoTrack:
                OnLyricsState(_lastState);
                break;
        }
    }

    private void ShowStatus(string text)
    {
        StopSearchDots();
        _lines = null;
        _lineBlocks.Clear();
        LyricsPanel.Children.Clear();
        LyricsViewport.Visibility = Visibility.Collapsed;
        PlainScroll.Visibility = Visibility.Collapsed;
        ResyncButton.Visibility = Visibility.Collapsed;
        LyricsScroll.Visibility = Visibility.Collapsed;
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
    }

    /// <summary>Two-line "searching" status: the track being looked up, then "Searching…".</summary>
    private void ShowSearching()
    {
        _lines = null;
        _lineBlocks.Clear();
        LyricsPanel.Children.Clear();
        LyricsViewport.Visibility = Visibility.Collapsed;
        PlainScroll.Visibility = Visibility.Collapsed;
        LyricsScroll.Visibility = Visibility.Collapsed;
        ResyncButton.Visibility = Visibility.Collapsed;

        StatusText.Inlines.Clear();
        string track = !string.IsNullOrWhiteSpace(_lastNp.Artist)
            ? $"{_lastNp.Title} — {_lastNp.Artist}"
            : _lastNp.Title;
        if (!string.IsNullOrWhiteSpace(track))
        {
            StatusText.Inlines.Add(new System.Windows.Documents.Run(track)
            { FontWeight = FontWeights.SemiBold, FontSize = 16 });
            StatusText.Inlines.Add(new System.Windows.Documents.LineBreak());
        }

        // Animate the trailing dots (1→2→3→…) so there's motion while the lookup runs.
        _searchBase = I18n.T("searching").TrimEnd('.', '…', ' ');
        _searchDots = 1;
        _searchRun = new System.Windows.Documents.Run(_searchBase + ".");
        StatusText.Inlines.Add(_searchRun);
        StatusText.Visibility = Visibility.Visible;
        StartSearchDots();
    }

    private void StartSearchDots()
    {
        _searchTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _searchTimer.Tick -= OnSearchDotsTick;
        _searchTimer.Tick += OnSearchDotsTick;
        _searchTimer.Start();
    }

    private void OnSearchDotsTick(object? sender, EventArgs e)
    {
        _searchDots = _searchDots % 3 + 1;       // 1, 2, 3, 1, …
        if (_searchRun is not null)
            _searchRun.Text = _searchBase + new string('.', _searchDots);
    }

    private void StopSearchDots()
    {
        _searchTimer?.Stop();
        _searchRun = null;
    }

    private void ShowPlain(string text)
    {
        StopSearchDots();
        _lines = null;
        _lineBlocks.Clear();
        LyricsPanel.Children.Clear();
        LyricsViewport.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;
        ResyncButton.Visibility = Visibility.Collapsed;
        LyricsScroll.Visibility = Visibility.Collapsed;
        PlainTextBlock.Text = text;
        PlainScroll.ScrollToVerticalOffset(0);
        PlainScroll.Visibility = Visibility.Visible;
    }

    private void ShowSynced(IReadOnlyList<LyricLine> lines)
    {
        StopSearchDots();
        StatusText.Visibility = Visibility.Collapsed;
        PlainScroll.Visibility = Visibility.Collapsed;
        LyricsViewport.Visibility = Visibility.Visible;

        // A fresh song resumes auto-scroll and hides the resync pill.
        _userScrolling = false;
        ResyncButton.Visibility = Visibility.Collapsed;

        _lines = lines;
        _lineBlocks.Clear();
        LyricsPanel.Children.Clear();

        foreach (var line in lines)
        {
            var tb = new System.Windows.Controls.TextBlock
            {
                Text = string.IsNullOrWhiteSpace(line.Text) ? "♪" : line.Text,
                FontSize = _fontSize,
                FontWeight = FontWeights.Bold,
                Foreground = _textBrush,
                Opacity = _inactiveOpacity,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = _align,
                LineHeight = _fontSize * 1.28,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Margin = new Thickness(0, 5, 0, 5),
            };
            _lineBlocks.Add(tb);
            LyricsPanel.Children.Add(tb);
        }

        _activeIndex = -1;
        _lastScrollTarget = double.NaN;
        LyricsTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        LyricsTranslate.Y = 0;

        // Pre-place on line 0 (after layout) so playback reaching it doesn't jump and the
        // first line is properly centred from the start.
        Dispatcher.BeginInvoke(new Action(() => { ScrollToIndex(0, animate: false); UpdateScrollBar(); }),
            DispatcherPriority.Loaded);
    }

    private void OnInterpTick(object? sender, EventArgs e)
    {
        UpdateProgress();
        UpdateScrollBar();

        if (_lines is null || _lines.Count == 0 || LyricsViewport.Visibility != Visibility.Visible)
            return;

        double pos = _lastNp.PositionMs;
        if (_lastNp.IsPlaying)
            pos += (DateTime.UtcNow - _lastNpAt).TotalMilliseconds;
        pos -= _offsetMs;

        // The 250 ms tick only DETECTS when the active line changes (derived from the
        // interpolated playback time); the actual re-centring is event-driven.
        int active = FindActiveIndex(pos);
        if (active == _activeIndex)
            return;

        _activeIndex = active;
        UpdateHighlight();
        // While the user is manually scrolling, keep highlighting the active line but don't
        // hijack the view — they resume auto-scroll via the resync pill.
        if (!_userScrolling)
            ScrollToIndex(Math.Max(active, 0), animate: true);
    }

    // Fixed-pixel top/bottom fade (so it does NOT grow with a taller window). Pure
    // transparency on the lyrics — faithful to whatever glass/colour is behind, no band.
    // Tune this freely; it behaves as absolute pixels up to ~half the viewport height (above
    // that the top and bottom fade bands would meet, so it is clamped to h/2).
    private const double EdgeFadePx = 30;

    private void UpdateEdgeFade()
    {
        // Use the viewport's (Grid) height — a reliable, content-independent size.
        double h = LyricsViewport.ActualHeight;
        if (h <= 0)
            return;

        double top = EdgeFadePx;
        // When the progress/times row is shown, fade the lyrics out further at the BOTTOM — enough
        // to clear the footer's own height — so less text overlaps the bar/times and they read
        // cleanly.
        double bottom = EdgeFadePx;
        if (FooterBar.Visibility == Visibility.Visible)
            bottom += FooterBar.ActualHeight;

        // Keep a sliver of fully-opaque lyrics between the two bands (clamp so they never overlap).
        top = Math.Min(top, h / 2);
        bottom = Math.Min(bottom, h - top - 1);

        // ABSOLUTE mapping pins the mask to the visible viewport box (0..h), NOT to the
        // scrolling StackPanel's content bounds — so only the top/bottom edges fade, the
        // same fixed amount regardless of where playback is in the song.
        var mask = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, h),
            MappingMode = BrushMappingMode.Absolute,
        };
        mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, top / h));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 1 - bottom / h));
        mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        mask.Freeze();

        // Apply the mask to the CANVAS that wraps the scrolling StackPanel (not the panel itself),
        // so the Absolute mapping above is anchored to the fixed viewport box and only the visible
        // top/bottom edges fade — never the song's start/end.
        LyricsCanvas.OpacityMask = mask;
    }

    /// <summary>Re-centre the current active line instantly (e.g. after a font change).</summary>
    private void RecenterInstant()
    {
        if (_lines is null || LyricsViewport.Visibility != Visibility.Visible)
            return;
        _lastScrollTarget = double.NaN; // force recompute
        ScrollToIndex(Math.Max(_activeIndex, 0), animate: false);
    }

    private bool _recenterQueued;

    /// <summary>Keep the active line centred DURING a resize: recenter instantly (no animation, so
    /// nothing trails) but deferred one dispatcher cycle to AFTER layout, so line heights have
    /// re-wrapped to the new width before we measure. Coalesced so we recenter at most once per
    /// frame. Skipped while the user is hand-scrolling.</summary>
    private void QueueRecenter()
    {
        if (_recenterQueued)
            return;
        _recenterQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _recenterQueued = false;
            if (_lines is null || LyricsViewport.Visibility != Visibility.Visible || _userScrolling)
                return;
            _lastScrollTarget = double.NaN;
            ScrollToIndex(Math.Max(_activeIndex, 0), animate: false);
            UpdateScrollBar();
        }), DispatcherPriority.Render);
    }

    private int FindActiveIndex(double pos)
    {
        int idx = -1;
        for (int i = 0; i < _lines!.Count; i++)
        {
            if (_lines[i].TimeMs <= pos) idx = i;
            else break;
        }
        return idx;
    }

    private void UpdateHighlight()
    {
        for (int i = 0; i < _lineBlocks.Count; i++)
        {
            var tb = _lineBlocks[i];
            tb.Foreground = _textBrush;
            tb.Opacity = i == _activeIndex ? 1.0 : _inactiveOpacity;
        }
    }

    private void ScrollToIndex(int idx, bool animate)
    {
        if (idx < 0 || idx >= _lineBlocks.Count || LyricsViewport.ActualHeight <= 0)
            return;

        // Compute the line's layout offset by summing real heights (transform-independent,
        // so the centring never drifts as the panel scrolls). Each block has 5px top+bottom
        // margin, so its slot is ActualHeight + 10 and its content starts 5px in.
        const double lineMargin = 10;
        double top = 5;
        for (int i = 0; i < idx; i++)
            top += _lineBlocks[i].ActualHeight + lineMargin;

        double center = top + _lineBlocks[idx].ActualHeight / 2;
        double target = Math.Round(LyricsViewport.ActualHeight / 2 - center); // pixel-snap

        if (!double.IsNaN(_lastScrollTarget) && Math.Abs(target - _lastScrollTarget) < 1)
            return; // already there — avoid re-animating to the same spot every tick
        _lastScrollTarget = target;

        if (animate)
        {
            var anim = new DoubleAnimation(target, TimeSpan.FromSeconds(0.4))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            LyricsTranslate.BeginAnimation(TranslateTransform.YProperty, anim);
        }
        else
        {
            LyricsTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            LyricsTranslate.Y = target;
        }
    }
}
