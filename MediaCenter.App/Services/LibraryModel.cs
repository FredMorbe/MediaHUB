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

    public IReadOnlyList<TitleRow> Titles { get; private set; } = Array.Empty<TitleRow>();

    public IReadOnlyDictionary<long, List<string>> Genres { get; private set; } = new Dictionary<long, List<string>>();

    public IReadOnlyList<ContinueItem> Continue { get; private set; } = Array.Empty<ContinueItem>();

    public IReadOnlyList<VideoRow> Inbox { get; private set; } = Array.Empty<VideoRow>();

    public IReadOnlyDictionary<long, List<VideoRow>> VideosByTitle { get; private set; } = new Dictionary<long, List<VideoRow>>();

    public IReadOnlyList<TrackRow> Tracks { get; private set; } = Array.Empty<TrackRow>();

    public IReadOnlyList<AlbumInfo> Albums { get; private set; } = Array.Empty<AlbumInfo>();

    public IReadOnlyList<ArtistInfo> Artists { get; private set; } = Array.Empty<ArtistInfo>();

    public IReadOnlyList<SavedStation> Stations { get; private set; } = Array.Empty<SavedStation>();

    public IReadOnlyList<PodcastInfo> Podcasts { get; private set; } = Array.Empty<PodcastInfo>();

    private Dictionary<string, string> _albumArt = new();

    public void Reload()
    {
        Volumes = library.RefreshVolumes();
        Titles = db.GetTitles();
        Genres = db.GetAllTitleGenres();
        Continue = db.GetContinueWatching();

        var videos = db.ListVideos();
        Inbox = videos.Where(v => v.IsInbox).ToList();
        VideosByTitle = videos.Where(v => !v.IsInbox).GroupBy(v => v.TitleId!.Value).ToDictionary(g => g.Key, g => g.ToList());

        _art = db.GetAllArtwork().ToDictionary(a => (a.Owner, a.OwnerId, a.Kind), a => Path.Combine(artwork.CacheDir, a.File));

        Tracks = db.ListTracks();
        Albums = MusicIndex.BuildAlbums(Tracks);
        Artists = MusicIndex.BuildArtists(Albums);
        _albumArt = db.GetAllAlbumArt().ToDictionary(a => a.Key, a => Path.Combine(artwork.CacheDir, a.Value.File));
        Stations = db.ListStations();
        Podcasts = db.ListPodcasts();
    }

    public string? AlbumArtPath(string albumKey) => _albumArt.GetValueOrDefault(albumKey);

    /// <summary>Absoluter Pfad eines Bildes im Cover-Cache; null, wenn kein Dateiname vorliegt.</summary>
    public string? CachePath(string? file) => file is null ? null : Path.Combine(artwork.CacheDir, file);

    /// <summary>Absoluter Pfad, wenn Datei und Laufwerk erreichbar sind; sonst null.</summary>
    public string? PathOf(TrackRow track) =>
        track.Missing ? null : Volumes.ResolvePath(track.VolumeId, track.RelPath) is { } path && File.Exists(path) ? path : null;

    public bool IsOnline(AlbumInfo album) => album.Tracks.Any(t => PathOf(t) is not null);

    /// <summary>Karte für ein Album (quadratisch).</summary>
    public CardItem ToCard(AlbumInfo album) => new()
    {
        TitleId = 0,
        Title = album.Title,
        Meta = $"{album.Artist}" + (album.Year is { } year ? $" · {year}" : ""),
        Kind = TitleKind.Movie,
        Image = AlbumArtPath(album.Key) is { } path ? ImageCache.Load(path, 360) : null,
        Offline = !IsOnline(album),
        Square = true,
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

    public CardItem ToCard(TitleRow title, double progress = 0)
    {
        var cover = BestCover(title.Id);
        return new CardItem
        {
            TitleId = title.Id,
            Title = title.Name,
            Meta = MetaOf(title),
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
