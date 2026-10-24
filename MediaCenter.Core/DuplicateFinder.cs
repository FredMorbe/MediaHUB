namespace MediaCenter.Core;

public enum DuplicateKind
{
    /// <summary>Ein Film liegt in mehreren Dateien (z. B. 1080p und 720p).</summary>
    MovieFiles,

    /// <summary>Eine Folge liegt in mehreren Dateien.</summary>
    EpisodeFiles,

    /// <summary>Zwei Einträge heißen gleich (vielleicht derselbe Film mit unterschiedlicher Schreibung oder ein Neuverfilmung).</summary>
    SimilarTitles,
}

public sealed record DuplicateFile(VideoRow Video, long Size);

/// <summary>Eine Gruppe von Einträgen, die doppelt sein könnten.</summary>
/// <param name="Label">Name des Titels, bei Folgen mit Staffel und Folge.</param>
/// <param name="Files">Die Dateien (nur bei <see cref="DuplicateKind.MovieFiles"/> und <see cref="DuplicateKind.EpisodeFiles"/>).</param>
/// <param name="Titles">Die gleichnamigen Einträge (nur bei <see cref="DuplicateKind.SimilarTitles"/>).</param>
public sealed record DuplicateGroup(DuplicateKind Kind, string Label, IReadOnlyList<DuplicateFile> Files, IReadOnlyList<TitleRow> Titles)
{
    /// <summary>Platz, den man gewänne, wenn nur die größte Datei bliebe.</summary>
    public long ReclaimableBytes => Files.Count < 2 ? 0 : Files.Sum(f => f.Size) - Files.Max(f => f.Size);
}

/// <summary>Findet doppelte Einträge in der Bibliothek. Reine Berechnung; gelöscht oder zusammengeführt wird nichts.</summary>
public static class DuplicateFinder
{
    /// <param name="sizes">Dateigröße in Bytes je Datei-Id (<see cref="VideoRow.FileId"/>).</param>
    public static List<DuplicateGroup> Find(IReadOnlyList<VideoRow> videos, IReadOnlyList<TitleRow> titles, IReadOnlyDictionary<long, long> sizes)
    {
        var byName = StringComparer.CurrentCultureIgnoreCase;
        var result = new List<DuplicateGroup>();
        DuplicateFile File(VideoRow video) => new(video, sizes.GetValueOrDefault(video.FileId));

        foreach (var title in titles)
        {
            var own = videos.Where(v => v.TitleId == title.Id).ToList();
            if (title.Kind == TitleKind.Movie)
            {
                if (own.Count >= 2) result.Add(new DuplicateGroup(DuplicateKind.MovieFiles, Label(title), own.Select(File).ToList(), Array.Empty<TitleRow>()));
                continue;
            }
            foreach (var episode in own.Where(v => v.Season is not null && v.Episode is not null).GroupBy(v => (v.Season, v.Episode)).Where(g => g.Count() >= 2))
            {
                result.Add(new DuplicateGroup(DuplicateKind.EpisodeFiles, $"{title.Name}  S{episode.Key.Season:00}E{episode.Key.Episode:00}",
                    episode.Select(File).ToList(), Array.Empty<TitleRow>()));
            }
        }

        foreach (var same in titles.GroupBy(t => (t.Kind, Key: SearchText.Fold(t.Name))).Where(g => g.Key.Key.Length > 0 && g.Count() >= 2))
        {
            var members = same.OrderBy(t => t.Year is null).ThenBy(t => t.Year).ToList();
            result.Add(new DuplicateGroup(DuplicateKind.SimilarTitles, members[0].Name, Array.Empty<DuplicateFile>(), members));
        }

        return result.OrderBy(g => g.Kind).ThenBy(g => g.Label, byName).ToList();
    }

    private static string Label(TitleRow title) => title.Year is { } year ? $"{title.Name} ({year})" : title.Name;
}
