using System.Text.RegularExpressions;

namespace MediaCenter.Core;

public sealed record TrackRow(
    long TrackId, long FileId, long VolumeId, string RelPath, bool Missing,
    string Title, string? Artist, string? AlbumArtist, string? Album, int? TrackNo, int? DiscNo, int? Year, string? Genre,
    double? DurationSeconds);

public sealed record AlbumInfo(string Key, string Title, string Artist, int? Year, IReadOnlyList<TrackRow> Tracks);

public sealed record ArtistInfo(string Name, IReadOnlyList<AlbumInfo> Albums);

/// <param name="Tracks">In der Reihenfolge der Playlist.</param>
public sealed record PlaylistInfo(long Id, string Name, IReadOnlyList<TrackRow> Tracks);

/// <summary>Gruppiert Titel zu Alben und Künstlern. Reine Berechnung, keine Datenbank.</summary>
public static partial class MusicIndex
{
    public const string UnknownArtist = "Unbekannter Künstler";
    public const string UnknownAlbum = "Unbekanntes Album";

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWord();

    /// <summary>Anzeigename des Künstlers eines Albums: Album-Künstler, sonst Künstler des Titels.</summary>
    public static string ArtistOf(TrackRow track) =>
        !string.IsNullOrWhiteSpace(track.AlbumArtist) ? track.AlbumArtist! :
        !string.IsNullOrWhiteSpace(track.Artist) ? track.Artist! : UnknownArtist;

    public static string AlbumKey(string artist, string album) => $"{Normalize(artist)}|{Normalize(album)}";

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
}
