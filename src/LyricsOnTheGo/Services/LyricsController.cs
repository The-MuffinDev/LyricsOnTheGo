using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LyricsOnTheGo.Models;

namespace LyricsOnTheGo.Services;

/// <summary>The kind of lyrics state currently shown for a track.</summary>
public enum LyricsKind { NoTrack, Searching, Synced, Plain, Instrumental, NotFound, OnlyUnsynced }

/// <summary>Immutable snapshot of the lyrics state, carrying synced lines or plain text when applicable.</summary>
public sealed record LyricsState(
    LyricsKind Kind,
    IReadOnlyList<LyricLine>? Lines = null,
    string? PlainText = null);

/// <summary>
/// Drives lyrics lookup for the current track: consults the disk cache first (skipped when a local
/// database is linked, which is fast enough not to need it), then runs a bounded retry loop against
/// the provider pipeline (max 5 attempts, 2500 ms backoff). Raises <see cref="Changed"/> on the
/// calling (UI) thread as the state progresses. Plain text is surfaced only when
/// <see cref="PlainFallback"/> is on; otherwise the track reports OnlyUnsynced.
/// </summary>
public sealed class LyricsController
{
    private const int MaxAttempts = 5;
    private const int RetryMs = 2500;

    private readonly LyricsAggregator _client = new(LyricsProviders.Entries);
    private string _currentKey = "";
    private CancellationTokenSource? _cts;

    public bool PlainFallback { get; set; } = true;

    public event Action<LyricsState>? Changed;

    /// <summary>Reacts to a now-playing change: cancels any in-flight lookup and starts a new one when the track changes.</summary>
    public void OnTrack(NowPlaying np)
    {
        if (!np.HasSession || (string.IsNullOrWhiteSpace(np.Title) && string.IsNullOrWhiteSpace(np.Artist)))
        {
            if (_currentKey != "")
            {
                _currentKey = "";
                _cts?.Cancel();
                Raise(new LyricsState(LyricsKind.NoTrack));
            }
            return;
        }

        string key = np.Key;
        if (key == _currentKey)
            return;

        _currentKey = key;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = FetchLoopAsync(np, _cts.Token);
    }

    private async Task FetchLoopAsync(NowPlaying np, CancellationToken ct)
    {
        Raise(new LyricsState(LyricsKind.Searching));

        string cacheKey = LyricsCache.Key(np.Title, np.Artist, np.Album, np.DurationMs);

        // With a local database linked, lookups are already instant — bypass the disk cache
        // entirely so results stay fresh and every lookup is timed live in diagnostics.
        bool useCache = !LocalDbConfig.IsLinked;

        if (useCache)
        {
            var cached = LyricsCache.Read(cacheKey);
            if (cached is not null)
            {
                DiagLog.LogCompleted(
                    LyricsAggregator.Track(np.Title, np.Artist), "cache",
                    DiagResult.CacheHit, cached.Source, 0, chosen: true);
                Deliver(cached);
                return;
            }
        }

        for (int attempt = 0; attempt < MaxAttempts && !ct.IsCancellationRequested; attempt++)
        {
            LyricsResult result;
            try { result = await _client.FetchAsync(np.Title, np.Artist, np.Album, np.DurationMs, ct); }
            catch { result = LyricsResult.NotFound; }

            if (ct.IsCancellationRequested)
                return;

            if (result.Found)
            {
                if (useCache)
                    LyricsCache.Write(cacheKey, result);
                Deliver(result);
                return;
            }

            try { await Task.Delay(RetryMs, ct); }
            catch (TaskCanceledException) { return; }
        }

        if (!ct.IsCancellationRequested)
            Raise(new LyricsState(LyricsKind.NotFound));
    }

    /// <summary>Translates a fetched result into a <see cref="LyricsState"/> and raises it, parsing synced LRC into lines.</summary>
    private void Deliver(LyricsResult result)
    {
        if (result.Instrumental)
        {
            Raise(new LyricsState(LyricsKind.Instrumental));
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.Synced))
        {
            var lines = LrcParser.Parse(result.Synced);
            if (lines.Count > 0)
            {
                Raise(new LyricsState(LyricsKind.Synced, Lines: lines));
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(result.Plain))
        {
            Raise(PlainFallback
                ? new LyricsState(LyricsKind.Plain, PlainText: result.Plain)
                : new LyricsState(LyricsKind.OnlyUnsynced));
            return;
        }

        Raise(new LyricsState(LyricsKind.NotFound));
    }

    private void Raise(LyricsState state) => Changed?.Invoke(state);
}
