using System.IO;
using MediaCenter.App.Services;

namespace MediaCenter.App.Player;

/// <summary>
/// Der Shader für Weißabgleich, Schärfe und Invertieren (mpv, gpu-next). Er sitzt im Hook OUTPUT, also hinter allen Bildberechnungen,
/// aber vor Untertiteln und Bildschirmanzeige: Untertitel bleiben beim Invertieren lesbar. Die Werte kommen zur Laufzeit
/// über <c>glsl-shader-opts</c> (siehe <see cref="MediaCenter.Core.ColorSettings.ShaderOptions"/>).
/// </summary>
public static class ColorShader
{
    private const string Source = """
        //!PARAM gain_r
        //!DESC Weißabgleich: Rot-Verstärkung
        //!TYPE float
        //!MINIMUM 0
        //!MAXIMUM 4
        1.0

        //!PARAM gain_g
        //!DESC Weißabgleich: Grün-Verstärkung
        //!TYPE float
        //!MINIMUM 0
        //!MAXIMUM 4
        1.0

        //!PARAM gain_b
        //!DESC Weißabgleich: Blau-Verstärkung
        //!TYPE float
        //!MINIMUM 0
        //!MAXIMUM 4
        1.0

        //!PARAM invert
        //!DESC Farben invertieren (0 oder 1)
        //!TYPE float
        //!MINIMUM 0
        //!MAXIMUM 1
        0.0

        //!PARAM sharpen
        //!DESC Schärfe (positiv schärfer, negativ weicher)
        //!TYPE float
        //!MINIMUM -3
        //!MAXIMUM 3
        0.0

        //!HOOK OUTPUT
        //!BIND HOOKED
        //!DESC Farbkorrektur: Schärfe, Weißabgleich und Invertieren
        vec4 hook() {
            vec4 c = HOOKED_texOff(0);
            // Unsharp-Mask: Bild minus Mittel der vier Nachbarpixel; mit negativer Stärke wird daraus ein Weichzeichner.
            vec3 blur = 0.25 * (HOOKED_texOff(vec2(1.0, 0.0)).rgb + HOOKED_texOff(vec2(-1.0, 0.0)).rgb
                              + HOOKED_texOff(vec2(0.0, 1.0)).rgb + HOOKED_texOff(vec2(0.0, -1.0)).rgb);
            c.rgb += sharpen * (c.rgb - blur);
            c.rgb = clamp(c.rgb * vec3(gain_r, gain_g, gain_b), 0.0, 1.0);
            c.rgb = mix(c.rgb, vec3(1.0) - c.rgb, invert);
            return c;
        }
        """;

    /// <summary>Pfad der Shader-Datei im Datenordner; wird angelegt oder aktualisiert, falls sie fehlt oder abweicht.</summary>
    public static string EnsureFile()
    {
        var path = Path.Combine(AppSettings.DataDirectory, "shaders", "colorgrade.glsl");
        var text = Source.ReplaceLineEndings("\n") + "\n";
        if (!File.Exists(path) || File.ReadAllText(path) != text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        return path;
    }
}
