namespace MediaCenter.Core;

/// <param name="Checked">Serien, deren Sendeplan abgefragt wurde.</param>
/// <param name="Failures">Serien, bei denen die Antwort nicht lesbar war („Titel: Grund“); der Lauf ging weiter.</param>
/// <param name="Aborted">TVmaze war nicht erreichbar oder lehnte ab: der Lauf endete früh, die übrigen Serien kommen beim nächsten Mal dran.</param>
public sealed record ScheduleRefresh(int Checked, IReadOnlyList<string> Failures, string? Aborted);

/// <summary>
/// Holt die Sendepläne der Serien aus der Bibliothek von TVmaze, so oft es nötig ist (laufende Serien halbtäglich, beendete selten), und sagt, welche
/// neuen Folgen noch nicht in der Bibliothek liegen. Gesendet werden nur Serientitel und Jahr. Alles Datenbankzeug läuft auf dem aufrufenden Thread;
/// nur der Abruf wartet.
/// </summary>
public sealed class ScheduleService(LibraryDb db, TvMazeClient tvmaze)
{
    /// <param name="force">Alle Serien abfragen, auch wenn ihr Plan noch gilt (Knopf „Aktualisieren“).</param>
    public async Task<ScheduleRefresh> RefreshAsync(IEnumerable<TitleRow> titles, DateTime nowUtc, bool force = false, CancellationToken ct = default)
    {
        var checks = db.ListScheduleChecks();
        var failures = new List<string>();
        var done = 0;
        foreach (var title in titles.Where(t => t.Kind == TitleKind.Series))
        {
            ct.ThrowIfCancellationRequested();
            checks.TryGetValue(title.Id, out var check);
            if (!force && check is not null && nowUtc - check.CheckedUtc < EpisodeCalendar.RefreshInterval(check)) continue;

            try
            {
                var schedule = await tvmaze.FindScheduleAsync(title.Name, title.Year, check?.TvMazeId, ct);
                db.SaveSchedule(title.Id, schedule, nowUtc);
                done++;
            }
            catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
            {
                // Diese eine Antwort ist unbrauchbar (zu groß, kaputt): nicht bei jedem Start neu versuchen.
                failures.Add($"{title.Name}: {ex.Message}");
                db.SaveSchedule(title.Id, null, nowUtc);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return new ScheduleRefresh(done, failures, ex.Message);
            }
        }
        return new ScheduleRefresh(done, failures, null);
    }

    /// <summary>Der Kalender: was bald läuft und was neu gesendet wurde, aber nicht in der Bibliothek liegt.</summary>
    public CalendarView Calendar(IEnumerable<VideoRow> videos, DateTime nowUtc, TimeSpan localOffset) =>
        EpisodeCalendar.Build(db.ListSchedule(nowUtc - EpisodeCalendar.RecentWindow - TimeSpan.FromDays(1), nowUtc + EpisodeCalendar.Horizon), videos, nowUtc, localOffset);

    /// <summary>
    /// Neu gesendete Folgen, die in der Bibliothek fehlen und von denen die App noch nichts gesagt hat (höchstens die der letzten Tage, damit nach
    /// einer Pause nicht alles auf einmal aufpoppt); sie gelten danach als gemeldet.
    /// </summary>
    public IReadOnlyList<ScheduleRow> TakeNewEpisodes(IEnumerable<VideoRow> videos, DateTime nowUtc, TimeSpan localOffset)
    {
        var fresh = Calendar(videos, nowUtc, localOffset).Missing
            .Where(r => !r.Notified && nowUtc - r.AirUtc <= TimeSpan.FromDays(4))
            .ToList();
        db.MarkNotified(fresh);
        return fresh;
    }
}
