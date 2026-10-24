using System.IO;
using System.Windows.Threading;
using MediaCenter.Core;

namespace MediaCenter.App.Services;

/// <summary>
/// Hält die Bibliothek von selbst aktuell: Kommt in einem Ordner einer Quelle eine Datei hinzu, fällt eine weg oder wird sie umbenannt, scannt die App
/// diese Quelle nach einer kurzen Ruhepause neu. Wird ein Laufwerk angeschlossen, wird seine Quelle gescannt. Während ein Video oder Sender läuft,
/// wartet die Aktualisierung bis zum Ende. Alles läuft auf dem Oberflächen-Thread (die Datenbank ist nicht für mehrere Threads gebaut);
/// der <see cref="FolderWatcher"/> meldet nur, was sich geändert hat.
/// </summary>
public sealed class LibraryWatch : IDisposable
{
    /// <summary>So lange muss in einem Ordner Ruhe sein, bevor gescannt wird (ein Kopiervorgang meldet laufend Änderungen).</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(8);

    /// <summary>Wie oft nach angeschlossenen und abgezogenen Laufwerken und neuen Quellen gesehen wird.</summary>
    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(30);

    /// <summary>Eine Datei, die noch kopiert wird, lässt sich nicht lesen: so oft wird die Quelle danach noch einmal versucht.</summary>
    private const int MaxRetries = 3;

    private readonly IShell _shell;
    private readonly Func<bool> _playerActive;
    private readonly FolderWatcher _watcher = new(Settle);
    private readonly DispatcherTimer _timer = new() { Interval = SyncInterval };
    private readonly HashSet<long> _pending = new();
    private readonly Dictionary<long, int> _retries = new();
    private readonly HashSet<long> _reportedFaults = new();
    private HashSet<long> _watched = new();
    private HashSet<long> _connected = new();
    private bool _running;
    private bool _disposed;

    public LibraryWatch(IShell shell, Func<bool> playerActive)
    {
        _shell = shell;
        _playerActive = playerActive;
        _watcher.Changed += ids => Dispatch(() => Queue(ids));
        _watcher.Faulted += (id, message) => Dispatch(() =>
        {
            if (_reportedFaults.Add(id)) _shell.ShowMessage($"Die Überwachung eines Ordners ist gestört: {message}", isError: true);
        });
        _timer.Tick += (_, _) => Sync();
    }

    /// <summary>Beginnt zu beobachten (und danach alle 30 s zu prüfen, ob sich Laufwerke oder Quellen geändert haben).</summary>
    public void Start()
    {
        Sync();
        _timer.Start();
    }

    /// <summary>Ob die Überwachung nach den Einstellungen laufen soll.</summary>
    private bool Enabled => _shell.Services.Settings.WatchFolders;

    private void Dispatch(Action action)
    {
        if (_disposed) return;
        _timer.Dispatcher.BeginInvoke(() =>
        {
            if (!_disposed) action();
        });
    }

    /// <summary>
    /// Gleicht die beobachteten Ordner mit den Quellen und den angeschlossenen Laufwerken ab. Ein Laufwerk, das neu dazukommt, wird gleich gescannt;
    /// beim Start gilt jedes angeschlossene Laufwerk als neu, damit Dateien, die bei geschlossener App dazukamen, auch auftauchen.
    /// </summary>
    private void Sync()
    {
        if (!Enabled)
        {
            if (_watched.Count > 0 || _watcher.Count > 0)
            {
                _watcher.Stop();
                _watched = new HashSet<long>();
                _connected = new HashSet<long>(); // beim Wiedereinschalten alles einmal abgleichen
                _pending.Clear();
            }
            return;
        }

        var library = _shell.Services.Model.Library;
        var status = library.RefreshVolumes();
        var connected = status.Connected.Keys.ToHashSet();
        var sources = _shell.Services.Db.GetSources();

        var targets = new List<WatchTarget>();
        foreach (var source in sources.Where(s => s.Kind == SourceKind.Folder))
        {
            if (status.ResolvePath(source.VolumeId, source.RelPath) is { } path && Directory.Exists(path)) targets.Add(new WatchTarget(source.Id, path));
        }

        // Quellen auf Laufwerken, die vorher nicht da waren (beim Start: alle) – es kann sich etwas getan haben, als die App nicht hinsah.
        var arrived = sources.Where(s => connected.Contains(s.VolumeId) && !_connected.Contains(s.VolumeId)).Select(s => s.Id).ToList();
        if (arrived.Count > 0) Queue(arrived);
        _connected = connected;

        var ids = targets.Select(t => t.SourceId).ToHashSet();
        if (!ids.SetEquals(_watched) || _watcher.Count != targets.Count)
        {
            foreach (var (target, error) in _watcher.Watch(targets))
            {
                ids.Remove(target.SourceId);
                if (_reportedFaults.Add(target.SourceId)) _shell.ShowMessage($"Der Ordner „{target.Path}“ lässt sich nicht überwachen: {error}", isError: true);
            }
            _watched = ids;
        }
        RunPending();
    }

