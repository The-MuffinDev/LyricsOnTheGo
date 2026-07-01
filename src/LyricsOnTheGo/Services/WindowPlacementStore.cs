using System;
using System.IO;
using System.Text.Json;

namespace LyricsOnTheGo.Services;

/// <summary>The overlay's last window bounds, in physical pixels (matches the glass code, which
/// works in physical coordinates via Native.GetWindowRect/SetBounds).</summary>
public sealed record WindowPlacement(int X, int Y, int Width, int Height);

/// <summary>
/// Remembers the overlay's window position and size across runs (its own window.json, kept
/// separate from settings.json). Saved when the window is hidden/closed, restored on launch —
/// but only if the saved rectangle still lands on a connected monitor, so unplugging a display
/// can't strand the overlay off-screen.
/// </summary>
public static class WindowPlacementStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LyricsOnTheGo");
    private static readonly string FilePath = Path.Combine(Dir, "window.json");

    public static WindowPlacement? Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(FilePath));
        }
        catch { /* fall back to defaults */ }
        return null;
    }

    public static void Save(WindowPlacement placement)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(placement));
        }
        catch { /* best-effort */ }
    }
}
