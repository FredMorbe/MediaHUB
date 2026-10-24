using System.Text.RegularExpressions;

namespace MediaCenter.Core;

/// <param name="Artist">null, wenn der Dateiname kein „Künstler - Titel“ ist.</param>
public sealed record FileNameParts(string? Artist, string Title);

/// <summary>
/// Liest Künstler und Titel aus dem Dateinamen einer Audiodatei, wie er bei heruntergeladenen Videos und Rips üblich ist:
/// „[HD 1080p] Künstler - Titel (Official Video)“. Reine Berechnung, ändert nichts.
/// </summary>
public static partial class TrackFileName
{
    /// <summary>"07 - ", "07. ", "07_", "07) "; "50 Cent - …" und "3 Doors Down" bleiben unberührt.</summary>
    [GeneratedRegex(@"^\s*\d{1,3}\s*[-._)]+\s*(?=\S)")]
    private static partial Regex LeadingNumber();

    /// <summary>Eckige Klammern am Anfang, meist Herkunft oder Qualität: "[HD 1080p]", "【MV】".</summary>
    [GeneratedRegex(@"^\s*(?:\[[^\]]*\]|【[^】]*】)\s*")]
    private static partial Regex LeadingTag();

    /// <summary>Künstler und Titel trennt ein Strich mit Leerzeichen drumherum (nicht der Bindestrich in "Jay-Z").</summary>
    [GeneratedRegex(@"\s+[-–—‐]\s+")]
    private static partial Regex Separator();

    /// <summary>Klammer am Ende, die nur Werbung für die Quelle ist: "(Official Video)", "[Lyrics]", "(HD)", "【4K】".</summary>
    [GeneratedRegex(@"\s*[\(\[【][^()\[\]【】]*?(?<![\p{L}\p{N}])(?:official|video|audio|lyrics?|mv|m/v|hd|hq|4k|\d{3,4}p|visualizer)(?![\p{L}\p{N}])[^()\[\]【】]*[\)\]】]\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex TrailingNoise();

    public static FileNameParts Parse(string path)
    {
        var original = Path.GetFileNameWithoutExtension(path).Trim();
        var name = original;
        if (!name.Contains(' ') && name.Contains('_')) name = name.Replace('_', ' '); // "Daft_Punk_-_One_More_Time"

        name = LeadingNumber().Replace(name, "", 1);
        while (LeadingTag().Match(name) is { Success: true, Length: > 0 } tag) name = name[tag.Length..];

        string? artist = null;
        var title = name.Trim();
        if (Separator().Match(name) is { Success: true } separator)
        {
            var left = name[..separator.Index].Trim();
            var right = name[(separator.Index + separator.Length)..].Trim();
            if (left.Length > 0 && right.Length > 0)
            {
                artist = left;
                title = right;
            }
        }

        while (TrailingNoise().Match(title) is { Success: true } noise && noise.Index > 0) title = title[..noise.Index].TrimEnd();

        return new FileNameParts(artist, title.Length > 0 ? title : original);
    }
}
