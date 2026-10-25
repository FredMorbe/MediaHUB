using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MediaCenter.App.Player;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App;

// Wiedergabe: mpv läuft eingebettet im PlayerLayer, das PlayerOverlay liegt als durchsichtiges Fenster darüber.
public partial class MainWindow
{
    /// <summary>Ab diesem Anteil der Laufzeit gilt ein Video als gesehen.</summary>
    private const double WatchedFraction = 0.92;

    /// <summary>Vorher gespeicherter Fortschritt unter dieser Grenze zählt nicht als "angefangen".</summary>
    private const double MinSavedSeconds = 15;

    private readonly PlayerOverlay _overlay = new();
    private readonly Dictionary<string, string> _stats = new();
    private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    /// <summary>Sekunden zwischen Ende einer Folge und Start der nächsten.</summary>
    private const int NextCountdownSeconds = 8;

    private readonly DispatcherTimer _nextTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private readonly DispatcherTimer _adjustSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _colorSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };

    private MpvPlayer? _mpv;
    private SubtitleAdjustment _adjustment = SubtitleAdjustment.Default;
    private bool _adjustDirty;
    private double _audioDelay;
    private bool _audioDirty;
    private bool _embedding;
    private CancellationTokenSource? _embedCts;
    private (long VideoId, string Title)? _selectEmbedded;
    private VideoRow? _playing;
    private VideoRow? _nextVideo;
    private int _nextSecondsLeft;
    private bool _nextDeclined;
    private List<MediaTrack> _tracks = new();
    private bool _fileReady;
    private bool _subtitlePicked;
    private bool _switching;
    private double _position;
    private double _duration;
    private bool _paused;
    private bool _eof;
    private bool _fullscreen;
    private DateTime _lastActivity = DateTime.UtcNow;
    private POINT _lastCursor;

    private void InitPlayer()
    {
        _overlay.BackClicked += () => _ = ExitPlayerAsync();
        _overlay.PlayPauseClicked += () => _ = TogglePauseAsync();
        _overlay.FullscreenClicked += ToggleFullscreen;
        _overlay.PictureInPictureClicked += TogglePictureInPicture;
        _overlay.DragMoved += DragPictureInPicture;
        _overlay.MuteClicked += () => _ = ToggleMuteAsync();
        _overlay.SeekRelative += seconds => _ = SeekAsync(seconds, relative: true);
        _overlay.SeekAbsolute += seconds => _ = SeekAsync(seconds, relative: false);
        _overlay.VolumeChanged += volume => _ = SetVolumeAsync(volume);
        _overlay.TrackChosen += (type, id) =>
        {
            if (_casting)
            {
                _ = ChooseCastTrackAsync(type, id);
                return;
            }
            // Eine von Hand gewählte Untertitelspur bleibt; die Automatik fasst sie nicht mehr an.
            if (type == "sub") _subtitlePicked = true;
            _ = _mpv?.CommandAsync("set", type == "audio" ? "aid" : "sid", id < 0 ? "no" : id.ToString(CultureInfo.InvariantCulture));
        };
        _overlay.AdjustmentChanged += OnAdjustmentChanged;
        _overlay.AudioDelayChanged += OnAudioDelayChanged;
        _overlay.SubtitleStyleChanged += OnSubtitleStyleChanged;
        _overlay.PictureChanged += picture => _ = ApplyPictureAsync(picture);
        _overlay.ColorChanged += OnColorChanged;
        _overlay.LutImportRequested += PickLutFiles;
        _overlay.LutRemoveRequested += RemoveLut;
        _overlay.SetLuts(Services.Luts.List());
        _colorSaveTimer.Tick += (_, _) =>
        {
            _colorSaveTimer.Stop();
            FlushColor();
        };
        _overlay.FrameStepRequested += direction => _ = StepFrameAsync(direction);
        _overlay.SubtitleEmbedRequested += trackId => _ = EmbedSubtitleAsync(trackId);
        _overlay.TranslateRequested += (trackId, targets) => _ = TranslateSubtitleAsync(trackId, targets);
        _overlay.TranslateCancelled += () => _translateCts?.Cancel();
        _overlay.BusyCancelled += () =>
        {
            _overlay.SetBusyCancelling();
            _embedCts?.Cancel();
        };
        _adjustSaveTimer.Tick += (_, _) =>
        {
            _adjustSaveTimer.Stop();
            FlushAdjustment();
        };
        _overlay.SubtitleFileRequested += PickSubtitleFile;
        _overlay.SubtitleFileDropped += AddSubtitle;
        _overlay.SubtitleRemoveRequested += RemoveSubtitle;
        _overlay.NextClicked += () => _ = PlayNextAsync();
        _overlay.EpisodeChosen += videoId => _ = PlayEpisodeAsync(videoId);
        _overlay.ChapterChosen += start => _ = SeekAsync(start, relative: false);
        _overlay.IntroSkipClicked += () => _ = SkipIntroAsync();
        _overlay.NextCancelled += () => CancelNextCountdown(declined: true);
        _nextTimer.Tick += (_, _) => OnNextTick();
        _overlay.KeyPressed += e => OnPlayerKey(e);
        _overlay.Activity += () => _lastActivity = DateTime.UtcNow;
        InitCast();
        InitSubtitleSearch();
        InitLive();
        InitHdr();

        LocationChanged += (_, _) => UpdateOverlay();
        SizeChanged += (_, _) => UpdateOverlay();
        StateChanged += (_, _) => UpdateOverlay();
        PlayerLayer.SizeChanged += (_, _) => UpdateOverlay();

        _idleTimer.Tick += (_, _) => UpdateControlsVisibility();
        _saveTimer.Tick += (_, _) => SaveProgress();

        // Einschlaf-Timer und Equalizer des Videoplayers: gleicher Stand wie in der Musikleiste.
        _overlay.SleepChosen += minutes =>
        {
            if (minutes == 0) Services.Sleep.Cancel();
            else if (minutes < 0) Services.Sleep.StartEndOfItem();
            else Services.Sleep.StartCountdown(TimeSpan.FromMinutes(minutes));
        };
        _overlay.SoundPresetChosen += id => Services.Sound.SetPreset(id);
        Services.Sleep.Changed += () => _overlay.SetSleep(Services.Sleep.Mode, Services.Sleep.Label);
        Services.Sound.Changed += () => _overlay.SetSoundPreset(Services.Sound.Preset);
        _overlay.SetSleep(Services.Sleep.Mode, Services.Sleep.Label);
        _overlay.SetSoundPreset(Services.Sound.Preset);

        // Der Einschlaf-Timer hält die Musik selbst an; das Video pausiert hier.
        Services.Sleep.Fired += message =>
        {
            if (PlayerActive) _ = SetPausedAsync(true);
            ShowMessage(message);
        };
    }

    private bool PlayerActive => PlayerLayer.Visibility == Visibility.Visible;

    // --- Windows-Mediensteuerung ---------------------------------------------------------------------------

    private MediaSession? _media;

    /// <summary>Folge, Pause oder Ende des Videos haben sich geändert; die Mediensteuerung von Windows zeigt es an.</summary>
    private event Action? VideoChanged;

    private void NotifyVideo() => VideoChanged?.Invoke();

    /// <summary>Medientasten, Lautstärke-Flyout und Sperrbildschirm: Video hat Vorrang vor Musik, Radio und Podcasts.</summary>
    private void StartMediaSession()
    {
        _media = new MediaSession(new System.Windows.Interop.WindowInteropHelper(this).Handle, new IMediaSource[] { new VideoMediaSource(this), new AudioMediaSource(Services.Audio) });
        _media.Failed += message => ShowMessage(message, isError: true);
        Closed += (_, _) => _media.Dispose();
    }

    private MediaInfo? VideoInfo() => _live is { } live && PlayerActive
        ? new MediaInfo(MediaKind.Video, live.Name, null, null, null, Playing: !_paused, CanNext: _liveList.Count > 1, CanPrevious: _liveList.Count > 1, Position: 0, Duration: 0)
        : _playing is null || !PlayerActive
        ? null
        : new MediaInfo(MediaKind.Video, TitleOf(_playing), null, null, _playing.TitleId is { } titleId ? Services.Model.BestCover(titleId)?.Path : null,
            Playing: !_paused, CanNext: _nextVideo is not null, CanPrevious: true, Position: _position, Duration: _duration);

    private sealed class VideoMediaSource(MainWindow window) : IMediaSource
    {
        public event Action? Changed
        {
            add => window.VideoChanged += value;
            remove => window.VideoChanged -= value;
        }

        public MediaInfo? Current => window.VideoInfo();

        public Task SetPlayingAsync(bool playing) => window.SetPausedAsync(!playing);

        public Task NextAsync() => window._nextVideo is null && window._live is null ? Task.CompletedTask : window.PlayNextAsync();

        public Task PreviousAsync() => window._live is not null ? window.ZapAsync(-1) : window.SeekAsync(0, relative: false);

        public Task StopAsync() => window.ExitPlayerAsync();
    }

    public Task PlayAsync(VideoRow video, bool resume) => PlayAtAsync(video, resume, startAt: null);

    /// <param name="startAt">Ab dieser Sekunde statt der gemerkten Position (z. B. zurück vom Fernseher an die Stelle dort).</param>
    private async Task PlayAtAsync(VideoRow video, bool resume, double? startAt)
    {
        var path = Services.Model.PathOf(video);
        if (path is null)
        {
            ShowMessage($"Die Datei ist nicht erreichbar (Laufwerk abgesteckt oder Datei fehlt): {video.RelPath}", isError: true);
            return;
        }
        if (_casting && video.Disc != DiscKind.None)
        {
            ShowMessage("DVDs und Blu-rays lassen sich nicht auf den Fernseher übertragen. Beende zuerst die Übertragung.", isError: true);
            return;
        }

        if (_live is not null) await EndLiveAsync(); // vom Sender zu einem Video: die Optionen für Dateien zurücksetzen

        if (_playing is not null)
        {
            // Wechsel mitten im Player (nächste Folge): die verlassene Folge sichern
            SaveProgress();
            FlushAdjustment();
            FlushColor();
        }

        var start = 0.0;
        if (startAt is { } explicitStart)
        {
            start = explicitStart;
        }
        else if (resume && Services.Db.GetPlayback(video.VideoId) is { Watched: false } saved && saved.PositionSeconds >= MinSavedSeconds)
        {
            start = saved.PositionSeconds;
        }

        await Services.Audio.PauseAsync(); // Video und Musik nicht gleichzeitig
        if (!PlayerActive) _fullscreenBeforePlayer = _fullscreen; // beim Schließen des Players bleibt das Vollbild, wie es vorher war
        PlayerLayer.Visibility = Visibility.Visible;
        Focus(); // der bisherige Fokus lag auf einem jetzt verdeckten Element; Tasten sollen am Fenster ankommen
        _overlay.Owner = this;
        _overlay.Show();
        UpdateOverlay();

        try
        {
            if (!_casting) await EnsureMpvAsync();
            _playing = video;
            _position = start;
            _duration = video.DurationSeconds ?? 0;
            _eof = false;
            if (video.Season is { } titleSeason && video.Episode is { } titleEpisode)
                _overlay.SetTitle(video.TitleName ?? video.ParsedTitle, $"Staffel {titleSeason} · Folge {titleEpisode}");
            else
                _overlay.SetTitle(TitleOf(video), video.Year?.ToString());
            _overlay.SetTracks(Array.Empty<TrackInfo>());
            _chapters = Array.Empty<Chapter>();
            _overlay.SetChapters(_chapters);

            CancelNextCountdown(declined: false);
            _nextDeclined = false;
            _nextVideo = EpisodeOrder.Next(video.TitleId is { } titleId ? Services.Model.VideosOf(titleId) : Array.Empty<VideoRow>(), video);
            _overlay.SetNextEpisode(_nextVideo is null ? null : TitleOf(_nextVideo));
            _overlay.SetEpisodes(video.TitleKind == TitleKind.Series && video.TitleId is { } episodesTitle ? EpisodeChoices.For(Services.Model, episodesTitle) : Array.Empty<EpisodeChoice>(), video.VideoId);

            if (_casting)
            {
                // Nächste Folge während der Übertragung: sie läuft weiter auf dem Fernseher, mpv bleibt ruhig.
                _castAudioStream = null;
                _audioDelay = 0; // der Ton-Zeitversatz wirkt nur in mpv, nicht auf dem Fernseher
                _audioDirty = false;
                _overlay.SetAudioDelay(_audioDelay);
                await LoadCastAsync(video, start);
                _lastActivity = DateTime.UtcNow;
                _idleTimer.Start();
                _saveTimer.Start();
                NotifyVideo();
                return;
            }

            // Ton über mpvs Spracherkennung; Untertitel wählt AutoSelectSubtitle, sobald die Spurliste da ist.
            // aid/sid neu setzen, weil eine frühere Handwahl sonst auf die nächste Datei überginge.
            var preferences = Services.Settings.Playback;
            _tracks = new List<MediaTrack>();
            _fileReady = false;
            _subtitlePicked = false;
            _switching = true; // Ereignisse der vorigen Datei bis zu mpvs start-file ignorieren
            await _mpv!.CommandAsync("set", "alang", preferences.AudioMpvList);
            await _mpv.CommandAsync("set", "aid", "auto");
            await _mpv.CommandAsync("set", "sid", "no");
            // Selbst hinzugefügte Untertitel dieses Videos mitladen (leere Liste löscht die des vorigen Videos).
            await _mpv.CommandAsync("set", "sub-files", string.Join(';', Services.Subtitles.List(video.VideoId)));
            // Zeitversatz, Größe und Höhe der Untertitel, die der Nutzer für dieses Video eingestellt hat (sonst die Standardwerte)
            _adjustment = Services.Db.GetSubtitleAdjustment(video.VideoId);
            _adjustDirty = false;
            _overlay.SetAdjustment(_adjustment);
            _overlay.SetCanEmbed(MkvSubtitleEmbedder.CanEmbedInto(path));
            await ApplyAdjustmentAsync(_adjustment);
            // Ton-Zeitversatz, den der Nutzer für dieses Video gemerkt hat (mpv behielte sonst den Wert des vorigen Videos)
            _audioDelay = Services.Db.GetAudioDelay(video.VideoId);
            _audioDirty = false;
            _overlay.SetAudioDelay(_audioDelay);
            await ApplyAudioDelayAsync(_audioDelay);
            await ApplySubtitleStyleAsync(Services.Settings.SubtitleStyle);
            await ResetPictureAsync();
            await LoadColorAsync(video);
            var target = path;
            if (video.Disc != DiscKind.None)
            {
                // DVD und Blu-ray (Ordner oder Abbild): mpv bekommt den Ort der Disc und spielt den Hauptfilm (den längsten Titel).
                var (deviceProperty, url) = DiscInput.ForMpv(video.Disc);
                await _mpv.CommandAsync("set", deviceProperty, path);
                target = url;
            }
            await _mpv.CommandAsync("loadfile", target, "replace", -1, $"start={start.ToString("0.###", CultureInfo.InvariantCulture)}");
            await _mpv.CommandAsync("set", "pause", "no");
            _lastActivity = DateTime.UtcNow;
            _idleTimer.Start();
            _saveTimer.Start();
            NotifyVideo();
        }
        catch
        {
            // Start fehlgeschlagen: Player-Ansicht wieder schließen, damit man nicht vor einem schwarzen Bild sitzt.
            await ExitPlayerAsync();
            throw;
        }
    }

    private async Task EnsureMpvAsync()
    {
        if (_mpv is not null) return;
        if (PlayerLayer.VideoHost.Handle == IntPtr.Zero) throw new InvalidOperationException("Video-Host hat kein Fenster-Handle.");

        var mpv = await MpvPlayer.StartAsync(Services.MpvPath, PlayerLayer.VideoHost.Handle);
        mpv.PropertyChanged += (name, data) => Dispatcher.Invoke(() => OnMpvProperty(name, data));
        mpv.CommandFailed += message => Dispatcher.Invoke(() => ShowMessage($"mpv-Befehl fehlgeschlagen: {message}", isError: true));
        mpv.FileStarted += () => Dispatcher.Invoke(() =>
        {
            _switching = false;
            _fileReady = false;
            _tracks = new List<MediaTrack>();
        });
        mpv.FileLoaded += () => Dispatcher.Invoke(() =>
        {
            _fileReady = true;
            _liveRetries = 0;
            AutoSelectSubtitle();
        });
        mpv.FileFailed += message => Dispatcher.Invoke(() => PlayerNotice($"{(_live is null ? "Die Datei" : "Der Sender")} lässt sich nicht abspielen: {message}", isError: true));
        mpv.Ended += reason => Dispatcher.Invoke(async () =>
        {
            ShowMessage($"mpv wurde beendet: {reason}", isError: true);
            _mpv = null;
            await ExitPlayerAsync();
        });
        foreach (var property in new[]
        {
            "time-pos", "duration", "pause", "eof-reached", "volume", "mute", "track-list",
            "video-params", "video-target-params", "hwdec-current", "frame-drop-count", "decoder-frame-drop-count", "estimated-vf-fps", "chapter-list",
        })
        {
            await mpv.ObserveAsync(property);
        }
        await mpv.CommandAsync("set", "af", Services.Sound.Filter ?? "");
        Services.Sound.Changed -= OnSoundChanged; // mpv kann neu gestartet werden; nur einmal anmelden
        Services.Sound.Changed += OnSoundChanged;
        _appliedHdrHint = null; // eine neue mpv-Instanz kennt den Wert nicht
        _mpv = mpv;
    }

    /// <summary>Der Equalizer wurde verstellt: ein laufendes Video klingt sofort anders.</summary>
    private async void OnSoundChanged()
    {
        if (_mpv is not null) await _mpv.CommandAsync("set", "af", Services.Sound.Filter ?? "");
    }

    /// <summary>Hinweis im Player (die Toasts des Hauptfensters liegen unter dem Video) und zusätzlich als Toast für danach.</summary>
    private void PlayerNotice(string text, bool isError)
    {
        _overlay.ShowNotice(text, isError);
        ShowMessage(text, isError);
    }

    private string TitleOf(VideoRow video)
    {
        var name = video.TitleName ?? video.ParsedTitle;
        return video.Season is { } season && video.Episode is { } episode ? $"{name}  S{season:00}E{episode:00}" : name;
    }

    private async Task TogglePauseAsync()
    {
        if (_casting) await SetPausedAsync(!_paused);
        else if (_mpv is not null) await _mpv.CommandAsync("cycle", "pause");
    }

    private async Task ExitPlayerAsync()
    {
        if (!PlayerActive) return;
        if (_embedding)
        {
            _overlay.ShowNotice("Das Einbetten läuft noch. Mit „Abbrechen“ lässt es sich beenden.", isError: false);
            return;
        }

        _idleTimer.Stop();
        _saveTimer.Stop();
        CancelNextCountdown(declined: false);
        SaveProgress();
        FlushAdjustment();
        FlushColor();
        _playing = null;
        _selectEmbedded = null;
        _nextVideo = null;
        _overlay.SetNextEpisode(null);
        _overlay.SetEpisodes(Array.Empty<EpisodeChoice>(), 0);
        _fileReady = false;

        RestorePictureInPicture();
        SetFullscreen(_fullscreenBeforePlayer);
        if (_casting)
        {
            _casting = false;
            _castTracks = null;
            _overlay.SetCastActive(null);
            await Services.Cast.StopAsync();
        }
        await EndLiveAsync();
        if (_mpv is not null) await _mpv.CommandAsync("stop");
        _overlay.Hide();
        PlayerLayer.Visibility = Visibility.Collapsed;
        NotifyVideo();
        ReloadView();
        _ = SyncTraktSafelyAsync(automatic: true); // das eben Gesehene geht an Trakt (höchstens alle zehn Minuten, nur wenn verbunden)
    }

    /// <summary>Schreibt Position und Gesehen-Status; sehr frühe Positionen werden nicht gemerkt.</summary>
    private void SaveProgress()
    {
        if (_playing is null) return;

        var watched = _eof || (_duration > 0 && _position / _duration >= WatchedFraction);
        if (watched)
        {
            Services.Db.SavePlayback(_playing.VideoId, 0, watched: true);
        }
        else if (_position >= MinSavedSeconds)
        {
            Services.Db.SavePlayback(_playing.VideoId, _position, watched: false);
        }
    }

    // --- Ereignisse von mpv --------------------------------------------------------------------------------

    private void OnMpvProperty(string name, JsonElement? data)
    {
        if (_casting) return; // mpv ruht: seine letzten Meldungen (leere Spurliste, Zeit null) dürfen die Anzeige des Fernsehers nicht überschreiben
        switch (name)
        {
            case "time-pos" or "duration" or "eof-reached" or "track-list" or "chapter-list" when _switching:
                break; // gehört noch zur vorigen Datei
            case "chapter-list":
                _chapters = data is { ValueKind: JsonValueKind.Array } list ? ParseChapters(list) : Array.Empty<Chapter>();
                _overlay.SetChapters(_chapters);
                break;
            case "time-pos" when data is { ValueKind: JsonValueKind.Number } v:
                _position = v.GetDouble();
                _overlay.SetPosition(_position);
                break;
            case "duration" when data is { ValueKind: JsonValueKind.Number } v:
                _duration = v.GetDouble();
                _overlay.SetDuration(_duration);
                break;
            case "pause" when data is { } v:
                _paused = v.GetBoolean();
                _overlay.SetPaused(_paused);
                NotifyVideo();
                break;
            case "eof-reached" when data is { } v:
                _eof = v.ValueKind == JsonValueKind.True;
                if (_eof && _live is not null)
                {
                    _ = ReconnectLiveAsync(); // ein Sender hat kein Ende: der Anbieter hat getrennt
                }
                else if (_eof)
                {
                    SaveProgress();
                    // Einschlaf-Timer „am Ende der Folge“: hier anhalten statt die nächste Folge zu starten.
                    if (!Services.Sleep.ConsumeEndOfItem()) BeginNextCountdown();
                }
                else
                {
                    CancelNextCountdown(declined: false); // zurückgespult: kein Countdown mehr
                }
                break;
            case "volume" when data is { ValueKind: JsonValueKind.Number } v:
                _mpvVolume = v.GetDouble();
                _overlay.SetVolume(_mpvVolume);
                break;
            case "mute" when data is { } v:
                _mpvMuted = v.GetBoolean();
                _overlay.SetMuted(_mpvMuted);
                break;
            case "video-params" when _switching:
                break;
            case "video-params":
                _overlay.SetVideoAspect(data is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty("aspect", out var aspect) && aspect.ValueKind == JsonValueKind.Number ? aspect.GetDouble() : null);
                OnVideoParams(data);
                break;
            case "video-target-params" when _switching:
                break;
            case "video-target-params":
                OnVideoTargetParams(data);
                break;
            case "track-list" when data is { ValueKind: JsonValueKind.Array } v:
                _tracks = ParseTracks(v);
                _overlay.SetTracks(_tracks.Select(t => new TrackInfo(t.Id, t.Type, t.Label, t.Selected, t.UserAdded, Translatable: CanTranslate(t))).ToList());
                AutoSelectSubtitle();
                break;
            default:
                _stats[name] = data is { } other ? other.ToString() : "–";
                _overlay.SetStats(
                    $"hw: {Stat("hwdec-current")} · Drops Anzeige/Decoder: {Stat("frame-drop-count")}/{Stat("decoder-frame-drop-count")} · fps: {Stat("estimated-vf-fps")}");
                break;
        }
    }

    // --- Kapitel und Intro -----------------------------------------------------------------------------------

    private IReadOnlyList<Chapter> _chapters = Array.Empty<Chapter>();

    private IReadOnlyList<Chapter> ParseChapters(JsonElement array)
    {
        try
        {
            return ChapterList.Parse(array);
        }
        catch (JsonException ex)
        {
            PlayerNotice($"Die Kapitel der Datei lassen sich nicht lesen: {ex.Message}", isError: true);
            return Array.Empty<Chapter>();
        }
    }

    /// <summary>Springt ans Ende des Vorspann-Kapitels, solange man darin ist; sonst 85 Sekunden vor.</summary>
    private async Task SkipIntroAsync()
    {
        if (_overlay.IntroRange is { } intro && _position >= intro.Start - 1 && _position < intro.End - 1)
        {
            await SeekAsync(intro.End, relative: false);
            _overlay.ShowNotice("Intro übersprungen", isError: false);
            return;
        }
        await SeekAsync(ChapterList.DefaultIntroSkipSeconds, relative: true);
        _overlay.ShowNotice($"{ChapterList.DefaultIntroSkipSeconds:0} Sekunden vor", isError: false);
    }

    private async Task StepChapterAsync(int direction)
    {
        if (_casting || _chapters.Count < 2) return;
        var target = direction > 0 ? ChapterList.NextStart(_chapters, _position) : ChapterList.PreviousStart(_chapters, _position);
        if (target is null)
        {
            _overlay.ShowNotice(direction > 0 ? "Das ist das letzte Kapitel." : "Kein voriges Kapitel.", isError: false);
            return;
        }
        await SeekAsync(target.Value, relative: false);
    }

    private string Stat(string key) => _stats.TryGetValue(key, out var value) ? value : "–";

    private List<MediaTrack> ParseTracks(JsonElement array)
    {
        var tracks = new List<MediaTrack>();
        foreach (var t in array.EnumerateArray())
        {
            var type = t.GetProperty("type").GetString()!;
            if (type is not ("audio" or "sub")) continue;

            string? Text(string name) => t.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            bool Flag(string name) => t.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;
            int? channels = t.TryGetProperty("demux-channel-count", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : null;
            int? ffIndex = t.TryGetProperty("ff-index", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt32() : null;

            var file = Text("external-filename");
            tracks.Add(new MediaTrack(
                t.GetProperty("id").GetInt32(), type, Text("lang"), Text("title"), Text("codec"),
                Flag("default"), Flag("forced"), Flag("external"), Flag("selected"), channels,
                file, UserAdded: file is not null && Services.Subtitles.Contains(file), FfIndex: ffIndex));
        }
        return tracks;
    }

    /// <summary>
    /// Wählt die Untertitelspur nach den Einstellungen. Läuft bei jeder neuen Spurliste, solange der Nutzer nicht selbst
    /// gewählt hat: externe Dateien kommen erst kurz nach dem Öffnen dazu, die gewählte Tonspur erst nach mpvs Vorauswahl.
    /// </summary>
    private void AutoSelectSubtitle()
    {
        // Gerade eingebettet: genau diese Spur wählen, auch wenn die Einstellungen sie nicht wählen würden.
        if (_fileReady && _mpv is not null && _playing is { } playing && _selectEmbedded is { } pending && pending.VideoId == playing.VideoId
            && _tracks.FirstOrDefault(t => t.Type == "sub" && !t.External && string.Equals(t.Title, pending.Title, StringComparison.OrdinalIgnoreCase)) is { } embedded)
        {
            _selectEmbedded = null;
            _subtitlePicked = true;
            if (!embedded.Selected) _ = _mpv.CommandAsync("set", "sid", embedded.Id.ToString(CultureInfo.InvariantCulture));
            return;
        }

        if (!_fileReady || _subtitlePicked || _playing is null || _mpv is null) return;
        if (!TrackSelector.TryPickSubtitle(_tracks, Services.Settings.Playback, out var wanted)) return;

        var current = _tracks.FirstOrDefault(t => t.Type == "sub" && t.Selected)?.Id;
        if (wanted == current) return;
        _ = _mpv.CommandAsync("set", "sid", wanted is { } id ? id.ToString(CultureInfo.InvariantCulture) : "no");
    }

    // --- Untertitel-Dateien ----------------------------------------------------------------------------------

    /// <summary>Dateiauswahl für „Untertitel-Datei laden …“; das Dialogfenster gehört dem Overlay, damit es über dem Video liegt.</summary>
    private void PickSubtitleFile()
    {
        if (_playing is null) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Untertitel-Datei wählen",
            Filter = "Untertitel (*.srt;*.ass;*.ssa;*.vtt;*.sub;*.smi)|*.srt;*.ass;*.ssa;*.vtt;*.sub;*.smi|Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
        };
        if (Services.Model.PathOf(_playing) is { } videoPath && System.IO.Path.GetDirectoryName(videoPath) is { } folder) dialog.InitialDirectory = folder;

        if (dialog.ShowDialog(_overlay) == true) AddSubtitle(dialog.FileName);
    }

    /// <summary>
    /// Speichert die Datei für dieses Video (Datenordner der App, bleibt beim nächsten Abspielen erhalten) und blendet sie sofort ein.
    /// Die Sprache kommt aus dem Dateinamen, sonst gilt die eingestellte Untertitelsprache.
    /// </summary>
    private void AddSubtitle(string source)
    {
        if (_playing is not { } video || _mpv is null) return;

        try
        {
            var language = SubtitleStore.DetectLanguage(source) ?? Languages.Get(Services.Settings.SubtitleLanguage);
            var stored = Services.Subtitles.Add(video.VideoId, source, language);
            _subtitlePicked = true; // gewählt ist gewählt: die Automatik fasst sie nicht an

            if (_tracks.FirstOrDefault(t => t.Type == "sub" && string.Equals(t.File, stored, StringComparison.OrdinalIgnoreCase)) is { } loaded)
            {
                _ = _mpv.CommandAsync("set", "sid", loaded.Id.ToString(CultureInfo.InvariantCulture)); // schon geladen: nur auswählen
            }
            else
            {
                _ = _mpv.CommandAsync("sub-add", stored, "select");
            }
            PlayerNotice($"Untertitel gespeichert und aktiv: {System.IO.Path.GetFileName(source)} ({language.Name})", isError: false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        {
            PlayerNotice($"Untertitel nicht hinzugefügt: {ex.Message}", isError: true);
        }
    }

    private void RemoveSubtitle(int trackId)
    {
        if (_mpv is null || _tracks.FirstOrDefault(t => t.Type == "sub" && t.Id == trackId) is not { UserAdded: true, File: { } file }) return;

        try
        {
            Services.Subtitles.Remove(file);
            _subtitlePicked = false; // war es die gewählte Spur, wählt die Automatik wieder nach den Einstellungen
            _ = _mpv.CommandAsync("sub-remove", trackId);
            PlayerNotice("Gespeicherten Untertitel entfernt.", isError: false);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            PlayerNotice($"Untertitel nicht entfernt: {ex.Message}", isError: true);
        }
    }

    // --- Zeitversatz und Darstellung -------------------------------------------------------------------------

    private void OnAdjustmentChanged(SubtitleAdjustment adjustment)
    {
        _adjustment = adjustment;
        _ = ApplyAdjustmentAsync(adjustment);
        _adjustDirty = true;
        _adjustSaveTimer.Stop();
        _adjustSaveTimer.Start(); // gespeichert wird, wenn 0,6 s nichts mehr geändert wurde
    }

    private async Task ApplyAdjustmentAsync(SubtitleAdjustment adjustment)
    {
        if (_mpv is null) return;

        await _mpv.CommandAsync("set", "sub-delay", adjustment.DelaySeconds.ToString("0.###", CultureInfo.InvariantCulture));
        await _mpv.CommandAsync("set", "sub-scale", adjustment.Scale.ToString("0.###", CultureInfo.InvariantCulture));
        await _mpv.CommandAsync("set", "sub-pos", adjustment.Position.ToString(CultureInfo.InvariantCulture));
    }

    private void OnAudioDelayChanged(double seconds)
    {
        if (_casting)
        {
            _overlay.SetAudioDelay(_audioDelay);
            _overlay.ShowNotice("Während der Übertragung lässt sich der Ton nicht verschieben.", isError: false);
            return;
        }
        _audioDelay = seconds;
        _ = ApplyAudioDelayAsync(seconds);
        _audioDirty = true;
        _adjustSaveTimer.Stop();
        _adjustSaveTimer.Start(); // gespeichert wird, wenn 0,6 s nichts mehr geändert wurde
    }

    private async Task ApplyAudioDelayAsync(double seconds)
    {
        if (_mpv is null) return;

        await _mpv.CommandAsync("set", "audio-delay", seconds.ToString("0.###", CultureInfo.InvariantCulture));
    }

    /// <summary>Setzt das globale Aussehen der Untertitel (Schrift, Farben, Hintergrund) in mpv.</summary>
    private async Task ApplySubtitleStyleAsync(SubtitleStyle style)
    {
        if (_mpv is null) return;

        foreach (var (name, value) in style.ToMpvProperties()) await _mpv.CommandAsync("set", name, value);
    }

    private void OnSubtitleStyleChanged(SubtitleStyle style)
    {
        Services.Settings.SubtitleStyle = style;
        Services.Settings.Save();
        _ = ApplySubtitleStyleAsync(style);
    }

    private void FlushAdjustment()
    {
        if (_playing is null) return;

        if (_adjustDirty)
        {
            Services.Db.SaveSubtitleAdjustment(_playing.VideoId, _adjustment);
            _adjustDirty = false;
        }
        if (_audioDirty)
        {
            Services.Db.SaveAudioDelay(_playing.VideoId, _audioDelay);
            _audioDirty = false;
        }
    }

    // --- Untertitel in die MKV einbetten ---------------------------------------------------------------------

    private static string FormatSize(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0} MB";

    /// <summary>Papierkorb, wo es einen gibt; auf Laufwerken ohne Papierkorb (USB) wird endgültig gelöscht.</summary>
    private static void RecycleOriginal(string path) => MediaCenter.App.Services.LibraryActions.Recycle(path);

    /// <summary>
    /// Schreibt den gespeicherten Untertitel in die MKV (ohne Neukodierung), nach Rückfrage. Das Video wird dafür beendet und
    /// danach an derselben Stelle fortgesetzt; bei jedem Fehler bleibt die Datei, wie sie war.
    /// </summary>
    private async Task EmbedSubtitleAsync(int trackId)
    {
        if (_embedding || _playing is not { } video || _mpv is null) return;
        if (_tracks.FirstOrDefault(t => t.Type == "sub" && t.Id == trackId) is not { UserAdded: true, File: { } stored } track) return;
        if (Services.Model.PathOf(video) is not { } path || !MkvSubtitleEmbedder.CanEmbedInto(path))
        {
            PlayerNotice("Einbetten ist nur bei MKV-Dateien möglich.", isError: true);
            return;
        }

        FlushAdjustment();
        FlushColor();
        var delay = _adjustment.DelaySeconds;
        var question = new StringBuilder()
            .AppendLine($"Den Untertitel „{Path.GetFileName(stored)}“ in diese Datei einbetten?")
            .AppendLine()
            .AppendLine($"{path}  ({FormatSize(new FileInfo(path).Length)})")
            .AppendLine()
            .AppendLine("• Die MKV wird neu geschrieben (ohne Neukodierung). Das kann je nach Laufwerk einige Minuten dauern und braucht kurz so viel freien Platz wie die Datei.")
            .AppendLine("• Die neue Datei wird geprüft und ersetzt dann das Original. Das Original kommt in den Papierkorb, auf Laufwerken ohne Papierkorb (z. B. USB) wird es gelöscht.")
            .AppendLine("• Die Wiedergabe wird dafür beendet und danach fortgesetzt.");
        if (delay != 0) question.AppendLine($"• Der eingestellte Zeitversatz ({delay.ToString("+0.0;-0.0", CultureInfo.GetCultureInfo("de-DE"))} s) wird in den Untertitel eingerechnet und danach auf 0 gestellt.");
        if (MessageBox.Show(_overlay, question.ToString(), "Untertitel einbetten", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        _embedding = true;
        SaveProgress(); // Position merken, PlayAsync setzt dort fort
        var language = track.Language ?? Languages.Get(Services.Settings.SubtitleLanguage);
        var title = SubtitleStore.DisplayName(stored);
        var fileName = Path.GetFileName(path);
        EmbedResult? result = null;
        string? failure = null;
        var cancelled = false;
        using (var cts = new CancellationTokenSource())
        {
            _embedCts = cts;
            try
            {
                await _mpv.CommandAsync("stop"); // gibt die Datei frei
                await Task.Delay(400);
                _overlay.ShowBusy("Untertitel werden eingebettet", $"{fileName} wird neu geschrieben …");
                var progress = new Progress<double>(f => _overlay.SetBusyProgress(f, $"{fileName} wird neu geschrieben … {f:P0}"));
                result = await Task.Run(() => Services.Embedder.EmbedAsync(path, stored, language, title, delay, RecycleOriginal, progress, cts.Token));
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                failure = "Einbetten abgebrochen. Die Datei ist unverändert.";
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or NotSupportedException
                                           or TimeoutException or UnauthorizedAccessException or ArgumentException)
            {
                failure = $"Einbetten nicht möglich, die Datei ist unverändert: {ex.Message}";
            }
            finally
            {
                _embedCts = null;
                _overlay.HideBusy();
            }
        }

        string message;
        var isError = false;
        if (result is not null)
        {
            message = $"Untertitel „{title}“ eingebettet.";
            try
            {
                Services.Subtitles.Remove(stored); // die gespeicherte Kopie ist jetzt überflüssig (sonst käme die Spur doppelt)
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                message += $" Die gespeicherte Kopie ließ sich nicht entfernen: {ex.Message}";
                isError = true;
            }
            if (delay != 0)
            {
                _adjustment = _adjustment with { DelaySeconds = 0 };
                Services.Db.SaveSubtitleAdjustment(video.VideoId, _adjustment);
            }
            if (result.OriginalLeftBehind is { } left)
            {
                message += $" Die alte Datei ließ sich nicht entfernen und liegt noch hier: {left}";
                isError = true;
            }
            _selectEmbedded = (video.VideoId, title);
        }
        else
        {
            message = failure ?? "Einbetten nicht möglich.";
            isError = !cancelled;
        }

        _embedding = false;
        try
        {
            await PlayAsync(video, resume: true); // dort weiter, wo das Video stand
        }
        catch (Exception ex)
        {
            message += $" Die Wiedergabe ließ sich nicht fortsetzen: {ex.Message}";
            isError = true;
        }
        PlayerNotice(message, isError);
    }

    // --- Nächste Folge ---------------------------------------------------------------------------------------

    /// <summary>Folge zu Ende: Countdown zur nächsten starten, wenn es eine gibt und die Funktion an ist.</summary>
    private void BeginNextCountdown()
    {
        if (_nextVideo is not { } next || _nextDeclined || !Services.Settings.AutoPlayNext) return;

        if (Services.Model.PathOf(next) is null)
        {
            PlayerNotice($"Nächste Folge nicht erreichbar (Laufwerk abgesteckt?): {TitleOf(next)}", isError: true);
            return;
        }
        _nextSecondsLeft = NextCountdownSeconds;
        _overlay.ShowNextCountdown(TitleOf(next), _nextSecondsLeft);
        _nextTimer.Start();
    }

    private void OnNextTick()
    {
        if (_nextVideo is not { } next)
        {
            CancelNextCountdown(declined: false);
            return;
        }
        if (--_nextSecondsLeft <= 0)
        {
            _ = PlayNextAsync();
            return;
        }
        _overlay.ShowNextCountdown(TitleOf(next), _nextSecondsLeft);
    }

    /// <param name="declined">Der Nutzer hat abgebrochen: für diese Folge kein neuer Countdown.</param>
    private void CancelNextCountdown(bool declined)
    {
        _nextTimer.Stop();
        _overlay.HideNextCountdown();
        if (declined) _nextDeclined = true;
    }

    /// <summary>Folge aus dem Folgenmenü des Players: setzt dort fort, wo sie zuletzt stand.</summary>
    private async Task PlayEpisodeAsync(long videoId)
    {
        if (_live is not null)
        {
            await PlayChannelAsync(videoId); // im Live-Modus ist das Menü das Sendermenü
            return;
        }
        if (_playing is null || _playing.TitleId is not { } titleId || _playing.VideoId == videoId) return;
        if (Services.Model.VideosOf(titleId).FirstOrDefault(v => v.VideoId == videoId) is not { } episode) return;

        try
        {
            await PlayAsync(episode, resume: true);
        }
        catch (Exception ex)
        {
            PlayerNotice($"Die Folge lässt sich nicht starten: {ex.Message}", isError: true);
        }
    }

    private async Task PlayNextAsync()
    {
        if (_live is not null)
        {
            await ZapAsync(1);
            return;
        }
        if (_nextVideo is not { } next) return;

        CancelNextCountdown(declined: false);
        if (Services.Model.PathOf(next) is null)
        {
            PlayerNotice($"Nächste Folge nicht erreichbar (Laufwerk abgesteckt?): {TitleOf(next)}", isError: true);
            return;
        }
        try
        {
            await PlayAsync(next, resume: false);
        }
        catch (Exception ex)
        {
            // Der Aufruf kommt aus Ereignissen (Taste, Schaltfläche, Countdown): ohne Meldung ginge der Fehler verloren.
            PlayerNotice($"Nächste Folge lässt sich nicht starten: {ex.Message}", isError: true);
        }
    }

    // --- Tasten, Vollbild, Steuerung ------------------------------------------------------------------------

    private void OnPlayerKey(KeyEventArgs e)
    {
        if (!PlayerActive) return;
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            // Bild (Belegung wie im MPC-BE): Strg+←/→ Einzelbild, Strg+↑/↓ Tempo, Strg+R Tempo zurück; diese Kombinationen sind fest.
            case Key.Left when ctrl && shift: _overlay.Pan(-PictureSettings.PanStep, 0); break;
            case Key.Right when ctrl && shift: _overlay.Pan(PictureSettings.PanStep, 0); break;
            case Key.Up when ctrl && shift: _overlay.Pan(0, -PictureSettings.PanStep); break;
            case Key.Down when ctrl && shift: _overlay.Pan(0, PictureSettings.PanStep); break;
            case Key.Left when ctrl: _ = StepFrameAsync(-1); break;
            case Key.Right when ctrl: _ = StepFrameAsync(1); break;
            case Key.Up when ctrl: _overlay.StepSpeed(1); break;
            case Key.Down when ctrl: _overlay.StepSpeed(-1); break;
            case Key.R when ctrl: _overlay.ResetSpeed(); break;
            default:
                if (!PlayerKeyMap.TryGetValue(e.Key.ToString(), out var action) || !RunPlayerAction(action, shift)) return;
                break;
        }
        _lastActivity = DateTime.UtcNow;
        e.Handled = true;
    }

    /// <summary>Taste → Aktion nach den Einstellungen; wird neu gebaut, wenn die Einstellungen eine neue Belegung bekommen (sie tauschen dann das ganze Wörterbuch aus).</summary>
    private Dictionary<string, PlayerAction> PlayerKeyMap
    {
        get
        {
            var source = Services.Settings.KeyBindings;
            if (_playerKeyMap is null || !ReferenceEquals(_playerKeySource, source))
            {
                _playerKeyMap = PlayerKeys.Resolve(source);
                _playerKeySource = source;
            }
            return _playerKeyMap;
        }
    }

    private Dictionary<string, PlayerAction>? _playerKeyMap;
    private Dictionary<string, string[]>? _playerKeySource;

    /// <summary>
    /// Führt eine Player-Aktion aus (Taste oder Controller). <paramref name="large"/> macht aus Springen die 60-Sekunden-Variante und aus Drehen die
    /// Gegenrichtung. false, wenn die Aktion hier nichts tun kann (keine nächste Folge): die Taste bleibt dann unbehandelt.
    /// </summary>
    private bool RunPlayerAction(PlayerAction action, bool large)
    {
        switch (action)
        {
            case PlayerAction.PlayPause: _ = TogglePauseAsync(); break;
            case PlayerAction.PictureInPicture: TogglePictureInPicture(); break;
            case PlayerAction.Fullscreen: ToggleFullscreen(); break;
            case PlayerAction.Exit:
                if (PictureInPictureActive) RestorePictureInPicture();
                else if (_fullscreen && !_fullscreenBeforePlayer) ToggleFullscreen();
                else _ = ExitPlayerAsync();
                break;
            case PlayerAction.SeekBack: _ = SeekAsync(large ? -60 : -10, relative: true); break;
            case PlayerAction.SeekForward: _ = SeekAsync(large ? 60 : 10, relative: true); break;
            case PlayerAction.VolumeUp: _ = ChangeVolumeAsync(5); break;
            case PlayerAction.VolumeDown: _ = ChangeVolumeAsync(-5); break;
            case PlayerAction.Mute: _ = ToggleMuteAsync(); break;
            case PlayerAction.Stats: _overlay.ToggleStats(); break;
            case PlayerAction.NextVideo when _live is not null: _ = ZapAsync(1); break;
            case PlayerAction.NextVideo when _nextVideo is not null: _ = PlayNextAsync(); break;
            case PlayerAction.NextVideo: return false;
            case PlayerAction.SkipIntro: _ = SkipIntroAsync(); break;
            case PlayerAction.ChapterPrevious when _live is not null: _ = ZapAsync(-1); break;
            case PlayerAction.ChapterNext when _live is not null: _ = ZapAsync(1); break;
            case PlayerAction.ChapterPrevious: _ = StepChapterAsync(-1); break;
            case PlayerAction.ChapterNext: _ = StepChapterAsync(1); break;
            case PlayerAction.SubtitleCycle: CycleTrack("sub"); break;
            case PlayerAction.AudioCycle: CycleTrack("audio"); break;
            case PlayerAction.SubtitleEarlier: _overlay.NudgeDelay(-0.1); break;
            case PlayerAction.SubtitleLater: _overlay.NudgeDelay(0.1); break;
            case PlayerAction.AudioEarlier: _overlay.NudgeAudioDelay(-0.05); break;
            case PlayerAction.AudioLater: _overlay.NudgeAudioDelay(0.05); break;
            case PlayerAction.NextAspect: _overlay.NextAspect(); break;
            case PlayerAction.Rotate: _overlay.Rotate(large ? -90 : 90); break;
            case PlayerAction.ZoomIn: _overlay.StepZoom(1); break;
            case PlayerAction.ZoomOut: _overlay.StepZoom(-1); break;
            case PlayerAction.ZoomReset: _overlay.ResetZoom(); break;
            case PlayerAction.FrameBack: _ = StepFrameAsync(-1); break;
            case PlayerAction.FrameForward: _ = StepFrameAsync(1); break;
            default: throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
        return true;
    }

    /// <summary>Wählt die nächste Ton- oder Untertitelspur (bei Untertiteln nach der letzten „aus“, dann wieder die erste) und sagt, welche läuft.</summary>
    private void CycleTrack(string type)
    {
        if (_casting)
        {
            _overlay.ShowNotice("Während der Übertragung lassen sich die Spuren nur im Menü wählen.", isError: false);
            return;
        }
        var tracks = _tracks.Where(t => t.Type == type).ToList();
        if (tracks.Count == 0)
        {
            _overlay.ShowNotice(type == "sub" ? "Diese Datei hat keine Untertitel." : "Diese Datei hat nur eine Tonspur.", isError: false);
            return;
        }
        var current = tracks.FindIndex(t => t.Selected);
        var next = type == "sub" ? (current + 1 >= tracks.Count ? -1 : current + 1) : (current + 1) % tracks.Count;
        if (type == "sub") _subtitlePicked = true;
        _ = _mpv?.CommandAsync("set", type == "audio" ? "aid" : "sid", next < 0 ? "no" : tracks[next].Id.ToString(CultureInfo.InvariantCulture));
        _overlay.ShowNotice(type == "sub" ? (next < 0 ? "Untertitel aus" : $"Untertitel: {tracks[next].Label}") : $"Ton: {tracks[next].Label}", isError: false);
    }

    /// <summary>Vollbild der ganzen App (Knopf in der Navigation, F11, Einstellung „Immer im Vollbild starten“) und des Players.</summary>
    public void SetFullscreen(bool on)
    {
        if (_fullscreen == on) return;
        _fullscreen = on;
        if (on)
        {
            _stateBeforeFullscreen = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
            WindowState = WindowState.Normal; // sonst übernimmt ein schon maximiertes Fenster die neue Größe nicht
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _stateBeforeFullscreen;
        }
        _overlay.SetFullscreen(on);
        FullscreenNavButton.Content = on ? "\uE73F" : "\uE740";
        FullscreenNavButton.ToolTip = on ? "Vollbild beenden (F11)" : "Vollbild (F11)";
    }

    private void ToggleFullscreen()
    {
        if (PictureInPictureActive) RestorePictureInPicture();
        else SetFullscreen(!_fullscreen);
    }

    /// <summary>Stand des Vollbilds, bevor der Player geöffnet wurde.</summary>
    private bool _fullscreenBeforePlayer;

    private WindowState _stateBeforeFullscreen = WindowState.Normal;

    /// <summary>Steuerung zeigen bei Bewegung, Taste oder Pause; nach drei Sekunden Ruhe ausblenden.</summary>
    private void UpdateControlsVisibility()
    {
        if (GetCursorPos(out var cursor) && (cursor.X != _lastCursor.X || cursor.Y != _lastCursor.Y))
        {
            _lastCursor = cursor;
            _lastActivity = DateTime.UtcNow;
        }
        var idle = DateTime.UtcNow - _lastActivity > TimeSpan.FromSeconds(3);
        _overlay.SetControlsVisible(!(idle && !_paused && !_overlay.IsMenuOpen
            && !(PictureInPictureActive && _overlay.IsNextCountdownVisible)));
    }

    /// <summary>Legt das Overlay-Fenster exakt über den Video-Host.</summary>
    private void UpdateOverlay()
    {
        if (!IsLoaded || !PlayerActive || WindowState == WindowState.Minimized) return;

        // Layout muss durchgelaufen sein, bevor Position und Größe des Hosts stimmen.
        Dispatcher.BeginInvoke(() =>
        {
            if (!PlayerActive || WindowState == WindowState.Minimized) return;
            var host = PlayerLayer.VideoHost;
            var source = PresentationSource.FromVisual(host)
                ?? throw new InvalidOperationException("Video-Host hat keine PresentationSource.");
            var topLeft = source.CompositionTarget.TransformFromDevice.Transform(host.PointToScreen(new Point(0, 0)));
            _overlay.Left = topLeft.X;
            _overlay.Top = topLeft.Y;
            _overlay.Width = host.ActualWidth;
            _overlay.Height = host.ActualHeight;
        }, DispatcherPriority.Render);
    }

    private void ClosePlayer()
    {
        _nextTimer.Stop();
        _idleTimer.Stop();
        _saveTimer.Stop();
        _adjustSaveTimer.Stop();
        _colorSaveTimer.Stop();
        _embedCts?.Cancel();
        _translateCts?.Cancel();
        SaveProgress();
        FlushAdjustment();
        FlushColor();
        _overlay.Close();
        _mpv?.Dispose();
        _mpv = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);
}
