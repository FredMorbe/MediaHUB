using System.Text.RegularExpressions;

namespace MediaCenter.Core;

/// <summary>Gemeinsame Vergleichsregeln der Album-Cover-Anbieter: ein Treffer muss Künstler und Albumtitel genau treffen.</summary>
internal static class AlbumMatching
{
    private static readonly Regex Suffix = new(@"\s*([\(\[][^\)\]]*[\)\]]|-\s*(ep|single)\s*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Vergleichsschlüssel eines Albumtitels: mit und ohne angehängten Zusatz in Klammern oder „- EP“/„- Single“.</summary>
    public static HashSet<string> TitleKeys(string title)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var full = PosterNames.MatchKey(title);
        if (full.Length > 0) keys.Add(full);
        var stripped = PosterNames.MatchKey(Suffix.Replace(title.Trim(), ""));
        if (stripped.Length > 0) keys.Add(stripped);
        return keys;
    }
}
