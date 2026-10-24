using System.IO;
using System.Net.Http;
using MediaCenter.App.Theming;
using MediaCenter.Core;

namespace MediaCenter.App.Services;

/// <summary>Alles, was die Oberfläche zum Arbeiten braucht; wird beim Start einmal gebaut.</summary>
public sealed class AppServices : IDisposable
{
    public required AppSettings Settings { get; init; }

    public required ThemeService Theme { get; init; }

    public required LibraryDb Db { get; init; }

    public required LibraryModel Model { get; init; }

    /// <summary>Das aktive Profil und der Wechsel zwischen Profilen.</summary>
    public required ProfileService Profiles { get; init; }

    public required string MpvPath { get; init; }

    public required AudioController Audio { get; init; }

    /// <summary>Einschlaf-Timer für Musik und Video.</summary>
    public required SleepTimer Sleep { get; init; }

    /// <summary>Equalizer für Musik und Video.</summary>
    public required SoundService Sound { get; init; }

    public required HttpClient Http { get; init; }

    /// <summary>Überträgt Videos per Google Cast auf einen Fernseher.</summary>
    public required CastService Cast { get; init; }

    public required RadioBrowserClient RadioSearch { get; init; }

    public required RadioService RadioStations { get; init; }

    public required PodcastSearchClient PodcastSearch { get; init; }

    public required PodcastService Podcasts { get; init; }

    /// <summary>Vom Nutzer hinzugefügte Untertitel, pro Video im Datenordner.</summary>
    public required SubtitleStore Subtitles { get; init; }

    public required LutStore Luts { get; init; }

    /// <summary>Schreibt Untertitel in MKV-Dateien (ffmpeg).</summary>
    public required MkvSubtitleEmbedder Embedder { get; init; }

    /// <summary>Bearbeitet MKV-Dateien: Spuren entfernen, hinzufügen, ändern, verschieben (ffmpeg).</summary>
    public required MkvEditor Editor { get; init; }

    /// <summary>Liest die Spuren einer Datei (ffprobe).</summary>
    public required FfprobeReader Probe { get; init; }

    /// <summary>Stumme Ausschnitte für die Vorschau beim Überfahren einer Karte.</summary>
    public required PreviewClips Previews { get; init; }

    /// <summary>Lädt Bilder aus dem Netz in den Cover-Cache.</summary>
    public required RemoteImages Images { get; init; }

    /// <summary>Senderlisten (M3U) und Programmführer (XMLTV) für Live-TV.</summary>
    public required LiveTvService LiveTv { get; init; }

    // --- Heimnetz-Server ---------------------------------------------------------------------------------

    private RemoteAccess? _remoteAccess;
    private RemoteServer? _remote;

    /// <summary>Das Hauptfenster; der Heimnetz-Server holt sich dort, was er braucht.</summary>
    public IRemoteHost? RemoteHost { get; set; }

    public static string RemoteFile => Path.Combine(AppSettings.DataDirectory, "remote.json");

    /// <summary>PIN und angemeldete Geräte des Heimnetz-Servers; liegen in remote.json im Datenordner (nicht in der Sicherung).</summary>
    public RemoteAccess RemoteAccess => _remoteAccess ??= LoadRemoteAccess();

    /// <summary>Der laufende Heimnetz-Server; null, solange er nie gestartet wurde.</summary>
    public RemoteServer? Remote => _remote;

    private RemoteAccess LoadRemoteAccess()
    {
        RemoteAccessState? state = null;
        if (File.Exists(RemoteFile))
        {
            try
            {
                state = System.Text.Json.JsonSerializer.Deserialize<RemoteAccessState>(File.ReadAllText(RemoteFile));
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new InvalidDataException($"Die Zugangsdaten des Heimnetz-Servers sind fehlerhaft: {RemoteFile}. Datei löschen, um eine neue PIN zu bekommen. ({ex.Message})", ex);
            }
        }
        var access = new RemoteAccess(state);
        if (state is null || state.Pin != access.Pin) SaveRemoteAccess(access); // die neue PIN merken
        access.Changed += () => SaveRemoteAccess(access);
        return access;
    }

