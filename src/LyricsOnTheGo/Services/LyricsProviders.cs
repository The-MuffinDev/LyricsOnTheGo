using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace LyricsOnTheGo.Services;

/// <summary>One registered lyrics source: its provider, fixed priority (0 = highest,
/// consulted first / wins ties), and a runtime on/off flag the diagnostics window toggles.</summary>
public sealed class ProviderEntry
{
    public string Name { get; }
    public ILyricsProvider Provider { get; }
    public int Priority { get; }
    public bool Enabled { get; set; }

    public ProviderEntry(string name, ILyricsProvider provider, int priority, bool enabled)
    {
        Name = name;
        Provider = provider;
        Priority = priority;
        Enabled = enabled;
    }
}

/// <summary>
/// App-wide registry of lyrics providers in priority order. Enabled/disabled state is shared
/// between the lyrics pipeline (which consults it on every lookup) and the diagnostics window
/// (which flips it live), and is persisted to providers.json so it survives restarts.
/// </summary>
public static class LyricsProviders
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LyricsOnTheGo");
    private static readonly string FilePath = Path.Combine(Dir, "providers.json");

    private static readonly List<ProviderEntry> EntryList = Build();

    /// <summary>Registered providers, highest priority first.</summary>
    public static IReadOnlyList<ProviderEntry> Entries => EntryList;

    /// <summary>Raised after any provider is enabled/disabled, so open UI can refresh.</summary>
    public static event Action? Changed;

    public static void SetEnabled(string name, bool enabled)
    {
        foreach (var e in EntryList)
        {
            if (e.Name != name || e.Enabled == enabled)
                continue;
            e.Enabled = enabled;
            Save();
            Changed?.Invoke();
            return;
        }
    }

    // Declaration order defines priority (0 = highest). To add a source later, drop another
    // ILyricsProvider in this array — the aggregator, registry persistence, and diagnostics
    // window all pick it up automatically. (NetEase was removed: too few reliable hits.)
    private static List<ProviderEntry> Build()
    {
        var saved = LoadEnabled();
        var providers = new (string Name, ILyricsProvider Provider)[]
        {
            ("lrclib", new LrclibProvider()),
        };

        var list = new List<ProviderEntry>(providers.Length);
        for (int i = 0; i < providers.Length; i++)
        {
            bool enabled = !saved.TryGetValue(providers[i].Name, out bool on) || on; // default: enabled
            list.Add(new ProviderEntry(providers[i].Name, providers[i].Provider, i, enabled));
        }
        return list;
    }

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

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var map = new Dictionary<string, bool>();
            foreach (var e in EntryList)
                map[e.Name] = e.Enabled;
            File.WriteAllText(FilePath, JsonSerializer.Serialize(map));
        }
        catch { /* best-effort */ }
    }
}
