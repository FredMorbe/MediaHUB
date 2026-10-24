namespace MediaCenter.Core;

// Radio-Favoriten, Podcast-Abos, Folgen und Hörfortschritt: alles Nutzerdaten, kein Scan überschreibt sie.
public sealed partial class LibraryDb
{
    // --- Radio ---------------------------------------------------------------------------------------------

    /// <summary>Speichert einen Sender als Favorit; derselbe Sender (gleiche Kennung oder URL) wird nur aktualisiert.</summary>
    public long SaveStation(RadioStation station) => InTransaction(() =>
    {
        var existing = station.Uuid is null
            ? Scalar<long?>("SELECT id FROM radio_stations WHERE url = $url", ("$url", station.Url))
            : Scalar<long?>("SELECT id FROM radio_stations WHERE station_uuid = $uuid OR url = $url", ("$uuid", station.Uuid), ("$url", station.Url));
        if (existing is { } id)
        {
            Run("""
                UPDATE radio_stations SET name = $name, url = $url, favicon_url = $icon, country = $country, tags = $tags, codec = $codec, bitrate = $bitrate
                WHERE id = $id
                """,
                ("$name", station.Name), ("$url", station.Url), ("$icon", station.FaviconUrl), ("$country", station.Country),
                ("$tags", station.Tags), ("$codec", station.Codec), ("$bitrate", station.Bitrate), ("$id", id));
            return id;
        }
        return Scalar<long>("""
            INSERT INTO radio_stations (station_uuid, name, url, favicon_url, country, tags, codec, bitrate, added_utc)
            VALUES ($uuid, $name, $url, $icon, $country, $tags, $codec, $bitrate, $now) RETURNING id
            """,
            ("$uuid", station.Uuid), ("$name", station.Name), ("$url", station.Url), ("$icon", station.FaviconUrl),
            ("$country", station.Country), ("$tags", station.Tags), ("$codec", station.Codec), ("$bitrate", station.Bitrate),
            ("$now", Utc(DateTime.UtcNow)));
    });

    public void RemoveStation(long id) => Run("DELETE FROM radio_stations WHERE id = $id", ("$id", id));

    public void SetStationFavicon(long id, string file) =>
        Run("UPDATE radio_stations SET favicon_file = $file WHERE id = $id", ("$file", file), ("$id", id));

