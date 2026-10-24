using System.Text.RegularExpressions;

namespace MediaCenter.Core;

/// <summary>Ein Ordner einer Quelle, auf den der <see cref="FolderWatcher"/> achtet.</summary>
public sealed record WatchTarget(long SourceId, string Path);

/// <summary>
/// Beobachtet die Ordner der Quellen und meldet, bei welchen etwas hinzukam, wegfiel oder umbenannt wurde. Ereignisse werden gesammelt und erst
/// gemeldet, wenn <c>settle</c> lang nichts mehr kam (ein Kopiervorgang schickt Hunderte). Der Watcher fasst die Datenbank nicht an: Er meldet
/// nur Quell-Ids, das Scannen macht der Aufrufer auf seinem Thread. <see cref="Changed"/> und <see cref="Faulted"/> kommen auf einem Thread-Pool-Thread.
/// </summary>
public sealed partial class FolderWatcher(TimeSpan settle) : IDisposable
{
    private readonly object _gate = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly HashSet<long> _pending = new();
    private Timer? _timer;
    private bool _disposed;

    /// <summary>Zählt die Beobachtungen: Ereignisse, die nach <see cref="Stop"/> oder einem neuen <see cref="Watch"/> noch eintreffen, gehören zur alten und zählen nicht.</summary>
    private int _generation;

    /// <summary>Quellen, bei denen sich etwas geändert hat; sie gehören neu gescannt.</summary>
    public event Action<IReadOnlyList<long>>? Changed;

    /// <summary>Windows hat die Beobachtung einer Quelle beendet oder konnte nicht alles melden (Ordner weg, Netzwerk, Puffer voll); die Quelle wird trotzdem zum Scannen gemeldet.</summary>
    public event Action<long, string>? Faulted;

    /// <summary>Anzahl der beobachteten Ordner.</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _watchers.Count;
        }
    }

    /// <summary>
    /// Ersetzt die beobachteten Ordner. Ordner, die sich nicht beobachten lassen (verschwunden, kein Zugriff), kommen mit dem Grund zurück,
    /// die übrigen werden beobachtet.
    /// </summary>
    public IReadOnlyList<(WatchTarget Target, string Error)> Watch(IEnumerable<WatchTarget> targets)
    {
        var failures = new List<(WatchTarget, string)>();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopWatchers();
            var generation = ++_generation;
            foreach (var target in targets)
            {
                try
                {
                    var watcher = new FileSystemWatcher(target.Path)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        InternalBufferSize = 64 * 1024,
                    };
                    var id = target.SourceId;
                    watcher.Created += (_, e) => OnEvent(id, e.FullPath, generation);
                    watcher.Changed += (_, e) => OnEvent(id, e.FullPath, generation);
                    watcher.Deleted += (_, e) => OnEvent(id, e.FullPath, generation);
                    watcher.Renamed += (_, e) =>
                    {
                        OnEvent(id, e.OldFullPath, generation);
                        OnEvent(id, e.FullPath, generation);
                    };
                    watcher.Error += (_, e) =>
                    {
                        if (generation != _generation) return;
                        Faulted?.Invoke(id, e.GetException().Message);
                        Queue(id, generation); // gerade jetzt kann etwas verpasst worden sein
                    };
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    failures.Add((target, ex.Message));
                }
            }
        }
        return failures;
    }

    /// <summary>Beendet die Beobachtung und verwirft, was noch nicht gemeldet wurde.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            StopWatchers();
            _generation++;
            _pending.Clear();
            _timer?.Dispose();
            _timer = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
    }

    /// <summary>
    /// Ob eine Änderung an diesem Pfad einen Scan wert ist: Medien-Dateien, und alles, was wie ein Ordner aussieht (keine oder keine übliche Endung:
    /// beim Löschen eines Ordners meldet Windows nur den Ordner, nicht die Dateien darin). Zwischendateien (.tmp, .part), Untertitel und Bilder zählen nicht.
    /// </summary>
    public static bool IsRelevant(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Length == 0 || !PlainExtension().IsMatch(extension)) return true;
        return MediaExtensions.Video.Contains(extension) || MediaExtensions.Audio.Contains(extension) || DiscDetector.WatchedExtensions.Contains(extension);
    }

    [GeneratedRegex(@"^\.[A-Za-z0-9]{1,5}$")]
    private static partial Regex PlainExtension();

    private void OnEvent(long sourceId, string path, int generation)
    {
        if (IsRelevant(path)) Queue(sourceId, generation);
    }

    private void Queue(long sourceId, int generation)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation) return;
            _pending.Add(sourceId);
            _timer ??= new Timer(_ => Flush());
            _timer.Change(settle, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        long[] ids;
        lock (_gate)
        {
            if (_disposed || _pending.Count == 0) return;
            ids = _pending.Order().ToArray();
            _pending.Clear();
        }
        Changed?.Invoke(ids);
    }

    private void StopWatchers()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
    }
}
