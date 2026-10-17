namespace MediaCenter.Core;

// Nutzerdaten: Titel, Zuordnungen, Genres, Fortschritt. Ein Scan überschreibt davon nichts.
public sealed partial class LibraryDb
{
    // --- Titel ---------------------------------------------------------------------------------------------

    public long CreateTitle(TitleKind kind, string name, int? year = null) => InTransaction(() =>
        CreateTitleCore(kind, name, kind == TitleKind.Movie ? year : null, createdBy: "user", TitleKey(kind, name, year)));

    private long CreateTitleCore(TitleKind kind, string name, int? year, string createdBy, string key)
    {
        var id = Scalar<long>(
            "INSERT INTO titles (kind, name, year, created_by) VALUES ($kind, $name, $year, $by) RETURNING id",
            ("$kind", kind == TitleKind.Movie ? "movie" : "series"), ("$name", name), ("$year", year), ("$by", createdBy));
        Run("INSERT OR IGNORE INTO title_keys (match_key, title_id) VALUES ($key, $id)", ("$key", key), ("$id", id));
        return id;
    }

    /// <summary>Ändert nur den Anzeigenamen; die Schlüssel für spätere Scans bleiben erhalten.</summary>
    public void RenameTitle(long titleId, string name) =>
        Run("UPDATE titles SET name = $name WHERE id = $id", ("$name", name), ("$id", titleId));

    /// <summary>Weist ein Video einem Titel zu und sperrt es gegen automatische Änderungen.</summary>
    public void AssignVideo(long videoId, long titleId, int? season, int? episode) => Run(
        "UPDATE videos SET title_id = $title, season = $season, episode = $episode, locked = 1 WHERE id = $id",
        ("$title", titleId), ("$season", season), ("$episode", episode), ("$id", videoId));

    /// <summary>Führt zwei Titel gleicher Art zusammen: Videos, Namensschlüssel und Genres wandern zum Ziel.</summary>
    public void MergeTitles(long fromId, long intoId) => InTransaction(() =>
    {
        if (fromId == intoId) throw new ArgumentException("Ein Titel kann nicht mit sich selbst zusammengeführt werden.");

        var kinds = Scalar<int>(
            "SELECT COUNT(DISTINCT kind) FROM titles WHERE id IN ($a, $b)", ("$a", fromId), ("$b", intoId));
        var count = Scalar<int>("SELECT COUNT(*) FROM titles WHERE id IN ($a, $b)", ("$a", fromId), ("$b", intoId));
        if (count != 2) throw new InvalidOperationException("Mindestens ein Titel existiert nicht.");
        if (kinds != 1) throw new InvalidOperationException("Film und Serie können nicht zusammengeführt werden.");

        Run("UPDATE videos SET title_id = $into WHERE title_id = $from", ("$into", intoId), ("$from", fromId));
        Run("UPDATE title_keys SET title_id = $into WHERE title_id = $from", ("$into", intoId), ("$from", fromId));
        Run("INSERT OR IGNORE INTO title_genres (title_id, genre_id) SELECT $into, genre_id FROM title_genres WHERE title_id = $from",
            ("$into", intoId), ("$from", fromId));
        Run("DELETE FROM titles WHERE id = $from", ("$from", fromId));
    });

    // --- Genres --------------------------------------------------------------------------------------------

    public void AddGenre(long titleId, string genre) => InTransaction(() =>
    {
        Run("INSERT OR IGNORE INTO genres (name) VALUES ($name)", ("$name", genre));
        Run("INSERT OR IGNORE INTO title_genres (title_id, genre_id) SELECT $title, id FROM genres WHERE name = $name",
            ("$title", titleId), ("$name", genre));
    });

    /// <summary>Mehrfachauswahl: ein Genre auf viele Titel anwenden.</summary>
    public void AddGenreToTitles(string genre, IEnumerable<long> titleIds) => InTransaction(() =>
    {
        foreach (var id in titleIds) AddGenre(id, genre);
    });

    public void RemoveGenre(long titleId, string genre) => Run(
        "DELETE FROM title_genres WHERE title_id = $title AND genre_id = (SELECT id FROM genres WHERE name = $name)",
        ("$title", titleId), ("$name", genre));

    public List<string> GetGenres(long titleId)
    {
        var result = new List<string>();
        using var cmd = Command(
            "SELECT g.name FROM title_genres tg JOIN genres g ON g.id = tg.genre_id WHERE tg.title_id = $id ORDER BY g.name COLLATE NOCASE",
            ("$id", titleId));
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(r.GetString(0));
        return result;
    }

