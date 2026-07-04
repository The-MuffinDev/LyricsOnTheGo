// Developer-only diagnostics window. Excluded from Release builds (and therefore from the MSI), so
// end users never see it — the tray entry and all references are likewise gated with #if DEBUG.
#if DEBUG
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using LyricsOnTheGo.Services;

namespace LyricsOnTheGo.Diagnostics;

/// <summary>
/// A standalone (non-overlay) window for running the app "in production": live switches to
/// enable/disable each lyrics provider, and a scrolling table of every lookup. Each row appears
/// the moment a provider call STARTS ("searching…", with a live-ticking ms) and updates in place
/// when the call returns (result, frozen ms, and a "chosen" marker on the winner). Built in code
/// (no XAML) so it stays self-contained.
/// </summary>
public sealed class DiagnosticsWindow : Window
{
    private const int MaxRows = 500;

    private static readonly Brush PanelBg = Frozen(0x1C, 0x1C, 0x1C);
    private static readonly Brush ListBg = Frozen(0x14, 0x14, 0x14);
    private static readonly Brush Fg = Frozen(0xDD, 0xDD, 0xDD);
    private static readonly Brush Muted = Frozen(0x8A, 0x8A, 0x8A);

    private static readonly ResultBrushConverter ResultBrush = new();
    private static readonly ChosenWeightConverter ChosenWeight = new();
    private static readonly ChosenBackgroundConverter ChosenBackground = new();

    private readonly ObservableCollection<DiagCall> _rows = new();
    private readonly ListView _list;
    private readonly CheckBox _autoscroll;
    private readonly TextBlock _emptyHint;
    private readonly TextBlock _providersLabel;
    private readonly TextBlock _providersHint;
    private readonly TextBlock _sourceLabel;
    private readonly Button _clearButton;
    private readonly DispatcherTimer _pump;
    private readonly GridViewColumn _cTime, _cTrack, _cProvider, _cResult, _cDetail, _cMs;

    public DiagnosticsWindow()
    {
        Width = 940;
        Height = 580;
        MinWidth = 560;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = ListBg;
        Foreground = Fg;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        var root = new DockPanel { LastChildFill = true };

        // ---- Top: provider switches + toolbar ----------------------------------
        var top = new StackPanel { Background = PanelBg };
        DockPanel.SetDock(top, Dock.Top);

        _providersLabel = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            Foreground = Fg,
            Margin = new Thickness(12, 10, 12, 2),
        };
        _providersHint = new TextBlock
        {
            Foreground = Muted,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Margin = new Thickness(12, 0, 12, 6),
        };

        // Live SMTC source: which app feeds the lookups and whether it's a browser (title-only mode).
        _sourceLabel = new TextBlock
        {
            Foreground = Muted,
            FontSize = 11,
            Margin = new Thickness(12, 0, 12, 6),
        };

