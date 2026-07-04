using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LyricsOnTheGo.Models;

/// <summary>
/// All user settings (README §4), persisted as JSON in the app's local data dir. Raises
/// <see cref="INotifyPropertyChanged"/> so the UI can bind and the overlay can live-apply.
/// Autostart is intentionally NOT stored here — it is OS state (HKCU\…\Run).
/// </summary>
public sealed class Settings : INotifyPropertyChanged
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LyricsOnTheGo");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private string _textColor = "#EDEDED";
    private string _bgColor = "#080808";
    private int _bgOpacity = 35;       // 0–100
    private int _fontSize = 24;        // 14–64
    private int _dim = 45;             // 10–90, step 5
    private string _align = "left";    // left | center
    private int _offset;               // −6000…+6000 ms, step 50 (inverted)
    private bool _clickThrough;
    private bool _plainFallback = true;
    private bool _autoHideHeader = true;
    private bool _showProgress;
    private bool _showTimes;
    private string _lang = "en";       // en | es

    public string TextColor { get => _textColor; set => Set(ref _textColor, value); }
    public string BgColor { get => _bgColor; set => Set(ref _bgColor, value); }
    public int BgOpacity { get => _bgOpacity; set => Set(ref _bgOpacity, Math.Clamp(value, 0, 100)); }
    public int FontSize { get => _fontSize; set => Set(ref _fontSize, Math.Clamp(value, 14, 72)); }
    public int Dim { get => _dim; set => Set(ref _dim, Math.Clamp(value, 10, 90)); }
    public string Align { get => _align; set => Set(ref _align, value); }
    // Offset is per-song (persisted by OffsetStore, not globally) so a tuned song doesn't shift
    // every other correctly-synced track. Excluded from settings.json.
    [JsonIgnore]
    public int Offset { get => _offset; set => Set(ref _offset, Math.Clamp(value, -6000, 6000)); }
    public bool ClickThrough { get => _clickThrough; set => Set(ref _clickThrough, value); }
    public bool PlainFallback { get => _plainFallback; set => Set(ref _plainFallback, value); }
    public bool AutoHideHeader { get => _autoHideHeader; set => Set(ref _autoHideHeader, value); }
    public bool ShowProgress { get => _showProgress; set => Set(ref _showProgress, value); }
    public bool ShowTimes { get => _showTimes; set => Set(ref _showTimes, value); }
    public string Lang { get => _lang; set => Set(ref _lang, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions);
                if (loaded is not null)
                    return loaded;
            }
        }
        catch { /* fall back to defaults */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch { /* best-effort */ }
    }

    /// <summary>Reset every value to its default (keeps the same instance so bindings update).</summary>
    public void ResetToDefaults()
    {
        var d = new Settings();
        TextColor = d.TextColor;
        BgColor = d.BgColor;
        BgOpacity = d.BgOpacity;
        FontSize = d.FontSize;
        Dim = d.Dim;
        Align = d.Align;
        Offset = d.Offset;
        ClickThrough = d.ClickThrough;
        PlainFallback = d.PlainFallback;
        AutoHideHeader = d.AutoHideHeader;
        ShowProgress = d.ShowProgress;
        ShowTimes = d.ShowTimes;
        Lang = d.Lang;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