    private void Queue(IEnumerable<long> ids)
    {
        if (!Enabled) return;
        foreach (var id in ids) _pending.Add(id);
        RunPending();
    }

    /// <summary>Scannt die vorgemerkten Quellen, sobald nichts anderes läuft.</summary>
    private async void RunPending()
    {
        if (_running || _disposed || _pending.Count == 0 || _playerActive()) return;

        _running = true;
        try
        {
            var done = new List<ScanResult>();
            var retry = new List<long>();
            while (_pending.Count > 0 && !_playerActive() && !_disposed)
            {
                var id = _pending.Order().First();
                _pending.Remove(id);
                var library = _shell.Services.Model.Library;
                if (_shell.Services.Db.GetSources().FirstOrDefault(s => s.Id == id) is not { } source) continue; // Quelle inzwischen entfernt

                var result = await library.ScanAsync(source, library.RefreshVolumes());
                done.Add(result);
                if (result.Failures.Count == 0)
                {
                    _retries.Remove(id);
                    continue;
                }

                // Meist ist eine Datei noch im Kopiervorgang: ein paar Minuten später noch einmal, erst danach als Fehler melden.
                var tries = _retries.GetValueOrDefault(id) + 1;
                if (tries <= MaxRetries)
                {
                    _retries[id] = tries;
                    retry.Add(id);
                }
                else
                {
                    _retries.Remove(id);
                    _shell.ShowMessage(
                        $"{result.Failures.Count} Datei(en) konnten nicht gelesen werden: " +
                        string.Join("; ", result.Failures.Take(3).Select(f => $"{Path.GetFileName(f.Path)} ({f.Error})")),
                        isError: true);
                }
            }

            Report(done);
            if (done.Any(r => r.Added + r.Updated + r.MarkedMissing + r.Moved + r.Restored > 0))
            {
                _shell.ReloadView();
                await _shell.RefreshArtworkAsync();
            }
            if (retry.Count > 0) _ = RetryLaterAsync(retry);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _shell.ShowMessage($"Die automatische Aktualisierung ist fehlgeschlagen: {ex.Message}", isError: true);
        }
        finally
        {
            _running = false;
        }
        if (_pending.Count > 0) RunPending();
    }

    private async Task RetryLaterAsync(IReadOnlyList<long> ids)
    {
        await Task.Delay(TimeSpan.FromMinutes(1));
        Dispatch(() => Queue(ids));
    }

    private void Report(IReadOnlyList<ScanResult> results)
    {
        var added = results.Sum(r => r.Added);
        var moved = results.Sum(r => r.Moved);
        var missing = results.Sum(r => r.MarkedMissing);
        var restored = results.Sum(r => r.Restored);
        var parts = new List<string>();
        if (added > 0) parts.Add($"{added} neue Datei{(added == 1 ? "" : "en")}");
        if (moved > 0) parts.Add($"{moved} verschoben");
        if (restored > 0) parts.Add($"{restored} wieder da");
        if (missing > 0) parts.Add($"{missing} fehl{(missing == 1 ? "t" : "en")}");
        if (parts.Count > 0) _shell.ShowMessage("Bibliothek aktualisiert: " + string.Join(", ", parts) + ".");
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _watcher.Dispose();
    }
}
