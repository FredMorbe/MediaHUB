using MediaCenter.Core;

namespace MediaCenter.Core.Tests;

public sealed class PictureSettingsTests
{
    [Fact]
    public void Defaults_change_nothing()
    {
        var picture = PictureSettings.Default;

        Assert.True(picture.IsDefault);
        Assert.Equal("no", picture.MpvAspectOverride);
        Assert.Equal(0, picture.MpvZoom);
        Assert.Equal(0, picture.MpvPanX);
        Assert.Equal(0, picture.MpvPanY);
        Assert.Equal(0, picture.MpvPanscan);
        Assert.True(picture.MpvKeepAspect);
        Assert.Equal(1, picture.Speed);
    }

    [Theory]
    [InlineData(50, -1)]
    [InlineData(100, 0)]
    [InlineData(200, 1)]
    [InlineData(400, 2)]
    public void Zoom_is_given_to_mpv_as_a_power_of_two(int percent, double expected)
    {
        Assert.Equal(expected, (PictureSettings.Default with { ZoomPercent = percent }).MpvZoom, 6);
    }

    private static readonly PictureFit Wide = new(16.0 / 9, 16.0 / 9); // 16:9-Film im 16:9-Fenster

    [Fact]
    public void Pan_range_follows_the_zoom_and_is_zero_without_zoom()
    {
        Assert.Equal(0, (PictureSettings.Default with { PanX = 100 }).MpvPanX);
        Assert.Equal(0, (PictureSettings.Default with { ZoomPercent = 80, PanX = 100 }).PanRangeX);
        Assert.Equal(0.25, (PictureSettings.Default with { ZoomPercent = 200 }).PanRangeX, 6);
        Assert.Equal(0.375, (PictureSettings.Default with { ZoomPercent = 400 }).PanRangeY, 6);
        Assert.Equal(0.25, (PictureSettings.Default with { ZoomPercent = 200, Fit = Wide }).PanRangeX, 6);
        Assert.Equal(0.25, (PictureSettings.Default with { ZoomPercent = 200, Fit = Wide }).PanRangeY, 6);
    }

    [Fact]
    public void A_wide_film_in_a_16_9_window_only_overhangs_vertically_once_zoomed_enough()
    {
        var film = PictureSettings.Default with { Fit = new PictureFit(2.35, 16.0 / 9) };
        var fittedHeight = (16.0 / 9) / 2.35; // Anteil der Fensterhöhe, den das Bild einnimmt

        Assert.Equal(0, (film with { ZoomPercent = 130 }).PanRangeY); // 130 % × 0,757 < 100 %: schwarze Ränder oben und unten
        Assert.True((film with { ZoomPercent = 130 }).PanRangeX > 0);
        Assert.Equal((1 - 1 / (2 * fittedHeight)) / 2, (film with { ZoomPercent = 200 }).PanRangeY, 6);
        Assert.Equal(0.25, (film with { ZoomPercent = 200 }).PanRangeX, 6);
    }

    [Fact]
    public void A_rotated_picture_is_narrow_and_only_overhangs_vertically()
    {
        var rotated = PictureSettings.Default with { Fit = Wide, Rotation = 90, ZoomPercent = 200 };

        Assert.Equal(0, rotated.PanRangeX);
        Assert.Equal(0.25, rotated.PanRangeY, 6);
        Assert.Equal(0, (rotated with { PanX = 100 }).MpvPanX);
    }

    [Fact]
    public void Fill_overhangs_on_the_short_side_and_stretch_fills_exactly()
    {
        var film = PictureSettings.Default with { Fit = new PictureFit(2.35, 16.0 / 9), ZoomPercent = 100 };

        var fill = film with { Aspect = AspectOption.All.Single(a => a.Id == "fill") };
        Assert.True(fill.PanRangeX > 0);   // Füllen schneidet links und rechts ab
        Assert.Equal(0, fill.PanRangeY);

        var stretch = film with { Aspect = AspectOption.All.Single(a => a.Id == "stretch"), ZoomPercent = 200 };
        Assert.Equal(0.25, stretch.PanRangeX, 6);
        Assert.Equal(0.25, stretch.PanRangeY, 6);
    }

    [Fact]
    public void A_given_aspect_replaces_the_aspect_of_the_file()
    {
        var picture = PictureSettings.Default with { Fit = Wide, ZoomPercent = 200, Aspect = AspectOption.All.Single(a => a.Id == "4:3") };

        Assert.Equal(1.0 / 6, picture.PanRangeX, 6); // 4:3 füllt 75 % der Fensterbreite: bei 200 % ragt 1/6 der Bildbreite über
        Assert.Equal(0.25, picture.PanRangeY, 6);
    }

