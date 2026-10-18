using System.IO;
using System.Net.Http;
using MediaCenter.App.Theming;
using MediaCenter.Core;

namespace MediaCenter.App.Services;

/// <summary>Alles, was die Oberfläche zum Arbeiten braucht; wird beim Start einmal gebaut.</summary>
public sealed class AppServices : IDisposable
{
    private const string DefaultMpvPath = @"C:\Program Files\MPV Player\mpv.exe";

    public required AppSettings Settings { get; init; }

    public required ThemeService Theme { get; init; }

    public required LibraryDb Db { get; init; }

    public required LibraryModel Model { get; init; }

    public required string MpvPath { get; init; }

    public required AudioController Audio { get; init; }

    /// <summary>Einschlaf-Timer für Musik und Video.</summary>
    public required SleepTimer Sleep { get; init; }

    /// <summary>Equalizer für Musik und Video.</summary>
    public required SoundService Sound { get; init; }

    public required HttpClient Http { get; init; }

    public required RadioBrowserClient RadioSearch { get; init; }

    public required RadioService RadioStations { get; init; }

    public required PodcastSearchClient PodcastSearch { get; init; }

    public required PodcastService Podcasts { get; init; }

    /// <summary>Vom Nutzer hinzugefügte Untertitel, pro Video im Datenordner.</summary>
    public required SubtitleStore Subtitles { get; init; }

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

    /// <summary>Schaltet die Online-Suche nach Postern nach der Einstellung ein oder aus (gilt ab dem nächsten Cover-Durchlauf).</summary>
    public void ApplyOnlineCovers() =>
        Model.Artwork.Online = Settings.OnlineCovers ? new OnlineArtwork(TvMaze, Omdb, Images, new IAlbumCoverSource[] { ITunes, MusicBrainz }) : null;

    /// <summary>Schaltet die Suche nach Songtexten im Internet nach der Einstellung ein oder aus (gilt ab dem nächsten Titel).</summary>
    public void ApplyOnlineLyrics() => Lyrics.Online = Settings.OnlineLyrics ? LrcLib : null;

    /// <summary>Ohne Zeitlimit des Clients: Antworten eines großen Modells brauchen lange, <see cref="OllamaClient"/> setzt sein eigenes.</summary>
    public required HttpClient OllamaHttp { get; init; }

    /// <summary>Liest den extrahierten Text einer Untertitelspur.</summary>
    public required SubtitleExtractor SubtitleText { get; init; }

    /// <summary>Spricht mit dem Ollama aus den Einstellungen (Adresse kann sich zur Laufzeit ändern, daher jedes Mal neu).</summary>
    public OllamaClient Ollama() => new(OllamaHttp, Settings.OllamaUri);

    public static AppServices Create()
    {
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
        var mpvPath = Environment.GetEnvironmentVariable("MEDIACENTER_MPV") ?? DefaultMpvPath;

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
            Previews = new PreviewClips(model, ffmpeg, settings, Path.Combine(AppSettings.DataDirectory, "previews")),
            MpvPath = mpvPath,
            Sound = sound,
            Audio = audio,
            Sleep = new SleepTimer(audio),
            Http = http,
            Images = images,
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
            Embedder = MkvSubtitleEmbedder.FromEnvironment(),
            Editor = MkvEditor.FromEnvironment(),
            Probe = probe,
            OllamaHttp = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan },
            SubtitleText = SubtitleExtractor.FromEnvironment(),
        };
        services.LoadOmdb();
        services.ApplyOnlineCovers();
        services.ApplyOnlineLyrics();
        return services;
    }

    public void Dispose()
    {
        Sleep.Dispose();
        Audio.Dispose();
        Http.Dispose();
        OllamaHttp.Dispose();
        Db.Dispose();
    }
}
