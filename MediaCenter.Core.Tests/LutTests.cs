using MediaCenter.Core;

namespace MediaCenter.Core.Tests;

public sealed class LutTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-lut-" + Guid.NewGuid().ToString("N"));

    public LutTests() => Directory.CreateDirectory(_base);

    public void Dispose() => Directory.Delete(_base, recursive: true);

    /// <summary>Eine gültige LUT der Größe 2 mit Kanaltausch (R↔B), wie in der Datei: Rot läuft am schnellsten.</summary>
    private static string Cube(string extra = "") =>
        "# Kommentar\nTITLE \"Tausch\"\n" + extra + "LUT_3D_SIZE 2\n" +
        "0 0 0\n0 0 1\n0 1 0\n0 1 1\n1 0 0\n1 0 1\n1 1 0\n1 1 1\n";

    private string Write(string name, string text)
    {
        var path = Path.Combine(_base, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void A_cube_file_is_read_with_size_title_and_default_domain()
    {
        var lut = CubeLut.Parse(Cube());

        Assert.Equal(2, lut.Size);
        Assert.Equal("Tausch", lut.Title);
        Assert.Equal((0f, 0f, 0f), lut.DomainMin);
        Assert.Equal((1f, 1f, 1f), lut.DomainMax);
        Assert.Equal(24, lut.Data.Length);
        Assert.Equal(new[] { 0f, 0f, 1f }, lut.Data[3..6]);
    }

    [Fact]
    public void Domain_windows_line_endings_and_exponents_are_understood()
    {
        var text = "DOMAIN_MIN 0.1 0.1 0.1\r\nDOMAIN_MAX 0.9 0.9 0.9\r\nLUT_3D_SIZE 2\r\n" + string.Join("\r\n", Enumerable.Repeat("0.25 0.5 1.0e0", 8));
        var lut = CubeLut.Parse(text);

        Assert.Equal(0.1f, lut.DomainMin.R);
        Assert.Equal(0.9f, lut.DomainMax.B);
        Assert.Equal(1f, lut.Data[2]);
    }

    [Fact]
    public void The_adobe_input_range_sets_the_domain_of_all_channels()
    {
        var lut = CubeLut.Parse(Cube("LUT_3D_INPUT_RANGE 0.0 0.5\n"));

        Assert.Equal((0.5f, 0.5f, 0.5f), lut.DomainMax);
    }

    [Theory]
    [InlineData("LUT_1D_SIZE 4\n0 0 0\n", "1D-LUTs")]
    [InlineData("0 0 0\n", "vor LUT_3D_SIZE")]
    [InlineData("LUT_3D_SIZE 2\n0 0 0\n", "unvollständig")]
    [InlineData("LUT_3D_SIZE 1\n", "ab 2")]
    [InlineData("LUT_3D_SIZE 66\n", "bis 65")]
    [InlineData("LUT_3D_SIZE 2\n0 0 x\n", "keine Zahl")]
    [InlineData("LUT_3D_SIZE 2\n0 0\n", "drei Zahlen")]
    [InlineData("LUT_3D_SIZE 2\nFOO 1\n", "Unbekannte Angabe")]
    [InlineData("DOMAIN_MIN 1 1 1\nDOMAIN_MAX 1 1 1\nLUT_3D_SIZE 2\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n", "DOMAIN_MAX")]
    [InlineData("", "LUT_3D_SIZE fehlt")]
    public void Broken_files_throw_with_a_clear_message(string text, string expected)
    {
        var ex = Assert.Throws<InvalidDataException>(() => CubeLut.Parse(text));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Too_many_entries_throw()
    {
        var text = Cube() + "0 0 0\n";

        Assert.Contains("Mehr Einträge", Assert.Throws<InvalidDataException>(() => CubeLut.Parse(text)).Message);
    }

    [Fact]
    public void The_shader_embeds_the_texture_as_half_float_hex_and_the_hook()
    {
        var shader = CubeLut.Parse(Cube()).ToMpvShader();

        Assert.Contains("//!TEXTURE lut3d\n//!SIZE 2 2 2\n//!FORMAT rgba16hf\n//!FILTER LINEAR\n//!BORDER CLAMP\n", shader);
        Assert.Contains("//!PARAM lut_strength", shader);
        Assert.Contains("//!HOOK OUTPUT", shader);
        Assert.DoesNotContain('\r', shader);

        // 8 Einträge × 4 Halbfloats × 2 Byte = 64 Byte = 128 Hex-Zeichen; 1.0 = 0x3c00, little endian "003c".
        var hex = shader.Split('\n').Single(l => l.Length > 100 && l.All(Uri.IsHexDigit));
        Assert.Equal(128, hex.Length);
        Assert.Equal("0000" + "0000" + "0000" + "003c", hex[..16]);        // (0,0,0,1)
        Assert.Equal("0000" + "0000" + "003c" + "003c", hex[16..32]);       // (0,0,1,1)
    }

    [Fact]
    public void Domain_is_baked_into_the_shader()
    {
        var shader = CubeLut.Parse(Cube("DOMAIN_MIN 0.1 0.2 0.3\n")).ToMpvShader();

        Assert.Contains("vec3 domain_min = vec3(0.1, 0.2, 0.3);", shader);
    }

    [Fact]
    public void Values_beyond_half_float_range_are_refused()
    {
        var text = Cube().Replace("1 1 1", "70000 1 1");

        Assert.Contains("außerhalb", Assert.Throws<InvalidDataException>(() => CubeLut.Parse(text).ToMpvShader()).Message);
    }

    // --- LutStore ---------------------------------------------------------------------------------------------

    private LutStore Store() => new(Path.Combine(_base, "store"));

    [Fact]
    public void Adding_copies_the_file_and_lists_it_alphabetically()
    {
        var store = Store();

        Assert.Empty(store.List());
        Assert.Equal("Zeta", store.Add(Write("Zeta.cube", Cube())));
        Assert.Equal("alpha", store.Add(Write("alpha.CUBE", Cube("\n"))));

        Assert.Equal(new[] { "alpha", "Zeta" }, store.List());
        Assert.True(store.Exists("Zeta"));
    }

    [Fact]
    public void The_same_file_twice_is_one_lut_and_another_with_the_same_name_gets_a_suffix()
    {
        var store = Store();
        var first = Write("Look.cube", Cube());
        var folder = Path.Combine(_base, "x");
        Directory.CreateDirectory(folder);
        var second = Path.Combine(folder, "Look.cube");
        File.WriteAllText(second, Cube("DOMAIN_MAX 2 2 2\n"));

        Assert.Equal("Look", store.Add(first));
        Assert.Equal("Look", store.Add(first));
        Assert.Equal("Look-2", store.Add(second));
        Assert.Equal(new[] { "Look", "Look-2" }, store.List());
    }

    [Fact]
    public void Invalid_or_foreign_files_are_not_added()
    {
        var store = Store();

        Assert.Throws<InvalidDataException>(() => store.Add(Write("kaputt.cube", "LUT_3D_SIZE 2\n")));
        Assert.Throws<InvalidDataException>(() => store.Add(Write("look.3dl", Cube())));
        Assert.Throws<FileNotFoundException>(() => store.Add(Path.Combine(_base, "nix.cube")));
        Assert.Empty(store.List());
    }

    [Fact]
    public void A_semicolon_in_the_name_is_replaced_because_it_separates_the_mpv_shader_list()
    {
        Assert.Equal("a_b", Store().Add(Write("a;b.cube", Cube())));
    }

    [Fact]
    public void The_shader_file_is_created_once_and_removed_with_the_lut()
    {
        var store = Store();
        var name = store.Add(Write("Look.cube", Cube()));

        var path = store.ShaderPath(name);
        Assert.StartsWith("//!TEXTURE", File.ReadAllText(path));
        var written = File.GetLastWriteTimeUtc(path);
        Assert.Equal(path, store.ShaderPath(name));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path)); // nicht neu geschrieben

        store.Remove(name);
        Assert.False(File.Exists(path));
        Assert.Empty(store.List());
        Assert.Throws<FileNotFoundException>(() => store.ShaderPath(name));
        Assert.Throws<FileNotFoundException>(() => store.Remove(name));
    }

    // --- ColorSettings ----------------------------------------------------------------------------------------

    [Fact]
    public void A_lut_makes_the_settings_non_default_and_validates_its_strength()
    {
        var color = ColorSettings.Default with { Lut = "Look" };

        Assert.False(color.IsDefault);
        Assert.True(color.UsesLut);
        Assert.False(color.UsesShader); // der Farb-Shader bleibt aus
        Assert.Equal("lut_strength=1", color.LutShaderOptions);
        Assert.Equal("lut_strength=0.35", (color with { LutStrength = 35 }).LutShaderOptions);
        Assert.Throws<ArgumentOutOfRangeException>(() => (color with { LutStrength = 101 }).Validated());
        Assert.Throws<ArgumentOutOfRangeException>(() => (color with { LutStrength = -1 }).Validated());
        Assert.Throws<ArgumentException>(() => (color with { Lut = " " }).Validated());
    }

    [Fact]
    public void Shader_options_contain_only_the_shaders_that_run()
    {
        var plain = ColorSettings.Default;
        var inverted = plain with { Invert = true };
        var lut = plain with { Lut = "Look", LutStrength = 50 };
        var both = lut with { Invert = true };

        Assert.Equal("", plain.AllShaderOptions);
        Assert.Equal("lut_strength=0.5", lut.AllShaderOptions);
        Assert.Equal(inverted.ShaderOptions, inverted.AllShaderOptions);
        Assert.Equal($"{both.ShaderOptions},lut_strength=0.5", both.AllShaderOptions);
    }
}
