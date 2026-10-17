namespace MediaCenter.Core;

/// <summary>Eine Sprache (oder Schreibweise), in die Untertitel übersetzt werden können.</summary>
/// <param name="Id">Stabiler Schlüssel („de“, „zh-Hant“); steht auch im Zwischenstand.</param>
/// <param name="PromptName">Wie das Modell die Zielsprache genannt bekommt (englisch, eindeutig).</param>
/// <param name="Rules">Zusätzliche Anweisungen für das Modell, je Zeile eine.</param>
/// <param name="LineLimit">Ab dieser Länge (in Zeichen) wird eine einzeilige Übersetzung auf zwei Zeilen verteilt.</param>
/// <param name="Check">Liefert ein Problem, wenn die Übersetzung nicht in der Zielsprache ist; sonst null.</param>
public sealed record TranslationTarget(
    string Id, string DisplayName, string PromptName, Language Language, string FileLabel, string Rules, int LineLimit,
    Func<string, string?> Check)
{
    // Zeichen, die nur in der vereinfachten Schreibweise vorkommen (die traditionelle Form ist eine andere). Modelle wie Qwen
    // rutschen trotz Anweisung gern ins Vereinfachte; schon ein Treffer macht die Antwort ungültig.
    private const string SimplifiedOnly =
        "们这个来说时会对过还没与为现样吗么见请谢让点开关门问间听语话觉爱学长东车书买卖钱热电视观实发应经动马鸟鱼龙国儿总条种当将头体难带离记认";

    public static TranslationTarget German { get; } = new(
        "de", "Deutsch", "German", Languages.Get("de"), "KI-Übersetzung",
        "- Write natural German as in a good dubbing. Use \"du\" for informal talk between friends or family and \"Sie\" only when the situation is formal. Use correct spelling (ä, ö, ü, ß).",
        LineLimit: 42,
        Check: text => text.Any(IsCjk) ? "enthält chinesische Zeichen statt deutschem Text" : null);

    public static TranslationTarget TraditionalChinese { get; } = new(
        "zh-Hant", "Chinesisch (traditionell)", "Traditional Chinese (繁體中文, as written in Taiwan)", Languages.Get("zh"), "KI-Übersetzung (traditionell)",
        "- Write Traditional Chinese (繁體中文) as used in Taiwan, with traditional characters only. Never use Simplified characters. Use full-width Chinese punctuation (，。？！). Render names of people consistently.",
        LineLimit: 18,
        Check: CheckTraditional);

    public static IReadOnlyList<TranslationTarget> All { get; } = new[] { German, TraditionalChinese };

    public static TranslationTarget Get(string id) =>
        All.FirstOrDefault(t => t.Id == id) ?? throw new ArgumentException($"Unbekannte Zielsprache „{id}“.", nameof(id));

    private static string? CheckTraditional(string text)
    {
        var index = text.IndexOfAny(SimplifiedOnly.ToCharArray());
        return index < 0 ? null : $"enthält vereinfachte Schriftzeichen („{text[index]}“)";
    }

    public static bool IsCjk(char c) => c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿';
}
