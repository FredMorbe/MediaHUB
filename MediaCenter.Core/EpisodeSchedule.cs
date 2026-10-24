namespace MediaCenter.Core;

/// <summary>Eine Folge mit Sendetermin laut TVmaze.</summary>
/// <param name="HasTime">false: TVmaze kennt nur das Datum (Zeit gilt als Mitternacht UTC).</param>
public sealed record ScheduledEpisode(int Season, int Number, string? Name, DateTime AirUtc, bool HasTime);

/// <param name="Status">Status der Serie bei TVmaze („Running“, „Ended“, „To Be Determined“ …).</param>
public sealed record ShowSchedule(int TvMazeId, string Name, string? Status, IReadOnlyList<ScheduledEpisode> Episodes)
{
    /// <summary>Die Serie läuft oder ist angekündigt: dann kommen neue Folgen dazu und der Plan wird oft neu geholt.</summary>
    public bool IsActive => Status is not "Ended";
}

/// <summary>Eine Folge im Kalender mit der Serie aus der Bibliothek, zu der sie gehört.</summary>
public sealed record ScheduleRow(long TitleId, string TitleName, int Season, int Episode, string? EpisodeName, DateTime AirUtc, bool HasTime, bool Notified);

/// <summary>Der Stand des Sendeplans einer Serie in der Datenbank.</summary>
/// <param name="TvMazeId">null: TVmaze kennt die Serie nicht (sicher genug) – es wird erst nach einer Weile wieder gefragt.</param>
public sealed record ScheduleCheck(long TitleId, int? TvMazeId, string? Status, DateTime CheckedUtc);

/// <summary>Was der Kalender zeigt: was bald läuft und was schon lief, aber nicht in der Bibliothek liegt.</summary>
/// <param name="Upcoming">Folgen, die noch nicht gesendet wurden, nach Termin.</param>
/// <param name="Missing">Gesendete Folgen der letzten Wochen, die in der Bibliothek fehlen, neueste zuerst.</param>
public sealed record CalendarView(IReadOnlyList<ScheduleRow> Upcoming, IReadOnlyList<ScheduleRow> Missing);

public static class EpisodeCalendar
{
    /// <summary>So weit voraus zeigt der Kalender Folgen.</summary>
    public static readonly TimeSpan Horizon = TimeSpan.FromDays(60);

    /// <summary>So weit zurück gelten gesendete Folgen noch als „neu“.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromDays(21);

    /// <summary>
    /// Teilt die Folgen auf. Eine Folge ohne Uhrzeit läuft „heute“ noch, bis der Tag vorbei ist (<paramref name="localOffset"/>: Abstand der
    /// Ortszeit zu UTC). Eine Folge zählt als vorhanden, wenn die Bibliothek ein Video mit dieser Staffel und Folge der Serie kennt.
    /// </summary>
    public static CalendarView Build(IEnumerable<ScheduleRow> rows, IEnumerable<VideoRow> videos, DateTime nowUtc, TimeSpan localOffset)
    {
        var have = videos.Where(v => v is { TitleId: not null, Season: not null, Episode: not null })
            .Select(v => (v.TitleId!.Value, v.Season!.Value, v.Episode!.Value)).ToHashSet();
        var today = (nowUtc + localOffset).Date;

        var upcoming = new List<ScheduleRow>();
        var missing = new List<ScheduleRow>();
        foreach (var row in rows)
        {
            var aired = row.HasTime ? row.AirUtc <= nowUtc : row.AirUtc.Date < today;
            if (!aired)
            {
                if (row.AirUtc - nowUtc <= Horizon) upcoming.Add(row);
            }
            else if (nowUtc - row.AirUtc <= RecentWindow && !have.Contains((row.TitleId, row.Season, row.Episode)))
            {
                missing.Add(row);
            }
        }
        return new CalendarView(
            upcoming.OrderBy(r => r.AirUtc).ThenBy(r => r.TitleName, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Season).ThenBy(r => r.Episode).ToList(),
            missing.OrderByDescending(r => r.AirUtc).ThenBy(r => r.TitleName, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Season).ThenBy(r => r.Episode).ToList());
    }

    /// <summary>Wie lange der Sendeplan einer Serie gilt, bevor er neu geholt wird: laufende Serien halbtäglich, beendete selten, unbekannte nach zwei Wochen.</summary>
    public static TimeSpan RefreshInterval(ScheduleCheck? check) => check switch
    {
        null => TimeSpan.Zero,
        { TvMazeId: null } => TimeSpan.FromDays(14),
        { Status: "Ended" } => TimeSpan.FromDays(30),
        _ => TimeSpan.FromHours(12),
    };
}
