namespace MediaCenter.Core;

/// <summary>Eine vom Nutzer angelegte Playlist (ohne ihre Titel; siehe <see cref="LibraryDb.ListPlaylistItems"/>).</summary>
public sealed record PlaylistRow(long Id, string Name, string CreatedUtc);

// Playlists: Listen von Titeln, die der Nutzer anlegt (Nutzerdaten, ein Scan fasst sie nie an). Ein Titel steht höchstens einmal in einer
// Playlist; verschwindet er aus der Bibliothek, verschwindet er auch aus den Playlists.
public sealed partial class LibraryDb
{
    public const int MaxPlaylistNameLength = 80;

    /// <summary>Legt eine leere Playlist an. Der Name ist Pflicht und je Playlist eindeutig (Groß-/Kleinschreibung egal).</summary>
    /// <exception cref="ArgumentException">Name leer, zu lang oder schon vergeben.</exception>
    /// <returns>Die Id der neuen Playlist.</returns>
    public long CreatePlaylist(string name) => InTransaction(() =>
    {
        name = ValidatedPlaylistName(name, exceptId: null);
        Run("INSERT INTO playlists (name, created_utc) VALUES ($name, $now)", ("$name", name), ("$now", Utc(DateTime.UtcNow)));
        return Scalar<long>("SELECT last_insert_rowid()");
    });

    /// <exception cref="ArgumentException">Name leer, zu lang oder schon vergeben.</exception>
    /// <exception cref="InvalidOperationException">Die Playlist gibt es nicht (mehr).</exception>
    public void RenamePlaylist(long playlistId, string name) => InTransaction(() =>
    {
        name = ValidatedPlaylistName(name, exceptId: playlistId);
        if (Run("UPDATE playlists SET name = $name WHERE id = $id", ("$name", name), ("$id", playlistId)) == 0) throw MissingPlaylist(playlistId);
    });

    /// <summary>Löscht die Playlist samt Zuordnung; die Titel bleiben in der Bibliothek.</summary>
    public void DeletePlaylist(long playlistId)
    {
        if (Run("DELETE FROM playlists WHERE id = $id", ("$id", playlistId)) == 0) throw MissingPlaylist(playlistId);
    }

    /// <summary>Alle Playlists nach Name.</summary>
    public List<PlaylistRow> ListPlaylists()
    {
        var rows = new List<PlaylistRow>();
        using var cmd = Command("SELECT id, name, created_utc FROM playlists ORDER BY name COLLATE NOCASE");
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(new PlaylistRow(r.GetInt64(0), r.GetString(1), r.GetString(2)));
        return rows;
    }

    /// <summary>Titel-Ids je Playlist in der Reihenfolge der Playlist; leere Playlists fehlen.</summary>
    public Dictionary<long, List<long>> ListPlaylistItems()
    {
        var map = new Dictionary<long, List<long>>();
        using var cmd = Command("SELECT playlist_id, track_id FROM playlist_items ORDER BY playlist_id, position");
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var id = r.GetInt64(0);
            if (!map.TryGetValue(id, out var list)) map[id] = list = new List<long>();
            list.Add(r.GetInt64(1));
        }
        return map;
    }

    /// <summary>Hängt Titel hinten an; Titel, die schon drin sind, bleiben an ihrem Platz.</summary>
    /// <returns>Anzahl der tatsächlich hinzugefügten Titel.</returns>
    /// <exception cref="InvalidOperationException">Die Playlist gibt es nicht (mehr).</exception>
    public int AddTracksToPlaylist(long playlistId, IEnumerable<long> trackIds) => InTransaction(() =>
    {
        if (Scalar<long>("SELECT COUNT(*) FROM playlists WHERE id = $id", ("$id", playlistId)) == 0) throw MissingPlaylist(playlistId);
        var next = Scalar<long>("SELECT COALESCE(MAX(position), 0) FROM playlist_items WHERE playlist_id = $id", ("$id", playlistId)) + 1;
        var added = 0;
        foreach (var trackId in trackIds.Distinct())
        {
            var inserted = Run("""
                INSERT OR IGNORE INTO playlist_items (playlist_id, track_id, position, added_utc) VALUES ($playlist, $track, $position, $now)
                """,
                ("$playlist", playlistId), ("$track", trackId), ("$position", next), ("$now", Utc(DateTime.UtcNow)));
            if (inserted == 0) continue;
            next++;
            added++;
        }
        return added;
    });

    public void RemoveTrackFromPlaylist(long playlistId, long trackId) =>
        Run("DELETE FROM playlist_items WHERE playlist_id = $playlist AND track_id = $track", ("$playlist", playlistId), ("$track", trackId));

    /// <summary>Setzt einen Titel an die Stelle <paramref name="newIndex"/> (0 = ganz oben); die übrigen rücken nach. Liegt der Index außerhalb, gilt der nächste gültige.</summary>
    /// <exception cref="InvalidOperationException">Der Titel steht nicht in der Playlist.</exception>
    public void MoveTrackInPlaylist(long playlistId, long trackId, int newIndex) => InTransaction(() =>
    {
        var order = new List<long>();
        using (var cmd = Command("SELECT track_id FROM playlist_items WHERE playlist_id = $id ORDER BY position", ("$id", playlistId)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read()) order.Add(r.GetInt64(0));
        }
        var from = order.IndexOf(trackId);
        if (from < 0) throw new InvalidOperationException($"Titel {trackId} steht nicht in Playlist {playlistId}.");

        order.RemoveAt(from);
        order.Insert(Math.Clamp(newIndex, 0, order.Count), trackId);
        for (var i = 0; i < order.Count; i++)
        {
            Run("UPDATE playlist_items SET position = $position WHERE playlist_id = $playlist AND track_id = $track",
                ("$position", i + 1), ("$playlist", playlistId), ("$track", order[i]));
        }
    });

    private string ValidatedPlaylistName(string name, long? exceptId)
    {
        name = name?.Trim() ?? "";
        if (name.Length == 0) throw new ArgumentException("Der Name der Playlist darf nicht leer sein.");
        if (name.Length > MaxPlaylistNameLength) throw new ArgumentException($"Der Name der Playlist darf höchstens {MaxPlaylistNameLength} Zeichen lang sein.");
        var taken = Scalar<long>(
            "SELECT COUNT(*) FROM playlists WHERE name = $name COLLATE NOCASE AND ($except IS NULL OR id <> $except)",
            ("$name", name), ("$except", exceptId));
        if (taken > 0) throw new ArgumentException($"Es gibt schon eine Playlist „{name}“.");
        return name;
    }

    private static InvalidOperationException MissingPlaylist(long playlistId) => new($"Die Playlist {playlistId} gibt es nicht (mehr).");
}
