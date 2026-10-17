using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using MediaCenter.App.Player;
using MediaCenter.Core;

namespace MediaCenter.App.Services;

public enum QueueKind
{
    Track,
    Radio,
    Podcast,
}

/// <param name="Source">Dateipfad (Titel) oder http(s)-Adresse (Radio, Podcast).</param>
/// <param name="Id">Podcast: Id der Folge, damit der Hörfortschritt gespeichert werden kann.</param>
public sealed record QueueItem(
    QueueKind Kind, string Title, string Subtitle, string Source, string? CoverPath = null, long? Id = null, double? Duration = null);

/// <summary>
/// Musik, Radio und Podcasts: eine eigene mpv-Instanz ohne Fenster, getrennt vom Videoplayer.
/// Die Warteschlange liegt in mpvs Playlist (lückenlose Übergänge); gestartet wird bei Titel N, davor liegende Titel
/// lädt <see cref="PreviousAsync"/> bei Bedarf neu. Alle Ereignisse kommen auf dem UI-Thread.
/// </summary>
public sealed class AudioController(string mpvPath, LibraryDb db, AppSettings settings) : IDisposable
{
    /// <summary>Ab dieser Position wird ein Podcast-Fortschritt gemerkt.</summary>
    private const double MinSavedSeconds = 15;

    private const double PlayedFraction = 0.95;

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _volumeTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private MpvPlayer? _mpv;
    private List<QueueItem> _queue = new();
    private int _offset;
    private int _mpvPosition = -1;
    private bool _starting;
    private bool _failed;

    public IReadOnlyList<QueueItem> Queue => _queue;

    /// <summary>Position des laufenden Eintrags in der gesamten Warteschlange, -1 ohne Wiedergabe.</summary>
    public int Index => _queue.Count == 0 ? -1 : _offset + Math.Max(_mpvPosition, 0);

    public QueueItem? Current => Index >= 0 && Index < _queue.Count ? _queue[Index] : null;

    public bool IsActive => _queue.Count > 0;

    public double Position { get; private set; }

    public double Duration { get; private set; }

    public bool Paused { get; private set; }

    public bool Muted { get; private set; }

    public double Volume { get; private set; } = settings.AudioVolume;

    /// <summary>Titelzeile, die ein Radiosender mitsendet ("Interpret - Titel").</summary>
    public string? StreamTitle { get; private set; }

    /// <summary>Etwas Strukturelles hat sich geändert (Titel, Pause, Lautstärke, Warteschlange).</summary>
    public event Action? Changed;

    /// <summary>Position oder Dauer haben sich geändert.</summary>
    public event Action? Tick;

    /// <summary>Etwas ging schief (Stream nicht erreichbar, mpv beendet).</summary>
    public event Action<string>? Error;

