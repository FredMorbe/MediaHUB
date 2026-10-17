using MediaCenter.Core;

namespace MediaCenter.Core.Tests;

public sealed class ColorSettingsTests
{
    [Fact]
    public void Defaults_change_nothing()
    {
        var color = ColorSettings.Default;

        Assert.True(color.IsDefault);
        Assert.False(color.UsesShader);
        Assert.Equal(0, color.ShaderSharpen);
        Assert.Equal((1.0, 1.0, 1.0), color.WhiteBalanceGains);
        Assert.Equal("gain_r=1,gain_g=1,gain_b=1,invert=0,sharpen=0", color.ShaderOptions);
    }

    [Theory]
    [InlineData(100, 1.3, 0.7)]
    [InlineData(-100, 0.7, 1.3)]
    [InlineData(50, 1.15, 0.85)]
    public void Warmer_means_more_red_and_less_blue(int temperature, double red, double blue)
    {
        var (r, g, b) = (ColorSettings.Default with { Temperature = temperature }).WhiteBalanceGains;

        Assert.Equal(red, r, 6);
        Assert.Equal(1, g, 6);
        Assert.Equal(blue, b, 6);
    }

    [Fact]
    public void Magenta_tint_lowers_green_and_green_tint_raises_it()
    {
        Assert.Equal(0.7, (ColorSettings.Default with { Tint = 100 }).WhiteBalanceGains.G, 6);
        Assert.Equal(1.3, (ColorSettings.Default with { Tint = -100 }).WhiteBalanceGains.G, 6);
        Assert.Equal(1, (ColorSettings.Default with { Tint = 100 }).WhiteBalanceGains.R, 6);
    }

    [Fact]
    public void Only_white_balance_sharpness_and_invert_need_the_shader()
    {
        var plain = ColorSettings.Default with { Brightness = 20, Contrast = -10, Saturation = 30, Hue = 5, Gamma = 15 };
        Assert.False(plain.UsesShader);
        Assert.True((plain with { Temperature = 1 }).UsesShader);
        Assert.True((plain with { Tint = -1 }).UsesShader);
        Assert.True((plain with { Sharpness = 1 }).UsesShader);
        Assert.True((plain with { Sharpness = -1 }).UsesShader);
        Assert.True((plain with { Invert = true }).UsesShader);
    }

    [Fact]
    public void Shader_options_use_a_decimal_point_whatever_the_culture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            var options = (ColorSettings.Default with { Temperature = 25, Tint = 10, Invert = true }).ShaderOptions;
            Assert.Equal("gain_r=1.075,gain_g=0.97,gain_b=0.925,invert=1,sharpen=0", options);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Sharpness_reaches_the_shader_scaled_and_signed()
    {
        Assert.Equal(0.75, (ColorSettings.Default with { Sharpness = 50 }).ShaderSharpen, 6);
        Assert.Equal(-1.5, (ColorSettings.Default with { Sharpness = -100 }).ShaderSharpen, 6);
        Assert.EndsWith("sharpen=-0.75", (ColorSettings.Default with { Sharpness = -50 }).ShaderOptions);
    }

    [Fact]
    public void Validation_rejects_each_value_out_of_range()
    {
        var bad = new Func<ColorSettings, ColorSettings>[]
        {
            c => c with { Brightness = 101 }, c => c with { Contrast = -101 }, c => c with { Saturation = 101 }, c => c with { Hue = -101 },
            c => c with { Gamma = 101 }, c => c with { Temperature = 101 }, c => c with { Tint = -101 }, c => c with { Sharpness = 101 },
        };
        foreach (var make in bad) Assert.Throws<ArgumentOutOfRangeException>(() => make(ColorSettings.Default).Validated());

        Assert.Same(ColorSettings.Default, ColorSettings.Default.Validated());
        Assert.Equal(100, (ColorSettings.Default with { Brightness = 100, Contrast = -100 }).Validated().Brightness);
    }
}
