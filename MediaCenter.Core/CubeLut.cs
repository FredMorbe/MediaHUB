using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace MediaCenter.Core;

/// <summary>
/// Eine 3D-LUT im Adobe-/Resolve-Format <c>.cube</c> und der mpv-Shader daraus. Der Shader legt die LUT als 3D-Textur ab und
/// schlägt jedes Pixel im Hook OUTPUT nach (trilinear, auf der Grafikkarte, also in Echtzeit).
/// </summary>
public sealed class CubeLut
{
    /// <summary>Größere LUTs gibt es praktisch nicht (üblich 17, 33, 65); der Shader-Text würde sonst viele MB groß.</summary>
    public const int MaxSize = 65;

    /// <summary>Kantenlänge des Würfels (Size × Size × Size Einträge).</summary>
    public int Size { get; }

    /// <summary>Ausgabewerte als R, G, B je Eintrag; der Rot-Index läuft am schnellsten, dann Grün, dann Blau (wie in der Datei).</summary>
    public float[] Data { get; }

    /// <summary>Eingabebereich; (0,0,0)–(1,1,1) ist der Normalfall.</summary>
    public (float R, float G, float B) DomainMin { get; }

    public (float R, float G, float B) DomainMax { get; }

    public string? Title { get; }

    private CubeLut(int size, float[] data, (float, float, float) min, (float, float, float) max, string? title)
    {
        Size = size;
        Data = data;
        DomainMin = min;
        DomainMax = max;
        Title = title;
    }

