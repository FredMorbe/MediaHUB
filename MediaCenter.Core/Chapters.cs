using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaCenter.Core;

/// <summary>Ein Kapitel einer Videodatei (mpv: Eintrag der Property chapter-list).</summary>
/// <param name="Title">Titel aus der Datei; leer, wenn die Datei keinen hat.</param>
public sealed record Chapter(int Index, string Title, double Start)
{
    /// <summary>Anzeigename: der Titel der Datei, sonst „Kapitel N“.</summary>
    public string Label => string.IsNullOrWhiteSpace(Title) ? $"Kapitel {Index + 1}" : Title.Trim();
}

/// <summary>Kapitelliste eines Videos und das Vorspann-Kapitel darin (für „Intro überspringen“).</summary>
public static partial class ChapterList
{
    /// <summary>Sprung, wenn die Datei kein Vorspann-Kapitel hat (übliche Länge eines Serienvorspanns samt Logos).</summary>
    public const double DefaultIntroSkipSeconds = 85;

    /// <summary>Kapitel, deren Titel so aussieht, gelten als Vorspann: „Intro“, „Opening“, „OP“, „Vorspann“, „Opening Credits“, „Title Sequence“ …</summary>
    [GeneratedRegex(@"^\s*(intro(duction)?|vorspann|titelsong|titelmusik|op|opening(\s+(credits|theme|titles?|sequence))?|title\s+(sequence|credits))(\s*\d+)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IntroTitle();

    /// <summary>Liest die chapter-list von mpv: ein Array aus Objekten mit „title“ und „time“. Die Kapitel kommen nach Zeit sortiert.</summary>
    /// <exception cref="JsonException">Ein Eintrag hat keine gültige Zeit.</exception>
    public static IReadOnlyList<Chapter> Parse(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array) throw new JsonException($"chapter-list ist kein Array, sondern {array.ValueKind}.");
        var chapters = new List<(string Title, double Start)>();
        foreach (var entry in array.EnumerateArray())
        {
            if (!entry.TryGetProperty("time", out var time) || time.ValueKind != JsonValueKind.Number)
                throw new JsonException("Ein Kapitel in chapter-list hat keine Zeit.");
            var title = entry.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            chapters.Add((title, time.GetDouble()));
        }
        return chapters.OrderBy(c => c.Start).Select((c, i) => new Chapter(i, c.Title, c.Start)).ToList();
    }

    /// <summary>Index des Kapitels, in dem <paramref name="position"/> liegt; -1 vor dem ersten Kapitel oder ohne Kapitel.</summary>
    public static int IndexAt(IReadOnlyList<Chapter> chapters, double position)
    {
        var index = -1;
        for (var i = 0; i < chapters.Count; i++)
        {
            if (chapters[i].Start <= position) index = i;
            else break;
        }
        return index;
    }

    /// <summary>Wer nach dem Beginn eines Kapitels weniger als so viele Sekunden weiter ist, springt mit „Vorheriges Kapitel“ zum vorigen, sonst an den Anfang dieses (wie im DVD-Player).</summary>
    public const double RestartThresholdSeconds = 3;

    /// <summary>Beginn des nächsten Kapitels nach <paramref name="position"/>; null im letzten Kapitel.</summary>
    public static double? NextStart(IReadOnlyList<Chapter> chapters, double position) =>
        chapters.FirstOrDefault(c => c.Start > position + 0.001) is { } next ? next.Start : null;

    /// <summary>Beginn des Kapitels, in dem man steht, wenn man schon etwas weiter ist, sonst des vorigen; null vor dem ersten Kapitel.</summary>
    public static double? PreviousStart(IReadOnlyList<Chapter> chapters, double position)
    {
        var index = IndexAt(chapters, position);
        if (index < 0) return null;
        if (position - chapters[index].Start > RestartThresholdSeconds || index == 0) return chapters[index].Start;
        return chapters[index - 1].Start;
    }

    /// <summary>Das Vorspann-Kapitel und sein Ende (Beginn des nächsten Kapitels, sonst <paramref name="duration"/>); null, wenn keins so heißt oder das Ende unbekannt ist.</summary>
    public static (double Start, double End)? FindIntro(IReadOnlyList<Chapter> chapters, double duration)
    {
        foreach (var chapter in chapters)
        {
            if (!IntroTitle().IsMatch(chapter.Title)) continue;
            var next = chapters.FirstOrDefault(c => c.Index == chapter.Index + 1);
            var end = next?.Start ?? duration;
            if (end > chapter.Start) return (chapter.Start, end);
        }
        return null;
    }
}
