using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Input;
using LyricsOnTheGo.Models;
using LyricsOnTheGo.Services;

namespace LyricsOnTheGo.Controls;

public partial class SettingsPanel : UserControl
{
    private Settings? _settings;

    /// <summary>Raised when the user closes the panel (host hides the overlay).</summary>
    public event Action? CloseRequested;

    public SettingsPanel()
    {
        InitializeComponent();
        Picker.ColorChanged += OnPickerColor;
    }

    public void Initialize(Settings settings)
    {
        _settings = settings;
        DataContext = settings;
        settings.PropertyChanged += OnSettingsChanged;

        I18n.Changed += Localize;
        Unloaded += (_, _) => I18n.Changed -= Localize;

        AutostartToggle.IsChecked = Autostart.IsEnabled();
        UpdateSegments();
        UpdateOffsetLabel();
        Localize();
    }

    /// <summary>Pull every label/hint/button text in the current language (README §7).</summary>
    private void Localize()
    {
        TitleText.Text = I18n.T("settingsTitle");

        // Group titles are shown uppercased to match the original visual style.
        HdrAppearance.Text = I18n.T("grpAppearance").ToUpperInvariant();
        HdrSync.Text = I18n.T("grpSync").ToUpperInvariant();
        HdrVisibility.Text = I18n.T("grpVisibility").ToUpperInvariant();
        HdrBehavior.Text = I18n.T("grpBehavior").ToUpperInvariant();
        HdrCache.Text = I18n.T("grpCache").ToUpperInvariant();

        LblTextColor.Text = I18n.T("textColor");
        LblBgColor.Text = I18n.T("bgColor");
        LblBgOpacity.Text = I18n.T("bgOpacity");
        HintBgOpacity.Text = I18n.T("bgOpacityHint");
        LblTextSize.Text = I18n.T("textSize");
        LblDim.Text = I18n.T("dimInactive");
        LblAlignment.Text = I18n.T("alignment");
        AlignLeftBtn.ToolTip = I18n.T("alignLeft");
        AlignCenterBtn.ToolTip = I18n.T("alignCenter");

        LblOffset.Text = I18n.T("offset");
        HintOffset.Text = I18n.T("offsetHint");

        LblAutohide.Text = I18n.T("autohide");
        HintAutohide.Text = I18n.T("autohideHint");
        LblShowProgress.Text = I18n.T("showProgress");
        LblShowTimes.Text = I18n.T("showTimes");

        LblAutostart.Text = I18n.T("autostart");
        HintAutostart.Text = I18n.T("autostartHint");
        LblClickthrough.Text = I18n.T("clickthrough");
        HintClickthrough.Text = I18n.T("clickthroughHint");
        LblPlainFallback.Text = I18n.T("plainFallback");

        HintCache.Text = I18n.T("cacheHint");
        ClearCacheLabel.Text = I18n.T("clearCache");
        ClearOffsetsLabel.Text = I18n.T("clearOffsets");

        HdrLocalDb.Text = I18n.T("grpLocalDb").ToUpperInvariant();
        HintLocalDb.Text = I18n.T("localDbHint");
        DbDumpsText.Text = I18n.T("localDbDownload");
        WarnLocalDb.Text = I18n.T("localDbWarn");
        SelectDbLabel.Text = I18n.T("selectDb");
        UnlinkDbLabel.Text = I18n.T("unlinkDb");
        UpdateLocalDbUi();

        ResetLabel.Text = I18n.T("reset");

        CoffeeLabel.Text = I18n.T("buyMeCoffee");
        VersionText.Text = $"LyricsOnTheGo v{AppVersion}";
    }

