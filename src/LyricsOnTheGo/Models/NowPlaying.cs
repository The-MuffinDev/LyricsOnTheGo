namespace LyricsOnTheGo.Models;

/// <summary>
/// A snapshot of the currently playing track from Windows SMTC, with the position
/// already interpolated to "now".
/// </summary>
public sealed record NowPlaying
{
    public bool HasSession { get; init; }

    /// <summary>SMTC SourceAppUserModelId of the session (e.g. "Spotify.exe", "MSEdge"). Empty without a session.</summary>
    public string SourceAppId { get; init; } = "";

    /// <summary>True when the session comes from a web browser, where the reported artist is
    /// often the uploader/channel (YouTube) rather than the real artist. Set by SmtcReader.</summary>
    public bool IsBrowser { get; init; }

    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public double DurationMs { get; init; }
    public double PositionMs { get; init; }
    public bool IsPlaying { get; init; }

    public static NowPlaying None { get; } = new();

    /// <summary>Stable identity of the track (title/artist/album/duration) — changes on song change.</summary>
    public string Key => $"{Title}|{Artist}|{Album}|{(int)(DurationMs / 1000)}".ToLowerInvariant();
}
