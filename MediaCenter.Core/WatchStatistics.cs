using System.Globalization;

namespace MediaCenter.Core;

/// <summary>Ein Video im Verlauf: wann zuletzt etwas damit geschah und wie weit es ist.</summary>
public sealed record HistoryEntry(
    long VideoId, long TitleId, string TitleName, TitleKind Kind, int? Season, int? Episode,
    double PositionSeconds, double? DurationSeconds, bool Watched, DateTime WhenUtc);

/// <summary>Gesehene Zeit in einem Monat.</summary>
public sealed record MonthBar(int Year, int Month, int Videos, double Hours);

/// <summary>Gesehene Zeit eines Genres oder Titels.</summary>
public sealed record HoursShare(string Name, double Hours);

public sealed record WatchStatistics(
    int Movies, int Series, int Videos, double LibraryHours, long LibraryBytes,
    int WatchedMovies, int WatchedEpisodes, double WatchedHours, double Last30DaysHours,
    IReadOnlyList<MonthBar> Months, IReadOnlyList<HoursShare> Genres, IReadOnlyList<HoursShare> TopTitles)
{
    /// <summary>Anzahl Monate im Verlauf (der laufende und die elf davor).</summary>
    public const int MonthCount = 12;

    public const int TopCount = 8;

    /// <summary>
    /// Rechnet Verlauf und Statistik aus dem, was die Datenbank kennt. Es gibt keinen Protokoll-Eintrag je Wiedergabe: gezählt wird der gemerkte
    /// Stand je Video — ein gesehenes Video mit seiner ganzen Länge, ein angefangenes mit der Stelle, bei der es steht — und als Zeitpunkt der letzte
    /// Eintrag zu diesem Video. Wer eine Folge zweimal sieht, zählt sie einmal; „Als ungesehen markieren“ nimmt sie wieder heraus.
    /// </summary>
    /// <param name="sizes">Dateigröße in Bytes je Datei-Id (<see cref="VideoRow.FileId"/>); fehlende Dateien zählen 0.</param>
    /// <param name="zone">Zeitzone für die Monate; null = die des Rechners.</param>
    public static WatchStatistics Build(
        IReadOnlyList<VideoRow> videos, IReadOnlyList<TitleRow> titles, IReadOnlyDictionary<long, List<string>> genres,
        IReadOnlyDictionary<long, PlaybackState> playback, IReadOnlyDictionary<long, long> sizes, DateTime nowUtc, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var titleById = titles.ToDictionary(t => t.Id);
        var owned = videos.Where(v => v.TitleId is { } id && titleById.ContainsKey(id)).ToList();

        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
        var months = Enumerable.Range(0, MonthCount)
            .Select(i => new DateTime(local.Year, local.Month, 1).AddMonths(-(MonthCount - 1 - i)))
            .Select(d => (d.Year, d.Month, Videos: 0, Hours: 0.0))
            .ToList();

        double watchedSeconds = 0, last30 = 0;
        int watchedMovies = 0, watchedEpisodes = 0;
        var perGenre = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var perTitle = new Dictionary<long, double>();

        foreach (var video in owned)
        {
            if (!playback.TryGetValue(video.VideoId, out var state)) continue;
            var seconds = SecondsOf(video, state);
            if (seconds <= 0 && !state.Watched) continue;

            var title = titleById[video.TitleId!.Value];
            watchedSeconds += seconds;
            perTitle[title.Id] = perTitle.GetValueOrDefault(title.Id) + seconds;
            if (genres.TryGetValue(title.Id, out var list))
                foreach (var genre in list) perGenre[genre] = perGenre.GetValueOrDefault(genre) + seconds;

            if (state.Watched)
            {
                if (title.Kind == TitleKind.Movie) watchedMovies++;
                else watchedEpisodes++;
            }

            var when = ParseUtc(state.UpdatedUtc);
            if (nowUtc - when <= TimeSpan.FromDays(30) && when <= nowUtc) last30 += seconds;
            var whenLocal = TimeZoneInfo.ConvertTimeFromUtc(when, zone);
            var slot = months.FindIndex(m => m.Year == whenLocal.Year && m.Month == whenLocal.Month);
            if (slot >= 0 && state.Watched) months[slot] = months[slot] with { Videos = months[slot].Videos + 1, Hours = months[slot].Hours + seconds / 3600 };
        }

        return new WatchStatistics(
            Movies: titles.Count(t => t.Kind == TitleKind.Movie && owned.Any(v => v.TitleId == t.Id)),
            Series: titles.Count(t => t.Kind == TitleKind.Series && owned.Any(v => v.TitleId == t.Id)),
            Videos: owned.Count,
            LibraryHours: owned.Sum(v => v.DurationSeconds ?? 0) / 3600,
            LibraryBytes: owned.Sum(v => sizes.GetValueOrDefault(v.FileId)),
            WatchedMovies: watchedMovies,
            WatchedEpisodes: watchedEpisodes,
            WatchedHours: watchedSeconds / 3600,
            Last30DaysHours: last30 / 3600,
            Months: months.Select(m => new MonthBar(m.Year, m.Month, m.Videos, m.Hours)).ToList(),
            Genres: perGenre.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.CurrentCultureIgnoreCase)
                .Take(TopCount).Select(p => new HoursShare(p.Key, p.Value / 3600)).ToList(),
            TopTitles: perTitle.OrderByDescending(p => p.Value).ThenBy(p => titleById[p.Key].Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(TopCount).Select(p => new HoursShare(titleById[p.Key].Name, p.Value / 3600)).ToList());
    }

    /// <summary>Gesehene Sekunden eines Videos: bei „gesehen“ die Länge, sonst die gemerkte Stelle (nie mehr als die Länge).</summary>
    private static double SecondsOf(VideoRow video, PlaybackState state)
    {
        if (state.Watched) return video.DurationSeconds ?? 0;
        return video.DurationSeconds is { } duration ? Math.Min(state.PositionSeconds, duration) : state.PositionSeconds;
    }

    private static DateTime ParseUtc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    /// <summary>
    /// Die zuletzt angeschauten Videos, das jüngste zuerst. Aufgenommen wird, was gesehen oder angefangen ist; „ungesehen“ markierte Videos
    /// (Stelle 0) stehen nicht darin.
    /// </summary>
    public static List<HistoryEntry> Recent(
        IReadOnlyList<VideoRow> videos, IReadOnlyList<TitleRow> titles, IReadOnlyDictionary<long, PlaybackState> playback, int limit)
    {
        var titleById = titles.ToDictionary(t => t.Id);
        var entries = new List<HistoryEntry>();
        foreach (var video in videos)
        {
            if (video.TitleId is not { } titleId || !titleById.TryGetValue(titleId, out var title)) continue;
            if (!playback.TryGetValue(video.VideoId, out var state) || (!state.Watched && state.PositionSeconds <= 0)) continue;
            entries.Add(new HistoryEntry(video.VideoId, titleId, title.Name, title.Kind, video.Season, video.Episode,
                state.PositionSeconds, video.DurationSeconds, state.Watched, ParseUtc(state.UpdatedUtc)));
        }
        return entries.OrderByDescending(e => e.WhenUtc).ThenBy(e => e.TitleName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.Season).ThenBy(e => e.Episode).Take(limit).ToList();
    }
}
