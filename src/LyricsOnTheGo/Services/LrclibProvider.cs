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
/// Fetches lyrics from LRCLIB, tolerant of how different players report metadata. Three
/// endpoints are queried CONCURRENTLY (not in a cascade, which used to stack three 6 s
/// timeouts to ~18 s when LRCLIB was slow at peak): exact /api/get, structured
/// /api/search (track+artist), and a combined free-text /api/search?q=title+artist (the
/// same shape the website's search box uses, which matches when structured metadata doesn't).
/// A SYNCED hit wins, preferring the exact get, then structured, then combined-q — each
/// duration-matched; otherwise the best plain-only hit. Disk cache is consulted by the caller.
/// </summary>
public sealed class LrclibProvider : ILyricsProvider
{
    public string Name => "lrclib";

    private static readonly HttpClient Http = CreateHttp();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static HttpClient CreateHttp()
    {
        // 15 s (not 6): at peak, LRCLIB's structured search can take ~9 s to return — and it DOES
        // return (often 20 synced hits). A 6 s cap turned those into false "not found"s. Since the
        // three queries now run concurrently, this is a single 15 s window, not a stacked cascade.
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "LyricsOnTheGo/2.0.0 (https://github.com/LuisAnchondo)");
        return http;
    }

    public async Task<LyricsResult> FetchAsync(string title, string artist, string album, double durationMs, CancellationToken ct)
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
            var r = EntryToResult(getEntry, "api/get (exact)");
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

        // Report a timeout distinctly from a genuine miss so diagnostics don't cry "not found".
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

            var r = EntryToResult(entry, detail);
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

    private LyricsResult? EntryToResult(LrcLibEntry e, string detail)
    {
        if (e.Instrumental)
            return new LyricsResult { Found = true, Instrumental = true, Source = Name, Detail = detail };

        string synced = e.SyncedLyrics ?? "";
        string plain = e.PlainLyrics ?? "";
        if (string.IsNullOrWhiteSpace(synced) && string.IsNullOrWhiteSpace(plain))
            return null;

        return new LyricsResult { Found = true, Synced = synced, Plain = plain, Source = Name, Detail = detail };
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
            return (default, true); // our own 6 s timeout, not a caller cancellation
        }
        catch
        {
            return (default, false);
        }
    }

    private static string Esc(string s) => Uri.EscapeDataString(s ?? "");

    private sealed class LrcLibEntry
    {
        [JsonPropertyName("syncedLyrics")] public string? SyncedLyrics { get; set; }
        [JsonPropertyName("plainLyrics")] public string? PlainLyrics { get; set; }
        [JsonPropertyName("instrumental")] public bool Instrumental { get; set; }
        [JsonPropertyName("duration")] public double? Duration { get; set; }
    }
}