    /// <summary>Alle Genre-Zuordnungen auf einmal: Titel-Id → Genres (alphabetisch).</summary>
    public Dictionary<long, List<string>> GetAllTitleGenres()
    {
        var result = new Dictionary<long, List<string>>();
        using var cmd = Command(
            "SELECT tg.title_id, g.name FROM title_genres tg JOIN genres g ON g.id = tg.genre_id ORDER BY g.name COLLATE NOCASE");
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var id = r.GetInt64(0);
            if (!result.TryGetValue(id, out var genres)) result[id] = genres = new List<string>();
            genres.Add(r.GetString(1));
        }
        return result;
    }

    // --- Fortschritt ---------------------------------------------------------------------------------------

    public void SavePlayback(long videoId, double positionSeconds, bool watched) => Run("""
        INSERT INTO playback (video_id, position_sec, watched, updated_utc) VALUES ($id, $pos, $watched, $now)
        ON CONFLICT (video_id) DO UPDATE SET position_sec = excluded.position_sec, watched = excluded.watched, updated_utc = excluded.updated_utc
        """,
        ("$id", videoId), ("$pos", positionSeconds), ("$watched", watched ? 1 : 0), ("$now", Utc(DateTime.UtcNow)));

    public PlaybackState? GetPlayback(long videoId)
    {
        using var cmd = Command("SELECT position_sec, watched, updated_utc FROM playback WHERE video_id = $id", ("$id", videoId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new PlaybackState(r.GetDouble(0), r.GetInt32(1) == 1, r.GetString(2)) : null;
    }

    // --- Untertitel-Anpassung --------------------------------------------------------------------------------

    /// <summary>Gespeicherte Anpassung des Videos, sonst die Standardwerte.</summary>
    public SubtitleAdjustment GetSubtitleAdjustment(long videoId)
    {
        using var cmd = Command("SELECT delay_sec, scale, position FROM subtitle_settings WHERE video_id = $id", ("$id", videoId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new SubtitleAdjustment(r.GetDouble(0), r.GetDouble(1), r.GetInt32(2)) : SubtitleAdjustment.Default;
    }

    /// <summary>Speichert die Anpassung; die Standardwerte löschen die Zeile.</summary>
    public void SaveSubtitleAdjustment(long videoId, SubtitleAdjustment adjustment)
    {
        adjustment.Validated();
        if (adjustment.IsDefault)
        {
            Run("DELETE FROM subtitle_settings WHERE video_id = $id", ("$id", videoId));
            return;
        }
        Run("""
            INSERT INTO subtitle_settings (video_id, delay_sec, scale, position) VALUES ($id, $delay, $scale, $pos)
            ON CONFLICT (video_id) DO UPDATE SET delay_sec = excluded.delay_sec, scale = excluded.scale, position = excluded.position
            """,
            ("$id", videoId), ("$delay", adjustment.DelaySeconds), ("$scale", adjustment.Scale), ("$pos", adjustment.Position));
    }

    /// <summary>
    /// Angefangene, nicht fertig gesehene Videos von Titeln, die als Datei erreichbar sind (nicht als fehlend markiert).
    /// Je Titel nur das zuletzt gesehene, zuletzt gesehene Titel zuerst.
    /// </summary>
    public List<ContinueItem> GetContinueWatching()
    {
        var rows = new List<ContinueItem>();
        var seen = new HashSet<long>();
        using var cmd = Command("""
            SELECT v.title_id, v.id, p.position_sec, f.duration_sec, v.season, v.episode
            FROM playback p
            JOIN videos v ON v.id = p.video_id
            JOIN files f ON f.id = v.file_id
            WHERE p.watched = 0 AND p.position_sec > 0 AND v.title_id IS NOT NULL AND f.missing = 0
            ORDER BY p.updated_utc DESC
            """);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (!seen.Add(r.GetInt64(0))) continue;
            rows.Add(new ContinueItem(r.GetInt64(0), r.GetInt64(1), r.GetDouble(2), r.GetDoubleOrNull(3), r.GetInt32OrNull(4), r.GetInt32OrNull(5)));
        }
        return rows;
    }

    // --- Aufräumen (nur auf ausdrücklichen Wunsch) ---------------------------------------------------------

    /// <summary>
    /// Löscht als fehlend markierte Dateien samt ihrer Videos und Fortschrittsdaten sowie vom Scan angelegte
    /// Titel ohne Videos und ohne Genres. Titel mit Nutzerdaten bleiben erhalten.
    /// </summary>
    public int RemoveMissingFiles() => InTransaction(() =>
    {
        var removed = Run("DELETE FROM files WHERE missing = 1");
        Run("""
            DELETE FROM titles
            WHERE created_by = 'scan'
              AND id NOT IN (SELECT title_id FROM videos WHERE title_id IS NOT NULL)
              AND id NOT IN (SELECT title_id FROM title_genres)
            """);
        return removed;
    });
}
