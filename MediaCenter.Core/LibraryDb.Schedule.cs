namespace MediaCenter.Core;

// Sendeplan der Serien (Kalender, „neue Folge“): abgeleitete Daten von TVmaze. Sie lassen sich jederzeit neu holen; was der Nutzer nicht sehen
// soll (notified), merkt sich die Datenbank je Folge.
public sealed partial class LibraryDb
{
    public ScheduleCheck? GetScheduleCheck(long titleId)
    {
        using var cmd = Command("SELECT title_id, tvmaze_id, status, checked_utc FROM series_schedule WHERE title_id = $id", ("$id", titleId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadScheduleCheck(r) : null;
    }

    /// <summary>Der Stand aller Serien auf einmal: Titel-Id → Stand.</summary>
    public Dictionary<long, ScheduleCheck> ListScheduleChecks()
    {
        var rows = new Dictionary<long, ScheduleCheck>();
        using var cmd = Command("SELECT title_id, tvmaze_id, status, checked_utc FROM series_schedule");
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows[r.GetInt64(0)] = ReadScheduleCheck(r);
        return rows;
    }

    private static ScheduleCheck ReadScheduleCheck(Microsoft.Data.Sqlite.SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetInt32OrNull(1), r.GetStringOrNull(2), ParseUtc(r.GetString(3))!.Value);

    /// <summary>
    /// Speichert den Sendeplan einer Serie und den Zeitpunkt der Abfrage. <paramref name="schedule"/> = null heißt: TVmaze kennt die Serie nicht;
    /// das wird vermerkt (damit nicht bei jedem Start neu gefragt wird) und alte Folgen verschwinden. Folgen, die schon gemeldet wurden, bleiben gemeldet.
    /// </summary>
    public void SaveSchedule(long titleId, ShowSchedule? schedule, DateTime nowUtc) => InTransaction(() =>
    {
        Run("""
            INSERT INTO series_schedule (title_id, tvmaze_id, status, checked_utc) VALUES ($id, $tvmaze, $status, $now)
            ON CONFLICT (title_id) DO UPDATE SET tvmaze_id = excluded.tvmaze_id, status = excluded.status, checked_utc = excluded.checked_utc
            """,
            ("$id", titleId), ("$tvmaze", schedule?.TvMazeId), ("$status", schedule?.Status), ("$now", Utc(nowUtc)));

        var keep = new HashSet<(int, int)>();
        foreach (var e in schedule?.Episodes ?? Array.Empty<ScheduledEpisode>())
        {
            keep.Add((e.Season, e.Number));
            Run("""
                INSERT INTO episode_air (title_id, season, episode, air_utc, has_time, name) VALUES ($id, $season, $episode, $air, $time, $name)
                ON CONFLICT (title_id, season, episode) DO UPDATE SET air_utc = excluded.air_utc, has_time = excluded.has_time, name = excluded.name
                """,
                ("$id", titleId), ("$season", e.Season), ("$episode", e.Number), ("$air", Utc(e.AirUtc)), ("$time", e.HasTime ? 1 : 0), ("$name", e.Name));
        }

        var stale = new List<(int, int)>();
        using (var cmd = Command("SELECT season, episode FROM episode_air WHERE title_id = $id", ("$id", titleId)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                if (!keep.Contains((r.GetInt32(0), r.GetInt32(1)))) stale.Add((r.GetInt32(0), r.GetInt32(1)));
            }
        }
        foreach (var (season, episode) in stale)
        {
            Run("DELETE FROM episode_air WHERE title_id = $id AND season = $season AND episode = $episode", ("$id", titleId), ("$season", season), ("$episode", episode));
        }
    });

    /// <summary>Die Folgen, die zwischen <paramref name="fromUtc"/> und <paramref name="toUtc"/> senden, mit dem Namen der Serie aus der Bibliothek.</summary>
    public List<ScheduleRow> ListSchedule(DateTime fromUtc, DateTime toUtc)
    {
        var rows = new List<ScheduleRow>();
        using var cmd = Command("""
            SELECT a.title_id, t.name, a.season, a.episode, a.name, a.air_utc, a.has_time, a.notified
            FROM episode_air a JOIN titles t ON t.id = a.title_id
            WHERE a.air_utc >= $from AND a.air_utc <= $to
            ORDER BY a.air_utc, t.name COLLATE NOCASE, a.season, a.episode
            """, ("$from", Utc(fromUtc)), ("$to", Utc(toUtc)));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new ScheduleRow(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetStringOrNull(4), ParseUtc(r.GetString(5))!.Value, r.GetInt32(6) == 1, r.GetInt32(7) == 1));
        }
        return rows;
    }

    /// <summary>Merkt Folgen als gemeldet („Neue Folge“-Hinweis wurde gezeigt).</summary>
    public void MarkNotified(IEnumerable<ScheduleRow> rows) => InTransaction(() =>
    {
        foreach (var row in rows)
        {
            Run("UPDATE episode_air SET notified = 1 WHERE title_id = $id AND season = $season AND episode = $episode",
                ("$id", row.TitleId), ("$season", row.Season), ("$episode", row.Episode));
        }
    });
}
