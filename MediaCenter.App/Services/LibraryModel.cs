using System.IO;
using System.Windows.Media;
using MediaCenter.Core;

namespace MediaCenter.App.Services;

/// <summary>Was eine Karte in einer Reihe zeigt.</summary>
public sealed class CardItem
{
    public required long TitleId { get; init; }

    public required string Title { get; init; }

    public required string Meta { get; init; }

    public required TitleKind Kind { get; init; }

    public ImageSource? Image { get; init; }

    /// <summary>0 bis 1; nur bei "Weiterschauen" gesetzt.</summary>
    public double Progress { get; init; }

    /// <summary>Kein Video des Titels ist gerade erreichbar (Laufwerk abgesteckt oder Datei fehlt).</summary>
    public bool Offline { get; init; }

    /// <summary>Quadratisches Cover (Alben, Podcasts, Sender) statt Poster im Hochformat.</summary>
    public bool Square { get; init; }

    /// <summary>Kantenlänge einer quadratischen Karte; die Musik-Seite nimmt kleinere für mehr Inhalt je Reihe.</summary>
    public double SquareSize { get; init; } = 176;

    /// <summary>Was die Seite mit der Karte verbindet (z. B. Album oder Podcast).</summary>
    public object? Tag { get; init; }
}

/// <summary>
/// Momentaufnahme der Bibliothek für die Oberfläche. <see cref="Reload"/> liest alles neu; die Seiten fragen
/// danach nur diese Momentaufnahme und die Datenbank nicht einzeln pro Karte.
/// </summary>
public sealed class LibraryModel(LibraryDb db, LibraryService library, ArtworkService artwork)
{
    private Dictionary<(ArtworkOwner, long, ArtworkKind), string> _art = new();

    public LibraryDb Db => db;

    public LibraryService Library => library;

    public ArtworkService Artwork => artwork;

    public VolumeStatus Volumes { get; private set; } = new(new Dictionary<long, VolumeInfo>(), Array.Empty<UnresolvedVolume>());

    /// <summary>Das aktive Profil beim letzten <see cref="Reload"/>. Ein Kinderprofil sieht nur freigegebene Titel und keine Musik, Sender oder Podcasts.</summary>
    public ProfileRow Profile { get; private set; } = new(LibraryDb.DefaultProfileId, "Ich", "#2EC4D6", false, false);

    /// <summary>Titel-Ids, die für Kinderprofile freigegeben sind (für alle Profile gleich).</summary>
    public IReadOnlySet<long> KidsTitleIds { get; private set; } = new HashSet<long>();

    /// <summary>Gibt Titel für Kinderprofile frei oder nimmt die Freigabe zurück.</summary>
    public void SetKidsTitles(IReadOnlyCollection<long> titleIds, bool on)
    {
        db.SetKidsTitles(titleIds, on);
        KidsTitleIds = db.GetKidsTitleIds();
    }

    private HashSet<long>? _allowed; // null = alles sichtbar

    private bool Allowed(long titleId) => _allowed is null || _allowed.Contains(titleId);

    public IReadOnlyList<TitleRow> Titles { get; private set; } = Array.Empty<TitleRow>();

    public IReadOnlyDictionary<long, List<string>> Genres { get; private set; } = new Dictionary<long, List<string>>();

    /// <summary>Infotexte aus dem Internet (oder selbst geschrieben) je Titel-Id; Titel ohne Infotexte fehlen.</summary>
    public IReadOnlyDictionary<long, TitleInfoRow> Infos { get; private set; } = new Dictionary<long, TitleInfoRow>();

    public IReadOnlyList<ContinueItem> Continue { get; private set; } = Array.Empty<ContinueItem>();

    /// <summary>Ids der Titel auf „Meine Liste“, der zuletzt vorgemerkte zuerst.</summary>
    public IReadOnlyList<long> Watchlist { get; private set; } = Array.Empty<long>();

    /// <summary>Ein Titel kam auf „Meine Liste“ oder wurde heruntergenommen (<see cref="SetOnWatchlist"/>); Seiten mit der Liste bauen sich neu auf.</summary>
    public event Action? WatchlistChanged;

    /// <summary>Sammlungen (Filmreihen) nach Name.</summary>
    public IReadOnlyList<CollectionRow> Collections { get; private set; } = Array.Empty<CollectionRow>();

    /// <summary>Film-Ids je Sammlungs-Id, nach Jahr geordnet; leere Sammlungen fehlen.</summary>
    public IReadOnlyDictionary<long, List<long>> CollectionItems { get; private set; } = new Dictionary<long, List<long>>();

