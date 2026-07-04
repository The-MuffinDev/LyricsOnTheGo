using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using LyricsOnTheGo.Models;

namespace LyricsOnTheGo.Services;

/// <summary>
/// Reads lyrics straight from a local LRCLIB SQLite dump (github.com/tranxuanthang/lrclib),
/// running the SAME queries the server does: an exact match on the normalized
/// name_lower/artist_name_lower columns within a ±2 s duration window, then an FTS5 search over
/// tracks_fts. The database is opened READ-ONLY (nothing is written to it). Active only while a
/// valid dump is linked via <see cref="LocalDbConfig"/>; otherwise it returns NotFound instantly.
/// </summary>
public sealed class LrclibLocalProvider : ILyricsProvider
{
    public string Name => "lrclib-local";

    public async Task<LyricsResult> FetchAsync(LyricsQuery query, CancellationToken ct)
    {
        string? db = LocalDbConfig.DbPath;
        if (string.IsNullOrEmpty(db) || !File.Exists(db) || string.IsNullOrWhiteSpace(query.Title))
            return LyricsResult.NotFound;

        try
        {
            // SQLite calls are synchronous/blocking; keep them off the aggregator's path.
            return await Task.Run(() => Query(db!, query), ct);
        }
        catch (OperationCanceledException) { return LyricsResult.NotFound; }
        catch { return LyricsResult.NotFound; }
    }

    /// <summary>Opens the dump read-only and resolves the best result: exact match first, FTS5 second.
    /// Browser sources skip the artist entirely — see <see cref="BrowserQuery"/>.</summary>
    private static LyricsResult Query(string db, LyricsQuery query)
    {
        double target = query.DurationMs / 1000.0;
        string nameLower = TextNorm.Normalize(query.Title);
        string artistLower = TextNorm.Normalize(query.Artist);

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = db,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();

        using var conn = new SqliteConnection(cs);
        conn.Open();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA query_only=ON; PRAGMA temp_store=MEMORY; PRAGMA mmap_size=268435456;";
            pragma.ExecuteNonQuery();
        }

        if (query.FromBrowser)
            return BrowserQuery(conn, query.Title, target);

        LyricsResult? instrumental = null;
        LyricsResult? bestPlain = null;

        // 1. Exact get (name + artist + duration ±2), prefer synced — the server's fast path.
        var exact = ExactGet(conn, nameLower, artistLower, target);
        if (exact is not null)
        {
            if (!string.IsNullOrWhiteSpace(exact.Synced))
                return exact;
            if (exact.Instrumental) instrumental = exact;
            else if (!string.IsNullOrWhiteSpace(exact.Plain)) bestPlain = exact;
        }

        // 2. FTS5 search — recovers matches the exact metadata missed.
        var (synced, plain) = FtsSearch(conn, nameLower, artistLower, target);
        if (synced is not null)
            return synced;
        bestPlain ??= plain;

        return instrumental ?? bestPlain ?? LyricsResult.NotFound;
    }

    /// <summary>Exact name+artist match within a ±2 s duration window, preferring a synced entry.</summary>
    private static LyricsResult? ExactGet(SqliteConnection conn, string nameLower, string artistLower, double target)
    {
        if (nameLower.Length == 0 || artistLower.Length == 0)
            return null;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT l.plain_lyrics, l.synced_lyrics, l.instrumental, t.name, t.artist_name, t.duration
FROM tracks t
LEFT JOIN lyrics l ON t.last_lyrics_id = l.id
WHERE t.name_lower = $n AND t.artist_name_lower = $a
  AND t.duration >= $lo AND t.duration <= $hi
ORDER BY (l.synced_lyrics IS NOT NULL) DESC, t.id
LIMIT 1";
        cmd.Parameters.AddWithValue("$n", nameLower);
        cmd.Parameters.AddWithValue("$a", artistLower);
        cmd.Parameters.AddWithValue("$lo", target > 0 ? target - 2.0 : double.MinValue);
        cmd.Parameters.AddWithValue("$hi", target > 0 ? target + 2.0 : double.MaxValue);

        using var r = cmd.ExecuteReader();
        return r.Read() ? RowToResult(r, target, "local get (exact)") : null;
    }

    /// <summary>
    /// Browser mode: the reported artist is untrusted (often the uploader/channel), so both
    /// passes are artist-free FTS over the whole tracks_fts table — bare quoted terms, so the
    /// title's words can match split across the name and artist columns, like the website's
    /// search box. Both passes (raw video title + cleaned version) are merged into ONE candidate
    /// pool ranked by (track name contained in the video title, duration proximity, pass) — the
    /// raw pass can rank a wrong song high on decoration/artist tokens while the cleaned pass
    /// holds a near-exact match, so neither pass may win on order alone.
    /// </summary>
    private static LyricsResult BrowserQuery(SqliteConnection conn, string title, double target)
    {
        string raw = TextNorm.Normalize(title);
        string cleaned = TextNorm.Normalize(TitleCleaner.Clean(title));

        var passes = new List<(string Match, string Detail)>();
        if (raw.Length > 0)
            passes.Add((AnyColumnMatch(raw), "local search (video title)"));
        if (cleaned.Length > 0 && cleaned != raw)
            passes.Add((AnyColumnMatch(cleaned), "local search (cleaned title)"));

        var titleTokens = TextNorm.TokenSet(title);

        LyricsResult? bestSynced = null, bestPlain = null;
        var syncedScore = WorstScore;
        var plainScore = WorstScore;

        for (int pass = 0; pass < passes.Count; pass++)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = FtsSql;
            cmd.Parameters.AddWithValue("$q", passes[pass].Match);

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var res = RowToResult(r, target, passes[pass].Detail);
                if (res is null)
                    continue;

                string name = r.IsDBNull(3) ? "" : r.GetString(3);
                double dur = r.IsDBNull(5) ? 0 : r.GetDouble(5);
                double diff = (target > 0 && dur > 0) ? Math.Abs(dur - target) : double.MaxValue;
                var score = (TextNorm.TitleContains(titleTokens, name), diff, pass);

                if (!string.IsNullOrWhiteSpace(res.Synced))
                {
                    if (Better(score, syncedScore)) { bestSynced = res; syncedScore = score; }
                }
                else
                {
                    if (Better(score, plainScore)) { bestPlain = res; plainScore = score; }
                }
            }
        }

        return bestSynced ?? bestPlain ?? LyricsResult.NotFound;
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

    /// <summary>Builds an unscoped FTS5 MATCH from prepared text: each word individually quoted,
    /// implicitly ANDed, free to match in any indexed column.</summary>
    private static string AnyColumnMatch(string prepared)
    {
        var sb = new StringBuilder(prepared.Length + 16);
        foreach (var term in prepared.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append('"').Append(term).Append('"');
        }
        return sb.ToString();
    }

    /// <summary>Full-text fallback over tracks_fts; returns the closest-duration synced hit and a plain fallback.</summary>
    private static (LyricsResult? Synced, LyricsResult? Plain) FtsSearch(
        SqliteConnection conn, string nameLower, string artistLower, double target)
    {
        if (nameLower.Length == 0)
            return (null, null);

        // Same column-scoped FTS5 query shape the server builds. PrepareInput already stripped
        // quotes/punctuation, so wrapping the normalized text in quotes is a safe phrase match.
        string match = $"(name_lower : \"{nameLower}\")";
        if (artistLower.Length > 0)
            match += $" AND (artist_name_lower : \"{artistLower}\")";

        return RunFts(conn, match, target, "local search (FTS)");
    }

    /// <summary>Top-20-by-rank FTS5 hits joined with their latest lyrics; shared by both FTS paths.</summary>
    private const string FtsSql = @"
