using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MediaCenter.Core;

/// <summary>Ein gefundenes Poster im Internet.</summary>
/// <param name="ShowName">Name beim Anbieter (zur Anzeige in Meldungen).</param>
public sealed record OnlinePoster(string ShowName, string ImageUrl);

/// <summary>Findet Poster im Internet. Gibt null zurück, wenn es keinen sicheren Treffer gibt (nie ein geratenes Bild).</summary>
public interface IPosterSource
{
    /// <summary>Kennung des Anbieters; unter ihr merkt sich die Datenbank ergebnislose Suchen.</summary>
    string Provider { get; }

    /// <exception cref="PosterSourceUnavailableException">Der Dienst lehnt ab (falscher Schlüssel, Tageslimit): weitere Anfragen sind in diesem Durchlauf sinnlos.</exception>
    Task<OnlinePoster?> FindAsync(string title, int? year, CancellationToken ct = default);
}

/// <summary>Ein sicher erkannter Film mit der Schreibweise des Dienstes.</summary>
/// <param name="PosterUrl">https-Adresse des Posters; null, wenn der Dienst keins hat.</param>
public sealed record MovieInfo(string Title, int? Year, string? PosterUrl);

/// <summary>Erkennt einen Film am Titel (und Jahr) und nennt seinen richtigen Titel und sein Jahr. Gibt null zurück, wenn es keinen sicheren Treffer gibt.</summary>
public interface IMovieInfoSource
{
    /// <exception cref="PosterSourceUnavailableException">Der Dienst lehnt ab (falscher Schlüssel, Tageslimit).</exception>
    Task<MovieInfo?> LookupAsync(string title, int? year, CancellationToken ct = default);
}

/// <summary>Der Dienst ist erreichbar, nimmt aber keine Anfragen an (ungültiger Schlüssel, Tageslimit erreicht).</summary>
public sealed class PosterSourceUnavailableException(string message) : Exception(message);

/// <summary>Vergleich von Titeln für die Suche nach Postern.</summary>
public static class PosterNames
{
    /// <summary>Kleinbuchstaben ohne Akzente, Satzzeichen und Leerraum: „Lindenstraße“ und „lindenstrasse“ sind gleich, „Dr. House“ und „Dr House“ auch.</summary>
    public static string Normalize(string text)
    {
        var decomposed = text.Replace("ß", "ss", StringComparison.Ordinal).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    private static readonly HashSet<string> Articles = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "der", "die", "das", "ein", "eine", "le", "la", "les", "el", "il",
    };

    private static readonly Dictionary<string, string> Roman = new(StringComparer.Ordinal)
    {
        ["ii"] = "2", ["iii"] = "3", ["iv"] = "4", ["v"] = "5", ["vi"] = "6", ["vii"] = "7", ["viii"] = "8", ["ix"] = "9", ["x"] = "10",
    };

    /// <summary>Wörter eines Titels: Kleinbuchstaben ohne Akzente, nur Buchstaben und Ziffern.</summary>
    public static IReadOnlyList<string> Tokens(string text)
    {
        var decomposed = text.Replace("ß", "ss", StringComparison.Ordinal).Normalize(NormalizationForm.FormD);
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c))
            {
                word.Append(char.ToLowerInvariant(c));
            }
            else if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }
        if (word.Length > 0) words.Add(word.ToString());
        return words;
    }

    /// <summary>
    /// Schlüssel für „ist derselbe Titel“: ohne Akzente, Satzzeichen und Großschreibung, ohne führenden Artikel („The Matrix Reloaded“ =
    /// „Matrix Reloaded“) und mit römischen Zahlen als Ziffern („Ghostbusters II“ = „Ghostbusters 2“).
    /// </summary>
    public static string MatchKey(string text)
    {
        var tokens = Tokens(text).ToList();
        if (tokens.Count > 1 && Articles.Contains(tokens[0])) tokens.RemoveAt(0);
        return string.Join(' ', tokens.Select(t => Roman.TryGetValue(t, out var digit) ? digit : t));
    }

    /// <summary>Folgenummern, die der Nutzer anders schreibt als der Dienst („Matrix 2 Reloaded“): 1–2-stellige Zahlen und II–IX.</summary>
    private static readonly Regex SequelNumber = new(@"^\W*(\d{1,2}|ii|iii|iv|vi|vii|viii|ix)\W*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SwappableNumber = new(@"\b(\d{1,2}|ii|iii|iv|vi|vii|viii|ix)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] RomanNumerals = { "", "", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X" };

    /// <summary>
    /// Der Titel mit vertauschter Zahlenschreibweise („Ghostbusters 2“ → „Ghostbusters II“, „Rocky IV“ → „Rocky 4“), weil Dienste die
    /// eine oder die andere führen. Ziffern 2–10 werden römisch, römische II–IX arabisch; null, wenn nichts zu tauschen ist.
    /// </summary>
    public static string? WithSwappedNumbers(string title)
    {
        var changed = false;
        var result = SwappableNumber.Replace(title, match =>
        {
            var word = match.Value;
            if (int.TryParse(word, out var number))
            {
                if (number is < 2 or > 10) return word;
                changed = true;
                return RomanNumerals[number];
            }
            var arabic = Array.IndexOf(RomanNumerals, word.ToUpperInvariant());
            if (arabic < 2) return word;
            changed = true;
            return arabic.ToString(CultureInfo.InvariantCulture);
        });
        return changed ? result : null;
    }

    /// <summary>Der Titel ohne solche Folgenummern; null, wenn keine enthalten ist oder nichts übrig bliebe.</summary>
    public static string? WithoutSequelNumbers(string title)
    {
        var words = title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var kept = words.Where(w => !SequelNumber.IsMatch(w)).ToArray();
        return kept.Length == 0 || kept.Length == words.Length ? null : string.Join(' ', kept);
    }
}
