using System.Globalization;

namespace MediaCenter.Core;

/// <summary>
/// Farbeinstellungen der laufenden Wiedergabe. Alle Regler gehen von −100 bis 100, 0 ändert nichts.
/// Helligkeit bis Gamma setzt mpv selbst; Weißabgleich, Schärfe und Invertieren laufen über einen kleinen Shader
/// (mpvs eigene Option sharpen wirkt unter gpu-next nicht).
/// </summary>
/// <param name="Brightness">mpv: brightness.</param>
/// <param name="Contrast">mpv: contrast.</param>
/// <param name="Saturation">mpv: saturation.</param>
/// <param name="Hue">Farbton, mpv: hue.</param>
/// <param name="Gamma">mpv: gamma.</param>
/// <param name="Temperature">Weißabgleich: negativ kühler (blauer), positiv wärmer (röter).</param>
/// <param name="Tint">Weißabgleich: negativ grüner, positiv magentafarbener.</param>
/// <param name="Sharpness">Positiv schärfer, negativ weicher (Unsharp-Mask im Shader).</param>
/// <param name="Invert">Farben umkehren (Negativ).</param>
/// <param name="Lut">Name der LUT aus der <see cref="LutStore"/>-Sammlung, null = keine.</param>
/// <param name="LutStrength">Wie stark die LUT wirkt, 0 bis 100 Prozent.</param>
public sealed record ColorSettings(
    int Brightness = 0,
    int Contrast = 0,
    int Saturation = 0,
    int Hue = 0,
    int Gamma = 0,
    int Temperature = 0,
    int Tint = 0,
    int Sharpness = 0,
    bool Invert = false,
    string? Lut = null,
    int LutStrength = 100)
{
    public const int Min = -100;
    public const int Max = 100;

    /// <summary>Wie stark ±100 beim Weißabgleich wirken: Rot und Blau (Temperatur) bzw. Grün (Tönung) um bis zu 30 %.</summary>
    public const double WhiteBalanceStrength = 0.3;

    public static readonly ColorSettings Default = new();

    public const int LutStrengthMax = 100;

    public bool IsDefault => this == Default;

    /// <summary>Weißabgleich, Schärfe und Invertieren brauchen den Shader; ohne alle drei bleibt der Bildaufbau von mpv unverändert.</summary>
    public bool UsesShader => Invert || Temperature != 0 || Tint != 0 || Sharpness != 0;

    /// <summary>Eine LUT ist gewählt; sie läuft in einem eigenen Shader.</summary>
    public bool UsesLut => Lut is not null;

    /// <summary>Wirft bei Werten außerhalb von −100 bis 100 (nie still klemmen).</summary>
    public ColorSettings Validated()
    {
        Check(Brightness, nameof(Brightness));
        Check(Contrast, nameof(Contrast));
        Check(Saturation, nameof(Saturation));
        Check(Hue, nameof(Hue));
        Check(Gamma, nameof(Gamma));
        Check(Temperature, nameof(Temperature));
        Check(Tint, nameof(Tint));
        Check(Sharpness, nameof(Sharpness));
        if (Lut is not null && string.IsNullOrWhiteSpace(Lut)) throw new ArgumentException("Der LUT-Name darf nicht leer sein.", nameof(Lut));
        if (LutStrength is < 0 or > LutStrengthMax) throw new ArgumentOutOfRangeException(nameof(LutStrength), LutStrength, $"{nameof(LutStrength)} zwischen 0 und {LutStrengthMax}.");
        return this;
    }

    private static void Check(int value, string name)
    {
        if (value is < Min or > Max) throw new ArgumentOutOfRangeException(name, value, $"{name} zwischen {Min} und {Max}.");
    }

    // --- Werte für mpv ----------------------------------------------------------------------------------------

    /// <summary>Stärke der Unsharp-Mask im Shader: ±100 entsprechen ±1,5 (negativ = weichzeichnen).</summary>
    public double ShaderSharpen => Sharpness / 100.0 * 1.5;

    /// <summary>Verstärkung je Farbkanal für den Shader: wärmer = mehr Rot und weniger Blau, magentafarbener = weniger Grün.</summary>
    public (double R, double G, double B) WhiteBalanceGains
    {
        get
        {
            var temperature = Temperature / 100.0 * WhiteBalanceStrength;
            var tint = Tint / 100.0 * WhiteBalanceStrength;
            return (1 + temperature, 1 - tint, 1 - temperature);
        }
    }

    /// <summary>Parameterliste für mpv (<c>glsl-shader-opts</c>), Zahlen mit Punkt.</summary>
    public string ShaderOptions
    {
        get
        {
            var (r, g, b) = WhiteBalanceGains;
            string Num(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
            return $"gain_r={Num(r)},gain_g={Num(g)},gain_b={Num(b)},invert={(Invert ? 1 : 0)},sharpen={Num(ShaderSharpen)}";
        }
    }

    /// <summary>Parameter des LUT-Shaders für mpv (<c>glsl-shader-opts</c>): die Stärke von 0 bis 1.</summary>
    public string LutShaderOptions => string.Create(CultureInfo.InvariantCulture, $"lut_strength={LutStrength / 100.0:0.####}");

    /// <summary>
    /// Alle Parameter der geladenen Shader in einer Liste für <c>glsl-shader-opts</c>; was nicht läuft, bleibt draußen
    /// (mpv bekäme sonst Parameter eines Shaders, den es nicht geladen hat).
    /// </summary>
    public string AllShaderOptions => string.Join(',', new[] { UsesShader ? ShaderOptions : null, UsesLut ? LutShaderOptions : null }.OfType<string>());
}