    /// <summary>Reflect the linked-database state: current path, status, and Unlink availability.</summary>
    private void UpdateLocalDbUi()
    {
        string? path = LocalDbConfig.DbPath;
        bool linked = LocalDbConfig.IsLinked;

        LocalDbPathText.Text = string.IsNullOrEmpty(path) ? I18n.T("localDbNone") : path;
        UnlinkDbBtn.IsEnabled = !string.IsNullOrEmpty(path);
        UnlinkDbBtn.Opacity = string.IsNullOrEmpty(path) ? 0.5 : 1.0;

        // The 117 GB size warning is only relevant before a database is linked.
        WarnLocalDb.Visibility = string.IsNullOrEmpty(path)
            ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        if (string.IsNullOrEmpty(path))
            LocalDbStatus.Text = "";
        else if (linked)
            LocalDbStatus.Text = I18n.T("localDbLinked");
        else
            LocalDbStatus.Text = I18n.T("localDbMissing");
        LocalDbStatus.Visibility = string.IsNullOrEmpty(LocalDbStatus.Text)
            ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    }

    private void OnSelectDb(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = I18n.T("selectDb"),
            Filter = "SQLite database (*.sqlite3;*.db;*.sqlite)|*.sqlite3;*.db;*.sqlite|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true)
            return;

        if (!LocalDbConfig.IsValidDump(dialog.FileName))
        {
            LocalDbStatus.Text = I18n.T("localDbInvalid");
            LocalDbStatus.Visibility = System.Windows.Visibility.Visible;
            return;
        }

        LocalDbConfig.SetPath(dialog.FileName);
        UpdateLocalDbUi();
    }

    private void OnUnlinkDb(object sender, System.Windows.RoutedEventArgs e)
    {
        LocalDbConfig.SetPath(null);
        UpdateLocalDbUi();
    }