    public List<SavedStation> ListStations()
    {
        var rows = new List<SavedStation>();
        using var cmd = Command("""
            SELECT id, station_uuid, name, url, favicon_url, favicon_file, country, tags, codec, bitrate
            FROM radio_stations ORDER BY name COLLATE NOCASE
            """);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new SavedStation(
                r.GetInt64(0),
                new RadioStation(r.GetStringOrNull(1), r.GetString(2), r.GetString(3), r.GetStringOrNull(4), r.GetStringOrNull(6),
                    r.GetStringOrNull(7), r.GetStringOrNull(8), r.GetInt32OrNull(9)),
                r.GetStringOrNull(5)));
        }
        return rows;
    }

    // --- Podcasts ------------------------------------------------------------------------------------------

    /// <summary>Legt ein Abo an oder aktualisiert Titel, Beschreibung und Bild des bestehenden.</summary>
    public long SavePodcast(string feedUrl, PodcastFeed feed) => InTransaction(() =>
    {
        var id = Scalar<long>("""
            INSERT INTO podcasts (feed_url, title, author, description, image_url, last_refreshed_utc, added_utc)
            VALUES ($url, $title, $author, $desc, $image, $now, $now)
            ON CONFLICT (feed_url) DO UPDATE SET
                title = excluded.title, author = excluded.author, description = excluded.description,
                image_url = excluded.image_url, last_refreshed_utc = excluded.last_refreshed_utc
            RETURNING id
            """,
            ("$url", feedUrl), ("$title", feed.Title), ("$author", feed.Author), ("$desc", feed.Description),
            ("$image", feed.ImageUrl), ("$now", Utc(DateTime.UtcNow)));
        UpsertEpisodes(id, feed.Episodes);
        return id;
    });

    /// <summary>Neue Folgen kommen dazu, bekannte werden aktualisiert (Titel, URL); Fortschritt hängt an der Folge und bleibt.</summary>
    /// <returns>Anzahl neu angelegter Folgen.</returns>
    public int UpsertEpisodes(long podcastId, IEnumerable<FeedEpisode> episodes) => InTransaction(() =>
    {
        var added = 0;
        foreach (var e in episodes)
        {
            var known = Scalar<long?>("SELECT id FROM podcast_episodes WHERE podcast_id = $p AND guid = $guid", ("$p", podcastId), ("$guid", e.Guid));
            if (known is null) added++;
            Run("""
                INSERT INTO podcast_episodes (podcast_id, guid, title, published_utc, audio_url, duration_sec, description)
                VALUES ($p, $guid, $title, $published, $url, $duration, $desc)
                ON CONFLICT (podcast_id, guid) DO UPDATE SET
                    title = excluded.title, published_utc = excluded.published_utc, audio_url = excluded.audio_url,
                    duration_sec = excluded.duration_sec, description = excluded.description
                """,
                ("$p", podcastId), ("$guid", e.Guid), ("$title", e.Title), ("$published", e.PublishedUtc is { } d ? Utc(d) : null),
                ("$url", e.AudioUrl), ("$duration", e.DurationSeconds), ("$desc", e.Description));
        }
        return added;
    });

    public void SetPodcastImage(long id, string file) => Run("UPDATE podcasts SET image_file = $file WHERE id = $id", ("$file", file), ("$id", id));

    /// <summary>Löscht das Abo samt Folgen und Fortschritt.</summary>
    public void DeletePodcast(long id) => Run("DELETE FROM podcasts WHERE id = $id", ("$id", id));

    public List<PodcastInfo> ListPodcasts()
    {
        var rows = new List<PodcastInfo>();
        using var cmd = Command("""
            SELECT id, feed_url, title, author, description, image_url, image_file, last_refreshed_utc
            FROM podcasts ORDER BY title COLLATE NOCASE
            """);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new PodcastInfo(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetStringOrNull(3), r.GetStringOrNull(4),
                r.GetStringOrNull(5), r.GetStringOrNull(6), r.GetStringOrNull(7)));
        }
        return rows;
    }

    /// <summary>Folgen eines Podcasts, neueste zuerst; Folgen ohne Datum stehen hinten.</summary>
    public List<EpisodeInfo> ListEpisodes(long podcastId)
    {
        var rows = new List<EpisodeInfo>();
        using var cmd = Command("""
            SELECT id, podcast_id, guid, title, published_utc, audio_url, duration_sec, description
            FROM podcast_episodes WHERE podcast_id = $p
            ORDER BY published_utc IS NULL, published_utc DESC, id DESC
            """, ("$p", podcastId));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new EpisodeInfo(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetStringOrNull(4),
                r.GetString(5), r.GetDoubleOrNull(6), r.GetStringOrNull(7)));
        }
        return rows;
    }

    public void SaveEpisodeProgress(long episodeId, double positionSeconds, bool played) => Run("""
        INSERT INTO podcast_progress (profile_id, episode_id, position_sec, played, updated_utc) VALUES ($profile, $id, $pos, $played, $now)
        ON CONFLICT (profile_id, episode_id) DO UPDATE SET position_sec = excluded.position_sec, played = excluded.played, updated_utc = excluded.updated_utc
        """,
        ("$profile", ActiveProfileId), ("$id", episodeId), ("$pos", positionSeconds), ("$played", played ? 1 : 0), ("$now", Utc(DateTime.UtcNow)));

    public Dictionary<long, EpisodeProgress> GetEpisodeProgress(long podcastId)
    {
        var map = new Dictionary<long, EpisodeProgress>();
        using var cmd = Command("""
            SELECT p.episode_id, p.position_sec, p.played
            FROM podcast_progress p JOIN podcast_episodes e ON e.id = p.episode_id WHERE e.podcast_id = $p AND p.profile_id = $profile
            """, ("$p", podcastId), ("$profile", ActiveProfileId));
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetInt64(0)] = new EpisodeProgress(r.GetDouble(1), r.GetInt32(2) == 1);
        return map;
    }
}
