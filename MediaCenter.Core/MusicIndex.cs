using System.Text.RegularExpressions;

namespace MediaCenter.Core;

/// <param name="Title">Angezeigter Titel: der eigene Name, falls der Titel umbenannt wurde, sonst der aus den Tags.</param>
/// <param name="Album">Angezeigtes Album, ebenso.</param>
/// <param name="TagTitle">Titel laut Tags, nur gesetzt, wenn der Titel umbenannt ist.</param>
/// <param name="TagAlbum">Album laut Tags (ohne Tag <see cref="MusicIndex.UnknownAlbum"/>), nur gesetzt, wenn das Album umbenannt ist.</param>
/// <param name="TagAlbumKey">Schlüssel des Albums laut Tags (Künstler und Album, wie sie in der Datei stehen); bei Titeln aus der Datenbank immer gesetzt.</param>
/// <param name="TagArtist">Künstler des Titels laut Tags (leer, wenn die Datei keinen hat), nur gesetzt, wenn der Künstler des Titels geändert ist.</param>
/// <param name="TagAlbumArtist">Künstler des Albums laut Tags (ohne Tags <see cref="MusicIndex.UnknownArtist"/>), nur gesetzt, wenn er geändert ist.</param>
public sealed record TrackRow(
    long TrackId, long FileId, long VolumeId, string RelPath, bool Missing,
    string Title, string? Artist, string? AlbumArtist, string? Album, int? TrackNo, int? DiscNo, int? Year, string? Genre,
    double? DurationSeconds,
    string? TagTitle = null, string? TagAlbum = null, string? TagAlbumKey = null, string? TagArtist = null, string? TagAlbumArtist = null);

public sealed record AlbumInfo(string Key, string Title, string Artist, int? Year, IReadOnlyList<TrackRow> Tracks);

public sealed record ArtistInfo(string Name, IReadOnlyList<AlbumInfo> Albums);

/// <summary>Künstler nach den Titeln: alle Titel, deren Künstler (nicht der des Albums) so heißt, auch aus Sammelalben.</summary>
public sealed record SongArtistInfo(string Name, IReadOnlyList<TrackRow> Tracks);

/// <param name="Tracks">In der Reihenfolge der Playlist.</param>
public sealed record PlaylistInfo(long Id, string Name, IReadOnlyList<TrackRow> Tracks);

/// <summary>Gruppiert Titel zu Alben und Künstlern. Reine Berechnung, keine Datenbank.</summary>
public static partial class MusicIndex
{
    public const string UnknownArtist = "Unbekannter Künstler";
    public const string UnknownAlbum = "Unbekanntes Album";
    public const string VariousArtists = "Verschiedene Interpreten";

    /// <summary>
    /// Alben ohne Album-Künstler-Tag, deren Titel im selben Ordner liegen, denselben Albumnamen tragen und verschiedene Künstler haben
    /// (Playlist-Ordner aus Videoportalen, Sampler): Sie sollen ein Album bleiben, statt je Künstler zu zerfallen.
    /// Liefert für jedes solche Album den Album-Künstler: der Künstler, dem mindestens drei Viertel der Titel gehören (so bleibt „Band“
    /// mit einzelnen „Band feat. Gast“ beisammen), sonst <see cref="VariousArtists"/>.
    /// </summary>
    /// <param name="tracks">Titel, wie sie in den Tags stehen (ohne eigene Namen und Künstler).</param>
    public static IReadOnlyDictionary<(long VolumeId, string Folder, string Album), string> CompilationArtists(IEnumerable<TrackRow> tracks)
    {
        var result = new Dictionary<(long, string, string), string>();
        foreach (var group in tracks
                     .Where(t => string.IsNullOrWhiteSpace(t.AlbumArtist) && t.Album is not null)
                     .GroupBy(CompilationKey))
        {
            var byArtist = group.GroupBy(t => Normalize(ArtistOf(t))).OrderByDescending(g => g.Count()).ToList();
            if (byArtist.Count < 2) continue;

            var top = byArtist[0];
            var artist = top.Count() * 4 >= group.Count() * 3 && ArtistOf(top.First()) != UnknownArtist ? ArtistOf(top.First()) : VariousArtists;
            result[group.Key] = artist;
        }
        return result;
    }

