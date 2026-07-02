using System.Text.Json.Serialization;

namespace LyricsOnTheGo.Models;

/// <summary>Outcome of a lyrics lookup. The non-ignored fields are what gets serialized to the disk cache.</summary>
public sealed record LyricsResult
{
    public bool Found { get; init; }
    public bool Instrumental { get; init; }
    public string Synced { get; init; } = "";
    public string Plain { get; init; } = "";
    public string Source { get; init; } = "";

    /// <summary>Human-readable note about what the provider matched (e.g. the candidate title +
    /// duration delta), surfaced in the diagnostics window. Not persisted to the cache.</summary>
    [JsonIgnore]
    public string Detail { get; init; } = "";

    /// <summary>The lookup didn't fail because the song is absent — the source timed out (or errored).
    /// Distinguishes a genuine "not found" from "source too slow" in diagnostics. Not persisted.</summary>
    [JsonIgnore]
    public bool TimedOut { get; init; }

    public static LyricsResult NotFound { get; } = new();
}
