using System.Globalization;
using System.Text;

namespace MediaCenter.Core;

/// <summary>Textvergleich für die Suche: Groß-/Kleinschreibung, Akzente und Satzzeichen sind egal, jedes Suchwort muss vorkommen.</summary>
public static class SearchText
{
    /// <summary>Kleinbuchstaben ohne Akzente; alles außer Buchstaben und Ziffern wird zu einem Leerzeichen („Dr. House“ → „dr house“).</summary>
    public static string Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (c == 'ß') builder.Append("ss");
            else builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Die Suchwörter der Eingabe; leer, wenn nichts Suchbares darin steht.</summary>
    public static IReadOnlyList<string> Terms(string query) => Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Alle Suchwörter kommen im Text vor (als Teil eines Wortes genügt: „nol“ findet „Nolan“). Ohne Suchwörter passt nichts.</summary>
    public static bool Matches(IReadOnlyList<string> terms, string? text)
    {
        if (terms.Count == 0 || string.IsNullOrEmpty(text)) return false;
        var folded = Fold(text);
        return terms.All(folded.Contains);
    }
}

/// <param name="Via">Der Schauspieler, über den der Titel gefunden wurde; null, wenn schon der Name passt.</param>
public sealed record TitleHit(TitleRow Title, string? Via);

public sealed record ActorHit(string Name, int Titles);

/// <param name="Song">Der passende Song, wenn das Album nicht über Titel oder Künstler, sondern über einen Song gefunden wurde.</param>
public sealed record AlbumHit(AlbumInfo Album, string? Song);

public sealed record SearchResults(IReadOnlyList<TitleHit> Titles, IReadOnlyList<ActorHit> Actors, IReadOnlyList<AlbumHit> Albums)
{
    public static readonly SearchResults None = new([], [], []);

    public bool IsEmpty => Titles.Count == 0 && Actors.Count == 0 && Albums.Count == 0;
}

/// <summary>Sucht in der Bibliothek: Filme und Serien nach Name und Schauspieler, Musik nach Album, Künstler und Song. Reine Berechnung.</summary>
public static class LibrarySearch
{
    /// <summary>So viele passende Schauspieler werden höchstens genannt.</summary>
    public const int MaxActors = 12;

    /// <param name="casts">Besetzung je Titel-Id als Namen mit Komma getrennt (aus den Infotexten); Titel ohne Eintrag sind nur über den Namen zu finden.</param>
    public static SearchResults Run(string query, IEnumerable<TitleRow> titles, IReadOnlyDictionary<long, string> casts, IEnumerable<AlbumInfo> albums)
    {
        var terms = SearchText.Terms(query);
        if (terms.Count == 0) return SearchResults.None;

        var wanted = string.Join(' ', terms);
        var byName = new List<(TitleRow Title, int Rank)>();
        var byActor = new List<TitleHit>();
        var actors = new Dictionary<string, (string Name, HashSet<long> Titles)>();

        foreach (var title in titles)
        {
            var folded = SearchText.Fold(title.Name);
            if (terms.All(folded.Contains))
            {
                // Genau der Name vor „beginnt damit“ vor „kommt irgendwo vor“.
                byName.Add((title, folded == wanted ? 0 : folded.StartsWith(wanted, StringComparison.Ordinal) ? 1 : 2));
                continue;
            }
            if (!casts.TryGetValue(title.Id, out var cast)) continue;

            string? via = null;
            foreach (var actor in Actors(cast))
            {
                if (!SearchText.Matches(terms, actor)) continue;
                via ??= actor;
                var key = SearchText.Fold(actor);
                if (!actors.TryGetValue(key, out var entry)) actors[key] = entry = (actor, new HashSet<long>());
                entry.Titles.Add(title.Id);
            }
            if (via is not null) byActor.Add(new TitleHit(title, via));
        }

        // Auch Titel, die schon über ihren Namen passen, zählen für die Schauspieler-Liste (der Name kann zufällig auch ein Schauspieler sein).
        foreach (var (title, _) in byName)
        {
            if (!casts.TryGetValue(title.Id, out var cast)) continue;
            foreach (var actor in Actors(cast).Where(a => SearchText.Matches(terms, a)))
            {
                var key = SearchText.Fold(actor);
                if (!actors.TryGetValue(key, out var entry)) actors[key] = entry = (actor, new HashSet<long>());
                entry.Titles.Add(title.Id);
            }
        }

        var hits = byName.OrderBy(h => h.Rank).ThenBy(h => h.Title.Name, StringComparer.CurrentCultureIgnoreCase).Select(h => new TitleHit(h.Title, null))
            .Concat(byActor.OrderBy(h => h.Title.Name, StringComparer.CurrentCultureIgnoreCase))
            .ToList();
        var actorHits = actors.Values.OrderByDescending(a => a.Titles.Count).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxActors).Select(a => new ActorHit(a.Name, a.Titles.Count)).ToList();

        var albumHits = new List<AlbumHit>();
        foreach (var album in albums)
        {
            if (SearchText.Matches(terms, album.Title) || SearchText.Matches(terms, album.Artist) || SearchText.Matches(terms, album.Artist + " " + album.Title))
            {
                albumHits.Add(new AlbumHit(album, null));
            }
            else if (album.Tracks.FirstOrDefault(t => SearchText.Matches(terms, t.Title)) is { } song)
            {
                albumHits.Add(new AlbumHit(album, song.Title));
            }
        }
        return new SearchResults(hits, actorHits, albumHits);
    }

    /// <summary>Die Namen einer Besetzungsliste („A, B, C“).</summary>
    public static IEnumerable<string> Actors(string cast) =>
        cast.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
