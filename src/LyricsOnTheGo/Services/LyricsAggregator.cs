using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LyricsOnTheGo.Models;

namespace LyricsOnTheGo.Services;

/// <summary>
/// Queries the enabled <see cref="ProviderEntry"/>s in parallel and returns the best result,
/// biased toward latency: it returns as soon as a good synced result is available rather than
/// waiting for every provider to finish.
///
/// Strategy:
///  - The instant the highest-priority provider (priority 0) returns SYNCED lyrics, return it.
///  - If a lower-priority provider returns SYNCED first, keep it but wait a short grace
///    window for a higher-priority source to beat it, then return the best synced seen.
///  - With no synced anywhere, collect everything and return the best fallback
///    (instrumental over plain; higher-priority provider wins ties).
/// Every call is recorded to <see cref="DiagLog"/> (provider, timing, outcome, which won).
/// Providers never throw — each maps its own failures to <see cref="LyricsResult.NotFound"/>.
/// </summary>
public sealed class LyricsAggregator
{
    // How long to let higher-priority providers catch up after a lower-priority synced hit.
    private const int GraceMs = 700;

    private readonly IReadOnlyList<ProviderEntry> _entries;

    public LyricsAggregator(IReadOnlyList<ProviderEntry> entries) => _entries = entries;

    /// <summary>Formats the "Title — Artist" label used for diagnostics and cache-hit rows.</summary>
    public static string Track(string title, string artist) =>
        string.IsNullOrWhiteSpace(artist) ? title : $"{title} — {artist}";

    /// <summary>Runs all enabled providers concurrently and returns the best result, recording each
    /// call to <see cref="DiagLog"/> and marking the winner.</summary>
    public async Task<LyricsResult> FetchAsync(LyricsQuery query, CancellationToken ct)
    {
        string track = Track(query.Title, query.Artist);

        var enabled = new List<ProviderEntry>();
        foreach (var e in _entries)
            if (e.Enabled)
                enabled.Add(e);

        if (enabled.Count == 0)
            return LyricsResult.NotFound;

        var pending = new List<Task<CallResult>>(enabled.Count);
        foreach (var e in enabled)
            pending.Add(RunAsync(e, track, query, ct));

        CallResult? best = null;

        while (pending.Count > 0)
        {
            var finished = await Task.WhenAny(pending);
            pending.Remove(finished);

            var c = finished.Result; // RunAsync never faults
            if (best is null || Score(c) > Score(best))
                best = c;

            if (!IsSynced(best.Result))
                continue;

            // Best possible: synced from the top-priority source. Take it now.
            if (best.Priority == 0)
                break;

            // A lower-priority synced hit — give higher-priority sources a brief grace.
            best = await GraceAsync(best, pending, ct);
            break;
        }

        // Leftover (not-awaited) provider calls keep running; their diagnostics rows finish live.
        if (best is not null && best.Result.Found)
            best.Call.MarkChosen();
        return best?.Result ?? LyricsResult.NotFound;
    }

    /// <summary>Waits up to <see cref="GraceMs"/> for a better (higher-priority) synced result.</summary>
    private static async Task<CallResult> GraceAsync(
        CallResult current, List<Task<CallResult>> pending, CancellationToken ct)
    {
        if (pending.Count == 0)
            return current;

        using var graceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        graceCts.CancelAfter(GraceMs);
        try
        {
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending).WaitAsync(graceCts.Token);
                pending.Remove(finished);

                var c = finished.Result;
                if (Score(c) > Score(current))
                    current = c;

                // Can't do better than a top-priority synced hit.
                if (current.Priority == 0 && IsSynced(current.Result))
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Grace elapsed (or the track changed): return the best synced seen so far.
        }

        return current;
    }

    /// <summary>Runs a single provider, timing it and recording the outcome to its diagnostics row.</summary>
    private static async Task<CallResult> RunAsync(
        ProviderEntry entry, string track, LyricsQuery query, CancellationToken ct)
    {
        // Register the diagnostics row before the call runs, so it appears immediately as
        // "searching…" with a live elapsed timer rather than only after the provider returns.
        var call = DiagLog.Begin(track, entry.Name);
        var sw = Stopwatch.StartNew();
        LyricsResult result;
        try
        {
            result = await entry.Provider.FetchAsync(query, ct);
        }
        catch
        {
            result = LyricsResult.NotFound;
        }
        sw.Stop();
        call.Complete(Classify(result), result.Detail, sw.ElapsedMilliseconds);
        return new CallResult(entry, result, sw.ElapsedMilliseconds, call);
    }

    /// <summary>Maps a provider result to the outcome shown in the diagnostics log.</summary>
    private static DiagResult Classify(LyricsResult r)
    {
        if (!r.Found)
            return r.TimedOut ? DiagResult.Error : DiagResult.NotFound;
        if (r.Instrumental)
            return DiagResult.Instrumental;
        if (!string.IsNullOrWhiteSpace(r.Synced))
            return DiagResult.Synced;
        if (!string.IsNullOrWhiteSpace(r.Plain))
            return DiagResult.Plain;
        return DiagResult.NotFound;
    }

    private static bool IsSynced(LyricsResult r) =>
        r.Found && !r.Instrumental && !string.IsNullOrWhiteSpace(r.Synced);

    // Kind dominates; provider priority only breaks ties (higher priority = higher score).
    private static int Score(CallResult c)
    {
        var r = c.Result;
        int kind =
            !r.Found ? 0 :
            IsSynced(r) ? 3000 :
            r.Instrumental ? 2000 :
            !string.IsNullOrWhiteSpace(r.Plain) ? 1000 : 0;
        return kind == 0 ? int.MinValue : kind - c.Priority;
    }

    private sealed record CallResult(ProviderEntry Entry, LyricsResult Result, long Ms, DiagCall Call)
    {
        public int Priority => Entry.Priority;
    }
}
