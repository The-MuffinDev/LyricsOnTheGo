using System;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LyricsOnTheGo.Services;

/// <summary>
/// Remembers the path to the user's local LRCLIB SQLite dump (its own localdb.json — the
/// 100+ GB database itself is never copied; only its path is stored, and it is opened read-only).
/// When a valid database is linked, the local provider takes priority; unlinking falls back to
/// the hosted API. Raises <see cref="Changed"/> so the provider registry can re-prioritize.
/// </summary>
public static class LocalDbConfig
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LyricsOnTheGo");
    private static readonly string FilePath = Path.Combine(Dir, "localdb.json");

    private static string? _dbPath = Load();

    /// <summary>Path to the linked SQLite dump, or null if none is set.</summary>
    public static string? DbPath => _dbPath;

    /// <summary>True when a database path is set and the file still exists.</summary>
    public static bool IsLinked
    {
        get
        {
            string? p = _dbPath;
            return !string.IsNullOrEmpty(p) && File.Exists(p);
        }
    }

    /// <summary>Raised after the linked path changes.</summary>
    public static event Action? Changed;

    /// <summary>Links (or, with null, unlinks) a database file, persisting the choice and notifying listeners.</summary>
    public static void SetPath(string? path)
    {
        string? normalized = string.IsNullOrWhiteSpace(path) ? null : path;
        if (normalized == _dbPath)
            return;
        _dbPath = normalized;
        Save(normalized);
        Changed?.Invoke();
    }

    /// <summary>Quick sanity check that a file is a usable LRCLIB dump (has the expected tables).</summary>
    public static bool IsValidDump(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;

            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString();

            using var conn = new SqliteConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type IN ('table','view') AND name IN ('tracks','lyrics')";
            long found = Convert.ToInt64(cmd.ExecuteScalar());
            return found >= 2;
        }
        catch
        {
            return false;
        }
    }

    private static string? Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var saved = JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath));
                return string.IsNullOrWhiteSpace(saved?.Path) ? null : saved!.Path;
            }
        }
        catch { /* fall back to none */ }
        return null;
    }

    private static void Save(string? path)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Stored { Path = path }));
        }
        catch { /* best-effort */ }
    }

    private sealed class Stored
    {
        public string? Path { get; set; }
    }
}
