namespace LyricsOnTheGo.Models;

/// <summary>
/// Input to a lyrics lookup. Carries the SMTC metadata plus <see cref="FromBrowser"/>, which
/// providers use to switch strategy: browser sources (YouTube in Chrome/Edge/Firefox…) often
/// report the uploader/channel as the artist, so providers search by title only there.
/// </summary>
public sealed record LyricsQuery(
    string Title,
    string Artist,
    string Album,
    double DurationMs,
    bool FromBrowser);
