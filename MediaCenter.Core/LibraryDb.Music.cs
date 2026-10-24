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

    /// <summary>
    /// Titel mit ihren Tags; eigene Namen und Künstler (<see cref="RenameTrack"/>, <see cref="RenameAlbum"/>) ersetzen Titel, Album und Künstler.
    /// Die Gruppierung zu Alben hängt am Album-Künstler, nicht am Künstler des einzelnen Titels: ein geänderter Titel-Künstler holt den Titel nicht aus seinem Album.
    /// </summary>
    public List<TrackRow> ListTracks()
    {
        var trackNames = ListTrackNames();
        var trackArtists = ListTrackArtists();
        var albumNames = ListAlbumNames();
        var albumArtists = ListAlbumArtists();
        var tagRows = new List<TrackRow>();
        using (var cmd = Command("""
            SELECT t.id, f.id, f.volume_id, f.rel_path, f.missing,
                   t.title, t.artist, t.album_artist, t.album, t.track_no, t.disc_no, t.year, t.genre, f.duration_sec
            FROM tracks t JOIN files f ON f.id = t.file_id
            ORDER BY t.id
            """))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                tagRows.Add(new TrackRow(
                    r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetInt32(4) == 1,
                    r.GetString(5), r.GetStringOrNull(6), r.GetStringOrNull(7), r.GetStringOrNull(8),
                    r.GetInt32OrNull(9), r.GetInt32OrNull(10), r.GetInt32OrNull(11), r.GetStringOrNull(12), r.GetDoubleOrNull(13)));
            }
        }

        // Ein Ordner mit Titeln verschiedener Künstler unter einem Albumnamen (Playlist aus YouTube, Sampler) bleibt ein Album.
        var compilations = MusicIndex.CompilationArtists(tagRows);
        var rows = new List<TrackRow>();
        foreach (var tagRow in tagRows)
        {
            var raw = compilations.TryGetValue(MusicIndex.CompilationKey(tagRow), out var compilationArtist) && string.IsNullOrWhiteSpace(tagRow.AlbumArtist)
                ? tagRow with { AlbumArtist = compilationArtist }
                : tagRow;
            var tagKey = MusicIndex.AlbumKeyOf(raw);
            var tagGroupArtist = MusicIndex.ArtistOf(raw);
            var row = raw with { TagAlbumKey = tagKey };

            if (trackNames.TryGetValue(raw.TrackId, out var title)) row = row with { Title = title, TagTitle = raw.Title };
            if (trackArtists.TryGetValue(raw.TrackId, out var songArtist))
            {
                row = row with { Artist = songArtist, TagArtist = raw.Artist ?? "" };
                if (string.IsNullOrWhiteSpace(raw.AlbumArtist)) row = row with { AlbumArtist = tagGroupArtist }; // das Album behält seinen Künstler
            }
            if (albumNames.TryGetValue(tagKey, out var album)) row = row with { Album = album, TagAlbum = raw.Album ?? MusicIndex.UnknownAlbum };
            if (albumArtists.TryGetValue(tagKey, out var albumArtist)) row = row with { AlbumArtist = albumArtist, TagAlbumArtist = tagGroupArtist };
            rows.Add(row);
        }
        return rows;
    }

    private Dictionary<long, string> ListTrackNames() => ReadNames("SELECT track_id, title FROM track_names", r => r.GetInt64(0));

    private Dictionary<long, string> ListTrackArtists() => ReadNames("SELECT track_id, artist FROM track_artists", r => r.GetInt64(0));

    private Dictionary<string, string> ListAlbumNames() => ReadNames("SELECT album_key, title FROM album_names", r => r.GetString(0));

    private Dictionary<string, string> ListAlbumArtists() => ReadNames("SELECT album_key, artist FROM album_artists", r => r.GetString(0));

    private Dictionary<TKey, string> ReadNames<TKey>(string sql, Func<Microsoft.Data.Sqlite.SqliteDataReader, TKey> key) where TKey : notnull
    {
        var names = new Dictionary<TKey, string>();
        using var cmd = Command(sql);
        using var r = cmd.ExecuteReader();
        while (r.Read()) names[key(r)] = r.GetString(1);
        return names;
    }

    // --- Eigene Namen und Künstler ---------------------------------------------------------------------------

    /// <summary>
    /// Gibt dem Titel einen eigenen Namen und, wenn <paramref name="artist"/> gesetzt ist, einen eigenen Künstler; die Datei und ihre Tags bleiben.
    /// Entspricht ein Wert dem aus den Tags, fällt der eigene weg.
    /// </summary>
    /// <param name="artist">null lässt den Künstler, wie er ist; leer nimmt einen eigenen Künstler zurück.</param>
    public void RenameTrack(TrackRow track, string title, string? artist = null)
    {
        title = title.Trim();
        if (title.Length == 0) throw new ArgumentException("Der Name darf nicht leer sein.", nameof(title));

        InTransaction(() =>
        {
            if (title == (track.TagTitle ?? track.Title)) Run("DELETE FROM track_names WHERE track_id = $id", ("$id", track.TrackId));
            else Run("""
                INSERT INTO track_names (track_id, title) VALUES ($id, $title)
                ON CONFLICT (track_id) DO UPDATE SET title = excluded.title
                """,
                ("$id", track.TrackId), ("$title", title));

            if (artist is not null) SetTrackArtist(track, artist.Trim());
        });
    }

    private void SetTrackArtist(TrackRow track, string artist)
    {
        if (artist.Length == 0 || artist == (track.TagArtist ?? track.Artist ?? "")) Run("DELETE FROM track_artists WHERE track_id = $id", ("$id", track.TrackId));
        else Run("""
            INSERT INTO track_artists (track_id, artist) VALUES ($id, $artist)
            ON CONFLICT (track_id) DO UPDATE SET artist = excluded.artist
            """,
            ("$id", track.TrackId), ("$artist", artist));
    }

    /// <summary>Nimmt Namen und Künstler des Titels zurück; er heißt wieder wie in den Tags.</summary>
    public void ResetTrack(long trackId) => InTransaction(() =>
    {
        Run("DELETE FROM track_names WHERE track_id = $id", ("$id", trackId));
        Run("DELETE FROM track_artists WHERE track_id = $id", ("$id", trackId));
    });

    /// <summary>
    /// Gibt dem Album einen eigenen Namen und, wenn <paramref name="artist"/> gesetzt ist, einen eigenen Künstler (gilt für alle seine Titel,
    /// auch für später hinzukommende). Das Cover wandert zum neuen Schlüssel, außer das Ziel hat schon eins; heißt das Album danach wie ein
    /// anderes desselben Künstlers, werden beide zu einem.
    /// </summary>
    /// <param name="tracks">Die Titel des Albums, wie sie <see cref="ListTracks"/> liefert.</param>
    /// <param name="artist">null lässt den Künstler, wie er ist; leer nimmt einen eigenen Künstler zurück. Ein geänderter Künstler gilt auch für alle Titel des Albums.</param>
    /// <returns>Der Schlüssel des Albums danach.</returns>
    public string RenameAlbum(IReadOnlyCollection<TrackRow> tracks, string title, string? artist = null)
    {
        title = title.Trim();
        if (title.Length == 0) throw new ArgumentException("Der Name darf nicht leer sein.", nameof(title));
        if (tracks.Count == 0) throw new ArgumentException("Das Album hat keine Titel.", nameof(tracks));
        var wanted = artist?.Trim();

        return InTransaction(() =>
        {
            var first = tracks.First();
            var oldKey = MusicIndex.AlbumKeyOf(first);
            foreach (var group in tracks.GroupBy(MusicIndex.TagAlbumKeyOf))
            {
                var any = group.First();
                var tagAlbum = any.TagAlbum ?? any.Album ?? MusicIndex.UnknownAlbum;
                if (title == tagAlbum) Run("DELETE FROM album_names WHERE album_key = $key", ("$key", group.Key));
                else Run("""
                    INSERT INTO album_names (album_key, title) VALUES ($key, $title)
                    ON CONFLICT (album_key) DO UPDATE SET title = excluded.title
                    """,
                    ("$key", group.Key), ("$title", title));

                if (wanted is null) continue;
                if (wanted.Length == 0 || wanted == (any.TagAlbumArtist ?? MusicIndex.ArtistOf(any))) Run("DELETE FROM album_artists WHERE album_key = $key", ("$key", group.Key));
                else Run("""
                    INSERT INTO album_artists (album_key, artist) VALUES ($key, $artist)
                    ON CONFLICT (album_key) DO UPDATE SET artist = excluded.artist
                    """,
                    ("$key", group.Key), ("$artist", wanted));
            }

            // Wird der Künstler des Albums geändert, gilt er auch für jeden Titel darin; bleibt er, wie er ist, behalten Titel ihren eigenen (Sampler).
            if (wanted is not null && (wanted.Length == 0 || wanted != MusicIndex.ArtistOf(first)))
            {
                foreach (var track in tracks) SetTrackArtist(track, wanted);
            }

            var shownArtist = wanted switch
            {
                null => MusicIndex.ArtistOf(first),
                { Length: 0 } => first.TagAlbumArtist ?? MusicIndex.ArtistOf(first),
                _ => wanted,
            };
            var newKey = MusicIndex.AlbumKey(shownArtist, title);
            MoveAlbumArt(oldKey, newKey);
            return newKey;
        });
    }

    /// <summary>Nimmt Namen und Künstler des Albums und seiner Titel zurück; alles heißt wieder wie in den Tags.</summary>
    /// <param name="tracks">Die Titel des Albums, wie sie <see cref="ListTracks"/> liefert.</param>
    /// <returns>Der Schlüssel des Albums danach.</returns>
    public string ResetAlbum(IReadOnlyCollection<TrackRow> tracks)
    {
        if (tracks.Count == 0) throw new ArgumentException("Das Album hat keine Titel.", nameof(tracks));

        return InTransaction(() =>
        {
            var first = tracks.First();
            foreach (var key in tracks.Select(MusicIndex.TagAlbumKeyOf).Distinct())
            {
                Run("DELETE FROM album_names WHERE album_key = $key", ("$key", key));
                Run("DELETE FROM album_artists WHERE album_key = $key", ("$key", key));
            }
            foreach (var track in tracks)
            {
                Run("DELETE FROM track_artists WHERE track_id = $id", ("$id", track.TrackId));
                Run("DELETE FROM track_names WHERE track_id = $id", ("$id", track.TrackId));
            }
            var newKey = MusicIndex.TagAlbumKeyOf(first);
            MoveAlbumArt(MusicIndex.AlbumKeyOf(first), newKey);
            return newKey;
        });
    }

    private void MoveAlbumArt(string oldKey, string newKey)
    {
        if (oldKey == newKey || GetAlbumArt(newKey) is not null) return; // das Ziel behält sein Cover, das alte räumt der nächste Abgleich weg
        Run("UPDATE album_art SET album_key = $new WHERE album_key = $old", ("$new", newKey), ("$old", oldKey));
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