    public static (long VolumeId, string Folder, string Album) CompilationKey(TrackRow track) =>
        (track.VolumeId, (Path.GetDirectoryName(track.RelPath) ?? "").ToLowerInvariant(), Normalize(track.Album ?? UnknownAlbum));

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWord();

    /// <summary>Anzeigename des Künstlers eines Albums: Album-Künstler, sonst Künstler des Titels.</summary>
    public static string ArtistOf(TrackRow track) =>
        !string.IsNullOrWhiteSpace(track.AlbumArtist) ? track.AlbumArtist! :
        !string.IsNullOrWhiteSpace(track.Artist) ? track.Artist! : UnknownArtist;

    public static string AlbumKey(string artist, string album) => $"{Normalize(artist)}|{Normalize(album)}";

    /// <summary>Schlüssel des angezeigten Albums des Titels.</summary>
    public static string AlbumKeyOf(TrackRow track) => AlbumKey(ArtistOf(track), track.Album ?? UnknownAlbum);

    /// <summary>Schlüssel des Albums laut Tags; so heißt es auch nach einer Änderung in der Datenbank.</summary>
    public static string TagAlbumKeyOf(TrackRow track) =>
        track.TagAlbumKey ?? AlbumKey(track.TagAlbumArtist ?? ArtistOf(track), track.TagAlbum ?? track.Album ?? UnknownAlbum);

    private static string Normalize(string text) => NonWord().Replace(text.ToLowerInvariant(), " ").Trim();

    public static IReadOnlyList<AlbumInfo> BuildAlbums(IEnumerable<TrackRow> tracks) =>
        tracks
            .GroupBy(t => AlbumKey(ArtistOf(t), t.Album ?? UnknownAlbum))
            .Select(g =>
            {
                var ordered = g
                    .OrderBy(t => t.DiscNo ?? 1)
                    .ThenBy(t => t.TrackNo ?? int.MaxValue)
                    .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                var year = g.Where(t => t.Year is not null).Select(t => t.Year).Min();
                return new AlbumInfo(g.Key, ordered[0].Album ?? UnknownAlbum, ArtistOf(ordered[0]), year, ordered);
            })
            .OrderBy(a => a.Artist, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(a => a.Year ?? int.MaxValue)
            .ThenBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>Verbindet die Playlists mit den Titeln der Bibliothek; Titel-Ids ohne Titel (inzwischen entfernt) fallen weg.</summary>
    public static IReadOnlyList<PlaylistInfo> BuildPlaylists(
        IEnumerable<PlaylistRow> playlists, IReadOnlyDictionary<long, List<long>> items, IEnumerable<TrackRow> tracks)
    {
        var byId = tracks.ToDictionary(t => t.TrackId);
        return playlists
            .Select(p => new PlaylistInfo(
                p.Id, p.Name,
                (items.TryGetValue(p.Id, out var ids) ? ids : new List<long>()).Where(byId.ContainsKey).Select(id => byId[id]).ToList()))
            .ToList();
    }

    public static IReadOnlyList<ArtistInfo> BuildArtists(IEnumerable<AlbumInfo> albums) =>
        albums
            .GroupBy(a => a.Artist, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new ArtistInfo(g.First().Artist, g.ToList()))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>Künstler des einzelnen Titels; ohne Titel-Künstler der des Albums.</summary>
    public static string SongArtistOf(TrackRow track) => !string.IsNullOrWhiteSpace(track.Artist) ? track.Artist! : ArtistOf(track);

    /// <summary>Gruppiert die Titel nach dem Künstler des Titels (ohne Rücksicht auf Groß- und Kleinschreibung); die Titel jedes Künstlers nach Album und Nummer.</summary>
    public static IReadOnlyList<SongArtistInfo> BuildSongArtists(IEnumerable<TrackRow> tracks) =>
        tracks
            .GroupBy(SongArtistOf, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new SongArtistInfo(g.Key, SortForArtist(g)))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private static List<TrackRow> SortForArtist(IEnumerable<TrackRow> tracks) =>
        tracks
            .OrderBy(t => t.Album ?? UnknownAlbum, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(t => t.DiscNo ?? 1)
            .ThenBy(t => t.TrackNo ?? int.MaxValue)
            .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}