    private static void SaveRemoteAccess(RemoteAccess access)
    {
        Directory.CreateDirectory(AppSettings.DataDirectory);
        var temp = RemoteFile + ".tmp";
        File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(access.State));
        File.Move(temp, RemoteFile, overwrite: true);
    }

    /// <summary>
    /// Bringt den Heimnetz-Server in den Zustand der Einstellungen: starten, mit neuem Anschluss neu starten oder anhalten.
    /// Gibt eine Fehlermeldung zurück, wenn er nicht starten konnte (Anschluss belegt), sonst null.
    /// </summary>
    public async Task<string?> ApplyHomeServerAsync()
    {
        if (!Settings.HomeServer)
        {
            if (_remote is not null) await _remote.StopAsync();
            return null;
        }

        var host = RemoteHost ?? throw new InvalidOperationException("Das Hauptfenster ist noch nicht bereit.");
        _remote ??= new RemoteServer(host, RemoteAccess);
        if (_remote.Running && _remote.Port == Settings.HomeServerPort && _remote.Lan == Settings.HomeServerLan) return null;

        try
        {
            await _remote.StartAsync(Settings.HomeServerPort, Settings.HomeServerLan);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return $"Der Heimnetz-Server konnte nicht starten (Anschluss {Settings.HomeServerPort} schon belegt?): {ex.Message}";
        }
    }

    private TraktService? _trakt;

    /// <summary>Trakt: Zugangsdaten, Anmeldung und Abgleich „gesehen“.</summary>
    public TraktService Trakt => _trakt ??= new TraktService(Db, Http, () => Profiles.Current.Id, () => Profiles.Current.Kids);

    /// <summary>Gleicht „gesehen“ mit Trakt ab (wenn verbunden) und sagt, was sich getan hat. Fehler werden angezeigt, nicht verschluckt.</summary>
    public async Task SyncTraktAsync(IShell shell, bool automatic)
    {
        try
        {
            var result = await Trakt.SyncAsync(Model.Titles, Db.ListVideos(), Db.GetAllPlayback(), automatic);
            if (result is null) return;

            if (result.MarkedWatched > 0) shell.ReloadView();
            var parts = new List<string>();
            if (result.MarkedWatched > 0) parts.Add($"{result.MarkedWatched} von Trakt als gesehen übernommen");
            if (result.Pushed > 0) parts.Add($"{result.Pushed} an Trakt gesendet");
            if (result.NotFound.Count > 0) parts.Add($"Trakt kennt {result.NotFound.Count} Titel nicht ({string.Join(", ", result.NotFound.Take(3))}{(result.NotFound.Count > 3 ? " …" : "")})");
            if (parts.Count > 0) shell.ShowMessage("Trakt: " + string.Join(", ", parts) + ".", isError: result.NotFound.Count > 0 && result.MarkedWatched + result.Pushed == 0);
            else if (!automatic) shell.ShowMessage("Trakt: alles auf dem gleichen Stand.");
        }
        catch (Exception ex) when (ex is TraktAuthException or HttpRequestException or TaskCanceledException or InvalidDataException or System.Text.Json.JsonException)
        {
            shell.ShowMessage($"Trakt-Abgleich nicht möglich: {ex.Message}", isError: true);
        }
    }

    private ScheduleService? _schedule;
    private bool _scheduleRunning;
    private bool _scheduleOfflineReported;

    /// <summary>Sendepläne der Serien von TVmaze: Kalender und neue Folgen.</summary>
    public ScheduleService Schedule => _schedule ??= new ScheduleService(Db, TvMaze);

    /// <summary>
    /// Holt die fälligen Sendepläne (mit <paramref name="force"/> alle) und meldet neue Folgen, die in der Bibliothek fehlen. Läuft auf dem Oberflächen-Thread;
    /// nur die Abrufe warten. Ist TVmaze nicht erreichbar, sagt die App das einmal je Sitzung.
    /// </summary>
    public async Task RefreshScheduleAsync(IShell shell, bool force = false)
    {
        if (!Settings.UpcomingEpisodes || _scheduleRunning) return;

        _scheduleRunning = true;
        try
        {
            var now = DateTime.UtcNow;
            var result = await Schedule.RefreshAsync(Model.Titles, now, force);
            if (result.Aborted is { } reason && (force || !_scheduleOfflineReported))
            {
                _scheduleOfflineReported = true;
                shell.ShowMessage($"Die Sendepläne der Serien konnten nicht geholt werden: {reason}", isError: force);
            }
            if (result.Failures.Count > 0) shell.ShowMessage($"Bei {result.Failures.Count} Serie(n) war die Antwort von TVmaze nicht lesbar: {string.Join("; ", result.Failures.Take(2))}", isError: true);

            var fresh = Schedule.TakeNewEpisodes(Db.ListVideos(), now, TimeZoneInfo.Local.GetUtcOffset(now));
            if (fresh.Count > 0 && !Model.Profile.Kids) // im Kinderprofil verrät ein Hinweis Serientitel, die dort nicht sichtbar sind
            {
                var names = fresh.Take(3).Select(r => $"{r.TitleName} S{r.Season:00}E{r.Episode:00}").ToList();
                shell.ShowMessage(fresh.Count == 1
                    ? $"Neue Folge erschienen: {names[0]}. Sie liegt noch nicht in der Bibliothek."
                    : $"{fresh.Count} neue Folgen erschienen, die noch nicht in der Bibliothek liegen: {string.Join(", ", names)}{(fresh.Count > 3 ? " …" : "")}.");
            }
        }
        finally
        {
            _scheduleRunning = false;
        }
    }

    /// <summary>Controller (Xbox, PlayStation): Tastendrücke für Oberfläche und Player; läuft, sobald das Hauptfenster <see cref="ControllerService.Start"/> aufruft.</summary>
    public ControllerService Controller { get; } = new();

    /// <summary>Serienposter von TVmaze; eine Instanz, weil sie die Abstände zwischen den Abrufen einhält.</summary>
    public required TvMazeClient TvMaze { get; init; }

    /// <summary>Songtexte: Dateien, Tags und LRCLIB.</summary>
    public required LyricsService Lyrics { get; init; }

    /// <summary>Songtexte von LRCLIB; eine Instanz, damit die Suche bei einer Einstellung an- und abgeschaltet werden kann.</summary>
    public required LrcLibClient LrcLib { get; init; }

    /// <summary>Album-Cover von iTunes; eine Instanz, weil sie die Abstände zwischen den Abrufen einhält.</summary>
    public required ITunesAlbumClient ITunes { get; init; }

    /// <summary>Album-Cover von MusicBrainz und dem Cover Art Archive; fragt erst, wenn iTunes nichts fand.</summary>
    public required MusicBrainzAlbumClient MusicBrainz { get; init; }

    /// <summary>Filmposter von OMDb; null, solange weder <see cref="OmdbKeyVariable"/> noch <see cref="OmdbKeyFile"/> einen Schlüssel hat.</summary>
    public OmdbClient? Omdb { get; private set; }

    /// <summary>Name der Umgebungsvariable mit dem OMDb-Schlüssel; sie hat Vorrang vor der Datei (z. B. für Tests).</summary>
    public const string OmdbKeyVariable = "MEDIACENTER_OMDB_KEY";

    /// <summary>Datei im Datenordner, in die die Einstellungen den eingegebenen OMDb-Schlüssel schreiben.</summary>
    public static string OmdbKeyFile => Path.Combine(AppSettings.DataDirectory, "omdb.key");

    /// <summary>Der Schlüssel stammt aus <see cref="OmdbKeyVariable"/> und nicht aus der Datei.</summary>
    public bool OmdbKeyFromVariable { get; private set; }

    /// <summary>Speichert den Schlüssel (null oder leer löscht ihn) und nutzt ihn sofort, ohne Neustart.</summary>
    public void SetOmdbKey(string? key)
    {
        key = key?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            File.Delete(OmdbKeyFile);
        }
        else
        {
            if (!key.All(char.IsAsciiLetterOrDigit)) throw new ArgumentException("Ein OMDb-Schlüssel besteht nur aus Buchstaben und Ziffern (ohne Leerzeichen).");
            Directory.CreateDirectory(AppSettings.DataDirectory);
            File.WriteAllText(OmdbKeyFile, key);
        }
        LoadOmdb();
        ApplyOnlineCovers();
    }

    private void LoadOmdb()
    {
        var fromVariable = Environment.GetEnvironmentVariable(OmdbKeyVariable)?.Trim();
        OmdbKeyFromVariable = !string.IsNullOrEmpty(fromVariable);
        var key = OmdbKeyFromVariable ? fromVariable : File.Exists(OmdbKeyFile) ? File.ReadAllText(OmdbKeyFile).Trim() : null;
        Omdb = string.IsNullOrEmpty(key) ? null : new OmdbClient(Http, key);
    }

    /// <summary>Poster, Hintergründe, deutsche Infotexte und Trailer von TMDB; null, solange weder <see cref="TmdbKeyVariable"/> noch <see cref="TmdbKeyFile"/> einen Schlüssel hat.</summary>
    public TmdbClient? Tmdb { get; private set; }

    /// <summary>Name der Umgebungsvariable mit dem TMDB-Schlüssel; sie hat Vorrang vor der Datei.</summary>
    public const string TmdbKeyVariable = "MEDIACENTER_TMDB_KEY";

    /// <summary>Datei im Datenordner, in die die Einstellungen den eingegebenen TMDB-Schlüssel schreiben.</summary>
    public static string TmdbKeyFile => Path.Combine(AppSettings.DataDirectory, "tmdb.key");

    /// <summary>Der Schlüssel stammt aus <see cref="TmdbKeyVariable"/> und nicht aus der Datei.</summary>
    public bool TmdbKeyFromVariable { get; private set; }

    /// <summary>Speichert den Schlüssel (null oder leer löscht ihn) und nutzt ihn sofort, ohne Neustart. Der v3-Schlüssel und das v4-Zugriffstoken gehen beide.</summary>
    public void SetTmdbKey(string? key)
    {
        key = key?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            File.Delete(TmdbKeyFile);
        }
        else
        {
            // Der v3-Schlüssel besteht aus Buchstaben und Ziffern, das v4-Token ist ein JWT (Buchstaben, Ziffern, „.“, „-“, „_“).
            if (key.Length < 20 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) throw new ArgumentException("Das ist kein TMDB-Schlüssel: erwartet wird der „API-Schlüssel“ (32 Zeichen) oder das „API Read Access Token“ ohne Leerzeichen.");
            Directory.CreateDirectory(AppSettings.DataDirectory);
            File.WriteAllText(TmdbKeyFile, key);
        }
        LoadTmdb();
        ApplyOnlineCovers();
    }

    private void LoadTmdb()
    {
        var fromVariable = Environment.GetEnvironmentVariable(TmdbKeyVariable)?.Trim();
        TmdbKeyFromVariable = !string.IsNullOrEmpty(fromVariable);
        var key = TmdbKeyFromVariable ? fromVariable : File.Exists(TmdbKeyFile) ? File.ReadAllText(TmdbKeyFile).Trim() : null;
        Tmdb = string.IsNullOrEmpty(key) ? null : new TmdbClient(Http, key);
    }

    /// <summary>Schaltet die Online-Suche nach Postern nach der Einstellung ein oder aus (gilt ab dem nächsten Cover-Durchlauf).</summary>
    public void ApplyOnlineCovers() =>
        Model.Artwork.Online = Settings.OnlineCovers
            ? new OnlineArtwork(TvMaze, Omdb, Images, new IAlbumCoverSource[] { ITunes, MusicBrainz }, TvMaze, Omdb, Tmdb?.Series, Tmdb?.Movies)
            : null;

    /// <summary>Schaltet die Suche nach Songtexten im Internet nach der Einstellung ein oder aus (gilt ab dem nächsten Titel).</summary>
    public void ApplyOnlineLyrics() => Lyrics.Online = Settings.OnlineLyrics ? LrcLib : null;

    /// <summary>Ohne Zeitlimit des Clients: Antworten eines großen Modells brauchen lange, <see cref="OllamaClient"/> setzt sein eigenes.</summary>
    public required HttpClient OllamaHttp { get; init; }

    /// <summary>Liest den extrahierten Text einer Untertitelspur.</summary>
    public required SubtitleExtractor SubtitleText { get; init; }

    /// <summary>Spricht mit dem Ollama aus den Einstellungen (Adresse kann sich zur Laufzeit ändern, daher jedes Mal neu).</summary>
    public OllamaClient Ollama() => new(OllamaHttp, Settings.OllamaUri);

    /// <summary>Wo der bisherige Stand liegt, wenn dieser Start eine Sicherung eingespielt hat; sonst null.</summary>
    public string? RestoredPreviousState { get; private set; }

    /// <summary>
    /// Meldet sich bei OpenSubtitles an und gibt den Client zurück (das Token gilt nur für diesen Client).
    /// </summary>
    /// <exception cref="OpenSubtitlesException">Zugangsdaten fehlen oder wurden abgelehnt, OpenSubtitles nicht erreichbar.</exception>
    public async Task<OpenSubtitlesClient> OpenSubtitlesAsync(CancellationToken ct = default)
    {
        OpenSubtitlesLogin login;
        try
        {
            login = OpenSubtitlesAccount.Load()
                ?? throw new OpenSubtitlesException("Der Zugang zu OpenSubtitles ist noch nicht eingerichtet (Einstellungen → Wiedergabe → Untertitel aus dem Internet).");
        }
        catch (InvalidDataException ex)
        {
            throw new OpenSubtitlesException(ex.Message, ex);
        }
        var client = new OpenSubtitlesClient(Http, login.Key, $"MediaCenter v{AppInfo.Version}");
        await client.LoginAsync(login.User, login.Password, ct);
        return client;
    }

    /// <summary>Eigener Client für Senderlisten und Programmführer: ihre Abrufe sind groß und dürfen länger dauern als die übrigen.</summary>
    private static HttpClient LiveHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"MediaCenter/{AppInfo.Version}");
        return client;
    }

    public static AppServices Create()
    {
        // Eine vorbereitete Wiederherstellung muss vor allem anderen eingespielt werden: Datenbank und Einstellungen sind danach andere.
        var restoredFrom = BackupService.ApplyPendingRestore(AppSettings.DataDirectory);
        var settings = AppSettings.Load();
        var db = new LibraryDb(Path.Combine(AppSettings.DataDirectory, "library.db"));
        var probe = FfprobeReader.FromEnvironment();
        var ffmpeg = FfmpegTool.FromEnvironment();
        var cache = Path.Combine(AppSettings.DataDirectory, "artwork");
        var library = new LibraryService(db, new WindowsVolumeProvider(), probe);
        var artwork = new ArtworkService(db, ffmpeg, probe, cache);

        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"MediaCenter/{AppInfo.Version}");
        var images = new RemoteImages(http, ffmpeg, cache);
        var mpvPath = ToolLocator.Mpv().Path;

        var model = new LibraryModel(db, library, artwork);
        var sound = new SoundService(settings);
        var lrcLib = new LrcLibClient(http, AppInfo.ProjectUrl);
        var audio = new AudioController(mpvPath, db, settings, sound);
        var services = new AppServices
        {
            Settings = settings,
            Theme = new ThemeService(settings),
            Db = db,
            Model = model,
            Profiles = new ProfileService(db, settings),
            Previews = new PreviewClips(model, ffmpeg, settings, Path.Combine(AppSettings.DataDirectory, "previews")),
            MpvPath = mpvPath,
            Sound = sound,
            Audio = audio,
            Sleep = new SleepTimer(audio),
            Http = http,
            Images = images,
            // Große Listen und Programmführer (mehrere zig MB) brauchen länger als die 20 s der übrigen Abrufe.
            LiveTv = new LiveTvService(db, LiveHttp()),
            TvMaze = new TvMazeClient(http),
            ITunes = new ITunesAlbumClient(http),
            MusicBrainz = new MusicBrainzAlbumClient(http, AppInfo.ProjectUrl),
            LrcLib = lrcLib,
            Lyrics = new LyricsService(probe, null, Path.Combine(AppSettings.DataDirectory, "lyrics")),
            RadioSearch = new RadioBrowserClient(http),
            RadioStations = new RadioService(db, images),
            PodcastSearch = new PodcastSearchClient(http),
            Podcasts = new PodcastService(db, new PodcastFeedClient(http), images),
            Subtitles = new SubtitleStore(Path.Combine(AppSettings.DataDirectory, "subtitles")),
            Luts = new LutStore(Path.Combine(AppSettings.DataDirectory, "luts")),
            Embedder = MkvSubtitleEmbedder.FromEnvironment(),
            Editor = MkvEditor.FromEnvironment(),
            Probe = probe,
            Cast = new CastService(ToolLocator.Ffmpeg().Path, SubtitleExtractor.FromEnvironment()),
            OllamaHttp = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan },
            SubtitleText = SubtitleExtractor.FromEnvironment(),
        };
        services.RestoredPreviousState = restoredFrom;
        services.Profiles.Initialize();
        services.LoadOmdb();
        services.LoadTmdb();
        services.ApplyOnlineCovers();
        services.ApplyOnlineLyrics();
        return services;
    }

    public void Dispose()
    {
        if (_remote is not null) Task.Run(() => _remote.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(3));
        Controller.Dispose();
        Sleep.Dispose();
        // Beendet eine laufende Übertragung (der Fernseher geht in den Ruhezustand zurück); höchstens ein paar Sekunden warten.
        Task.Run(() => Cast.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(10));
        Audio.Dispose();
        Http.Dispose();
        OllamaHttp.Dispose();
        Db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // gibt library.db frei, bevor eine neu gestartete App sie austauscht
    }
}