    /// <summary>Liest nur die Sammlungen neu (nach einer Änderung); die übrige Momentaufnahme bleibt.</summary>
    public void ReloadCollections()
    {
        Collections = db.ListCollections();
        CollectionItems = db.ListCollectionItems();
        if (_allowed is null) return;

        // Im Kinderprofil nur freigegebene Filme; Sammlungen ohne einen solchen verschwinden.
        CollectionItems = CollectionItems.ToDictionary(c => c.Key, c => c.Value.Where(Allowed).ToList()).Where(c => c.Value.Count > 0).ToDictionary(c => c.Key, c => c.Value);
        Collections = Collections.Where(c => CollectionItems.ContainsKey(c.Id)).ToList();
    }

    /// <summary>Die Sammlungen, in denen ein Film steht.</summary>
    public IEnumerable<CollectionRow> CollectionsOf(long titleId) =>
        Collections.Where(c => CollectionItems.TryGetValue(c.Id, out var ids) && ids.Contains(titleId));

    public IReadOnlyList<VideoRow> Inbox { get; private set; } = Array.Empty<VideoRow>();

    public IReadOnlyDictionary<long, List<VideoRow>> VideosByTitle { get; private set; } = new Dictionary<long, List<VideoRow>>();

    public IReadOnlyList<TrackRow> Tracks { get; private set; } = Array.Empty<TrackRow>();

    public IReadOnlyList<AlbumInfo> Albums { get; private set; } = Array.Empty<AlbumInfo>();

    public IReadOnlyList<ArtistInfo> Artists { get; private set; } = Array.Empty<ArtistInfo>();

    public IReadOnlyList<SongArtistInfo> SongArtists { get; private set; } = Array.Empty<SongArtistInfo>();

    public IReadOnlyList<PlaylistInfo> Playlists { get; private set; } = Array.Empty<PlaylistInfo>();

    /// <summary>Eine Playlist wurde angelegt, umbenannt, gelöscht oder ihre Titel haben sich geändert (<see cref="ReloadPlaylists"/>).</summary>
    public event Action? PlaylistsChanged;

    public IReadOnlyList<SavedStation> Stations { get; private set; } = Array.Empty<SavedStation>();

    public IReadOnlyList<PodcastInfo> Podcasts { get; private set; } = Array.Empty<PodcastInfo>();

    private Dictionary<string, string> _albumArt = new();

    public void Reload()
    {
        Volumes = library.RefreshVolumes();
        Profile = db.GetProfile(db.ActiveProfileId);
        KidsTitleIds = db.GetKidsTitleIds();
        _allowed = Profile.Kids ? KidsTitleIds.ToHashSet() : null;
        Titles = db.GetTitles().Where(t => Allowed(t.Id)).ToList();
        Genres = db.GetAllTitleGenres();
        Infos = db.ListTitleInfos();
        Continue = db.GetContinueWatching().Where(c => Allowed(c.TitleId)).ToList();
        Watchlist = db.GetWatchlist().Where(Allowed).ToList();
        ReloadCollections();

        var videos = db.ListVideos();
        Inbox = Profile.Kids ? Array.Empty<VideoRow>() : videos.Where(v => v.IsInbox).ToList();
        VideosByTitle = videos.Where(v => !v.IsInbox && Allowed(v.TitleId!.Value)).GroupBy(v => v.TitleId!.Value).ToDictionary(g => g.Key, g => g.ToList());

        _art = db.GetAllArtwork().ToDictionary(a => (a.Owner, a.OwnerId, a.Kind), a => Path.Combine(artwork.CacheDir, a.File));

        LoadMusic();
        Stations = Profile.Kids ? Array.Empty<SavedStation>() : db.ListStations();
        Podcasts = Profile.Kids ? Array.Empty<PodcastInfo>() : db.ListPodcasts();
    }

    private void LoadMusic()
    {
        Tracks = Profile.Kids ? Array.Empty<TrackRow>() : db.ListTracks();
        Albums = MusicIndex.BuildAlbums(Tracks);
        Artists = MusicIndex.BuildArtists(Albums);
        SongArtists = MusicIndex.BuildSongArtists(Tracks);
        _albumArt = db.GetAllAlbumArt().ToDictionary(a => a.Key, a => Path.Combine(artwork.CacheDir, a.Value.File));
        Playlists = MusicIndex.BuildPlaylists(db.ListPlaylists(), db.ListPlaylistItems(), Tracks);
    }

