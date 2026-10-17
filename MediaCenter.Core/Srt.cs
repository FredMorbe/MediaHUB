using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MediaCenter.Core;

/// <param name="Text">Zeilen mit \n getrennt, ohne abschließenden Umbruch.</param>
public sealed record SrtCue(int Index, TimeSpan Start, TimeSpan End, string Text);

/// <summary>Liest und schreibt SubRip-Dateien (.srt).</summary>
public static partial class Srt
{
    [GeneratedRegex(@"^\s*(?:(\d{1,3}):)?(\d{1,2}):(\d{1,2})[,.](\d{1,3})\s*-->\s*(?:(\d{1,3}):)?(\d{1,2}):(\d{1,2})[,.](\d{1,3})")]
    private static partial Regex TimingRegex();

    /// <summary>
    /// Zerlegt SRT-Text in Einträge. Ein Block ohne gültige Zeitangabe ist ein Fehler (mit Zeilennummer), kein stilles Überspringen;
    /// leere Einträge bleiben erhalten, damit die Zeiten der übrigen unverändert sind. Die Nummern werden fortlaufend neu vergeben.
    /// </summary>
    public static IReadOnlyList<SrtCue> Parse(string content)
    {
        var lines = content.TrimStart('﻿').Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        var cues = new List<SrtCue>();
        var i = 0;
        while (i < lines.Length)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                i++;
                continue;
            }

            var blockStart = i;
            var timingLine = i;
            // Optionale Nummer vor der Zeitangabe.
            if (!TimingRegex().IsMatch(lines[timingLine]) && int.TryParse(lines[timingLine].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _)) timingLine++;
            if (timingLine >= lines.Length || TimingRegex().Match(lines[timingLine]) is not { Success: true } timing)
            {
                throw new InvalidDataException($"Keine gültige SRT-Zeitangabe in Zeile {blockStart + 1}: „{Shorten(lines[Math.Min(timingLine, lines.Length - 1)])}“.");
            }

            var text = new List<string>();
            i = timingLine + 1;
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
            {
                // Fehlende Leerzeile vor dem nächsten Eintrag („2“ und dann eine Zeitangabe): hier endet der Text.
                if (i + 1 < lines.Length && int.TryParse(lines[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _) && TimingRegex().IsMatch(lines[i + 1])) break;
                text.Add(lines[i].TrimEnd());
                i++;
            }
            cues.Add(new SrtCue(cues.Count + 1, TimeOf(timing, 1), TimeOf(timing, 5), string.Join('\n', text)));
        }

        if (cues.Count == 0) throw new InvalidDataException("Die Untertiteldatei enthält keine Einträge.");
        return cues;
    }

    public static string Format(IEnumerable<SrtCue> cues)
    {
        var builder = new StringBuilder();
        var number = 0;
        foreach (var cue in cues)
        {
            builder.Append(++number).Append("\r\n")
                .Append(Time(cue.Start)).Append(" --> ").Append(Time(cue.End)).Append("\r\n")
                .Append(cue.Text.Replace("\n", "\r\n")).Append("\r\n\r\n");
        }
        return builder.ToString();
    }

    private static TimeSpan TimeOf(Match match, int firstGroup)
    {
        int Number(int group) => match.Groups[group].Success ? int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture) : 0;
        // „,5“ heißt 500 ms, nicht 5: die Millisekunden sind ein Dezimalbruch.
        var fraction = match.Groups[firstGroup + 3].Value.PadRight(3, '0');
        return new TimeSpan(0, Number(firstGroup), Number(firstGroup + 1), Number(firstGroup + 2), int.Parse(fraction, CultureInfo.InvariantCulture));
    }

    private static string Time(TimeSpan time) =>
        $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00},{time.Milliseconds:000}";

    private static string Shorten(string line) => line.Length <= 60 ? line : line[..60] + "…";
}
