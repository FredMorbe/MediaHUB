using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MediaCenter.Core;

// Live-TV: Senderlisten (M3U), ihre Sender und der Programmführer (XMLTV). Die Sender und der Führer sind abgeleitet und lassen sich jederzeit neu laden;
// was der Nutzer selbst gesetzt hat (Favoriten, Name der Liste, Adresse des Führers), bleibt beim Neuladen.
public sealed partial class LibraryDb
{
    private const string LiveChannelColumns = "id, source_id, name, url, group_name, tvg_id, epg_id, logo_url, number, favorite";

    private static LiveChannel ReadLiveChannel(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetStringOrNull(4), r.GetStringOrNull(5), r.GetStringOrNull(6),
            r.GetStringOrNull(7), r.GetInt32OrNull(8), r.GetInt32(9) == 1);

    private static DateTime? ParseUtc(string? text) =>
        text is null ? null : DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    // --- Senderlisten --------------------------------------------------------------------------------------

    /// <exception cref="InvalidOperationException">Diese Liste (dieselbe Adresse) gibt es schon.</exception>
    public long AddLiveSource(string name, string location, string? epgLocation)
    {
        if (Scalar<long?>("SELECT id FROM live_sources WHERE location = $loc", ("$loc", location)) is not null)
        {
            throw new InvalidOperationException("Diese Senderliste ist schon eingetragen.");
        }
        return Scalar<long>("""
            INSERT INTO live_sources (name, location, epg_location, created_utc) VALUES ($name, $loc, $epg, $now) RETURNING id
            """,
            ("$name", name), ("$loc", location), ("$epg", epgLocation), ("$now", Utc(DateTime.UtcNow)));
    }

