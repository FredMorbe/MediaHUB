using System.Text.Json;
using MediaCenter.Core;

namespace MediaCenter.Core.Tests;

public sealed class SubtitleStyleTests
{
    private static string Value(SubtitleStyle style, string name) => style.ToMpvProperties().Single(p => p.Name == name).Value;

    [Fact]
    public void The_default_matches_the_mpv_defaults_and_is_valid()
    {
        var style = SubtitleStyle.Default.Validated();

        Assert.True(style.IsDefault);
        Assert.Equal("38", Value(style, "sub-font-size"));
        Assert.Equal("1.65", Value(style, "sub-outline-size"));
        Assert.Equal("outline-and-shadow", Value(style, "sub-border-style"));
        Assert.Equal("no", Value(style, "sub-bold"));
        Assert.Equal("scale", Value(style, "sub-ass-override"));
    }

    [Fact]
    public void Every_property_is_sent_so_an_earlier_look_is_always_overwritten()
    {
        var names = SubtitleStyle.Default.ToMpvProperties().Select(p => p.Name).ToArray();

        Assert.Equal(
            new[] { "sub-font", "sub-font-size", "sub-color", "sub-outline-color", "sub-outline-size", "sub-shadow-offset", "sub-back-color", "sub-border-style", "sub-bold", "sub-ass-override" },
            names);
    }

    [Fact]
    public void The_box_uses_the_background_box_style_with_the_opacity_as_alpha()
    {
        var style = new SubtitleStyle(Background: SubtitleBackground.Box, BackgroundOpacity: 100, Bold: true, ForceStyle: true, Font: "Verdana");

        Assert.Equal("background-box", Value(style, "sub-border-style"));
        Assert.Equal("#FF000000", Value(style, "sub-back-color"));
        Assert.Equal("yes", Value(style, "sub-bold"));
        Assert.Equal("force", Value(style, "sub-ass-override"));
        Assert.Equal("Verdana", Value(style, "sub-font"));
        Assert.Equal("#00000000", Value(style with { BackgroundOpacity = 0 }, "sub-back-color"));
    }

    [Fact]
    public void Numbers_are_written_with_a_decimal_point_whatever_the_culture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            Assert.Equal("2.4", Value(new SubtitleStyle(OutlineSize: 2.4), "sub-outline-size"));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("FontSize", 10.0)]
    [InlineData("FontSize", 200.0)]
    [InlineData("OutlineSize", -1.0)]
    [InlineData("OutlineSize", 7.0)]
    [InlineData("ShadowOffset", 9.0)]
    public void Out_of_range_numbers_are_refused(string property, double value)
    {
        var style = property switch
        {
            "FontSize" => new SubtitleStyle(FontSize: value),
            "OutlineSize" => new SubtitleStyle(OutlineSize: value),
            _ => new SubtitleStyle(ShadowOffset: value),
        };

        var ex = Assert.Throws<ArgumentException>(() => style.Validated());
        Assert.Equal(property, ex.ParamName);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#FFF")]
    [InlineData("#GGGGGG")]
    [InlineData("#80FFFFFF")]
    [InlineData("")]
    public void Colours_must_be_six_digit_hex(string colour)
    {
        Assert.Throws<ArgumentException>(() => new SubtitleStyle(TextColor: colour).Validated());
        Assert.Throws<ArgumentException>(() => new SubtitleStyle(OutlineColor: colour).Validated());
    }

    [Fact]
    public void Font_opacity_and_background_are_checked()
    {
        Assert.Throws<ArgumentException>(() => new SubtitleStyle(Font: "  ").Validated());
        Assert.Throws<ArgumentException>(() => new SubtitleStyle(Font: "Ar\nial").Validated());
        Assert.Throws<ArgumentException>(() => new SubtitleStyle(BackgroundOpacity: 101).Validated());
        Assert.Throws<ArgumentException>(() => new SubtitleStyle(Background: (SubtitleBackground)9).Validated());
    }

    [Fact]
    public void All_presets_are_valid_and_differ_from_each_other()
    {
        Assert.NotEmpty(SubtitleStyle.Presets);
        foreach (var (_, style) in SubtitleStyle.Presets) style.Validated();
        Assert.Equal(SubtitleStyle.Presets.Count, SubtitleStyle.Presets.Select(p => p.Style).Distinct().Count());
        Assert.Equal(SubtitleStyle.Default, SubtitleStyle.Presets[0].Style);
    }

    [Fact]
    public void The_style_survives_a_json_round_trip_with_the_background_as_text()
    {
        var style = new SubtitleStyle(Font: "Georgia", FontSize: 52, TextColor: "#FFE14D", Background: SubtitleBackground.Box, Bold: true);

        var json = JsonSerializer.Serialize(style);
        var back = JsonSerializer.Deserialize<SubtitleStyle>(json);

        Assert.Contains("\"Box\"", json);
        Assert.Equal(style, back);
    }

    [Fact]
    public void A_settings_file_without_the_style_gets_the_default()
    {
        var back = JsonSerializer.Deserialize<SubtitleStyle>("{}");

        Assert.Equal(SubtitleStyle.Default, back);
    }
}
