using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace LyricsOnTheGo.Services;

/// <summary>One registered lyrics source: its provider, current priority (0 = highest,
/// consulted first / wins ties), and whether it's active for lookups. Both are recomputed as
/// state changes (e.g. a local database is linked/unlinked).</summary>
public sealed class ProviderEntry
{
    public string Name { get; }
    public ILyricsProvider Provider { get; }
    public int Priority { get; internal set; }
    public bool Enabled { get; internal set; }

    public ProviderEntry(string name, ILyricsProvider provider)
    {
        Name = name;
        Provider = provider;
    }
}

/// <summary>
/// App-wide registry of lyrics providers. The pipeline reads it on every lookup; the diagnostics
/// window flips providers on/off. User intent is persisted to providers.json, but the effective
/// priority/enabled are derived: when a valid local LRCLIB database is linked, the local provider
/// is priority 0 (instant, offline) and the hosted API is the fallback; with no database linked,
/// the API is priority 0 and the local provider is inactive.
/// </summary>
public static class LyricsProviders
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LyricsOnTheGo");
    private static readonly string FilePath = Path.Combine(Dir, "providers.json");

    private static readonly ProviderEntry Api = new("lrclib", new LrclibProvider());
    private static readonly ProviderEntry Local = new("lrclib-local", new LrclibLocalProvider());

    // Declaration order here is just display order; effective priority is set in Recompute().
    private static readonly List<ProviderEntry> EntryList = new() { Api, Local };

    // Persisted user intent (name -> wanted-on), separate from the effective Enabled flag.
    private static readonly Dictionary<string, bool> UserEnabled = LoadEnabled();

    static LyricsProviders()
    {
        LocalDbConfig.Changed += Recompute;
        Recompute();
    }

    public static IReadOnlyList<ProviderEntry> Entries => EntryList;

    /// <summary>Raised after any provider's priority/enabled changes, so open UI can refresh.</summary>
    public static event Action? Changed;

    /// <summary>Records the user's on/off choice for a provider, persists it, and re-derives state.</summary>
    public static void SetEnabled(string name, bool enabled)
    {
        UserEnabled[name] = enabled;
        SaveEnabled();
        Recompute();
    }

    /// <summary>Recomputes effective priority + enabled from the linked-database state and user intent.</summary>
    private static void Recompute()
    {
        bool linked = LocalDbConfig.IsLinked;

        if (linked) { Local.Priority = 0; Api.Priority = 1; }
        else        { Api.Priority = 0; Local.Priority = 1; }

        Api.Enabled = WantsOn("lrclib");
        // The local provider only participates when a valid database is actually linked.
        Local.Enabled = linked && WantsOn("lrclib-local");

        Changed?.Invoke();
    }

    private static bool WantsOn(string name) => !UserEnabled.TryGetValue(name, out bool on) || on; // default: on

    private static Dictionary<string, bool> LoadEnabled()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(FilePath));
                if (loaded is not null)
                    return loaded;
            }
        }
        catch { /* fall back to all-enabled */ }
        return new Dictionary<string, bool>();
    }

    private static void SaveEnabled()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(UserEnabled));
        }
        catch { /* best-effort */ }
    }
}
