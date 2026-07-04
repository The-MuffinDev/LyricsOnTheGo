using System;

namespace LyricsOnTheGo.Services;

/// <summary>
/// Detects whether an SMTC SourceAppUserModelId belongs to a web browser. AUMIDs are matched by
/// case-insensitive substring because their exact shape varies per install/profile (e.g. Chrome
/// reports "Chrome" or "Chrome.PROFILE_HASH", Edge "MSEdge", Firefox sometimes an install hash).
/// The diagnostics window shows the live AUMID, so unknown browsers can be added to the list.
/// </summary>
public static class BrowserDetect
{
    private static readonly string[] Tokens =
    {
        "chrome",          // Google Chrome (also matches Chromium)
        "msedge",          // Microsoft Edge
        "microsoftedge",   // legacy UWP Edge
        "firefox",
        "mozilla",
        "308046B0AF4A39CB", // Firefox default-install AUMID hash
        "opera",
        "brave",
        "vivaldi",
        "librewolf",
        "waterfox",
        "thorium",
    };

    /// <summary>True when the AUMID looks like a web browser session.</summary>
    public static bool IsBrowser(string? sourceAppId)
    {
        if (string.IsNullOrEmpty(sourceAppId))
            return false;

        foreach (var token in Tokens)
            if (sourceAppId.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
