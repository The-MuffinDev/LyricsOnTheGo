using System;
using System.Collections.Generic;
using System.Globalization;
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

    public async Task<LyricsResult> FetchAsync(string title, string artist, string album, double durationMs, CancellationToken ct)
    {
        string? db = LocalDbConfig.DbPath;
        if (string.IsNullOrEmpty(db) || !File.Exists(db) || string.IsNullOrWhiteSpace(title))
            return LyricsResult.NotFound;

        try
        {
            // SQLite calls are synchronous/blocking; keep them off the aggregator's path.
            return await Task.Run(() => Query(db!, title, artist, durationMs), ct);
        }
        catch (OperationCanceledException) { return LyricsResult.NotFound; }
        catch { return LyricsResult.NotFound; }
    }

    /// <summary>Opens the dump read-only and resolves the best result: exact match first, FTS5 second.</summary>
    private static LyricsResult Query(string db, string title, string artist, double durationMs)
    {
        double target = durationMs / 1000.0;
        string nameLower = PrepareInput(title);
        string artistLower = PrepareInput(artist);

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

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT l.plain_lyrics, l.synced_lyrics, l.instrumental, t.name, t.artist_name, t.duration
FROM (SELECT rowid FROM tracks_fts WHERE tracks_fts MATCH $q ORDER BY rank LIMIT 20) s
JOIN tracks t ON t.id = s.rowid
LEFT JOIN lyrics l ON t.last_lyrics_id = l.id";
        cmd.Parameters.AddWithValue("$q", match);

        LyricsResult? bestSynced = null;
        double bestDiff = double.MaxValue;
        LyricsResult? bestPlain = null;

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            double dur = r.IsDBNull(5) ? 0 : r.GetDouble(5);
            double diff = (target > 0 && dur > 0) ? Math.Abs(dur - target) : double.MaxValue;

            var res = RowToResult(r, target, "local search (FTS)");
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
        string delta = (target > 0 && dur > 0) ? $" (Δ{Math.Abs(dur - target):0.0}s)" : "";
        string who = artist.Length > 0 ? $"“{name}” · {artist}" : $"“{name}”";
        return $"{prefix}: {who}{delta}";
    }

    // Port of the LRCLIB server's prepare_input normalization, which must match exactly for the
    // equality-based ExactGet to hit: unaccent, punctuation to spaces, strip apostrophes, lowercase,
    // and collapse whitespace. The server stores name_lower/artist_name_lower already normalized.

    private static readonly HashSet<char> PunctToSpace = new(new[]
    {
        '`', '~', '!', '@', '#', '$', '%', '^', '&', '*', '(', ')', '_', '|', '+', '-', '=', '?',
        ';', ':', '"', ',', '.', '<', '>', '{', '}', '[', ']', '\\', '/', ' ', '\n',
    });

    private static string PrepareInput(string input)
    {
        if (string.IsNullOrEmpty(input))
            return "";

        string deburred = RemoveDiacritics(input);
        var sb = new StringBuilder(deburred.Length);
        foreach (char c in deburred)
        {
            if (c == '\'' || c == '’')
                continue; // apostrophes are removed, not spaced
            sb.Append(PunctToSpace.Contains(c) ? ' ' : c);
        }

        return CollapseWhitespace(sb.ToString().ToLowerInvariant());
    }

    private static string RemoveDiacritics(string text)
    {
        string norm = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(norm.Length);
        foreach (char c in norm)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string CollapseWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool prevSpace = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!prevSpace && sb.Length > 0)
                    sb.Append(' ');
                prevSpace = true;
            }
            else
            {
                sb.Append(c);
                prevSpace = false;
            }
        }
        if (sb.Length > 0 && sb[^1] == ' ')
            sb.Length--;
        return sb.ToString();
    }
}
