using System.Text.RegularExpressions;

namespace MediaCenter.Core;

/// <summary>Was die Bibliothek aus den Tags (oder ersatzweise aus dem Dateinamen) einer Audiodatei übernimmt.</summary>
public sealed partial record TrackTags(
    string Title, string? Artist, string? AlbumArtist, string? Album, int? TrackNo, int? DiscNo, int? Year, string? Genre)
{
    /// <summary>"07 - Titel", "07. Titel", "07_Titel" oder zweistellig "07 Titel"; "7 Nation Army" bleibt unberührt.</summary>
    [GeneratedRegex(@"^\s*(\d{1,3})\s*[-._)]+\s*(.+)$|^\s*(\d{2})\s+(.+)$")]
    private static partial Regex LeadingNumber();

    [GeneratedRegex(@"^\s*(\d{1,4})")]
    private static partial Regex LeadingInt();

    [GeneratedRegex(@"^\s*(\d{4})")]
    private static partial Regex LeadingYear();

    public static TrackTags From(IReadOnlyDictionary<string, string>? tags, string path)
    {
        string? Tag(params string[] names)
        {
            foreach (var name in names)
            {
                if (tags is not null && tags.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            return null;
        }

        var (nameNumber, nameTitle) = ParseFileName(path);

        return new TrackTags(
            Title: Tag("title") ?? nameTitle,
            Artist: Tag("artist"),
            AlbumArtist: Tag("album_artist", "albumartist"),
            // Ohne Album-Tag dient der Ordnername als Album (Alben liegen meist in einem eigenen Ordner).
            Album: Tag("album") ?? Path.GetFileName(Path.GetDirectoryName(path)),
            TrackNo: ParseNumber(Tag("track", "tracknumber")) ?? nameNumber,
            DiscNo: ParseNumber(Tag("disc", "discnumber")),
            Year: ParseYear(Tag("date", "year", "originaldate")),
            Genre: Tag("genre"));
    }

    private static (int? Number, string Title) ParseFileName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var match = LeadingNumber().Match(name);
        if (!match.Success) return (null, name.Trim());

        var number = match.Groups[1].Success ? match.Groups[1] : match.Groups[3];
        var title = match.Groups[2].Success ? match.Groups[2] : match.Groups[4];
        return (int.Parse(number.Value), title.Value.Trim());
    }

    /// <summary>"3" und "3/12" ergeben 3.</summary>
    private static int? ParseNumber(string? text) =>
        text is not null && LeadingInt().Match(text) is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

    private static int? ParseYear(string? text) =>
        text is not null && LeadingYear().Match(text) is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;
}
