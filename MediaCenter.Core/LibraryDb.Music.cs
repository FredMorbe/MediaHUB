namespace MediaCenter.Core;

// Musik: Tags der Audiodateien (abgeleitet, werden beim Scan überschrieben) und Cover der Alben.
public sealed partial class LibraryDb
{
    public void ApplyTrack(long fileId, TrackTags tags) => Run("""
        INSERT INTO tracks (file_id, title, artist, album_artist, album, track_no, disc_no, year, genre)
        VALUES ($file, $title, $artist, $albumArtist, $album, $track, $disc, $year, $genre)
        ON CONFLICT (file_id) DO UPDATE SET
            title = excluded.title, artist = excluded.artist, album_artist = excluded.album_artist, album = excluded.album,
            track_no = excluded.track_no, disc_no = excluded.disc_no, year = excluded.year, genre = excluded.genre
        """,
        ("$file", fileId), ("$title", tags.Title), ("$artist", tags.Artist), ("$albumArtist", tags.AlbumArtist), ("$album", tags.Album),
        ("$track", tags.TrackNo), ("$disc", tags.DiscNo), ("$year", tags.Year), ("$genre", tags.Genre));

    public List<TrackRow> ListTracks()
    {
        var rows = new List<TrackRow>();
        using var cmd = Command("""
            SELECT t.id, f.id, f.volume_id, f.rel_path, f.missing,
                   t.title, t.artist, t.album_artist, t.album, t.track_no, t.disc_no, t.year, t.genre, f.duration_sec
            FROM tracks t JOIN files f ON f.id = t.file_id
            ORDER BY t.id
            """);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new TrackRow(
                r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetInt32(4) == 1,
                r.GetString(5), r.GetStringOrNull(6), r.GetStringOrNull(7), r.GetStringOrNull(8),
                r.GetInt32OrNull(9), r.GetInt32OrNull(10), r.GetInt32OrNull(11), r.GetStringOrNull(12), r.GetDoubleOrNull(13)));
        }
        return rows;
    }

    // --- Album-Cover ---------------------------------------------------------------------------------------

    /// <returns>Dateiname im Cover-Cache oder null.</returns>
    public string? GetAlbumArt(string albumKey)
    {
        using var cmd = Command("SELECT file FROM album_art WHERE album_key = $key", ("$key", albumKey));
        return cmd.ExecuteScalar() as string;
    }

    public Dictionary<string, (string File, string Source)> GetAllAlbumArt()
    {
        var map = new Dictionary<string, (string, string)>();
        using var cmd = Command("SELECT album_key, file, source FROM album_art");
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetString(0)] = (r.GetString(1), r.GetString(2));
        return map;
    }

    public void SetAlbumArt(string albumKey, string source, string file) => Run("""
        INSERT INTO album_art (album_key, source, file, updated_utc) VALUES ($key, $source, $file, $now)
        ON CONFLICT (album_key) DO UPDATE SET source = excluded.source, file = excluded.file, updated_utc = excluded.updated_utc
        """,
        ("$key", albumKey), ("$source", source), ("$file", file), ("$now", Utc(DateTime.UtcNow)));

    /// <summary>Letzte Suche dieses Albums bei diesem Anbieter, oder null.</summary>
    public OnlineLookup? GetAlbumLookup(string albumKey, string provider)
    {
        using var cmd = Command(
            "SELECT query, found, checked_utc FROM album_lookups WHERE album_key = $key AND provider = $provider",
            ("$key", albumKey), ("$provider", provider));
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new OnlineLookup(r.GetString(0), r.GetInt32(1) == 1, DateTime.Parse(r.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime())
            : null;
    }

    public void SetAlbumLookup(string albumKey, string provider, string query, bool found) => Run("""
        INSERT INTO album_lookups (album_key, provider, query, found, checked_utc) VALUES ($key, $provider, $query, $found, $now)
        ON CONFLICT (album_key, provider) DO UPDATE SET query = excluded.query, found = excluded.found, checked_utc = excluded.checked_utc
        """,
        ("$key", albumKey), ("$provider", provider), ("$query", query), ("$found", found ? 1 : 0), ("$now", Utc(DateTime.UtcNow)));

    /// <summary>Vergisst gemerkte Suchen von Alben, die es nicht mehr gibt.</summary>
    public void DeleteOrphanedAlbumLookups(ISet<string> liveAlbumKeys) => InTransaction(() =>
    {
        var keys = new List<string>();
        using (var cmd = Command("SELECT DISTINCT album_key FROM album_lookups"))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read()) keys.Add(r.GetString(0));
        }
        foreach (var key in keys.Where(k => !liveAlbumKeys.Contains(k))) Run("DELETE FROM album_lookups WHERE album_key = $key", ("$key", key));
    });

    /// <summary>Löscht Cover von Alben, die es nicht mehr gibt; liefert deren Dateinamen.</summary>
    public List<string> DeleteOrphanedAlbumArt(ISet<string> liveAlbumKeys) => InTransaction(() =>
    {
        var files = new List<string>();
        foreach (var (key, (file, _)) in GetAllAlbumArt())
        {
            if (liveAlbumKeys.Contains(key)) continue;
            files.Add(file);
            DeleteAlbumArt(key);
        }
        return files;
    });

    public void DeleteAlbumArt(string albumKey) => Run("DELETE FROM album_art WHERE album_key = $key", ("$key", albumKey));
}
