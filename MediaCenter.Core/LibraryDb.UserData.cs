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

    /// <summary>Führt zwei Titel gleicher Art zusammen: Videos, Namensschlüssel, Genres, der Platz auf „Meine Liste“ und die Sammlungen wandern zum Ziel.</summary>
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
        // Die Einträge aller Profile wandern mit.
        Run("INSERT OR IGNORE INTO watchlist (profile_id, title_id, added_utc) SELECT profile_id, $into, added_utc FROM watchlist WHERE title_id = $from",
            ("$into", intoId), ("$from", fromId));
        Run("INSERT OR IGNORE INTO kids_titles (title_id) SELECT $into FROM kids_titles WHERE title_id = $from", ("$into", intoId), ("$from", fromId));
        Run("INSERT OR IGNORE INTO collection_titles (collection_id, title_id, added_utc) SELECT collection_id, $into, added_utc FROM collection_titles WHERE title_id = $from",
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
        INSERT INTO playback (profile_id, video_id, position_sec, watched, updated_utc) VALUES ($profile, $id, $pos, $watched, $now)
        ON CONFLICT (profile_id, video_id) DO UPDATE SET position_sec = excluded.position_sec, watched = excluded.watched, updated_utc = excluded.updated_utc
        """,
        ("$profile", ActiveProfileId), ("$id", videoId), ("$pos", positionSeconds), ("$watched", watched ? 1 : 0), ("$now", Utc(DateTime.UtcNow)));

    public PlaybackState? GetPlayback(long videoId)
    {
        using var cmd = Command("SELECT position_sec, watched, updated_utc FROM playback WHERE video_id = $id AND profile_id = $profile", ("$id", videoId), ("$profile", ActiveProfileId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new PlaybackState(r.GetDouble(0), r.GetInt32(1) == 1, r.GetString(2)) : null;
    }

    /// <summary>Der Fortschritt aller Videos auf einmal: Video-Id → Stand.</summary>
    public Dictionary<long, PlaybackState> GetAllPlayback()
    {
        var result = new Dictionary<long, PlaybackState>();
        using var cmd = Command("SELECT video_id, position_sec, watched, updated_utc FROM playback WHERE profile_id = $profile", ("$profile", ActiveProfileId));
        using var r = cmd.ExecuteReader();
        while (r.Read()) result[r.GetInt64(0)] = new PlaybackState(r.GetDouble(1), r.GetInt32(2) == 1, r.GetString(3));
        return result;
    }

    /// <summary>
    /// Markiert Videos als gesehen oder ungesehen. Beides setzt die gemerkte Stelle auf null zurück: ein gesehenes Video beginnt beim
    /// nächsten Mal von vorn, ein ungesehenes taucht nicht mehr unter „Weiterschauen“ auf.
    /// </summary>
    /// <exception cref="InvalidOperationException">Ein Video ist nicht in der Bibliothek; dann ändert sich nichts.</exception>
    public void SetVideosWatched(IEnumerable<long> videoIds, bool watched) => InTransaction(() =>
    {
        foreach (var id in videoIds.Distinct())
        {
            if (Scalar<long>("SELECT COUNT(*) FROM videos WHERE id = $id", ("$id", id)) == 0) throw new InvalidOperationException($"Video {id} ist nicht in der Bibliothek.");
            SavePlayback(id, 0, watched);
        }
    });

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

    // --- Ton-Zeitversatz ---------------------------------------------------------------------------------------

    /// <summary>Gespeicherter Ton-Zeitversatz des Videos in Sekunden (positiv = Ton später), sonst 0.</summary>
    public double GetAudioDelay(long videoId)
    {
        using var cmd = Command("SELECT delay_sec FROM audio_settings WHERE video_id = $id", ("$id", videoId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? AudioSync.Validated(r.GetDouble(0)) : 0;
    }

    /// <summary>Speichert den Ton-Zeitversatz; 0 löscht die Zeile.</summary>
    public void SaveAudioDelay(long videoId, double seconds)
    {
        AudioSync.Validated(seconds);
        if (seconds == 0)
        {
            Run("DELETE FROM audio_settings WHERE video_id = $id", ("$id", videoId));
            return;
        }
        Run("""
            INSERT INTO audio_settings (video_id, delay_sec) VALUES ($id, $delay)
            ON CONFLICT (video_id) DO UPDATE SET delay_sec = excluded.delay_sec
            """,
            ("$id", videoId), ("$delay", seconds));
    }

    // --- Farbeinstellungen -----------------------------------------------------------------------------------

    /// <summary>Gespeicherte Farbeinstellungen des Videos, sonst die Standardwerte.</summary>
    public ColorSettings GetColorSettings(long videoId)
    {
        using var cmd = Command(
            "SELECT brightness, contrast, saturation, hue, gamma, temperature, tint, sharpness, invert, lut, lut_strength FROM color_settings WHERE video_id = $id",
            ("$id", videoId));
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new ColorSettings(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), r.GetInt32(8) == 1,
                r.IsDBNull(9) ? null : r.GetString(9), r.GetInt32(10)).Validated()
            : ColorSettings.Default;
    }

    /// <summary>Speichert die Farbeinstellungen; die Standardwerte löschen die Zeile.</summary>
    public void SaveColorSettings(long videoId, ColorSettings color)
    {
        color.Validated();
        if (color.IsDefault)
        {
            Run("DELETE FROM color_settings WHERE video_id = $id", ("$id", videoId));
            return;
        }
        Run("""
            INSERT INTO color_settings (video_id, brightness, contrast, saturation, hue, gamma, temperature, tint, sharpness, invert, lut, lut_strength)
            VALUES ($id, $brightness, $contrast, $saturation, $hue, $gamma, $temperature, $tint, $sharpness, $invert, $lut, $lutStrength)
            ON CONFLICT (video_id) DO UPDATE SET brightness = excluded.brightness, contrast = excluded.contrast, saturation = excluded.saturation,
                hue = excluded.hue, gamma = excluded.gamma, temperature = excluded.temperature, tint = excluded.tint,
                sharpness = excluded.sharpness, invert = excluded.invert, lut = excluded.lut, lut_strength = excluded.lut_strength
            """,
            ("$id", videoId), ("$brightness", color.Brightness), ("$contrast", color.Contrast), ("$saturation", color.Saturation),
            ("$hue", color.Hue), ("$gamma", color.Gamma), ("$temperature", color.Temperature), ("$tint", color.Tint),
            ("$sharpness", color.Sharpness), ("$invert", color.Invert ? 1 : 0), ("$lut", color.Lut), ("$lutStrength", color.LutStrength));
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
            WHERE p.profile_id = $profile AND p.watched = 0 AND p.position_sec > 0 AND v.title_id IS NOT NULL AND f.missing = 0
            ORDER BY p.updated_utc DESC
            """, ("$profile", ActiveProfileId));
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

    // --- Aus der Bibliothek entfernen (die Dateien auf der Platte bleiben) --------------------------------------

    /// <summary>
    /// Nimmt Videos aus der Bibliothek, löscht aber nie eine Datei auf der Platte: der Eintrag samt Fortschritt und Einstellungen verschwindet,
    /// der Pfad kommt auf die Liste der entfernten Dateien, damit ihn kein Scan wieder aufnimmt. Titel, die dadurch ohne Video bleiben, werden mitgelöscht
    /// (samt Genres und Schlüsseln). Bilder im Cover-Cache räumt <see cref="ArtworkService.PruneOrphans"/> danach weg.
    /// </summary>
    /// <returns>Anzahl der entfernten Videos.</returns>
    /// <exception cref="InvalidOperationException">Ein Video ist nicht in der Bibliothek; dann wird nichts entfernt.</exception>
    public int RemoveVideos(IEnumerable<long> videoIds) => InTransaction(() =>
    {
        var removed = 0;
        var titles = new HashSet<long>();
        foreach (var videoId in videoIds.Distinct())
        {
            long fileId, volumeId;
            string relPath;
            long? titleId;
            using (var cmd = Command("SELECT f.id, f.volume_id, f.rel_path, v.title_id FROM videos v JOIN files f ON f.id = v.file_id WHERE v.id = $id", ("$id", videoId)))
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read()) throw new InvalidOperationException($"Video {videoId} ist nicht in der Bibliothek.");
                fileId = r.GetInt64(0);
                volumeId = r.GetInt64(1);
                relPath = r.GetString(2);
                titleId = r.GetInt64OrNull(3);
            }

            Run("INSERT OR IGNORE INTO removed_files (volume_id, rel_path, removed_utc) VALUES ($vol, $rel, $utc)",
                ("$vol", volumeId), ("$rel", relPath), ("$utc", Utc(DateTime.UtcNow)));
            // Eine einzeln hinzugefügte Datei ist selbst die Quelle.
            Run("DELETE FROM sources WHERE volume_id = $vol AND kind = 'file' AND rel_path = $rel COLLATE NOCASE", ("$vol", volumeId), ("$rel", relPath));
            Run("DELETE FROM files WHERE id = $id", ("$id", fileId)); // Video, Streams, Fortschritt, Untertitel- und Farbeinstellungen folgen
            if (titleId is { } title) titles.Add(title);
            removed++;
        }

        foreach (var title in titles)
        {
            Run("DELETE FROM titles WHERE id = $t AND id NOT IN (SELECT title_id FROM videos WHERE title_id IS NOT NULL)", ("$t", title));
        }
        return removed;
    });

    /// <summary>Entfernt einen Film oder eine Serie mit allen Videos aus der Bibliothek (siehe <see cref="RemoveVideos"/>); auch ein Titel ohne Video verschwindet.</summary>
    /// <returns>Anzahl der entfernten Videos.</returns>
    public int RemoveTitle(long titleId) => InTransaction(() =>
    {
        var videoIds = new List<long>();
        using (var cmd = Command("SELECT id FROM videos WHERE title_id = $t", ("$t", titleId)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read()) videoIds.Add(r.GetInt64(0));
        }
        var removed = RemoveVideos(videoIds);
        Run("DELETE FROM titles WHERE id = $t", ("$t", titleId));
        return removed;
    });

    /// <summary>Relative Pfade der entfernten Dateien eines Laufwerks; der Scan überspringt sie.</summary>
    public HashSet<string> GetRemovedFiles(long volumeId)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = Command("SELECT rel_path FROM removed_files WHERE volume_id = $vol", ("$vol", volumeId));
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(r.GetString(0));
        return result;
    }

    public int CountRemovedFiles() => Scalar<int>("SELECT COUNT(*) FROM removed_files");

    /// <summary>Nimmt eine Datei von der Liste der entfernten, damit der nächste Scan sie wieder aufnimmt (z. B. wenn der Nutzer sie ausdrücklich hinzufügt).</summary>
    public void UnremoveFile(long volumeId, string relPath) =>
        Run("DELETE FROM removed_files WHERE volume_id = $vol AND rel_path = $rel", ("$vol", volumeId), ("$rel", relPath));

    /// <summary>Leert die Liste der entfernten Dateien: der nächste Scan nimmt sie wieder auf.</summary>
    /// <returns>Anzahl der Dateien, die wieder aufgenommen werden können.</returns>
    public int ClearRemovedFiles() => Run("DELETE FROM removed_files");
}