    public static CubeLut Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"LUT-Datei nicht gefunden: {path}", path);
        return Parse(File.ReadAllText(path));
    }

    /// <summary>Liest eine <c>.cube</c>-Datei. Alles, was nicht eindeutig lesbar ist, wirft <see cref="InvalidDataException"/> mit Zeilennummer.</summary>
    public static CubeLut Parse(string text)
    {
        var size = 0;
        string? title = null;
        (float, float, float) min = (0, 0, 0), max = (1, 1, 1);
        float[]? data = null;
        var filled = 0;

        var lineNumber = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var keyword = parts[0];

            if (char.IsLetter(keyword[0]))
            {
                switch (keyword)
                {
                    case "TITLE":
                        title = line[keyword.Length..].Trim().Trim('"');
                        break;
                    case "LUT_3D_SIZE":
                        if (data is not null) throw Error(lineNumber, "LUT_3D_SIZE steht nach den Daten.");
                        if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out size) || size < 2)
                            throw Error(lineNumber, "LUT_3D_SIZE braucht eine ganze Zahl ab 2.");
                        if (size > MaxSize) throw Error(lineNumber, $"Die LUT hat Größe {size}; unterstützt wird bis {MaxSize}.");
                        data = new float[size * size * size * 3];
                        break;
                    case "LUT_1D_SIZE":
                        throw Error(lineNumber, "1D-LUTs werden nicht unterstützt, nur 3D-LUTs (LUT_3D_SIZE).");
                    case "DOMAIN_MIN":
                        min = ReadTriple(parts, lineNumber);
                        break;
                    case "DOMAIN_MAX":
                        max = ReadTriple(parts, lineNumber);
                        break;
                    case "LUT_3D_INPUT_RANGE": // Adobe-Schreibweise für einen gemeinsamen Bereich aller Kanäle
                        if (parts.Length != 3) throw Error(lineNumber, "Erwartet werden zwei Zahlen.");
                        var (low, _, _) = ReadTriple([parts[0], parts[1], parts[1], parts[1]], lineNumber);
                        var (high, _, _) = ReadTriple([parts[0], parts[2], parts[2], parts[2]], lineNumber);
                        min = (low, low, low);
                        max = (high, high, high);
                        break;
                    default:
                        throw Error(lineNumber, $"Unbekannte Angabe „{keyword}“.");
                }
                continue;
            }

            if (data is null) throw Error(lineNumber, "Daten vor LUT_3D_SIZE.");
            if (filled == data.Length) throw Error(lineNumber, $"Mehr Einträge als {size}³.");
            var (r, g, b) = ReadTriple(parts, lineNumber, expectKeyword: false);
            data[filled++] = r;
            data[filled++] = g;
            data[filled++] = b;
        }

        if (data is null) throw new InvalidDataException("Keine 3D-LUT: LUT_3D_SIZE fehlt.");
        if (filled != data.Length) throw new InvalidDataException($"Die LUT ist unvollständig: {filled / 3} Einträge, erwartet {size * size * size} ({size}³).");
        if (max.Item1 <= min.Item1 || max.Item2 <= min.Item2 || max.Item3 <= min.Item3) throw new InvalidDataException("DOMAIN_MAX muss in allen Kanälen größer als DOMAIN_MIN sein.");

        return new CubeLut(size, data, min, max, string.IsNullOrEmpty(title) ? null : title);
    }

    private static (float, float, float) ReadTriple(string[] parts, int lineNumber, bool expectKeyword = true)
    {
        var offset = expectKeyword ? 1 : 0;
        if (parts.Length != offset + 3) throw Error(lineNumber, "Erwartet werden drei Zahlen.");

        var values = new float[3];
        for (var i = 0; i < 3; i++)
        {
            if (!float.TryParse(parts[offset + i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || !float.IsFinite(values[i]))
                throw Error(lineNumber, $"„{parts[offset + i]}“ ist keine Zahl.");
        }
        return (values[0], values[1], values[2]);
    }

    private static InvalidDataException Error(int lineNumber, string message) => new($"Zeile {lineNumber}: {message}");

    /// <summary>
    /// Der mpv-Shader (gpu-next): die LUT als Textur im Format rgba16hf (Halbfloat, Hex im Shader-Text) und ein Hook, der das Bild
    /// im Verhältnis <c>lut_strength</c> (0 bis 1, per <c>glsl-shader-opts</c>) mit dem Nachschlagewert mischt.
    /// </summary>
    public string ToMpvShader()
    {
        // Kein Eintrag über Halbfloat-Grenzen hinaus: 65504 ist der größte Wert, darüber wäre die Textur unendlich.
        var bytes = new byte[Size * Size * Size * 8];
        for (var i = 0; i < Size * Size * Size; i++)
        {
            for (var c = 0; c < 3; c++)
            {
                var value = Data[i * 3 + c];
                if (Math.Abs(value) > (float)Half.MaxValue) throw new InvalidDataException($"Eintrag {i + 1}: Wert {value.ToString(CultureInfo.InvariantCulture)} liegt außerhalb dessen, was der Shader speichern kann.");
                BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(i * 8 + c * 2), (Half)value);
            }
            BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(i * 8 + 6), (Half)1f);
        }

        string Vec(float r, float g, float b) => string.Create(CultureInfo.InvariantCulture, $"vec3({r:0.######}, {g:0.######}, {b:0.######})");
        var n = Size.ToString(CultureInfo.InvariantCulture);

        var text = new StringBuilder();
        text.Append("//!TEXTURE lut3d\n");
        text.Append($"//!SIZE {n} {n} {n}\n");
        text.Append("//!FORMAT rgba16hf\n");
        text.Append("//!FILTER LINEAR\n");
        text.Append("//!BORDER CLAMP\n");
        text.Append(Convert.ToHexString(bytes).ToLowerInvariant()).Append("\n\n");
        text.Append("""
            //!PARAM lut_strength
            //!DESC LUT-Stärke (0 = aus, 1 = voll)
            //!TYPE float
            //!MINIMUM 0
            //!MAXIMUM 1
            1.0


            """.ReplaceLineEndings("\n"));
        text.Append($"//!HOOK OUTPUT\n//!BIND HOOKED\n//!BIND lut3d\n//!DESC LUT {(Title ?? "").Replace("\n", " ")}\n");
        text.Append($$"""
            vec4 hook() {
                vec4 c = HOOKED_texOff(0);
                vec3 domain_min = {{Vec(DomainMin.R, DomainMin.G, DomainMin.B)}};
                vec3 domain_max = {{Vec(DomainMax.R, DomainMax.G, DomainMax.B)}};
                // Der Eintrag i liegt in der Mitte des Texels i, deshalb auf (i + 0,5) / Size abbilden.
                vec3 pos = clamp((c.rgb - domain_min) / (domain_max - domain_min), 0.0, 1.0);
                vec3 graded = texture(lut3d, pos * ({{n}}.0 - 1.0) / {{n}}.0 + 0.5 / {{n}}.0).rgb;
                c.rgb = clamp(mix(c.rgb, graded, lut_strength), 0.0, 1.0);
                return c;
            }

            """.ReplaceLineEndings("\n"));
        return text.ToString();
    }
}
