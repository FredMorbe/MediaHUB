using System.Text.RegularExpressions;

namespace MediaCenter.Core;

/// <param name="Id">Zweibuchstabiger Schlüssel, wie er in den Einstellungen steht.</param>
/// <param name="Codes">Weitere Kürzel, unter denen Dateien die Sprache markieren (ISO 639-2 B und T).</param>
public sealed record Language(string Id, string Name, string EnglishName, params string[] Codes)
{
    /// <summary>Kommagetrennte Liste für mpvs <c>alang</c> und <c>slang</c>.</summary>
    public string MpvList => string.Join(',', new[] { Id }.Concat(Codes));
}

/// <summary>Die Sprachen, die in den Einstellungen wählbar sind, und das Erkennen von Sprachkürzeln in Dateien.</summary>
public static partial class Languages
{
    public static readonly IReadOnlyList<Language> All = new Language[]
    {
        new("de", "Deutsch", "German", "deu", "ger"),
        new("en", "Englisch", "English", "eng"),
        new("fr", "Französisch", "French", "fra", "fre"),
        new("es", "Spanisch", "Spanish", "spa"),
        new("it", "Italienisch", "Italian", "ita"),
        new("ja", "Japanisch", "Japanese", "jpn"),
        new("ko", "Koreanisch", "Korean", "kor"),
        new("zh", "Chinesisch", "Chinese", "zho", "chi"),
        new("ru", "Russisch", "Russian", "rus"),
        new("pt", "Portugiesisch", "Portuguese", "por"),
        new("nl", "Niederländisch", "Dutch", "nld", "dut"),
        new("pl", "Polnisch", "Polish", "pol"),
        new("tr", "Türkisch", "Turkish", "tur"),
        new("sv", "Schwedisch", "Swedish", "swe"),
        new("da", "Dänisch", "Danish", "dan"),
        new("no", "Norwegisch", "Norwegian", "nor", "nob", "nno"),
        new("fi", "Finnisch", "Finnish", "fin"),
        new("cs", "Tschechisch", "Czech", "ces", "cze"),
        new("hu", "Ungarisch", "Hungarian", "hun"),
        new("el", "Griechisch", "Greek", "ell", "gre"),
        new("ar", "Arabisch", "Arabic", "ara"),
        new("he", "Hebräisch", "Hebrew", "heb"),
        new("hi", "Hindi", "Hindi", "hin"),
        new("th", "Thailändisch", "Thai", "tha"),
        new("uk", "Ukrainisch", "Ukrainian", "ukr"),
        new("ro", "Rumänisch", "Romanian", "ron", "rum"),
    };

    /// <summary>Die Sprache zu einem Schlüssel aus den Einstellungen; ein unbekannter Schlüssel ist ein Fehler.</summary>
    public static Language Get(string id) =>
        All.FirstOrDefault(l => l.Id == id) ?? throw new ArgumentException($"Unbekannte Sprache „{id}“.", nameof(id));

    /// <summary>Erkennt Kürzel (de, deu, ger, de-DE, pt_BR) und Namen (Deutsch, German); null bei Unbekanntem.</summary>
    public static Language? Find(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var key = text.Trim().ToLowerInvariant();
        var region = key.IndexOfAny(new[] { '-', '_' });
        if (region > 0) key = key[..region];
        return All.FirstOrDefault(l =>
            l.Id == key || l.Codes.Contains(key) || l.Name.ToLowerInvariant() == key || l.EnglishName.ToLowerInvariant() == key);
    }

    /// <summary>
    /// Sucht einen Sprachnamen oder ein dreibuchstabiges Kürzel in einem Titel, etwa „German.srt“ oder „deu forced“.
    /// Zweibuchstabige Wörter zählen nicht („no“ ist auch ein englisches Wort).
    /// </summary>
    public static Language? FindInText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        foreach (var token in WordSplit().Split(text))
        {
            if (token.Length < 3) continue;
            if (Find(token) is { } language) return language;
        }
        return null;
    }

    /// <summary>Anzeigename für ein Sprachkürzel aus einer Datei; unbekannte Kürzel bleiben wie sie sind.</summary>
    public static string Display(string? code) =>
        Find(code)?.Name ?? (string.IsNullOrWhiteSpace(code) || code == "und" ? "Unbekannt" : code);

    [GeneratedRegex(@"[^\p{L}]+")]
    private static partial Regex WordSplit();
}
