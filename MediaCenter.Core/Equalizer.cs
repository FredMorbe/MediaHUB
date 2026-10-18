using System.Globalization;
using System.Text;

namespace MediaCenter.Core;

/// <summary>Eine Klang-Voreinstellung: eine Verstärkung in dB je Band von <see cref="Equalizer.Bands"/>.</summary>
public sealed record EqualizerPreset(string Id, string Name, IReadOnlyList<int> Gains);

/// <summary>
/// Zehn-Band-Equalizer für mpv. Aus den Verstärkungen (dB) entsteht ein Audiofilter (<c>lavfi</c> mit <c>equalizer</c> je Band), den Musik und Video
/// gleichermaßen bekommen. Reine Berechnung, kein mpv.
/// </summary>
public static class Equalizer
{
    public const int MinGain = -12;

    public const int MaxGain = 12;

    /// <summary>Mittenfrequenzen in Hz, eine Oktave breit.</summary>
    public static IReadOnlyList<int> Bands { get; } = new[] { 32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

    public static string Flat { get; } = "flat";

    public static IReadOnlyList<EqualizerPreset> Presets { get; } = new[]
    {
        new EqualizerPreset("flat", "Aus", new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
        new EqualizerPreset("bass", "Bass", new[] { 6, 5, 4, 2, 0, 0, 0, 0, 0, 0 }),
        new EqualizerPreset("treble", "Höhen", new[] { 0, 0, 0, 0, 0, 1, 2, 4, 5, 6 }),
        new EqualizerPreset("vocal", "Stimme", new[] { -2, -2, -1, 1, 3, 4, 3, 1, 0, -1 }),
        new EqualizerPreset("loudness", "Loudness", new[] { 5, 4, 2, 0, -1, -1, 0, 2, 4, 5 }),
        new EqualizerPreset("rock", "Rock", new[] { 4, 3, 2, -1, -2, -1, 2, 3, 4, 4 }),
    };

    public static EqualizerPreset PresetById(string id) =>
        Presets.FirstOrDefault(p => p.Id == id) ?? throw new ArgumentException($"Unbekannte Klang-Voreinstellung „{id}“.", nameof(id));

    /// <summary>Wirft bei falscher Anzahl oder Werten außerhalb von ±12 dB (nie still klemmen).</summary>
    public static IReadOnlyList<int> Validated(IReadOnlyList<int> gains)
    {
        if (gains.Count != Bands.Count) throw new ArgumentException($"Der Equalizer hat {Bands.Count} Bänder, übergeben wurden {gains.Count}.", nameof(gains));
        for (var i = 0; i < gains.Count; i++)
        {
            if (gains[i] < MinGain || gains[i] > MaxGain)
                throw new ArgumentOutOfRangeException(nameof(gains), gains[i], $"Band {Bands[i]} Hz: Verstärkung zwischen {MinGain} und {MaxGain} dB.");
        }
        return gains;
    }

    /// <summary>Id der Voreinstellung mit genau diesen Werten, sonst „custom“.</summary>
    public static string MatchingPreset(IReadOnlyList<int> gains) =>
        Presets.FirstOrDefault(p => p.Gains.SequenceEqual(gains))?.Id ?? "custom";

    /// <summary>
    /// Der mpv-Audiofilter für diese Verstärkungen; null, wenn alles 0 ist (kein Filter, also kein Eingriff in den Klang).
    /// Vor den Bändern senkt ein Vorverstärker um die größte Anhebung, damit der Pegel nicht übersteuert.
    /// </summary>
    public static string? ToMpvFilter(IReadOnlyList<int> gains)
    {
        Validated(gains);
        var filters = new List<string>();
        var peak = gains.Max();
        if (peak > 0) filters.Add($"volume=-{peak}dB");
        for (var i = 0; i < gains.Count; i++)
        {
            if (gains[i] == 0) continue;
            filters.Add(string.Create(CultureInfo.InvariantCulture, $"equalizer=f={Bands[i]}:t=o:w=1:g={gains[i]}"));
        }
        if (filters.Count == 0) return null;

        var text = new StringBuilder("lavfi=[").AppendJoin(',', filters).Append(']');
        return text.ToString();
    }
}