    public List<LiveSource> ListLiveSources()
    {
        var rows = new List<LiveSource>();
        using var cmd = Command("""
            SELECT s.id, s.name, s.location, s.epg_location, s.refreshed_utc, s.guide_refreshed_utc,
                   (SELECT COUNT(*) FROM live_channels c WHERE c.source_id = s.id)
            FROM live_sources s ORDER BY s.name COLLATE NOCASE, s.id
            """);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new LiveSource(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetStringOrNull(3), ParseUtc(r.GetStringOrNull(4)),
                ParseUtc(r.GetStringOrNull(5)), r.GetInt32(6)));
        }
        return rows;
    }

    public LiveSource? GetLiveSource(long id) => ListLiveSources().FirstOrDefault(s => s.Id == id);

    /// <summary>Löscht die Liste samt ihren Sendern (und deren Favoriten) und ihrem Programmführer.</summary>
    public void RemoveLiveSource(long id) => Run("DELETE FROM live_sources WHERE id = $id", ("$id", id));

    public void RenameLiveSource(long id, string name) => Run("UPDATE live_sources SET name = $name WHERE id = $id", ("$name", name), ("$id", id));

    public void SetLiveGuideLocation(long id, string? epgLocation) =>
        Run("UPDATE live_sources SET epg_location = $epg WHERE id = $id", ("$epg", epgLocation), ("$id", id));

    // --- Sender --------------------------------------------------------------------------------------------

    /// <summary>
    /// Ersetzt die Sender der Liste durch den neuen Stand. Ein Sender, den es schon gab (gleicher Name und gleiche Adresse, sonst nur gleiche Adresse),
    /// behält Kennung und Favorit; neue kommen dazu, verschwundene gehen. Doppelte Einträge der Liste zählen einmal.
    /// </summary>
    public LiveRefresh ReplaceLiveChannels(long sourceId, IReadOnlyList<M3uChannel> channels, int skipped = 0) => InTransaction(() =>
    {
        var existing = new List<(long Id, string Name, string Url)>();
        using (var cmd = Command("SELECT id, name, url FROM live_channels WHERE source_id = $s", ("$s", sourceId)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read()) existing.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)));
        }

        var byKey = new Dictionary<(string, string), long>();
        foreach (var e in existing) byKey.TryAdd((e.Name, e.Url), e.Id);
        var byUrl = existing.GroupBy(e => e.Url).ToDictionary(g => g.Key, g => g.Select(e => e.Id).ToList());

        var seen = new HashSet<long>();
        var listed = new HashSet<(string, string)>();
        var added = 0;
        var position = 0;
        foreach (var channel in channels)
        {
            if (!listed.Add((channel.Name, channel.Url))) continue; // derselbe Eintrag zweimal

            long? id = null;
            if (byKey.TryGetValue((channel.Name, channel.Url), out var exact))
            {
                id = exact;
            }
            else if (byUrl.TryGetValue(channel.Url, out var sameUrl))
            {
                // Umbenannter Sender: die Adresse erkennt ihn wieder, solange es unter ihr noch einen unbenutzten Sender gibt.
                id = sameUrl.Where(c => !seen.Contains(c)).Select(c => (long?)c).FirstOrDefault();
            }

            if (id is { } updateId)
            {
                Run("""
                    UPDATE live_channels SET name = $name, name_key = $key, group_name = $group, tvg_id = $tvg, logo_url = $logo, number = $number, position = $pos
                    WHERE id = $id
                    """,
                    ("$name", channel.Name), ("$key", PosterNames.Normalize(channel.Name)), ("$group", channel.Group), ("$tvg", channel.TvgId),
                    ("$logo", channel.LogoUrl), ("$number", channel.Number), ("$pos", position), ("$id", updateId));
                seen.Add(updateId);
            }
            else
            {
                var newId = Scalar<long>("""
                    INSERT INTO live_channels (source_id, name, name_key, url, group_name, tvg_id, logo_url, number, position)
                    VALUES ($s, $name, $key, $url, $group, $tvg, $logo, $number, $pos) RETURNING id
                    """,
                    ("$s", sourceId), ("$name", channel.Name), ("$key", PosterNames.Normalize(channel.Name)), ("$url", channel.Url), ("$group", channel.Group),
                    ("$tvg", channel.TvgId), ("$logo", channel.LogoUrl), ("$number", channel.Number), ("$pos", position));
                seen.Add(newId);
                added++;
            }
            position++;
        }

        var removed = 0;
        foreach (var e in existing.Where(e => !seen.Contains(e.Id)))
        {
            Run("DELETE FROM live_channels WHERE id = $id", ("$id", e.Id));
            removed++;
        }
        Run("UPDATE live_sources SET refreshed_utc = $now WHERE id = $id", ("$now", Utc(DateTime.UtcNow)), ("$id", sourceId));
        return new LiveRefresh(seen.Count, added, removed, skipped);
    });

    private static (string Where, (string, object?)[] Parameters) LiveWhere(LiveChannelFilter filter)
    {
        var where = new List<string>();
        var parameters = new List<(string, object?)>();
        if (filter.SourceId is { } source)
        {
            where.Add("source_id = $source");
            parameters.Add(("$source", source));
        }
        if (filter.Group is { } group)
        {
            if (group.Length == 0)
            {
                where.Add("group_name IS NULL");
            }
            else
            {
                where.Add("group_name = $group COLLATE NOCASE");
                parameters.Add(("$group", group));
            }
        }
        if (filter.FavoritesOnly) where.Add("favorite = 1");
        if (filter.Text is { } text && PosterNames.Normalize(text) is { Length: > 0 } key)
        {
            where.Add("name_key LIKE $key ESCAPE '\\'");
            parameters.Add(("$key", "%" + key.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"));
        }
        return (where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where), parameters.ToArray());
    }

    /// <summary>Die Sender in der Reihenfolge der Listen; <paramref name="limit"/> und <paramref name="offset"/> blättern.</summary>
    public List<LiveChannel> ListLiveChannels(LiveChannelFilter filter, int limit = int.MaxValue, int offset = 0)
    {
        var (where, parameters) = LiveWhere(filter);
        var rows = new List<LiveChannel>();
        using var cmd = Command($"SELECT {LiveChannelColumns} FROM live_channels{where} ORDER BY source_id, position LIMIT $limit OFFSET $offset",
            parameters.Concat(new (string, object?)[] { ("$limit", limit), ("$offset", offset) }).ToArray());
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(ReadLiveChannel(r));
        return rows;
    }

    public int CountLiveChannels(LiveChannelFilter filter)
    {
        var (where, parameters) = LiveWhere(filter);
        return Scalar<int>($"SELECT COUNT(*) FROM live_channels{where}", parameters);
    }

    public LiveChannel? GetLiveChannel(long id)
    {
        using var cmd = Command($"SELECT {LiveChannelColumns} FROM live_channels WHERE id = $id", ("$id", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadLiveChannel(r) : null;
    }

    /// <summary>Die Gruppen mit Anzahl, in der Reihenfolge ihres ersten Senders; Sender ohne Gruppe stehen unter dem leeren Namen.</summary>
    public List<LiveGroup> ListLiveGroups(long? sourceId = null)
    {
        var rows = new List<LiveGroup>();
        using var cmd = Command("""
            SELECT COALESCE(group_name, ''), COUNT(*) FROM live_channels WHERE ($source IS NULL OR source_id = $source)
            GROUP BY COALESCE(group_name, '') COLLATE NOCASE ORDER BY MIN(source_id), MIN(position)
            """, ("$source", sourceId));
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(new LiveGroup(r.GetString(0), r.GetInt32(1)));
        return rows;
    }

    public void SetLiveFavorite(long channelId, bool favorite) =>
        Run("UPDATE live_channels SET favorite = $f WHERE id = $id", ("$f", favorite ? 1 : 0), ("$id", channelId));

    // --- Programmführer ------------------------------------------------------------------------------------

    /// <summary>Merkt, welche Kennung im Programmführer zu welchem Sender gehört (null löscht die Zuordnung).</summary>
    public void SetLiveEpgIds(long sourceId, IReadOnlyDictionary<long, string?> epgIds) => InTransaction(() =>
    {
        Run("UPDATE live_channels SET epg_id = NULL WHERE source_id = $s", ("$s", sourceId));
        foreach (var (channelId, epgId) in epgIds)
        {
            if (epgId is not null) Run("UPDATE live_channels SET epg_id = $epg WHERE id = $id", ("$epg", epgId), ("$id", channelId));
        }
    });

    /// <summary>Ersetzt den Programmführer der Liste durch die Sendungen.</summary>
    public void ReplaceLiveGuide(long sourceId, IReadOnlyList<XmltvProgramme> programmes) => InTransaction(() =>
    {
        Run("DELETE FROM live_epg WHERE source_id = $s", ("$s", sourceId));

        using var cmd = Command("""
            INSERT INTO live_epg (source_id, epg_id, start_utc, stop_utc, title, description) VALUES ($s, $epg, $start, $stop, $title, $desc)
            """);
        var s = cmd.Parameters.Add("$s", SqliteType.Integer);
        var epg = cmd.Parameters.Add("$epg", SqliteType.Text);
        var start = cmd.Parameters.Add("$start", SqliteType.Text);
        var stop = cmd.Parameters.Add("$stop", SqliteType.Text);
        var title = cmd.Parameters.Add("$title", SqliteType.Text);
        var desc = cmd.Parameters.Add("$desc", SqliteType.Text);
        foreach (var p in programmes)
        {
            s.Value = sourceId;
            epg.Value = p.ChannelId;
            start.Value = Utc(p.StartUtc);
            stop.Value = Utc(p.StopUtc);
            title.Value = p.Title;
            desc.Value = (object?)p.Description ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
        Run("UPDATE live_sources SET guide_refreshed_utc = $now WHERE id = $id", ("$now", Utc(DateTime.UtcNow)), ("$id", sourceId));
    });

    /// <summary>Löscht Sendungen, die vor <paramref name="beforeUtc"/> zu Ende waren.</summary>
    public int PruneLiveGuide(DateTime beforeUtc) => Run("DELETE FROM live_epg WHERE stop_utc <= $before", ("$before", Utc(beforeUtc)));

    private LiveProgramme? ReadProgramme(string sql, params (string, object?)[] parameters)
    {
        using var cmd = Command(sql, parameters);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new LiveProgramme(ParseUtc(r.GetString(0))!.Value, ParseUtc(r.GetString(1))!.Value, r.GetString(2), r.GetStringOrNull(3)) : null;
    }

    /// <summary>Was zu <paramref name="nowUtc"/> läuft und was danach kommt.</summary>
    public LiveNowNext GetLiveNowNext(long sourceId, string epgId, DateTime nowUtc)
    {
        var now = ReadProgramme("""
            SELECT start_utc, stop_utc, title, description FROM live_epg
            WHERE source_id = $s AND epg_id = $epg AND start_utc <= $now AND stop_utc > $now ORDER BY start_utc DESC LIMIT 1
            """, ("$s", sourceId), ("$epg", epgId), ("$now", Utc(nowUtc)));
        var after = now is null ? nowUtc : now.StopUtc;
        var next = ReadProgramme("""
            SELECT start_utc, stop_utc, title, description FROM live_epg
            WHERE source_id = $s AND epg_id = $epg AND start_utc >= $after ORDER BY start_utc LIMIT 1
            """, ("$s", sourceId), ("$epg", epgId), ("$after", Utc(after)));
        return new LiveNowNext(now, next);
    }

    /// <summary>Die Sendungen eines Senders, die zwischen <paramref name="fromUtc"/> und <paramref name="toUtc"/> laufen.</summary>
    public List<LiveProgramme> ListLiveProgrammes(long sourceId, string epgId, DateTime fromUtc, DateTime toUtc)
    {
        var rows = new List<LiveProgramme>();
        using var cmd = Command("""
            SELECT start_utc, stop_utc, title, description FROM live_epg
            WHERE source_id = $s AND epg_id = $epg AND stop_utc > $from AND start_utc < $to ORDER BY start_utc
            """, ("$s", sourceId), ("$epg", epgId), ("$from", Utc(fromUtc)), ("$to", Utc(toUtc)));
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(new LiveProgramme(ParseUtc(r.GetString(0))!.Value, ParseUtc(r.GetString(1))!.Value, r.GetString(2), r.GetStringOrNull(3)));
        return rows;
    }
}
