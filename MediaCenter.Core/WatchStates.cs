namespace MediaCenter.Core;

public enum WatchState
{
    /// <summary>Nichts davon gesehen oder angefangen.</summary>
    Unwatched,

    /// <summary>Etwas gesehen oder angefangen, aber nicht alles.</summary>
    InProgress,

    Watched,
}

/// <summary>Wie weit ein Film oder eine Serie gesehen ist. Reine Berechnung aus den Videos und dem gespeicherten Fortschritt.</summary>
public static class WatchStates
{
    /// <summary>Gesehen ist ein Titel, wenn jedes seiner Videos als gesehen markiert ist; ohne Videos gilt er als ungesehen.</summary>
    public static WatchState Of(IReadOnlyList<VideoRow> videos, IReadOnlyDictionary<long, PlaybackState> playback)
    {
        if (videos.Count == 0) return WatchState.Unwatched;

        var watched = videos.Count(v => playback.TryGetValue(v.VideoId, out var p) && p.Watched);
        if (watched == videos.Count) return WatchState.Watched;
        var started = watched > 0 || videos.Any(v => playback.TryGetValue(v.VideoId, out var p) && p.PositionSeconds > 0);
        return started ? WatchState.InProgress : WatchState.Unwatched;
    }
}

public enum TitleSort
{
    Name,

    /// <summary>Neueste Filme und Serien zuerst; ohne Jahr ans Ende.</summary>
    Year,

    /// <summary>Zuletzt in die Bibliothek aufgenommen zuerst.</summary>
    Added,

    /// <summary>Beste Wertung zuerst; ohne Wertung ans Ende.</summary>
    Rating,

    /// <summary>Höchste Auflösung zuerst; ohne bekannte Auflösung ans Ende.</summary>
    Resolution,
}

public static class TitleOrdering
{
    /// <param name="ratings">Wertung je Titel-Id (aus den Infotexten).</param>
    /// <param name="heights">Größte Bildhöhe je Titel-Id.</param>
    public static List<TitleRow> Order(IEnumerable<TitleRow> titles, TitleSort sort, IReadOnlyDictionary<long, double> ratings, IReadOnlyDictionary<long, int> heights)
    {
        var byName = StringComparer.CurrentCultureIgnoreCase;
        return sort switch
        {
            TitleSort.Name => titles.OrderBy(t => t.Name, byName).ToList(),
            TitleSort.Year => titles.OrderByDescending(t => t.Year.HasValue).ThenByDescending(t => t.Year ?? 0).ThenBy(t => t.Name, byName).ToList(),
            TitleSort.Added => titles.OrderByDescending(t => t.Id).ToList(),
            TitleSort.Rating => titles.OrderByDescending(t => ratings.ContainsKey(t.Id)).ThenByDescending(t => ratings.GetValueOrDefault(t.Id)).ThenBy(t => t.Name, byName).ToList(),
            TitleSort.Resolution => titles.OrderByDescending(t => heights.ContainsKey(t.Id)).ThenByDescending(t => heights.GetValueOrDefault(t.Id)).ThenBy(t => t.Name, byName).ToList(),
            _ => throw new ArgumentOutOfRangeException(nameof(sort), sort, null),
        };
    }
}
