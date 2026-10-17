using System.Text.RegularExpressions;

namespace MediaCenter.Core;

// Laufwerke, Quellen, Dateien und das Anwenden von Scan-Ergebnissen.
public sealed partial class LibraryDb
{
    // --- Laufwerke -----------------------------------------------------------------------------------------

    public IReadOnlyList<KnownVolume> GetVolumes()
    {
        var result = new List<KnownVolume>();
        using var cmd = Command("SELECT id, serial, label, file_system, capacity, name, last_root, last_seen_utc FROM volumes ORDER BY id");
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new KnownVolume(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4),
                r.GetString(5), r.GetStringOrNull(6), r.GetStringOrNull(7)));
        }
        return result;
    }

    public long AddVolume(VolumeInfo volume) => Scalar<long>("""
        INSERT INTO volumes (serial, label, file_system, capacity, name, last_root, last_seen_utc)
        VALUES ($serial, $label, $fs, $capacity, $name, $root, $seen)
        RETURNING id
        """,
        ("$serial", volume.Serial), ("$label", volume.Label), ("$fs", volume.FileSystem), ("$capacity", volume.Capacity),
        ("$name", volume.Label.Length > 0 ? volume.Label : volume.Serial),
        ("$root", volume.RootPath), ("$seen", Utc(DateTime.UtcNow)));

    /// <summary>Merkt sich Label, Einhängepunkt und Zeitpunkt, wenn das Laufwerk gesehen wurde.</summary>
    public void TouchVolume(long volumeId, VolumeInfo volume) => Run(
        "UPDATE volumes SET label = $label, last_root = $root, last_seen_utc = $seen WHERE id = $id",
        ("$label", volume.Label), ("$root", volume.RootPath), ("$seen", Utc(DateTime.UtcNow)), ("$id", volumeId));

    /// <summary>Ordnet ein neu formatiertes oder geklontes Laufwerk ausdrücklich einem bekannten Eintrag zu.</summary>
    public void RelinkVolume(long volumeId, VolumeInfo volume) => Run("""
        UPDATE volumes SET serial = $serial, file_system = $fs, capacity = $capacity,
                           label = $label, last_root = $root, last_seen_utc = $seen
        WHERE id = $id
        """,
        ("$serial", volume.Serial), ("$fs", volume.FileSystem), ("$capacity", volume.Capacity), ("$label", volume.Label),
        ("$root", volume.RootPath), ("$seen", Utc(DateTime.UtcNow)), ("$id", volumeId));

    // --- Quellen -------------------------------------------------------------------------------------------

    public long AddSource(long volumeId, SourceKind kind, string relPath) => InTransaction(() =>
    {
        Run("INSERT OR IGNORE INTO sources (volume_id, kind, rel_path, added_utc) VALUES ($vol, $kind, $rel, $added)",
            ("$vol", volumeId), ("$kind", kind == SourceKind.Folder ? "folder" : "file"), ("$rel", relPath), ("$added", Utc(DateTime.UtcNow)));
        return Scalar<long>("SELECT id FROM sources WHERE volume_id = $vol AND rel_path = $rel", ("$vol", volumeId), ("$rel", relPath));
    });

    public IReadOnlyList<Source> GetSources()
    {
        var result = new List<Source>();
        using var cmd = Command("SELECT id, volume_id, kind, rel_path FROM sources ORDER BY id");
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new Source(r.GetInt64(0), r.GetInt64(1), r.GetString(2) == "folder" ? SourceKind.Folder : SourceKind.File, r.GetString(3)));
        }
        return result;
    }

    // --- Dateien -------------------------------------------------------------------------------------------

    /// <summary>Bekannte Dateien eines Laufwerks unterhalb von relRoot (Schlüssel: relativer Pfad).</summary>
    public Dictionary<string, KnownFile> GetFilesUnder(long volumeId, string relRoot)
    {
        var result = new Dictionary<string, KnownFile>(StringComparer.OrdinalIgnoreCase);
        using var cmd = Command("SELECT id, rel_path, size, mtime_utc, missing FROM files WHERE volume_id = $vol", ("$vol", volumeId));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var rel = r.GetString(1);
            if (IsUnder(rel, relRoot)) result[rel] = new KnownFile(r.GetInt64(0), r.GetInt64(2), r.GetString(3), r.GetInt32(4) == 1);
        }
        return result;
    }

    public static bool IsUnder(string relPath, string relRoot) =>
        relRoot.Length == 0
        || relPath.Equals(relRoot, StringComparison.OrdinalIgnoreCase)
        || relPath.StartsWith(relRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Legt die Datei an oder aktualisiert sie an Ort und Stelle (die ID bleibt, damit Nutzerdaten hängen bleiben).</summary>
    public long UpsertFile(long volumeId, string relPath, long size, DateTime mtimeUtc, ProbeResult probe) => InTransaction(() =>
    {
        var video = probe.Video;
        var fileId = Scalar<long>("""
            INSERT INTO files (volume_id, rel_path, size, mtime_utc, missing, duration_sec, container, width, height, video_codec, is_hdr)
            VALUES ($vol, $rel, $size, $mtime, 0, $duration, $container, $width, $height, $codec, $hdr)
            ON CONFLICT (volume_id, rel_path) DO UPDATE SET
                size = excluded.size, mtime_utc = excluded.mtime_utc, missing = 0,
                duration_sec = excluded.duration_sec, container = excluded.container,
                width = excluded.width, height = excluded.height, video_codec = excluded.video_codec, is_hdr = excluded.is_hdr
            RETURNING id
            """,
            ("$vol", volumeId), ("$rel", relPath), ("$size", size), ("$mtime", Utc(mtimeUtc)),
            ("$duration", probe.DurationSeconds), ("$container", probe.Container),
            ("$width", video?.Width), ("$height", video?.Height), ("$codec", video?.Codec), ("$hdr", probe.IsHdr ? 1 : 0));

        Run("DELETE FROM streams WHERE file_id = $file", ("$file", fileId));
        foreach (var s in probe.Streams)
        {
            Run("""
                INSERT INTO streams (file_id, idx, type, codec, profile, channels, language, title, is_default, is_forced)
                VALUES ($file, $idx, $type, $codec, $profile, $channels, $lang, $title, $default, $forced)
                """,
                ("$file", fileId), ("$idx", s.Index), ("$type", s.Type), ("$codec", s.Codec), ("$profile", s.Profile),
                ("$channels", s.Channels), ("$lang", s.Language), ("$title", s.Title),
                ("$default", s.IsDefault ? 1 : 0), ("$forced", s.IsForced ? 1 : 0));
        }
        return fileId;
    });

    public void SetMissing(long fileId, bool missing) =>
        Run("UPDATE files SET missing = $m WHERE id = $id", ("$m", missing ? 1 : 0), ("$id", fileId));

    // --- Scan-Ergebnis anwenden ----------------------------------------------------------------------------

    /// <summary>
    /// Speichert das Parser-Ergebnis. Die wirksame Zuordnung (Titel, Staffel, Episode) wird nur verändert,
    /// solange das Video nicht vom Nutzer gesperrt wurde.
    /// </summary>
    public void ApplyScanned(long fileId, ParsedName parsed) => InTransaction(() =>
    {
        long? titleId = null;
        int? season = null, episode = null;
        if (parsed.Certain)
        {
            titleId = GetOrCreateScannedTitle(parsed);
            season = parsed.Season;
            episode = parsed.Episode;
        }

        var parsedArgs = new (string, object?)[]
        {
            ("$file", fileId), ("$pkind", parsed.Kind == VideoKind.Movie ? "movie" : "episode"), ("$ptitle", parsed.Title),
            ("$pyear", parsed.Year), ("$pseason", parsed.Season), ("$pepisode", parsed.Episode), ("$pcertain", parsed.Certain ? 1 : 0),
            ("$title", titleId), ("$season", season), ("$episode", episode),
        };

        var existingLock = Scalar<int?>("SELECT locked FROM videos WHERE file_id = $file", ("$file", fileId));
        if (existingLock is null)
        {
            Run("""
                INSERT INTO videos (file_id, parsed_kind, parsed_title, parsed_year, parsed_season, parsed_episode, parsed_certain,
                                    title_id, season, episode, locked)
                VALUES ($file, $pkind, $ptitle, $pyear, $pseason, $pepisode, $pcertain, $title, $season, $episode, 0)
                """, parsedArgs);
        }
        else if (existingLock == 0)
        {
            Run("""
                UPDATE videos SET parsed_kind = $pkind, parsed_title = $ptitle, parsed_year = $pyear, parsed_season = $pseason,
                                  parsed_episode = $pepisode, parsed_certain = $pcertain,
                                  title_id = $title, season = $season, episode = $episode
                WHERE file_id = $file
                """, parsedArgs);
        }
        else
        {
            Run("""
                UPDATE videos SET parsed_kind = $pkind, parsed_title = $ptitle, parsed_year = $pyear, parsed_season = $pseason,
                                  parsed_episode = $pepisode, parsed_certain = $pcertain
                WHERE file_id = $file
                """, parsedArgs);
        }
    });

    private long GetOrCreateScannedTitle(ParsedName parsed)
    {
        var kind = parsed.Kind == VideoKind.Movie ? TitleKind.Movie : TitleKind.Series;
        var key = TitleKey(kind, parsed.Title, parsed.Year);

        var existing = Scalar<long?>("SELECT title_id FROM title_keys WHERE match_key = $key", ("$key", key));
        if (existing is { } id) return id;

        return CreateTitleCore(kind, parsed.Title, kind == TitleKind.Movie ? parsed.Year : null, createdBy: "scan", key);
    }

    /// <summary>Normalisierter Schlüssel: "Mr. Robot" und "mr robot" führen zum selben Titel.</summary>
    internal static string TitleKey(TitleKind kind, string name, int? year)
    {
        var normalized = Regex.Replace(name.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
        return kind == TitleKind.Series ? $"series|{normalized}" : $"movie|{normalized}|{year}";
    }

    // --- Abfragen ------------------------------------------------------------------------------------------

    public List<VideoRow> ListVideos()
    {
        var rows = new List<VideoRow>();
        using var cmd = Command("""
            SELECT v.id, f.id, f.volume_id, f.rel_path, f.missing,
                   v.title_id, t.kind, t.name, t.year, v.season, v.episode, v.locked,
                   v.parsed_title, v.parsed_kind,
                   f.duration_sec, f.width, f.height, f.video_codec, f.is_hdr
            FROM videos v
            JOIN files f ON f.id = v.file_id
            LEFT JOIN titles t ON t.id = v.title_id
            ORDER BY COALESCE(t.name, v.parsed_title) COLLATE NOCASE, v.season, v.episode, f.rel_path
            """);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new VideoRow(
                r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetInt32(4) == 1,
                r.GetInt64OrNull(5),
                r.GetStringOrNull(6) is { } kind ? (kind == "movie" ? TitleKind.Movie : TitleKind.Series) : null,
                r.GetStringOrNull(7), r.GetInt32OrNull(8), r.GetInt32OrNull(9), r.GetInt32OrNull(10), r.GetInt32(11) == 1,
                r.GetString(12), r.GetString(13) == "movie" ? VideoKind.Movie : VideoKind.Episode,
                r.GetDoubleOrNull(14), r.GetInt32OrNull(15), r.GetInt32OrNull(16), r.GetStringOrNull(17), r.GetInt32(18) == 1));
        }
        return rows;
    }
}