    /// <summary>Titel, Alben, Künstler, Cover und Playlists wurden von Hand umbenannt (oder ihr Cover kam dazu): liest nur die Musik neu und meldet es.</summary>
    /// <param name="oldAlbumKey">Schlüssel des umbenannten Albums vorher; null, wenn nur ein Titel umbenannt wurde.</param>
    /// <param name="newAlbumKey">Schlüssel danach.</param>
    public void ReloadMusic(string? oldAlbumKey = null, string? newAlbumKey = null)
    {
        LoadMusic();
        MusicChanged?.Invoke(oldAlbumKey, newAlbumKey);
    }

    /// <summary>Die Musik wurde neu gelesen (<see cref="ReloadMusic"/>); Argumente: Albumschlüssel vorher und nachher, beide null ohne Albumwechsel. Die Musik-Seite baut sich neu auf und bleibt, wo sie ist.</summary>
    public event Action<string?, string?>? MusicChanged;

    /// <summary>Liest nur die Playlists neu (nach einer Änderung) und meldet es; die übrige Momentaufnahme bleibt, die Seiten behalten ihren Stand.</summary>
    public void ReloadPlaylists()
    {
        Playlists = MusicIndex.BuildPlaylists(db.ListPlaylists(), db.ListPlaylistItems(), Tracks);
        PlaylistsChanged?.Invoke();
    }

    /// <summary>Cover der Playlist: das erste Album-Cover ihrer Titel; null, wenn keiner eins hat.</summary>
    public string? PlaylistCover(PlaylistInfo playlist) => playlist.Tracks
        .Select(t => AlbumArtPath(MusicIndex.AlbumKey(MusicIndex.ArtistOf(t), t.Album ?? MusicIndex.UnknownAlbum)))
        .FirstOrDefault(p => p is not null);

    public bool IsOnWatchlist(long titleId) => Watchlist.Contains(titleId);

    /// <summary>Wie weit jeder Titel gesehen ist; liest den Fortschritt frisch aus der Datenbank (er ändert sich beim Abspielen).</summary>
    public Dictionary<long, WatchState> WatchStatesByTitle()
    {
        var playback = db.GetAllPlayback();
        return Titles.ToDictionary(t => t.Id, t => WatchStates.Of(VideosOf(t.Id), playback));
    }

    public WatchState WatchStateOf(long titleId) => WatchStates.Of(VideosOf(titleId), db.GetAllPlayback());

    /// <summary>Wertung je Titel-Id für die Sortierung; Titel ohne Wertung fehlen.</summary>
    public Dictionary<long, double> Ratings() =>
        Infos.Values.Where(i => i.Rating is not null).ToDictionary(i => i.TitleId, i => i.Rating!.Value);

    /// <summary>Größte Bildhöhe je Titel-Id für die Sortierung; Titel ohne bekannte Höhe fehlen.</summary>
    public Dictionary<long, int> Heights() =>
        VideosByTitle.Select(p => (p.Key, Height: p.Value.Max(v => v.Height))).Where(x => x.Height is not null).ToDictionary(x => x.Key, x => x.Height!.Value);

    /// <summary>Besetzung je Titel-Id für die Suche nach Schauspielern.</summary>
    public Dictionary<long, string> Casts() =>
        Infos.Values.Where(i => !string.IsNullOrWhiteSpace(i.Cast)).ToDictionary(i => i.TitleId, i => i.Cast!);

    /// <summary>Setzt einen Titel auf „Meine Liste“ oder nimmt ihn herunter und meldet es; die übrige Momentaufnahme bleibt.</summary>
    public void SetOnWatchlist(long titleId, bool on)
    {
        db.SetOnWatchlist(titleId, on);
        Watchlist = db.GetWatchlist();
        WatchlistChanged?.Invoke();
    }

    /// <summary>Die Titel auf „Meine Liste“, der zuletzt vorgemerkte zuerst.</summary>
    public IEnumerable<TitleRow> WatchlistTitles() => Watchlist.Select(FindTitle).OfType<TitleRow>();

    public string? AlbumArtPath(string albumKey) => _albumArt.GetValueOrDefault(albumKey);

    /// <summary>Absoluter Pfad eines Bildes im Cover-Cache; null, wenn kein Dateiname vorliegt.</summary>
    public string? CachePath(string? file) => file is null ? null : Path.Combine(artwork.CacheDir, file);

    /// <summary>Absoluter Pfad, wenn Datei und Laufwerk erreichbar sind; sonst null.</summary>
    public string? PathOf(TrackRow track) =>
        track.Missing ? null : Volumes.ResolvePath(track.VolumeId, track.RelPath) is { } path && File.Exists(path) ? path : null;

