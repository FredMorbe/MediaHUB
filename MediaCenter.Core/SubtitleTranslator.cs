using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaCenter.Core;

/// <summary>Eine Übersetzung ist nicht möglich oder das Modell liefert dauerhaft Unbrauchbares. Die Meldung nennt den Eintrag.</summary>
public sealed class TranslationException(string message) : Exception(message);

/// <param name="DoneCues">Einträge, die fertig sind (auch die, die nichts zu übersetzen hatten).</param>
public sealed record TranslationProgress(int DoneCues, int TotalCues);

/// <param name="BlockCues">Einträge pro Anfrage; mehr Kontext je Anfrage, aber länger bis zur Antwort und mehr Fehlerquellen.</param>
/// <param name="ContextCues">Davorliegende Einträge samt Übersetzung, die das Modell zur Orientierung bekommt.</param>
/// <param name="MaxAttempts">Versuche je Block, bevor er halbiert wird.</param>
/// <param name="SourceLanguage">Englischer Name der Ausgangssprache; null lässt das Modell sie selbst erkennen.</param>
public sealed record TranslationOptions(int BlockCues = 30, int ContextCues = 4, int MaxAttempts = 3, int MaxBlockChars = 2500, string? SourceLanguage = null);

/// <summary>
/// Übersetzt Untertitel mit einem Chatmodell. Zeiten und Nummern gehen nie durch das Modell, es sieht nur den Text; die Datei
/// wird danach aus den unveränderten Zeiten und den übersetzten Texten neu zusammengesetzt. Lange Dateien werden in Blöcke
/// zerlegt, jeder geprüft (Anzahl, Formatierung, Zielsprache) und bei Mängeln erneut gefragt oder halbiert. Ein Zwischenstand
/// auf der Platte erlaubt das Fortsetzen nach Abbruch.
/// </summary>
public sealed partial class SubtitleTranslator(IChatModel model)
{
    private const string Schema = """
        {"type":"object","properties":{"items":{"type":"array","items":{"type":"object","properties":{"id":{"type":"integer"},"text":{"type":"string"}},"required":["id","text"]}}},"required":["items"]}
        """;

    [GeneratedRegex(@"<[^>]+>|\{[^}]*\}")]
    private static partial Regex TagRegex();

    private sealed record Context(string Source, string Translation);

    private sealed record Checkpoint(string Key, Dictionary<int, string> Done);

    public async Task<IReadOnlyList<SrtCue>> TranslateAsync(
        IReadOnlyList<SrtCue> cues, TranslationTarget target, TranslationOptions? options = null,
        IProgress<TranslationProgress>? progress = null, string? checkpointPath = null, CancellationToken ct = default)
    {
        options ??= new TranslationOptions();
        var key = KeyOf(cues, target, options);
        var done = LoadCheckpoint(checkpointPath, key);
        var work = cues.Where(HasWords).ToList();
        var translated = new Dictionary<int, string>(done);

        progress?.Report(new TranslationProgress(cues.Count - work.Count(c => !translated.ContainsKey(c.Index)), cues.Count));
        foreach (var block in Blocks(work, options))
        {
            ct.ThrowIfCancellationRequested();
            if (block.All(c => translated.ContainsKey(c.Index))) continue;

            var context = ContextBefore(work, block[0], translated, options.ContextCues);
            foreach (var (index, text) in await TranslateBlockAsync(block, context, target, options, ct)) translated[index] = text;
            SaveCheckpoint(checkpointPath, new Checkpoint(key, translated));
            progress?.Report(new TranslationProgress(cues.Count - work.Count(c => !translated.ContainsKey(c.Index)), cues.Count));
        }

        var result = cues.Select(c => translated.TryGetValue(c.Index, out var text) ? c with { Text = Wrap(text, target) } : c).ToList();
        if (checkpointPath is not null && File.Exists(checkpointPath)) File.Delete(checkpointPath);
        return result;
    }

    // --- Blöcke --------------------------------------------------------------------------------------------

    private static IEnumerable<List<SrtCue>> Blocks(List<SrtCue> work, TranslationOptions options)
    {
        var block = new List<SrtCue>();
        var chars = 0;
        foreach (var cue in work)
        {
            if (block.Count > 0 && (block.Count >= options.BlockCues || chars + cue.Text.Length > options.MaxBlockChars))
            {
                yield return block;
                block = new List<SrtCue>();
                chars = 0;
            }
            block.Add(cue);
            chars += cue.Text.Length;
        }
        if (block.Count > 0) yield return block;
    }

