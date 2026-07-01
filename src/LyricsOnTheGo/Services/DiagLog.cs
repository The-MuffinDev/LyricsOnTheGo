using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace LyricsOnTheGo.Services;

/// <summary>How a lyrics lookup turned out (or "searching" while still in flight).</summary>
public enum DiagResult { Searching, Synced, Plain, Instrumental, NotFound, Error, CacheHit }

/// <summary>
/// One row of lyrics-lookup activity, created the moment a provider call STARTS so the
/// diagnostics window can show it (with a live-ticking ms) before the call finishes. The
/// pipeline calls <see cref="Complete"/> when the provider returns and <see cref="MarkChosen"/>
/// on the winner; the UI flushes those via <see cref="Pump"/> on its own thread. Bindable via
/// <see cref="INotifyPropertyChanged"/>.
/// </summary>
public sealed class DiagCall : INotifyPropertyChanged
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private long? _finalMs;
    private DiagResult _result = DiagResult.Searching;
    private string _detail = "";
    private bool _chosen;
    private int _dirty; // set (from any thread) when Complete/MarkChosen changed state; flushed by Pump

    public DateTime StartTime { get; }
    public string Track { get; }
    public string Provider { get; }

    public DiagCall(string track, string provider)
    {
        StartTime = DateTime.Now;
        Track = track;
        Provider = provider;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Time => StartTime.ToString("HH:mm:ss");
    public DiagResult Result => _result;
    public bool Chosen => _chosen;
    public bool Running => _finalMs is null;
    public string Detail => _detail;

    public string MsText => _finalMs is long f
        ? (f > 0 ? f.ToString() : "")
        : _sw.ElapsedMilliseconds.ToString();

    public string ResultText => Label(_result) + (_chosen ? "  ◄ chosen" : "");

    /// <summary>Marks the call finished. Safe from any thread; the UI reflects it on the next Pump.</summary>
    public void Complete(DiagResult result, string detail, long ms)
    {
        _result = result;
        _detail = detail ?? "";
        _finalMs = ms;
        Interlocked.Exchange(ref _dirty, 1);
    }

    public void MarkChosen()
    {
        _chosen = true;
        Interlocked.Exchange(ref _dirty, 1);
    }

    /// <summary>Call on the UI thread (e.g. from a timer): advances the live ms while running and
    /// flushes a completion/chosen change. Returns true while the call is still running.</summary>
    public bool Pump()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 1)
        {
            Raise(nameof(MsText));
            Raise(nameof(ResultText));
            Raise(nameof(Detail));
            Raise(nameof(Result));
            Raise(nameof(Chosen));
            Raise(nameof(Running));
            return Running;
        }
        if (Running)
        {
            Raise(nameof(MsText));
            return true;
        }
        return false;
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string Label(DiagResult r) => r switch
    {
        DiagResult.Searching => "searching…",
        DiagResult.Synced => "● synced",
        DiagResult.Plain => "plain",
        DiagResult.Instrumental => "instrumental",
        DiagResult.NotFound => "not found",
        DiagResult.Error => "timeout / error",
        DiagResult.CacheHit => "cache hit",
        _ => r.ToString(),
    };
}

/// <summary>
/// App-wide ring buffer of lyrics-lookup activity. The pipeline calls <see cref="Begin"/> when a
/// provider call starts (returning a live <see cref="DiagCall"/> to complete later), or
/// <see cref="LogCompleted"/> for one-shot rows (e.g. cache hits). The diagnostics window
/// subscribes to <see cref="Added"/> and reads <see cref="Snapshot"/> on open. Thread-safe.
/// </summary>
public static class DiagLog
{
    private const int MaxRows = 500;

    private static readonly object Lock = new();
    private static readonly LinkedList<DiagCall> Rows = new();

    public static event Action<DiagCall>? Added;
    public static event Action? Cleared;

    /// <summary>Records the start of a provider call; complete it via the returned handle.</summary>
    public static DiagCall Begin(string track, string provider)
    {
        var call = new DiagCall(track, provider);
        AddInternal(call);
        Added?.Invoke(call);
        return call;
    }

    /// <summary>Records an already-finished row (e.g. a cache hit).</summary>
    public static void LogCompleted(string track, string provider, DiagResult result, string detail, long ms, bool chosen)
    {
        var call = new DiagCall(track, provider);
        call.Complete(result, detail, ms);
        if (chosen)
            call.MarkChosen();
        AddInternal(call);
        Added?.Invoke(call);
    }

    public static IReadOnlyList<DiagCall> Snapshot()
    {
        lock (Lock)
            return Rows.ToList();
    }

    public static void Clear()
    {
        lock (Lock)
            Rows.Clear();
        Cleared?.Invoke();
    }

    private static void AddInternal(DiagCall call)
    {
        lock (Lock)
        {
            Rows.AddLast(call);
            while (Rows.Count > MaxRows)
                Rows.RemoveFirst();
        }
    }
}