    public bool IsOnline(AlbumInfo album) => album.Tracks.Any(t => PathOf(t) is not null);

    /// <summary>Karte für ein Album (quadratisch).</summary>
    /// <param name="meta">Zeile unter dem Titel; null zeigt Künstler und Jahr.</param>
    public CardItem ToCard(AlbumInfo album, string? meta = null) => new()
    {
        TitleId = 0,
        Title = album.Title,
        Meta = meta ?? $"{album.Artist}" + (album.Year is { } year ? $" · {year}" : ""),
        Kind = TitleKind.Movie,
        Image = AlbumArtPath(album.Key) is { } path ? ImageCache.Load(path, 360) : null,
        Offline = !IsOnline(album),
        Square = true,
        SquareSize = 140,
        Tag = album,
    };

    public string? ArtPath(ArtworkOwner owner, long id, ArtworkKind kind) => _art.GetValueOrDefault((owner, id, kind));

    public TitleRow? FindTitle(long id) => Titles.FirstOrDefault(t => t.Id == id);

    public IReadOnlyList<VideoRow> VideosOf(long titleId) =>
        VideosByTitle.TryGetValue(titleId, out var list) ? list : Array.Empty<VideoRow>();

    public IReadOnlyList<string> GenresOf(long titleId) =>
        Genres.TryGetValue(titleId, out var list) ? list : Array.Empty<string>();

    /// <summary>Absoluter Pfad, wenn Datei und Laufwerk erreichbar sind; sonst null.</summary>
    public string? PathOf(VideoRow video) =>
        video.Missing ? null : Volumes.ResolvePath(video.VolumeId, video.RelPath) is { } path && File.Exists(path) ? path : null;

    public bool IsOnline(long titleId) => VideosOf(titleId).Any(v => PathOf(v) is not null);

    public IEnumerable<string> AllGenres() =>
        Genres.Values.SelectMany(g => g).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g, StringComparer.OrdinalIgnoreCase);

    /// <summary>Hochformat-Cover, sonst Querformat; bei keinem von beiden null (die Karte zeigt dann einen Platzhalter).</summary>
    public (string Path, ArtworkKind Kind)? BestCover(long titleId)
    {
        if (ArtPath(ArtworkOwner.Title, titleId, ArtworkKind.Poster) is { } poster) return (poster, ArtworkKind.Poster);
        if (ArtPath(ArtworkOwner.Title, titleId, ArtworkKind.Landscape) is { } landscape) return (landscape, ArtworkKind.Landscape);
        return null;
    }

    public string MetaOf(TitleRow title)
    {
        var videos = VideosOf(title.Id);
        if (title.Kind == TitleKind.Movie)
        {
            var parts = new List<string>();
            if (title.Year is { } year) parts.Add(year.ToString());
            if (videos.FirstOrDefault()?.DurationSeconds is { } seconds) parts.Add($"{Math.Round(seconds / 60):0} min");
            return string.Join(" · ", parts);
        }
        var seasons = videos.Where(v => v.Season is not null).Select(v => v.Season).Distinct().Count();
        var seasonText = seasons > 0 ? $"{seasons} {(seasons == 1 ? "Staffel" : "Staffeln")} · " : "";
        return $"{seasonText}{videos.Count} {(videos.Count == 1 ? "Folge" : "Folgen")}";
    }

    /// <param name="meta">Zeile unter dem Titel; null zeigt die Angaben zum Titel (Jahr, Länge, Staffeln).</param>
    public CardItem ToCard(TitleRow title, double progress = 0, string? meta = null)
    {
        var cover = BestCover(title.Id);
        return new CardItem
        {
            TitleId = title.Id,
            Title = title.Name,
            Meta = meta ?? MetaOf(title),
            Kind = title.Kind,
            Image = cover is { } c ? ImageCache.Load(c.Path, 360) : null,
            Progress = progress,
            Offline = !IsOnline(title.Id),
        };
    }

    /// <summary>Karten für "Weiterschauen" mit dem Fortschritt der angefangenen Folge bzw. des Films.</summary>
    public IEnumerable<CardItem> ContinueCards() =>
        Continue.Select(item => (Item: item, Title: FindTitle(item.TitleId)))
            .Where(x => x.Title is not null)
            .Select(x => ToCard(x.Title!, x.Item.DurationSeconds is > 0 ? Math.Clamp(x.Item.PositionSeconds / x.Item.DurationSeconds.Value, 0, 1) : 0));
}