SELECT l.plain_lyrics, l.synced_lyrics, l.instrumental, t.name, t.artist_name, t.duration
FROM (SELECT rowid FROM tracks_fts WHERE tracks_fts MATCH $q ORDER BY rank LIMIT 20) s
JOIN tracks t ON t.id = s.rowid
LEFT JOIN lyrics l ON t.last_lyrics_id = l.id";

    /// <summary>Runs an FTS5 MATCH (top 20 by rank) and ranks rows: closest-duration synced hit plus a plain fallback.</summary>
    private static (LyricsResult? Synced, LyricsResult? Plain) RunFts(
        SqliteConnection conn, string match, double target, string detail)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = FtsSql;
        cmd.Parameters.AddWithValue("$q", match);

        LyricsResult? bestSynced = null;
        double bestDiff = double.MaxValue;
        LyricsResult? bestPlain = null;

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            double dur = r.IsDBNull(5) ? 0 : r.GetDouble(5);
            double diff = (target > 0 && dur > 0) ? Math.Abs(dur - target) : double.MaxValue;

            var res = RowToResult(r, target, detail);
            if (res is null)
                continue;

            if (!string.IsNullOrWhiteSpace(res.Synced))
            {
                if (bestSynced is null || diff < bestDiff)
                {
                    bestSynced = res;
                    bestDiff = diff;
                }
            }
            else
            {
                bestPlain ??= res;
            }
        }

        return (bestSynced, bestPlain);
    }

    private static LyricsResult? RowToResult(SqliteDataReader r, double target, string detailPrefix)
    {
        bool instrumental = !r.IsDBNull(2) && r.GetBoolean(2);
        string name = r.IsDBNull(3) ? "" : r.GetString(3);
        string artist = r.IsDBNull(4) ? "" : r.GetString(4);
        double dur = r.IsDBNull(5) ? 0 : r.GetDouble(5);
        string detail = BuildDetail(detailPrefix, name, artist, dur, target);

        if (instrumental)
            return new LyricsResult { Found = true, Instrumental = true, Source = "lrclib-local", Detail = detail };

        string plain = r.IsDBNull(0) ? "" : r.GetString(0);
        string synced = r.IsDBNull(1) ? "" : r.GetString(1);
        if (string.IsNullOrWhiteSpace(plain) && string.IsNullOrWhiteSpace(synced))
            return null;

        return new LyricsResult { Found = true, Plain = plain, Synced = synced, Source = "lrclib-local", Detail = detail };
    }

    private static string BuildDetail(string prefix, string name, string artist, double dur, double target)
    {
        string duration = dur > 0
            ? $" · {(int)(dur / 60)}:{(int)dur % 60:00}" + (target > 0 ? $" (Δ{Math.Abs(dur - target):0.0}s)" : "")
            : "";
        string who = artist.Length > 0 ? $"“{name}” · {artist}" : $"“{name}”";
        return $"{prefix}: {who}{duration}";
    }

    // Normalization (the server's prepare_input port) lives in TextNorm, shared with the API
    // provider's browser-mode title-containment ranking.
}