    private static List<Context> ContextBefore(List<SrtCue> work, SrtCue first, Dictionary<int, string> translated, int count)
    {
        var position = work.IndexOf(first);
        return work.Take(position).Reverse()
            .Where(c => translated.ContainsKey(c.Index)).Take(count).Reverse()
            .Select(c => new Context(c.Text, translated[c.Index])).ToList();
    }

    /// <summary>Übersetzt den Block; schlägt die Prüfung dauerhaft fehl, wird er halbiert (bis herunter zu einem einzelnen Eintrag).</summary>
    private async Task<Dictionary<int, string>> TranslateBlockAsync(
        List<SrtCue> block, List<Context> context, TranslationTarget target, TranslationOptions options, CancellationToken ct)
    {
        string? problem = null;
        for (var attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var answer = await model.ChatJsonAsync(SystemPrompt(target, options), UserMessage(block, context, problem), Schema, ct);
            var (result, found) = Validate(block, answer, target);
            if (result is not null) return result;

            problem = found; // geht beim nächsten Versuch mit in die Anfrage (bei Temperatur 0 käme sonst dieselbe Antwort)
        }

        if (block.Count == 1) throw new TranslationException($"Eintrag {block[0].Index} („{Short(block[0].Text)}“) ließ sich nicht übersetzen: {problem}");

        var half = block.Count / 2;
        var left = await TranslateBlockAsync(block[..half], context, target, options, ct);
        var leftContext = context.Concat(block[..half].Select(c => new Context(c.Text, left[c.Index]))).TakeLast(options.ContextCues).ToList();
        var right = await TranslateBlockAsync(block[half..], leftContext, target, options, ct);
        foreach (var (index, text) in right) left[index] = text;
        return left;
    }

    private static string SystemPrompt(TranslationTarget target, TranslationOptions options) => $$"""
        You are a professional subtitle translator for films and series.
        Translate the "text" of every item from {{options.SourceLanguage ?? "the source language (detect it yourself)"}} into {{target.PromptName}}.
        Rules:
        - Translate the meaning as natural spoken dialogue in subtitle style. Keep it concise.
        - Return exactly one item for every input item, with the same "id". Never merge, split, skip or add items.
        - Keep formatting tags such as <i>, </i>, <b>, {\an8} exactly as in the source, and keep line breaks (\n) where the source has them, for example dialogue dashes.
        - "previous" lists the preceding lines with their translations. Use it only for context and consistency (names, terms, tone). Do not output it.
        - Keep names of people and places consistent. Do not add notes, explanations or quotation marks.
        {{target.Rules}}
        - Respond only with JSON: {"items":[{"id":1,"text":"..."}]}
        """;

    private static string UserMessage(List<SrtCue> block, List<Context> context, string? problem)
    {
        var message = new Dictionary<string, object?>
        {
            ["previous"] = context.Select(c => new { source = c.Source, translation = c.Translation }),
            ["items"] = block.Select((c, i) => new { id = i + 1, text = c.Text }),
        };
        if (problem is not null)
        {
            message["problem"] = $"Your previous answer was rejected: {problem}. Answer again with exactly {block.Count} items (ids 1 to {block.Count}).";
        }
        return JsonSerializer.Serialize(message, OllamaClient.JsonOptions);
    }

    // --- Prüfung -------------------------------------------------------------------------------------------

    /// <summary>Gibt die Texte nach Eintragsnummer zurück oder das Problem, das die Antwort unbrauchbar macht.</summary>
    private static (Dictionary<int, string>? Result, string? Problem) Validate(List<SrtCue> block, string answer, TranslationTarget target)
    {
        List<(int Id, string Text)> items;
        try
        {
            using var doc = JsonDocument.Parse(answer);
            items = doc.RootElement.GetProperty("items").EnumerateArray()
                .Select(e => (e.GetProperty("id").GetInt32(), e.GetProperty("text").GetString() ?? ""))
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return (null, "the answer was not the requested JSON");
        }

        if (items.Count != block.Count) return (null, $"it had {items.Count} items instead of {block.Count}");
        if (!items.Select(i => i.Id).Order().SequenceEqual(Enumerable.Range(1, block.Count))) return (null, $"the ids must be 1 to {block.Count}, each exactly once");

        var result = new Dictionary<int, string>();
        foreach (var (id, raw) in items)
        {
            var source = block[id - 1];
            var text = raw.Replace("\r\n", "\n").Replace("\\n", "\n").Trim();
            if (text.Length == 0) return (null, $"item {id} was empty");
            if (text.Length > source.Text.Length * 4 + 40) return (null, $"item {id} was far longer than its source");
            if (!Tags(source.Text).Order().SequenceEqual(Tags(text).Order())) return (null, $"item {id} changed the formatting tags of the source ({string.Join(" ", Tags(source.Text))})");
            if (target.Check(text) is { } wrong) return (null, $"item {id} {wrong}");

            result[source.Index] = text;
        }
        return (result, null);
    }

