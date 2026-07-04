using System.Threading;
using System.Threading.Tasks;
using LyricsOnTheGo.Models;

namespace LyricsOnTheGo.Services;

/// <summary>
/// A source of lyrics for a single track. Implementations must be self-contained and
/// tolerant: any failure (network, throttling, bad JSON, cancellation) is reported as
/// <see cref="LyricsResult.NotFound"/> rather than thrown, so the aggregator can race
/// several providers without one bad source taking down the lookup.
/// </summary>
public interface ILyricsProvider
{
    /// <summary>Short, stable id used in <see cref="LyricsResult.Source"/> and logs (e.g. "lrclib", "lrclib-local").</summary>
    string Name { get; }

    /// <summary>Looks up lyrics for a track; returns <see cref="LyricsResult.NotFound"/> on any failure.</summary>
    Task<LyricsResult> FetchAsync(LyricsQuery query, CancellationToken ct);
}