    private void OnDbDumps(object sender, System.Windows.RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://lrclib.net/db-dumps") { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    /// <summary>Refresh OS-state controls each time the panel is opened.</summary>
    public void OnOpened()
    {
        AutostartToggle.IsChecked = Autostart.IsEnabled();
        CacheStatus.Text = "";
        CacheStatus.Visibility = System.Windows.Visibility.Collapsed;
        UpdateLocalDbUi();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Settings.Offset):
                UpdateOffsetLabel();
                break;
            case nameof(Settings.Align):
                UpdateSegments();
                break;
            case nameof(Settings.Lang):
                I18n.SetLanguage(_settings?.Lang);
                UpdateSegments();
                break;
        }
    }

    private void UpdateOffsetLabel()
    {
        if (_settings is null) return;
        int ms = _settings.Offset;
        string sign = ms > 0 ? "+" : ms < 0 ? "−" : "";
        OffsetValue.Text = $"{sign}{Math.Abs(ms) / 1000.0:0.0}s";
    }

    private void UpdateSegments()
    {
        if (_settings is null) return;
        AlignLeftBtn.Tag = _settings.Align == "left" ? "selected" : null;
        AlignCenterBtn.Tag = _settings.Align == "center" ? "selected" : null;
        LangEnBtn.Tag = _settings.Lang == "en" ? "selected" : null;
        LangEsBtn.Tag = _settings.Lang == "es" ? "selected" : null;
    }

    private void OnClose(object sender, System.Windows.RoutedEventArgs e) => CloseRequested?.Invoke();

    /// <summary>Drag the window from the settings header (ignoring the lang/close buttons); a
    /// double-click toggles borderless fullscreen, matching the main header.</summary>
    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        for (System.Windows.DependencyObject? cur = e.OriginalSource as System.Windows.DependencyObject;
             cur is not null; cur = System.Windows.Media.VisualTreeHelper.GetParent(cur))
        {
            if (cur is System.Windows.Controls.Primitives.ButtonBase)
                return;
        }

        if (e.ClickCount == 2)
        {
            (System.Windows.Window.GetWindow(this) as MainWindow)?.ToggleFullscreen();
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { System.Windows.Window.GetWindow(this)?.DragMove(); }
            catch { /* DragMove can throw if the button was already released */ }
        }
    }

    private void OnAlignLeft(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_settings is not null) _settings.Align = "left";
    }

    private void OnAlignCenter(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_settings is not null) _settings.Align = "center";
    }

    private string _colorTarget = "";

    private void OnPickTextColor(object sender, System.Windows.RoutedEventArgs e)
        => OpenColorPicker("text", _settings?.TextColor, TextSwatch);

    private void OnPickBgColor(object sender, System.Windows.RoutedEventArgs e)
        => OpenColorPicker("bg", _settings?.BgColor, BgSwatch);

    private void OpenColorPicker(string target, string? current, System.Windows.UIElement anchor)
    {
        _colorTarget = target;
        Picker.SetColor(current);
        ColorPopup.PlacementTarget = anchor;
        ColorPopup.IsOpen = true;
    }

    private void OnPickerColor(string hex)
    {
        if (_settings is null) return;
        if (_colorTarget == "text") _settings.TextColor = hex;
        else if (_colorTarget == "bg") _settings.BgColor = hex;
    }

    private void OnLangEn(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_settings is not null) _settings.Lang = "en";
    }

    private void OnLangEs(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_settings is not null) _settings.Lang = "es";
    }

    private void OnAutostartToggle(object sender, System.Windows.RoutedEventArgs e)
    {
        bool desired = AutostartToggle.IsChecked == true;
        if (!Autostart.SetEnabled(desired))
            AutostartToggle.IsChecked = !desired; // revert on failure
    }

    /// <summary>Clicking anywhere on a slider jumps the thumb to that point and immediately starts
    /// dragging it (so a press-and-hold keeps tracking the cursor), not just a single jump.</summary>
    private void OnSliderTrackPress(object sender, MouseButtonEventArgs e)
    {
        var slider = (Slider)sender;
        if (slider.Template.FindName("PART_Track", slider) is not System.Windows.Controls.Primitives.Track track
            || track.Thumb is null)
            return;

        // A press already on the thumb uses the default drag.
        if (track.Thumb.IsMouseOver)
            return;

        slider.Value = track.ValueFromPoint(e.GetPosition(track));

        // Force the track to re-arrange the thumb to the NEW value BEFORE handing it the gesture,
        // otherwise the thumb captures from its old position and the drag snaps back to it.
        slider.UpdateLayout();

        // Hand the gesture to the thumb so the same hold becomes a drag from the clicked point.
        track.Thumb.RaiseEvent(new MouseButtonEventArgs(e.MouseDevice, e.Timestamp, MouseButton.Left)
        {
            RoutedEvent = System.Windows.UIElement.MouseLeftButtonDownEvent,
            Source = track.Thumb,
        });
        e.Handled = true;
    }

    private void OnClearCache(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            int n = LyricsCache.Clear();
            CacheStatus.Text = I18n.T("cacheCleared").Replace("{n}", n.ToString());
        }
        catch
        {
            CacheStatus.Text = I18n.T("clearFailed");
        }
        CacheStatus.Visibility = System.Windows.Visibility.Visible;
    }

    private void OnClearOffsets(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            int n = OffsetStore.Clear();
            CacheStatus.Text = I18n.T("offsetsCleared").Replace("{n}", n.ToString());
            // Reset the live slider so the current song reflects the cleared state.
            if (_settings is not null) _settings.Offset = 0;
        }
        catch
        {
            CacheStatus.Text = I18n.T("clearFailed");
        }
        CacheStatus.Visibility = System.Windows.Visibility.Visible;
    }

    private void OnReset(object sender, System.Windows.RoutedEventArgs e)
    {
        _settings?.ResetToDefaults();
        AutostartToggle.IsChecked = Autostart.IsEnabled();
    }

    private void OnGitHub(object sender, System.Windows.RoutedEventArgs e)
    {
        const string url = "https://github.com/The-MuffinDev";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private void OnBuyMeCoffee(object sender, System.Windows.RoutedEventArgs e)
    {
        const string url = "https://www.buymeacoffee.com/TheMuffinDev";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    /// <summary>App version (major.minor.patch) from the assembly — the single source in the .csproj.</summary>
    private static string AppVersion
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }
}
