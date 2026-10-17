using System.IO;
using System.Windows.Media;

namespace MediaCenter.App.Theming;

/// <summary>Ein Farbstil: Akzent und Gegenakzent, je für dunkle und helle Oberfläche abgestimmt.</summary>
public sealed record AccentPalette(string Id, string Name, Color DarkAccent, Color DarkAccent2, Color LightAccent, Color LightAccent2);

public static class Palettes
{
    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static IReadOnlyList<AccentPalette> All { get; } = new[]
    {
        new AccentPalette("signal", "Signal", C("#22d3ee"), C("#ff7847"), C("#0369a1"), C("#d4531d")),
        new AccentPalette("bernstein", "Bernstein", C("#fbbf24"), C("#f87171"), C("#b45309"), C("#be123c")),
        new AccentPalette("violett", "Violett", C("#a78bfa"), C("#f472b6"), C("#6d28d9"), C("#be185d")),
        new AccentPalette("wald", "Wald", C("#34d399"), C("#fbbf24"), C("#047857"), C("#b45309")),
        new AccentPalette("rose", "Rose", C("#fb7185"), C("#fdba74"), C("#be123c"), C("#c2410c")),
        new AccentPalette("graphit", "Graphit", C("#e5e7eb"), C("#9ca3af"), C("#111827"), C("#4b5563")),
    };

    public static AccentPalette Get(string id) =>
        All.FirstOrDefault(p => p.Id == id)
        ?? throw new InvalidDataException($"Unbekannter Farbstil '{id}'. Gültig: {string.Join(", ", All.Select(p => p.Id))}.");
}
