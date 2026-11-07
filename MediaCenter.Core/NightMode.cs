using System.Globalization;

namespace MediaCenter.Core;

/// <summary>
/// Nachtmodus für Filme: Action wird leiser, Sprache bleibt verständlich. Zwei Schritte: Mehrkanalton wird auf Stereo gemischt, wobei der
/// Center-Kanal (dort liegt die Sprache) um 6 dB angehoben wird (die normale Mischung drückt ihn herunter), danach ein Kompressor und ein Begrenzer.
/// Die Stärke (0 bis 100 %) stellt Schwelle, Verhältnis, Ausgleichsverstärkung und Begrenzer stufenlos ein; 0 lässt den Ton unberührt.
/// Gemessen an einem Testsignal (Sprache −28 dB, Action −12 dB RMS): Bei 33 %, 67 % und 100 % sank die Action um 6, 7 und 10 dB, die Sprache blieb bei
/// etwa −25 dB; bei 5.1 stieg die Sprache zusätzlich um etwa 6 dB.
/// </summary>
public static class NightMode
{
    public const int Off = 0;
    public const int Max = 100;
    public const int Step = 5;

    // center_mix_level hebt den Center an, rematrix_maxval verhindert, dass die Mischung deshalb alles andere herunterregelt.
    private const string Downmix = "aresample=out_chlayout=stereo:center_mix_level=2.0:rematrix_maxval=2.0";

    /// <summary>Stützpunkte der Kompression bei 0 %, 33 %, 67 % und 100 %; dazwischen wird linear gemischt. Bei 0 % wirkt der Kompressor noch nicht (Verhältnis 1).</summary>
    private static readonly (double Threshold, double Ratio, double Attack, double Release, double Makeup, double Limit)[] Anchors =
    {
        (-18, 1, 10, 300, 0, 0.90),
        (-24, 2, 10, 300, 4, 0.80),
        (-26, 3, 8, 300, 7, 0.75),
        (-32, 5, 5, 250, 11, 0.70),
    };

    /// <summary>mpv-Teilkette (Filter durch Komma getrennt, ohne <c>lavfi=[…]</c>); null bei 0 %.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Stärke außerhalb von 0 bis 100.</exception>
    public static string? Filter(int strength)
    {
        Validated(strength);
        if (strength == Off) return null;

        var position = (double)strength / Max * (Anchors.Length - 1);
        var lower = Math.Min((int)position, Anchors.Length - 2);
        var t = position - lower;
        double Mix(Func<(double Threshold, double Ratio, double Attack, double Release, double Makeup, double Limit), double> pick) =>
            pick(Anchors[lower]) + (pick(Anchors[lower + 1]) - pick(Anchors[lower])) * t;

        // alimiter ohne level=0 hebt den Pegel selbständig an und würde die Stärke verfälschen.
        return string.Create(CultureInfo.InvariantCulture,
            $"{Downmix},acompressor=threshold={Mix(a => a.Threshold):0.#}dB:ratio={Mix(a => a.Ratio):0.##}:attack={Mix(a => a.Attack):0}:release={Mix(a => a.Release):0}" +
            $":makeup={Mix(a => a.Makeup):0.#}dB,alimiter=limit={Mix(a => a.Limit):0.##}:level=0");
    }

    /// <summary>„Aus“ oder „60 %“.</summary>
    public static string Label(int strength) => strength == Off ? "Aus" : $"{strength} %";

    /// <summary>Gibt <paramref name="strength"/> zurück, wenn sie zwischen 0 und 100 liegt; sonst eine Ausnahme (kaputte Einstellungen sollen laut auffallen).</summary>
    public static int Validated(int strength) =>
        strength is >= Off and <= Max ? strength : throw new ArgumentOutOfRangeException(nameof(strength), strength, $"Der Nachtmodus geht von {Off} bis {Max} Prozent.");
}