    private static List<string> Tags(string text) => TagRegex().Matches(text).Select(m => m.Value).ToList();

    /// <summary>Hat der Eintrag etwas zu übersetzen? Reine Zeichen wie „♪“ oder „...“ bleiben, wie sie sind.</summary>
    private static bool HasWords(SrtCue cue) => TagRegex().Replace(cue.Text, "").Any(char.IsLetter);

    private static string Short(string text)
    {
        var line = text.Replace('\n', ' ');
        return line.Length <= 40 ? line : line[..40] + "…";
    }

    // --- Zeilenumbruch -------------------------------------------------------------------------------------

    /// <summary>Verteilt eine zu lange einzeilige Übersetzung auf zwei Zeilen (deutsche Texte werden länger als englische).</summary>
    internal static string Wrap(string text, TranslationTarget target)
    {
        if (text.Contains('\n')) return text;

        var visible = TagRegex().Replace(text, "");
        if (visible.Length <= target.LineLimit) return text;

        var cjk = visible.Any(TranslationTarget.IsCjk);
        var middle = text.Length / 2;
        var positions = Enumerable.Range(1, text.Length - 1).Where(i => !InsideTag(text, i)).ToList();
        var candidates = positions.Where(i => text[i] == ' ' || (cjk && "，。？！、；：".Contains(text[i - 1]))).ToList();
        // Chinesisch ohne Satzzeichen: irgendwo trennen, nur nicht in einem lateinischen Wort/einer Zahl und nicht vor einem Satzzeichen.
        if (candidates.Count == 0 && cjk) candidates = positions.Where(i => !(IsAsciiWordChar(text[i - 1]) && IsAsciiWordChar(text[i])) && !ClosingPunctuation.Contains(text[i])).ToList();
        if (candidates.Count == 0) return text;

        var at = candidates.MinBy(i => Math.Abs(i - middle));
        return text[..at].TrimEnd() + "\n" + text[at..].TrimStart();
    }

    private const string ClosingPunctuation = "，。？！、；：,.?!;:)]）」』";

    private static bool IsAsciiWordChar(char c) => char.IsAsciiLetterOrDigit(c);

    private static bool InsideTag(string text, int index)
    {
        var angle = text.LastIndexOf('<', index - 1);
        if (angle >= 0 && text.IndexOf('>', angle) >= index) return true;

        var brace = text.LastIndexOf('{', index - 1);
        return brace >= 0 && text.IndexOf('}', brace) >= index;
    }

    // --- Zwischenstand -------------------------------------------------------------------------------------

    /// <summary>Gleiche Datei, Zielsprache, Modell und Einstellungen = derselbe Auftrag; sonst darf ein alter Zwischenstand nicht gelten.</summary>
    private string KeyOf(IReadOnlyList<SrtCue> cues, TranslationTarget target, TranslationOptions options)
    {
        var text = $"{target.Id}|{model.Name}|{options}|" + string.Join('\u001f', cues.Select(c => $"{c.Start.Ticks}{c.Text}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static Dictionary<int, string> LoadCheckpoint(string? path, string key)
    {
        if (path is null || !File.Exists(path)) return new Dictionary<int, string>();
        try
        {
            var saved = JsonSerializer.Deserialize<Checkpoint>(File.ReadAllText(path), OllamaClient.JsonOptions)
                ?? throw new InvalidDataException("Datei ist leer.");
            return saved.Key == key ? saved.Done : new Dictionary<int, string>(); // anderer Auftrag: von vorn
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new InvalidDataException($"Der Zwischenstand der Übersetzung ist beschädigt und muss gelöscht werden: {path} ({ex.Message})", ex);
        }
    }

    private static void SaveCheckpoint(string? path, Checkpoint checkpoint)
    {
        if (path is null) return;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(checkpoint, OllamaClient.JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}
