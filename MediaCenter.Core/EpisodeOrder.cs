namespace MediaCenter.Core;

public static class EpisodeOrder
{
    /// <summary>
    /// Die Folge nach <paramref name="current"/> innerhalb derselben Serie (nach Staffel und Folge), null bei der letzten.
    /// Filme und Videos ohne Folgennummer haben keine nächste Folge; doppelte Nummern (zwei Fassungen) werden übersprungen.
    /// </summary>
    public static VideoRow? Next(IEnumerable<VideoRow> videos, VideoRow current)
    {
        if (current.TitleKind != TitleKind.Series || current.Episode is null) return null;

        return videos
            .Where(v => v.TitleId == current.TitleId && v.Episode is not null)
            .OrderBy(v => v.Season ?? 0)
            .ThenBy(v => v.Episode)
            .ThenBy(v => v.VideoId)
            .SkipWhile(v => v.VideoId != current.VideoId)
            .Skip(1)
            .FirstOrDefault(v => (v.Season ?? 0, v.Episode) != (current.Season ?? 0, current.Episode));
    }
}
