using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LyricsOnTheGo.Models;

namespace LyricsOnTheGo.Services;

/// <summary>
/// Fetches lyrics from the hosted LRCLIB API, tolerant of the varying metadata quality different
/// players report. Three endpoints are queried concurrently — exact /api/get, structured
/// /api/search (track+artist), and a free-text /api/search?q=title+artist (the shape the website's
/// search box uses, which matches when structured metadata does not). Concurrency bounds the worst
/// case to a single request timeout instead of the sum of all three. A synced hit wins, preferring
/// the exact get, then structured, then free-text — each duration-matched; otherwise the best
/// plain-only hit. Browser sources take a separate title-only path (see
/// <see cref="FetchBrowserAsync"/>). The disk cache is consulted by the caller, not here.
/// </summary>
public sealed class LrclibProvider : ILyricsProvider
{
    public string Name => "lrclib";

    private static readonly HttpClient Http = CreateHttp();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>App version (major.minor.patch) pulled from the assembly, so the User-Agent tracks
    /// the single version defined in the .csproj rather than a hard-coded string.</summary>
    private static string AppVersion
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "1.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    private static HttpClient CreateHttp()
    {
        // Generous timeout: at peak, LRCLIB's structured search can take ~9 s and still return valid
        // synced results. Because the three queries run concurrently, this bounds the whole lookup.
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"LyricsOnTheGo/{AppVersion} (https://github.com/LuisAnchondo)");
        return http;
    }

    public Task<LyricsResult> FetchAsync(LyricsQuery query, CancellationToken ct) =>
        query.FromBrowser
            ? FetchBrowserAsync(query.Title, query.DurationMs, ct)
            : FetchStandardAsync(query.Title, query.Artist, query.Album, query.DurationMs, ct);

    /// <summary>
    /// Browser sources (YouTube in Chrome/Edge/…) often report the uploader/channel as the
    /// artist, so the artist is never sent. Two concurrent title-only free-text searches — the
    /// raw video title and a cleaned version with "(Official Video)"-style decorations stripped —
    /// are merged into ONE candidate pool ranked by (entry name contained in the video title,
    /// duration proximity, pass). Ranking across both lists matters: the raw-title search can
    /// return a synced hit for the WRONG song (FTS ranks decoration/artist tokens high) while the
    /// cleaned search holds a near-exact match, so neither pass may win on arrival order alone.
    /// </summary>
    private async Task<LyricsResult> FetchBrowserAsync(string title, double durationMs, CancellationToken ct)
    {
        double targetSeconds = durationMs / 1000.0;

        string cleaned = TitleCleaner.Clean(title);
        bool hasCleaned = cleaned.Length > 0 && !string.Equals(cleaned, title, StringComparison.OrdinalIgnoreCase);

        var rawTask = GetAsync<List<LrcLibEntry>>($"https://lrclib.net/api/search?q={Esc(title)}", ct);
        var cleanedTask = hasCleaned
            ? GetAsync<List<LrcLibEntry>>($"https://lrclib.net/api/search?q={Esc(cleaned)}", ct)
            : null;

        var (rawList, t1) = await rawTask;
        List<LrcLibEntry>? cleanedList = null;
        bool t2 = false;
        if (cleanedTask is not null)
            (cleanedList, t2) = await cleanedTask;
        bool timedOut = t1 || t2;

        var titleTokens = TextNorm.TokenSet(title);

        LyricsResult? bestSynced = null, bestPlain = null;
        var syncedScore = WorstScore;
        var plainScore = WorstScore;

        void Consider(List<LrcLibEntry>? list, string detail, int pass)
        {
            if (list is null)
                return;
            foreach (var e in list)
            {
                var r = EntryToResult(e, detail, targetSeconds);
                if (r is null)
                    continue;

                double dur = e.Duration ?? 0;
                double diff = (targetSeconds > 0 && dur > 0) ? Math.Abs(dur - targetSeconds) : double.MaxValue;
                var score = (TextNorm.TitleContains(titleTokens, e.TrackName ?? ""), diff, pass);

                if (!string.IsNullOrWhiteSpace(e.SyncedLyrics))
                {
                    if (Better(score, syncedScore)) { bestSynced = r; syncedScore = score; }
                }
                else
                {
                    if (Better(score, plainScore)) { bestPlain = r; plainScore = score; }
                }
            }
        }

        Consider(rawList, "search (q=video title)", 0);
        Consider(cleanedList, "search (q=cleaned title)", 1);

        if (bestSynced is not null)
            return bestSynced;
        if (bestPlain is not null)
            return bestPlain;

        return timedOut
            ? LyricsResult.NotFound with { TimedOut = true, Detail = "timeout (LRCLIB slow)" }
            : LyricsResult.NotFound;
    }

    private static readonly (bool Match, double Diff, int Pass) WorstScore = (false, double.MaxValue, int.MaxValue);

    /// <summary>Candidate ranking for browser mode: title containment dominates, then duration
    /// proximity; the earlier pass (raw video title) only breaks exact ties.</summary>
    private static bool Better((bool Match, double Diff, int Pass) a, (bool Match, double Diff, int Pass) b)
    {
        if (a.Match != b.Match)
            return a.Match;
        if (Math.Abs(a.Diff - b.Diff) > 0.001)
            return a.Diff < b.Diff;
        return a.Pass < b.Pass;
    }

    private async Task<LyricsResult> FetchStandardAsync(string title, string artist, string album, double durationMs, CancellationToken ct)
    {
        double targetSeconds = durationMs / 1000.0;
        int durSec = Math.Max(0, (int)(durationMs / 1000));

        string getUrl = "https://lrclib.net/api/get"
            + $"?artist_name={Esc(artist)}&track_name={Esc(title)}&album_name={Esc(album)}&duration={durSec}";
        string structuredUrl = $"https://lrclib.net/api/search?track_name={Esc(title)}&artist_name={Esc(artist)}";
        string combined = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
        string qUrl = $"https://lrclib.net/api/search?q={Esc(combined)}";

        // Start all three at once; awaiting in order just collects results already in flight.
        var getTask = GetAsync<LrcLibEntry>(getUrl, ct);
        var structuredTask = GetAsync<List<LrcLibEntry>>(structuredUrl, ct);
        var qTask = GetAsync<List<LrcLibEntry>>(qUrl, ct);

        var (getEntry, t1) = await getTask;
        var (structuredList, t2) = await structuredTask;
        var (qList, t3) = await qTask;
        bool timedOut = t1 || t2 || t3;

        LyricsResult? bestPlain = null;

        // 1. Exact get — ideal when the player reports clean metadata (e.g. Spotify).
        if (getEntry is not null)
        {
            var r = EntryToResult(getEntry, "api/get (exact)", targetSeconds);
            if (r is not null)
            {
                if (!string.IsNullOrWhiteSpace(r.Synced))
                    return r;
                bestPlain ??= r;
            }
        }

        // 2. Structured search, then 3. combined free-text search (title + artist, like the website).
        var searches = new[]
        {
            (structuredList, "search (track+artist)"),
            (qList, "search (q=title+artist)"),
        };
        foreach (var (list, detail) in searches)
        {
            var (synced, plain) = SelectBest(list ?? new List<LrcLibEntry>(), targetSeconds, detail);
            if (synced is not null)
                return synced;
            bestPlain ??= plain;
        }

        if (bestPlain is not null)
            return bestPlain;

        // Distinguish a timeout from a genuine miss so diagnostics can report it accurately.
        return timedOut
            ? LyricsResult.NotFound with { TimedOut = true, Detail = "timeout (LRCLIB slow)" }
            : LyricsResult.NotFound;
    }

    /// <summary>Picks the synced hit whose duration is closest to the player's, plus the first plain fallback.</summary>
    private (LyricsResult? Synced, LyricsResult? Plain) SelectBest(
        List<LrcLibEntry> entries, double targetSeconds, string detail)
    {
        LyricsResult? bestSynced = null;
        double bestDiff = double.MaxValue;
        LyricsResult? bestPlain = null;

        foreach (var entry in entries)
        {
            bool hasSynced = !string.IsNullOrWhiteSpace(entry.SyncedLyrics);
            double dur = entry.Duration ?? 0.0;
            double diff = (targetSeconds > 0 && dur > 0) ? Math.Abs(dur - targetSeconds) : double.MaxValue;

            var r = EntryToResult(entry, detail, targetSeconds);
            if (r is null)
                continue;

            if (hasSynced)
            {
                if (bestSynced is null || diff < bestDiff)
                {
                    bestSynced = r;
                    bestDiff = diff;
                }
            }
            else
            {
                bestPlain ??= r;
            }
        }

        return (bestSynced, bestPlain);
    }

    /// <summary>Maps an LRCLIB entry to a <see cref="LyricsResult"/> (Detail carries the matched
    /// entry's name/artist and its duration + Δ vs the player's), or null when it carries no lyrics.</summary>
    private LyricsResult? EntryToResult(LrcLibEntry e, string detail, double targetSeconds)
    {
        string name = e.TrackName ?? "";
        string artist = e.ArtistName ?? "";
        if (name.Length > 0)
            detail += artist.Length > 0 ? $": “{name}” · {artist}" : $": “{name}”";
        detail += DurLabel(e.Duration ?? 0, targetSeconds);

        if (e.Instrumental)
            return new LyricsResult { Found = true, Instrumental = true, Source = Name, Detail = detail };

        string synced = e.SyncedLyrics ?? "";
        string plain = e.PlainLyrics ?? "";
        if (string.IsNullOrWhiteSpace(synced) && string.IsNullOrWhiteSpace(plain))
            return null;

        return new LyricsResult { Found = true, Synced = synced, Plain = plain, Source = Name, Detail = detail };
    }

    /// <summary>" · 3:33 (Δ2.0s)" for the matched entry, or "" when it reports no duration.</summary>
    private static string DurLabel(double dur, double target)
    {
        if (dur <= 0)
            return "";
        string label = $" · {(int)(dur / 60)}:{(int)dur % 60:00}";
        if (target > 0)
            label += $" (Δ{Math.Abs(dur - target):0.0}s)";
        return label;
    }

    /// <summary>GETs and deserializes; the bool reports a timeout (HttpClient.Timeout elapsed) as
    /// distinct from a track-change cancellation or a genuine empty/error response.</summary>
    private static async Task<(T? Value, bool TimedOut)> GetAsync<T>(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return (default, false);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            return (await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct), false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (default, true); // the request timeout elapsed, not a caller-requested cancellation
        }
        catch
        {
            return (default, false);
        }
    }

    private static string Esc(string s) => Uri.EscapeDataString(s ?? "");

    private sealed class LrcLibEntry
    {
        [JsonPropertyName("trackName")] public string? TrackName { get; set; }
        [JsonPropertyName("artistName")] public string? ArtistName { get; set; }
        [JsonPropertyName("syncedLyrics")] public string? SyncedLyrics { get; set; }
        [JsonPropertyName("plainLyrics")] public string? PlainLyrics { get; set; }
        [JsonPropertyName("instrumental")] public bool Instrumental { get; set; }
        [JsonPropertyName("duration")] public double? Duration { get; set; }
    }
}