        var switches = new WrapPanel { Margin = new Thickness(12, 0, 12, 6) };
        foreach (var entry in LyricsProviders.Entries)
        {
            var cb = new CheckBox
            {
                Content = entry.Name,
                IsChecked = entry.Enabled,
                Foreground = Fg,
                Margin = new Thickness(0, 0, 18, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            string name = entry.Name;
            cb.Checked += (_, _) => LyricsProviders.SetEnabled(name, true);
            cb.Unchecked += (_, _) => LyricsProviders.SetEnabled(name, false);
            switches.Children.Add(cb);
        }

        var toolbar = new DockPanel { Margin = new Thickness(12, 2, 12, 10) };
        _clearButton = new Button
        {
            Padding = new Thickness(10, 3, 10, 3),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        _clearButton.Click += (_, _) => DiagLog.Clear();
        _autoscroll = new CheckBox
        {
            IsChecked = true,
            Foreground = Fg,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
        };
        var toolRight = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        toolRight.Children.Add(_autoscroll);
        toolRight.Children.Add(_clearButton);
        DockPanel.SetDock(toolRight, Dock.Right);
        toolbar.Children.Add(toolRight);

        top.Children.Add(_providersLabel);
        top.Children.Add(_providersHint);
        top.Children.Add(_sourceLabel);
        top.Children.Add(switches);
        top.Children.Add(new Border { Height = 1, Background = Frozen(0x2A, 0x2A, 0x2A), Margin = new Thickness(0, 2, 0, 6) });
        top.Children.Add(toolbar);
        root.Children.Add(top);

        // ---- Body: log table ----------------------------------------------------
        var gv = new GridView { AllowsColumnReorder = false };
        _cTime = Column("Time", nameof(DiagCall.Time), 74);
        _cTrack = Column("Track", nameof(DiagCall.Track), 260);
        _cProvider = Column("Provider", nameof(DiagCall.Provider), 90);
        _cResult = Column("Result", nameof(DiagCall.ResultText), 120);
        _cDetail = Column("Detail", nameof(DiagCall.Detail), 300);
        _cMs = Column("ms", nameof(DiagCall.MsText), 60);
        gv.Columns.Add(_cTime);
        gv.Columns.Add(_cTrack);
        gv.Columns.Add(_cProvider);
        gv.Columns.Add(_cResult);
        gv.Columns.Add(_cDetail);
        gv.Columns.Add(_cMs);

        _list = new ListView
        {
            View = gv,
            ItemsSource = _rows,
            Background = ListBg,
            BorderThickness = new Thickness(0),
            Foreground = Fg,
            ItemContainerStyle = BuildItemStyle(),
        };
        _list.Resources.Add(typeof(GridViewColumnHeader), BuildHeaderStyle());

        _emptyHint = new TextBlock
        {
            Foreground = Muted,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

        var body = new Grid();
        body.Children.Add(_list);
        body.Children.Add(_emptyHint);
        root.Children.Add(body);

        Content = root;

        // Seed with history, then follow live.
        foreach (var r in DiagLog.Snapshot())
            _rows.Add(r);
        _rows.CollectionChanged += (_, _) => UpdateEmptyHint();
        UpdateEmptyHint();

        DiagLog.Added += OnLogAdded;
        DiagLog.Cleared += OnLogCleared;
        DiagLog.SourceChanged += OnSourceChanged;
        I18n.Changed += ApplyLang;
        ApplyLang();

        // Drive the live ms counters (and flush completions) on the UI thread.
        _pump = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _pump.Tick += (_, _) =>
        {
            foreach (var r in _rows)
                r.Pump();
        };
        _pump.Start();

        Closed += (_, _) =>
        {
            _pump.Stop();
            DiagLog.Added -= OnLogAdded;
            DiagLog.Cleared -= OnLogCleared;
            DiagLog.SourceChanged -= OnSourceChanged;
            I18n.Changed -= ApplyLang;
        };
    }

    private void OnLogAdded(DiagCall call)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _rows.Add(call);
            while (_rows.Count > MaxRows)
                _rows.RemoveAt(0);
            if (_autoscroll.IsChecked == true)
                _list.ScrollIntoView(call);
        }));
    }

    private void OnLogCleared() => Dispatcher.BeginInvoke(new Action(() => _rows.Clear()));

    private void OnSourceChanged() => Dispatcher.BeginInvoke(new Action(UpdateSourceLabel));

    private void UpdateSourceLabel()
    {
        string app = DiagLog.SourceAppId;
        string text = app.Length == 0
            ? $"{I18n.T("diagSource")}: {I18n.T("diagSourceNone")}"
            : DiagLog.SourceIsBrowser
                ? $"{I18n.T("diagSource")}: {app} — {I18n.T("diagSourceBrowser")}"
                : $"{I18n.T("diagSource")}: {app}";

        double sec = DiagLog.SourceDurationMs / 1000.0;
        if (sec > 0)
            text += $"  ·  {I18n.T("diagTrackDuration")}: {(int)(sec / 60)}:{(int)sec % 60:00}";

        _sourceLabel.Text = text;
    }

    private void UpdateEmptyHint()
        => _emptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void ApplyLang()
    {
        Title = I18n.T("diagTitle");
        _providersLabel.Text = I18n.T("diagProviders");
        _providersHint.Text = I18n.T("diagProvidersHint");
        _clearButton.Content = I18n.T("diagClear");
        _autoscroll.Content = I18n.T("diagAutoscroll");
        _emptyHint.Text = I18n.T("diagWaiting");
        _cTime.Header = I18n.T("diagColTime");
        _cTrack.Header = I18n.T("diagColTrack");
        _cProvider.Header = I18n.T("diagColProvider");
        _cResult.Header = I18n.T("diagColResult");
        _cDetail.Header = I18n.T("diagColDetail");
        _cMs.Header = I18n.T("diagColMs");
        UpdateSourceLabel();
    }

    private static GridViewColumn Column(string header, string path, double width) => new()
    {
        Header = header,
        Width = width,
        DisplayMemberBinding = new Binding(path),
    };

    private static Style BuildItemStyle()
    {
        var style = new Style(typeof(ListViewItem));
        style.Setters.Add(new Setter(ForegroundProperty, new Binding(nameof(DiagCall.Result)) { Converter = ResultBrush }));
        style.Setters.Add(new Setter(FontWeightProperty, new Binding(nameof(DiagCall.Chosen)) { Converter = ChosenWeight }));
        style.Setters.Add(new Setter(BackgroundProperty, new Binding(nameof(DiagCall.Chosen)) { Converter = ChosenBackground }));
        style.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        return style;
    }

    private static Style BuildHeaderStyle()
    {
        var style = new Style(typeof(GridViewColumnHeader));
        style.Setters.Add(new Setter(BackgroundProperty, Frozen(0x22, 0x22, 0x22)));
        style.Setters.Add(new Setter(ForegroundProperty, Frozen(0xB0, 0xB0, 0xB0)));
        style.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
        style.Setters.Add(new Setter(BorderBrushProperty, Frozen(0x2A, 0x2A, 0x2A)));
        style.Setters.Add(new Setter(PaddingProperty, new Thickness(8, 4, 8, 4)));
        style.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
        return style;
    }

    internal static Brush BrushFor(DiagResult result) => result switch
    {
        DiagResult.Synced => Frozen(0x2F, 0xD4, 0x60),
        DiagResult.CacheHit => Frozen(0x35, 0xC7, 0xB0),
        DiagResult.Plain => Frozen(0xE0, 0xC0, 0x40),
        DiagResult.Instrumental => Frozen(0x7A, 0xA0, 0xFF),
        DiagResult.NotFound => Frozen(0xC8, 0x66, 0x60),
        DiagResult.Error => Frozen(0xE8, 0x9A, 0x3C),
        DiagResult.Searching => Frozen(0x9A, 0x9A, 0x9A),
        _ => Frozen(0xDD, 0xDD, 0xDD),
    };

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static readonly Brush ChosenTint = FrozenArgb(0x22, 0x17, 0xD3, 0x46);

    private static SolidColorBrush FrozenArgb(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private sealed class ResultBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => BrushFor(value is DiagResult r ? r : DiagResult.NotFound);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class ChosenWeightConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is true ? FontWeights.SemiBold : FontWeights.Normal;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class ChosenBackgroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is true ? ChosenTint : Brushes.Transparent;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
#endif
