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
public sealed class AudioController(string mpvPath, LibraryDb db, AppSettings settings, SoundService sound) : IDisposable
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

    // Überblenden: der zweite Player (_spare) steht bereit, bis der Titel ausklingt; beim Überblenden wird er zum Hauptplayer,
    // der bisherige (_fading) blendet aus und wird danach selbst zum Ersatzplayer.
    private readonly DispatcherTimer _fadeTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private MpvPlayer? _spare;
    private MpvPlayer? _fading;
    private bool _preparing;
    private bool _crossfading;
    private DateTime _fadeStartedUtc;
    private int _fadeSeconds;

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
        await CancelCrossfadeAsync();

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
        await CancelCrossfadeAsync();
        if (_mpv is not null) await _mpv.CommandAsync("stop");
        Clear();
    }

    // --- Warteschlange bearbeiten --------------------------------------------------------------------------

    /// <summary>
    /// Hängt Titel an die Warteschlange an oder schiebt sie direkt hinter den laufenden Titel („Als Nächstes“). Läuft nichts oder läuft Radio
    /// oder ein Podcast, beginnt stattdessen die Wiedergabe dieser Titel.
    /// </summary>
    public async Task EnqueueAsync(IReadOnlyList<QueueItem> items, bool next)
    {
        if (items.Count == 0) throw new ArgumentException("Es gibt nichts hinzuzufügen.", nameof(items));
        foreach (var item in items)
        {
            if (item.Kind != QueueKind.Track) throw new ArgumentException("Nur Musiktitel lassen sich in die Warteschlange legen.", nameof(items));
            Validate(item);
        }

        if (_mpv is null || Current is not { Kind: QueueKind.Track })
        {
            await PlayAsync(items);
            return;
        }

        var at = next ? Index + 1 : _queue.Count;
        for (var i = 0; i < items.Count; i++)
        {
            if (next) await _mpv.CommandAsync("loadfile", items[i].Source, "insert-at", at + i - _offset);
            else await _mpv.CommandAsync("loadfile", items[i].Source, "append");
        }
        Edit(queue => queue.InsertRange(at, items));
    }

    /// <summary>Nimmt einen noch nicht gespielten Titel aus der Warteschlange.</summary>
    public async Task RemoveUpcomingAsync(int queueIndex)
    {
        RequireUpcoming(queueIndex);
        await _mpv!.CommandAsync("playlist-remove", queueIndex - _offset);
        Edit(queue => queue.RemoveAt(queueIndex));
    }

    /// <summary>Verschiebt einen noch nicht gespielten Titel um einen Platz (-1 früher, +1 später); der laufende Titel bleibt, wo er ist.</summary>
    public async Task MoveUpcomingAsync(int queueIndex, int delta)
    {
        if (delta is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(delta), delta, "Ein Platz nach oben (-1) oder unten (+1).");
        RequireUpcoming(queueIndex);
        var target = queueIndex + delta;
        if (target <= Index || target >= _queue.Count) return;

        // mpv stellt den Eintrag vor den Eintrag mit dem Zielindex; nach unten also hinter den Nachbarn.
        await _mpv!.CommandAsync("playlist-move", queueIndex - _offset, target - _offset + (delta > 0 ? 1 : 0));
        Edit(queue => (queue[queueIndex], queue[target]) = (queue[target], queue[queueIndex]));
    }

    /// <summary>Leert, was nach dem laufenden Titel kommt; der laufende Titel spielt weiter.</summary>
    public async Task ClearUpcomingAsync()
    {
        if (_mpv is null || !IsActive || Index + 1 >= _queue.Count) return;

        var index = Index;
        await _mpv.CommandAsync("playlist-clear"); // behält nur den laufenden Eintrag
        _offset = index;
        _mpvPosition = 0;
        Edit(queue => queue.RemoveRange(index + 1, queue.Count - index - 1));
    }

    /// <summary>Springt zu einem Eintrag der Warteschlange (auch einem schon gespielten).</summary>
    public async Task JumpToAsync(int queueIndex)
    {
        if (_mpv is null || queueIndex < 0 || queueIndex >= _queue.Count) throw new ArgumentOutOfRangeException(nameof(queueIndex));
        if (queueIndex >= _offset) await _mpv.CommandAsync("playlist-play-index", queueIndex - _offset);
        else await PlayAsync(_queue, queueIndex);
    }

    private void RequireUpcoming(int queueIndex)
    {
        if (_mpv is null || !IsActive) throw new InvalidOperationException("Es läuft keine Warteschlange.");
        if (queueIndex <= Index || queueIndex >= _queue.Count) throw new ArgumentOutOfRangeException(nameof(queueIndex), queueIndex, "Nur Titel nach dem laufenden lassen sich ändern.");
    }

    /// <summary>Ändert die Warteschlange auf einer Kopie, damit Anzeigen am neuen Objekt erkennen, dass sich etwas geändert hat.</summary>
    private void Edit(Action<List<QueueItem>> change)
    {
        var copy = new List<QueueItem>(_queue);
        change(copy);
        _queue = copy;
        Changed?.Invoke();
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
        _fadeTimer.Tick += (_, _) => OnFadeTick();

        // Erst die gespeicherte Lautstärke setzen, dann beobachten (StartPlayerAsync): das erste volume-Ereignis (mpv-Standard 100)
        // würde sonst Volume überschreiben, bevor der gespeicherte Wert gesendet ist.
        _mpv = await StartPlayerAsync("audio", Volume);
    }

    /// <summary>Startet eine Audio-Instanz. Ereignisse zählen nur, solange sie der Hauptplayer ist; die anderen sind Ersatz oder blenden gerade aus.</summary>
    private async Task<MpvPlayer> StartPlayerAsync(string name, double volume)
    {
        var mpv = await MpvPlayer.StartAsync(mpvPath, null, name);
        mpv.PropertyChanged += (property, data) => _dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(mpv, _mpv)) OnProperty(property, data);
        });
        mpv.CommandFailed += message => _dispatcher.BeginInvoke(() => Error?.Invoke($"mpv-Befehl fehlgeschlagen: {message}"));
        mpv.FileFailed += message => _dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(mpv, _mpv)) return;
            _failed = true; // der Abbruch danach ist kein "zu Ende gehört"
            Error?.Invoke($"Wiedergabe nicht möglich: {message}");
        });
        mpv.Ended += reason => _dispatcher.BeginInvoke(() => OnEnded(mpv, reason));
        await mpv.CommandAsync("set", "volume", volume.ToString("0.#", CultureInfo.InvariantCulture));
        await mpv.CommandAsync("set", "af", sound.Filter ?? "");
        sound.Changed -= OnSoundChanged; // mpv kann neu gestartet werden; nur einmal anmelden
        sound.Changed += OnSoundChanged;
        foreach (var property in new[] { "time-pos", "duration", "pause", "volume", "mute", "playlist-pos", "metadata/by-key/icy-title", "idle-active" })
        {
            await mpv.ObserveAsync(property);
        }
        return mpv;
    }

    private void OnEnded(MpvPlayer mpv, string reason)
    {
        if (ReferenceEquals(mpv, _spare)) _spare = null;
        else if (ReferenceEquals(mpv, _fading)) _fading = null;
        else if (ReferenceEquals(mpv, _mpv))
        {
            _mpv = null;
            _fadeTimer.Stop();
            _crossfading = false;
            Clear();
            Error?.Invoke($"Der Audio-Player (mpv) wurde beendet: {reason}");
        }
    }

    /// <summary>Der Equalizer wurde verstellt: ein laufender Titel klingt sofort anders.</summary>
    private async void OnSoundChanged()
    {
        foreach (var mpv in new[] { _mpv, _spare, _fading })
        {
            if (mpv is not null) await mpv.CommandAsync("set", "af", sound.Filter ?? "");
        }
    }

    private void OnProperty(string name, JsonElement? data)
    {
        switch (name)
        {
            case "time-pos" when data is { ValueKind: JsonValueKind.Number } v:
                Position = v.GetDouble();
                Tick?.Invoke();
                MaybeCrossfade();
                break;
            case "duration" when data is { ValueKind: JsonValueKind.Number } v:
                Duration = v.GetDouble();
                Tick?.Invoke();
                break;
            case "pause" when data is { } v:
                Paused = v.ValueKind == JsonValueKind.True;
                if (Paused) SaveProgress();
                if (_fading is not null) _ = _fading.CommandAsync("set", "pause", Paused ? "yes" : "no"); // der ausklingende Titel hält mit an
                Changed?.Invoke();
                break;
            case "mute" when data is { } v:
                Muted = v.ValueKind == JsonValueKind.True;
                Changed?.Invoke();
                break;
            case "volume" when !_crossfading && data is { ValueKind: JsonValueKind.Number } v: // beim Überblenden setzt die App die Lautstärke selbst
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

    // --- Überblenden ---------------------------------------------------------------------------------------

    /// <summary>Wird bei jedem Zeitschritt des Hauptplayers gefragt: Ersatzplayer bereitstellen, dann den nächsten Titel einsetzen lassen.</summary>
    private void MaybeCrossfade()
    {
        if (_crossfading || Paused || _mpv is null) return;
        var seconds = settings.AudioCrossfadeSeconds;
        if (seconds <= 0 || Current is not { Kind: QueueKind.Track } || Index + 1 >= _queue.Count || _queue[Index + 1].Kind != QueueKind.Track) return;

        if (!Crossfade.ShouldPrepare(Position, Duration, seconds)) return;
        if (_spare is null)
        {
            if (!_preparing) _ = PrepareSpareAsync();
            return;
        }

        if (Crossfade.ShouldStart(Position, Duration, seconds)) _ = BeginCrossfadeAsync(seconds);
    }

    private async Task PrepareSpareAsync()
    {
        _preparing = true;
        try
        {
            // Der Ersatzplayer ist stumm, bis er einsetzt.
            _spare = await StartPlayerAsync("audio2", 0);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Error?.Invoke($"Überblenden nicht möglich, der zweite Player ließ sich nicht starten: {ex.Message}");
            settings.AudioCrossfadeSeconds = 0; // sonst käme die Meldung bei jedem Titel wieder
            settings.Save();
        }
        finally
        {
            _preparing = false;
        }
    }

    private async Task BeginCrossfadeAsync(int seconds)
    {
        if (_crossfading || _spare is not { } incoming || _mpv is not { } outgoing) return;
        _crossfading = true;
        var startIndex = Index + 1;
        try
        {
            _spare = null;
            await incoming.CommandAsync("set", "volume", "0");
            await incoming.CommandAsync("set", "mute", Muted ? "yes" : "no");
            await incoming.CommandAsync("set", "pause", "no");
            await incoming.CommandAsync("loadfile", _queue[startIndex].Source, "replace", -1, "start=0");
            for (var i = startIndex + 1; i < _queue.Count; i++) await incoming.CommandAsync("loadfile", _queue[i].Source, "append");
            await outgoing.CommandAsync("playlist-clear"); // der ausklingende Titel soll nicht von selbst weiterspringen

            // Ab jetzt ist der einsetzende Titel „der laufende“.
            _mpv = incoming;
            _fading = outgoing;
            _offset = startIndex;
            _mpvPosition = -1;
            _starting = true; // idle-active des Ersatzplayers bis zum ersten playlist-pos nicht als Ende werten
            Position = 0;
            Duration = _queue[startIndex].Duration ?? 0;
            StreamTitle = null;
            _fadeSeconds = seconds;
            _fadeStartedUtc = DateTime.UtcNow;
            _fadeTimer.Start();
            Changed?.Invoke();
        }
        catch
        {
            _crossfading = false;
            throw;
        }
    }

    private void OnFadeTick()
    {
        if (_mpv is not { } incoming) return;

        var progress = (DateTime.UtcNow - _fadeStartedUtc).TotalSeconds / _fadeSeconds;
        var (outGain, inGain) = Crossfade.Gains(progress);
        var volume = Volume;
        _ = incoming.CommandAsync("set", "volume", (volume * inGain).ToString("0.#", CultureInfo.InvariantCulture));
        if (_fading is { } outgoing) _ = outgoing.CommandAsync("set", "volume", (volume * outGain).ToString("0.#", CultureInfo.InvariantCulture));
        if (progress < 1) return;

        _ = FinishCrossfadeAsync();
    }

    private async Task FinishCrossfadeAsync()
    {
        _fadeTimer.Stop();
        var outgoing = _fading;
        _fading = null;
        if (_mpv is { } incoming) await incoming.CommandAsync("set", "volume", Volume.ToString("0.#", CultureInfo.InvariantCulture));
        _crossfading = false;
        await ParkAsync(outgoing);
    }

    /// <summary>Bricht eine laufende Überblendung ab (neue Wiedergabe, Stopp): der ausklingende Titel verstummt, der Hauptplayer bekommt die volle Lautstärke zurück.</summary>
    private async Task CancelCrossfadeAsync()
    {
        _fadeTimer.Stop();
        var outgoing = _fading;
        _fading = null;
        if (!_crossfading && outgoing is null) return;

        _crossfading = false;
        if (_mpv is { } mpv) await mpv.CommandAsync("set", "volume", Volume.ToString("0.#", CultureInfo.InvariantCulture));
        await ParkAsync(outgoing);
    }

    /// <summary>Der ausgeblendete Player wartet stumm als Ersatz auf den nächsten Übergang.</summary>
    private async Task ParkAsync(MpvPlayer? player)
    {
        if (player is null) return;

        await player.CommandAsync("stop");
        await player.CommandAsync("set", "volume", "0");
        _spare = player;
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
        sound.Changed -= OnSoundChanged;
        _saveTimer.Stop();
        _volumeTimer.Stop();
        _fadeTimer.Stop();
        SaveProgress();
        _fading?.Dispose();
        _spare?.Dispose();
        _mpv?.Dispose();
    }
}
