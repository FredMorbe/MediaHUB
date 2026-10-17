using System.Text.RegularExpressions;

namespace MediaCenter.Core;

public enum VideoKind
{
    Movie,
    Episode,
}

/// <param name="Certain">
/// true, wenn die Zuordnung belastbar ist (Episode mit erkanntem Serientitel, Film mit Jahr);
/// sonst landet die Datei in der Inbox und wartet auf eine Entscheidung.
/// </param>
public sealed record ParsedName(VideoKind Kind, string Title, int? Year, int? Season, int? Episode, bool Certain);

/// <summary>Leitet Titel, Jahr sowie Staffel/Episode aus Dateinamen und Ordnerstruktur ab.</summary>
public static partial class FileNameParser
{
    // Erste Fundstelle (nach mindestens einem Titelwort) beendet den Titel.
    private static readonly string[] QualityTokens =
    {
        "2160p", "1080p", "1080i", "720p", "576p", "480p", "4k", "uhd", "bluray", "blu-ray", "bdrip", "brrip",
        "webrip", "web-dl", "webdl", "hdtv", "dvdrip", "remux", "x264", "x265", "h264", "h265", "hevc",
        "hdr", "hdr10", "dts", "ac3", "eac3", "atmos", "german", "multi", "dl",
    };

    private const string UnknownSeries = "Unbekannte Serie";

    private static readonly Regex QualityRegex = new(
        $"(?<![a-z0-9])({string.Join("|", QualityTokens.Select(Regex.Escape))})(?![a-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [GeneratedRegex(@"^(?<title>.*?)[ ._\-\[(]*S(?<s>\d{1,2})[ ._-]?E(?<e>\d{1,3})(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex SxxExxRegex();

    [GeneratedRegex(@"^(?<title>.*?)[ ._\-]+(?<s>\d{1,2})x(?<e>\d{2,3})(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex NxMMRegex();

    [GeneratedRegex(@"[ ._\(\[\-]((?:19|20)\d{2})(?=[ ._\)\]\-]|$)")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"^(season|staffel|s)[ ._\-]*\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolderRegex();

    public static ParsedName Parse(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);

        var match = SxxExxRegex().Match(name);
        if (!match.Success) match = NxMMRegex().Match(name);
        if (match.Success)
        {
            var title = Clean(match.Groups["title"].Value);
            if (title.Length == 0) title = SeriesTitleFromFolders(path);
            return new ParsedName(
                VideoKind.Episode, title, null,
                int.Parse(match.Groups["s"].Value), int.Parse(match.Groups["e"].Value),
                Certain: title != UnknownSeries);
        }

        int? year = null;
        var titlePart = name;
        var years = YearRegex().Matches(name);
        if (years.Count > 0)
        {
            var last = years[^1];
            year = int.Parse(last.Groups[1].Value);
            titlePart = name[..last.Index];
        }
        else
        {
            var quality = QualityRegex.Match(name);
            if (quality.Success && quality.Index > 0) titlePart = name[..quality.Index];
        }

        var movieTitle = Clean(titlePart);
        if (movieTitle.Length == 0) movieTitle = Clean(name);
        return new ParsedName(VideoKind.Movie, movieTitle, year, null, null, Certain: year is not null);
    }

    public static bool IsSeasonFolder(string folderName) => SeasonFolderRegex().IsMatch(folderName);

    private static string SeriesTitleFromFolders(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null && IsSeasonFolder(Path.GetFileName(dir)))
        {
            dir = Path.GetDirectoryName(dir);
        }
        var title = dir is null ? "" : Clean(Path.GetFileName(dir));
        return title.Length == 0 ? UnknownSeries : title;
    }

    private static string Clean(string raw) =>
        Regex.Replace(raw.Replace('.', ' ').Replace('_', ' '), @"\s+", " ").Trim(' ', '-', '(', '[');
}
