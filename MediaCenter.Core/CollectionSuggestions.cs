using System.Text.RegularExpressions;

namespace MediaCenter.Core;

/// <summary>Eine vermutete Filmreihe: Filme, deren Namen nach demselben Grundnamen aussehen.</summary>
public sealed record CollectionSuggestion(string Name, IReadOnlyList<long> TitleIds);

/// <summary>
/// Schlägt Sammlungen vor, indem es Filme mit gemeinsamem Grundnamen zusammenfasst („The Matrix“, „The Matrix Reloaded“, „Iron Man 2“, „Die Hard: …“).
/// Das ist eine Vermutung aus den Namen und nur ein Vorschlag; angelegt wird erst auf Wunsch des Nutzers.
/// </summary>
public static partial class CollectionSuggestions
{
    /// <summary>Eine Zählung am Ende des Namens: „2“, „IV“, „Teil 3“, „Part 2“.</summary>
    [GeneratedRegex(@"\s+(\d{1,2}|[ivx]{1,4}|(teil|part|kapitel|chapter)\s+\d{1,2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingCount();

    /// <summary>Trenner zwischen Reihe und Untertitel: „Die Hard: Jetzt erst recht“, „Star Wars - Episode V“.</summary>
    private static readonly string[] Separators = { ": ", " - ", " – ", " — " };

    /// <summary>Kürzester Grundname, bei dem ein Präfix-Treffer zählt (sonst fasste „Man“ jeden „Man of …“ zusammen).</summary>
    private const int MinPrefixLength = 6;

    /// <summary>Grundname eines Filmtitels ohne Untertitel und Zählung; null, wenn nichts Brauchbares übrig bleibt.</summary>
    public static string? BaseName(string name)
    {
        var text = name.Trim();
        foreach (var separator in Separators)
        {
            var cut = text.IndexOf(separator, StringComparison.Ordinal);
            if (cut >= 3) text = text[..cut].TrimEnd();
        }
        while (TrailingCount().Match(text) is { Success: true } match && match.Index >= 3) text = text[..match.Index].TrimEnd();
        return text.Length >= 2 ? text : null;
    }

    /// <param name="existing">Film-Ids je Sammlungs-Id (<see cref="LibraryDb.ListCollectionItems"/>); wer schon in einer gleichnamigen Sammlung steht, wird nicht noch einmal vorgeschlagen.</param>
    public static List<CollectionSuggestion> Find(
        IEnumerable<TitleRow> titles, IReadOnlyList<CollectionRow> collections, IReadOnlyDictionary<long, List<long>> existing)
    {
        var items = new List<(TitleRow Title, string Display, string Key)>();
        foreach (var title in titles.Where(t => t.Kind == TitleKind.Movie))
        {
            if (BaseName(title.Name) is not { } display) continue;
            var key = SearchText.Fold(display);
            if (key.Length > 0) items.Add((title, display, key));
        }

        // Gleiche Grundnamen bilden eine Gruppe; ein Grundname, der mit einem anderen plus Leerzeichen beginnt, schließt sich an.
        var keys = items.Select(i => i.Key).Distinct().OrderBy(k => k.Length).ThenBy(k => k, StringComparer.Ordinal).ToList();
        var parent = keys.ToDictionary(k => k, k => k);
        string Root(string key)
        {
            while (parent[key] != key) key = parent[key] = parent[parent[key]];
            return key;
        }
        foreach (var shorter in keys)
        {
            if (shorter.Length < MinPrefixLength && !shorter.Contains(' ')) continue;
            foreach (var longer in keys)
            {
                if (longer.Length > shorter.Length && longer.StartsWith(shorter + " ", StringComparison.Ordinal)) parent[Root(longer)] = Root(shorter);
            }
        }

        var result = new List<CollectionSuggestion>();
        foreach (var group in items.GroupBy(i => Root(i.Key)))
        {
            var members = group.ToList();
            if (members.Count < 2) continue;

            var name = members.OrderBy(m => m.Key.Length).ThenBy(m => m.Display, StringComparer.CurrentCultureIgnoreCase).First().Display;
            var already = collections
                .Where(c => string.Equals(SearchText.Fold(c.Name), SearchText.Fold(name), StringComparison.Ordinal))
                .SelectMany(c => existing.GetValueOrDefault(c.Id) ?? new List<long>())
                .ToHashSet();
            var ids = members.Select(m => m.Title)
                .Where(t => !already.Contains(t.Id))
                .OrderBy(t => t.Year is null).ThenBy(t => t.Year).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(t => t.Id).ToList();
            if (ids.Count >= 2) result.Add(new CollectionSuggestion(name, ids));
        }
        return result.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