    public async Task PlayAsync(IReadOnlyList<QueueItem> items, int startIndex = 0, double startSeconds = 0)
    {
        if (items.Count == 0) throw new ArgumentException("Die Warteschlange ist leer.", nameof(items));
        if (startIndex < 0 || startIndex >= items.Count) throw new ArgumentOutOfRangeException(nameof(startIndex));
        foreach (var item in items) Validate(item);

        SaveProgress();
        await EnsureStartedAsync();

        _queue = items.ToList();
        _offset = startIndex;
        _mpvPosition = -1;
        _starting = true;
        _failed = false;
        Position = startSeconds;
        Duration = items[startIndex].Duration ?? 0;
        StreamTitle = null;
        Paused = false;
        Changed?.Invoke();

        await _mpv!.CommandAsync("loadfile", items[startIndex].Source, "replace", -1, $"start={startSeconds.ToString("0.###", CultureInfo.InvariantCulture)}");
        for (var i = startIndex + 1; i < items.Count; i++) await _mpv.CommandAsync("loadfile", items[i].Source, "append");
        await _mpv.CommandAsync("set", "pause", "no");
        _saveTimer.Start();
    }

    private static void Validate(QueueItem item)
    {
        if (item.Kind == QueueKind.Track)
        {
            if (!Path.IsPathRooted(item.Source) || !File.Exists(item.Source)) throw new FileNotFoundException($"Datei nicht erreichbar: {item.Source}", item.Source);
        }
        else
        {
            HttpFetch.RequireWebUri(item.Source);
        }
    }

    public async Task TogglePauseAsync()
    {
        if (_mpv is not null && IsActive) await _mpv.CommandAsync("cycle", "pause");
    }

    public async Task PauseAsync()
    {
        if (_mpv is not null && IsActive && !Paused) await _mpv.CommandAsync("set", "pause", "yes");
    }

    public async Task NextAsync()
    {
        if (_mpv is not null && IsActive) await _mpv.CommandAsync("playlist-next");
    }

    /// <summary>Nach mehr als drei Sekunden an den Anfang des Titels, sonst zum vorherigen.</summary>
    public async Task PreviousAsync()
    {
        if (_mpv is null || !IsActive) return;
        if (Position > 3 || Index == 0) await _mpv.CommandAsync("seek", 0, "absolute");
        else if (_mpvPosition > 0) await _mpv.CommandAsync("playlist-prev");
        else await PlayAsync(_queue, Index - 1);
    }

    public async Task SeekAsync(double seconds)
    {
        if (_mpv is not null && IsActive) await _mpv.CommandAsync("seek", seconds, "absolute");
    }

    public async Task SeekRelativeAsync(double seconds)
    {
        if (_mpv is not null && IsActive) await _mpv.CommandAsync("seek", seconds, "relative");
    }

    public async Task SetVolumeAsync(double volume)
    {
        Volume = Math.Clamp(volume, 0, 100);
        if (_mpv is not null) await _mpv.CommandAsync("set", "volume", Volume.ToString("0.#", CultureInfo.InvariantCulture));
        _volumeTimer.Stop(); // gespeichert wird erst, wenn der Regler eine Sekunde ruht
        _volumeTimer.Start();
    }

    public async Task ToggleMuteAsync()
    {
        if (_mpv is not null) await _mpv.CommandAsync("cycle", "mute");
    }

    public async Task StopAsync()
    {
        SaveProgress();
        _saveTimer.Stop();
        if (_mpv is not null) await _mpv.CommandAsync("stop");
        Clear();
    }

    private void Clear()
    {
        _queue = new List<QueueItem>();
        _offset = 0;
        _mpvPosition = -1;
        Position = 0;
        Duration = 0;
        StreamTitle = null;
        Changed?.Invoke();
    }

    // --- mpv -----------------------------------------------------------------------------------------------

    private async Task EnsureStartedAsync()
    {
        if (_mpv is not null) return;

        _saveTimer.Tick += (_, _) => SaveProgress();
        _volumeTimer.Tick += (_, _) =>
        {
            _volumeTimer.Stop();
            settings.AudioVolume = Volume;
            settings.Save();
        };

        var mpv = await MpvPlayer.StartAsync(mpvPath, null, "audio");
        mpv.PropertyChanged += (name, data) => _dispatcher.BeginInvoke(() => OnProperty(name, data));
        mpv.CommandFailed += message => _dispatcher.BeginInvoke(() => Error?.Invoke($"mpv-Befehl fehlgeschlagen: {message}"));
        mpv.FileFailed += message => _dispatcher.BeginInvoke(() =>
        {
            _failed = true; // der Abbruch danach ist kein "zu Ende gehört"
            Error?.Invoke($"Wiedergabe nicht möglich: {message}");
        });
        mpv.Ended += reason => _dispatcher.BeginInvoke(() =>
        {
            _mpv = null;
            Clear();
            Error?.Invoke($"Der Audio-Player (mpv) wurde beendet: {reason}");
        });
        // Erst die gespeicherte Lautstärke setzen, dann beobachten: das erste volume-Ereignis (mpv-Standard 100)
        // würde sonst Volume überschreiben, bevor der gespeicherte Wert gesendet ist.
        await mpv.CommandAsync("set", "volume", Volume.ToString("0.#", CultureInfo.InvariantCulture));
        foreach (var property in new[] { "time-pos", "duration", "pause", "volume", "mute", "playlist-pos", "metadata/by-key/icy-title", "idle-active" })
        {
            await mpv.ObserveAsync(property);
        }
        _mpv = mpv;
    }

    private void OnProperty(string name, JsonElement? data)
    {
        switch (name)
        {
            case "time-pos" when data is { ValueKind: JsonValueKind.Number } v:
                Position = v.GetDouble();
                Tick?.Invoke();
                break;
            case "duration" when data is { ValueKind: JsonValueKind.Number } v:
                Duration = v.GetDouble();
                Tick?.Invoke();
                break;
            case "pause" when data is { } v:
                Paused = v.ValueKind == JsonValueKind.True;
                if (Paused) SaveProgress();
                Changed?.Invoke();
                break;
            case "mute" when data is { } v:
                Muted = v.ValueKind == JsonValueKind.True;
                Changed?.Invoke();
                break;
            case "volume" when data is { ValueKind: JsonValueKind.Number } v:
                Volume = v.GetDouble();
                Changed?.Invoke();
                break;
            case "playlist-pos" when data is { ValueKind: JsonValueKind.Number } v:
                _mpvPosition = v.GetInt32();
                if (_mpvPosition >= 0)
                {
                    _starting = false;
                    StreamTitle = null;
                    Duration = Current?.Duration ?? Duration;
                    Changed?.Invoke();
                }
                break;
            case "metadata/by-key/icy-title":
                StreamTitle = data is { ValueKind: JsonValueKind.String } t ? t.GetString() : null;
                Changed?.Invoke();
                break;
            case "idle-active" when data is { } v && v.ValueKind == JsonValueKind.True && !_starting && IsActive:
                // Die Playlist ist durchgelaufen (oder ein Stream ist ausgefallen).
                SaveProgress(finished: !_failed);
                _saveTimer.Stop();
                Clear();
                break;
        }
    }

    /// <summary>Merkt bei Podcast-Folgen Position und Gehört-Status; Radio und Musik haben keinen Fortschritt.</summary>
    private void SaveProgress(bool finished = false)
    {
        if (Current is not { Kind: QueueKind.Podcast, Id: { } episodeId }) return;

        var played = finished || (Duration > 0 && Position / Duration >= PlayedFraction);
        if (played) db.SaveEpisodeProgress(episodeId, 0, played: true);
        else if (Position >= MinSavedSeconds) db.SaveEpisodeProgress(episodeId, Position, played: false);
    }

    public void Dispose()
    {
        _saveTimer.Stop();
        _volumeTimer.Stop();
        SaveProgress();
        _mpv?.Dispose();
    }
}
