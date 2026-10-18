using System.Globalization;
using System.Text.RegularExpressions;

namespace MediaCenter.Core;

/// <param name="Time">Sekunden, ab denen die Zeile gesungen wird; null bei Texten ohne Zeitmarken.</param>
public sealed record LyricLine(double? Time, string Text);

/// <param name="Source">Woher der Text kommt („Datei (.lrc)“, „Tag“, „LRCLIB“), zur Anzeige.</param>
public sealed record Lyrics(IReadOnlyList<LyricLine> Lines, string Source)
{
    /// <summary>Alle Zeilen haben eine Zeitmarke; die Anzeige kann mitlaufen.</summary>
    public bool Synced => Lines.Count > 0 && Lines.All(l => l.Time is not null);

    /// <summary>Index der Zeile, die bei <paramref name="seconds"/> gerade gesungen wird; -1 vor der ersten Zeile oder bei Texten ohne Zeitmarken.</summary>
    public int LineAt(double seconds)
    {
        if (!Synced) return -1;
        int low = 0, high = Lines.Count - 1, found = -1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (Lines[middle].Time <= seconds)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return found;
    }
}

/// <summary>Liest Songtexte: LRC (mit Zeitmarken) und einfachen Text. Reine Berechnung.</summary>
public static partial class LyricsParser
{
    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex TimeTag();

    [GeneratedRegex(@"^\[(ti|ar|al|au|by|length|re|ve|offset):(.*)\]\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex InfoTag();

    [GeneratedRegex(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>")]
    private static partial Regex WordTag();

    /// <summary>Hat der Text mindestens eine Zeitmarke wie [01:23.45]?</summary>
    public static bool LooksLikeLrc(string text) => TimeTag().IsMatch(text);

    /// <summary>
    /// Liest einen LRC-Text. Eine Zeile kann mehrere Zeitmarken haben (Refrain), Wortmarken &lt;00:12.30&gt; fallen weg, <c>[offset:ms]</c> verschiebt
    /// alle Zeiten. Das Ergebnis ist nach Zeit sortiert. Ohne eine einzige Zeitmarke wird der Text als einfacher Text gelesen.
    /// </summary>
    public static Lyrics Parse(string text, string source)
    {
        if (!LooksLikeLrc(text)) return FromPlainText(text, source);

        var lines = new List<LyricLine>();
        var offset = 0.0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            if (InfoTag().Match(line) is { Success: true } info)
            {
                if (info.Groups[1].Value.Equals("offset", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(info.Groups[2].Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
                {
                    offset = ms / 1000;
                }
                continue;
            }

            var matches = TimeTag().Matches(line);
            if (matches.Count == 0) continue; // Zeile ohne Zeitmarke in einer zeitgesteuerten Datei: Kopfzeile oder Rest
            var content = WordTag().Replace(TimeTag().Replace(line, ""), "").Trim();
            foreach (Match match in matches) lines.Add(new LyricLine(Seconds(match) , content));
        }

        // Ein positiver Versatz lässt den Text früher erscheinen (LRC-Konvention).
        var shifted = lines.Select(l => l with { Time = Math.Max(0, l.Time!.Value - offset) }).OrderBy(l => l.Time).ToList();
        return new Lyrics(shifted, source);
    }

    /// <summary>Text ohne Zeitmarken: eine Zeile je Absatz-Zeile, Leerzeilen bleiben als Pausen erhalten (am Rand entfernt).</summary>
    public static Lyrics FromPlainText(string text, string source)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => new LyricLine(null, l.TrimEnd())).ToList();
        while (lines.Count > 0 && lines[0].Text.Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Text.Length == 0) lines.RemoveAt(lines.Count - 1);
        return new Lyrics(lines, source);
    }

    private static double Seconds(Match match)
    {
        var minutes = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var fraction = match.Groups[3].Success ? double.Parse("0." + match.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        return minutes * 60 + seconds + fraction;
    }
}