    [Fact]
    public void Default_with_a_fit_is_still_the_default()
    {
        Assert.True((PictureSettings.Default with { Fit = Wide }).IsDefault);
        Assert.False((PictureSettings.Default with { Fit = Wide, Rotation = 90 }).IsDefault);
    }

    [Fact]
    public void Section_to_the_right_moves_the_picture_to_the_left()
    {
        var picture = PictureSettings.Default with { ZoomPercent = 200, PanX = 100, PanY = -100 };

        Assert.Equal(-0.25, picture.MpvPanX, 6);
        Assert.Equal(0.25, picture.MpvPanY, 6);
        Assert.Equal(-0.125, (picture with { PanX = 50 }).MpvPanX, 6);
    }

    [Fact]
    public void Aspect_options_map_to_the_three_mpv_properties()
    {
        var ratio = PictureSettings.Default with { Aspect = AspectOption.All.Single(a => a.Id == "2.35:1") };
        var fill = PictureSettings.Default with { Aspect = AspectOption.All.Single(a => a.Id == "fill") };
        var stretch = PictureSettings.Default with { Aspect = AspectOption.All.Single(a => a.Id == "stretch") };

        Assert.Equal("235:100", ratio.MpvAspectOverride);
        Assert.Equal(0, ratio.MpvPanscan);
        Assert.True(ratio.MpvKeepAspect);

        Assert.Equal("no", fill.MpvAspectOverride);
        Assert.Equal(1, fill.MpvPanscan);
        Assert.True(fill.MpvKeepAspect);

        Assert.Equal("no", stretch.MpvAspectOverride);
        Assert.Equal(0, stretch.MpvPanscan);
        Assert.False(stretch.MpvKeepAspect);
    }

    [Fact]
    public void Aspect_options_have_unique_ids_and_cycle_back_to_the_start()
    {
        Assert.Equal(AspectOption.All.Count, AspectOption.All.Select(a => a.Id).Distinct().Count());
        Assert.All(AspectOption.All.Where(a => a.Kind == AspectKind.Ratio), a => Assert.False(string.IsNullOrEmpty(a.Mpv)));

        var option = AspectOption.Original;
        var seen = new List<string>();
        for (var i = 0; i < AspectOption.All.Count; i++)
        {
            seen.Add(option.Id);
            option = option.Next();
        }
        Assert.Equal(AspectOption.Original, option);
        Assert.Equal(AspectOption.All.Select(a => a.Id), seen);
    }

    [Fact]
    public void Speed_steps_go_up_and_down_and_stop_at_the_ends()
    {
        Assert.Equal(1.25, PictureSettings.StepSpeed(1, 1));
        Assert.Equal(0.75, PictureSettings.StepSpeed(1, -1));
        Assert.Equal(0.25, PictureSettings.StepSpeed(0.5, -1));
        Assert.Null(PictureSettings.StepSpeed(0.25, -1));
        Assert.Null(PictureSettings.StepSpeed(8, 1));
        Assert.Equal(2, PictureSettings.StepSpeed(1.6, 1)); // zwischen zwei Stufen: nächste Stufe in der Richtung
        Assert.Equal(1.5, PictureSettings.StepSpeed(1.6, -1));
    }

    [Theory]
    [InlineData(0, 90, 90)]
    [InlineData(270, 90, 0)]
    [InlineData(0, -90, 270)]
    [InlineData(90, 180, 270)]
    [InlineData(180, -270, 270)]
    public void Rotation_wraps_in_ninety_degree_steps(int rotation, int degrees, int expected)
    {
        Assert.Equal(expected, PictureSettings.Rotated(rotation, degrees));
    }

    [Fact]
    public void Rotation_by_a_non_right_angle_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PictureSettings.Rotated(0, 45));
    }

    [Fact]
    public void Validation_rejects_values_out_of_range()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => (PictureSettings.Default with { Speed = 0.1 }).Validated());
        Assert.Throws<ArgumentOutOfRangeException>(() => (PictureSettings.Default with { Speed = 9 }).Validated());
        Assert.Throws<ArgumentOutOfRangeException>(() => (PictureSettings.Default with { Speed = double.NaN }).Validated());
        Assert.Throws<ArgumentOutOfRangeException>(() => (PictureSettings.Default with { ZoomPercent = 49 }).Validated());
        Assert.Throws<ArgumentOutOfRangeException>(() => (PictureSettings.Default with { ZoomPercent = 401 }).Validated());
        Assert.Throws<ArgumentOutOfRangeException>(() => (PictureSettings.Default with { PanX = 101 }).Validated());
        Assert.Throws<ArgumentOutOfRangeException>(() => (PictureSettings.Default with { PanY = -101 }).Validated());
        Assert.Throws<ArgumentOutOfRangeException>(() => (PictureSettings.Default with { Rotation = 45 }).Validated());
        Assert.Same(PictureSettings.Default, PictureSettings.Default.Validated());
    }
}
