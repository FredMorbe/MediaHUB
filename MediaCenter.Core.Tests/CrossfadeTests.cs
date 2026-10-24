using MediaCenter.Core;

namespace MediaCenter.Core.Tests;

public sealed class CrossfadeTests
{
    [Fact]
    public void Off_means_no_crossfade_whatever_the_track_length()
    {
        Assert.False(Crossfade.Applies(300, 0));
        Assert.False(Crossfade.ShouldPrepare(299, 300, 0));
        Assert.False(Crossfade.ShouldStart(299, 300, 0));
    }

    [Fact]
    public void Short_tracks_play_gapless_instead()
    {
        Assert.False(Crossfade.Applies(11.9, 4));
        Assert.True(Crossfade.Applies(12, 4));
        Assert.False(Crossfade.Applies(double.NaN, 4));
        Assert.False(Crossfade.Applies(double.PositiveInfinity, 4)); // Radio ohne bekannte Länge
        Assert.False(Crossfade.Applies(0, 4));
    }

    [Fact]
    public void The_second_player_is_prepared_a_little_before_the_fade_starts()
    {
        const double duration = 200;
        Assert.False(Crossfade.ShouldPrepare(duration - 10.1, duration, 4)); // 10,1 s vor Schluss: noch zu früh (Schwelle 4 + 5 s)
        Assert.True(Crossfade.ShouldPrepare(duration - 9, duration, 4));
        Assert.False(Crossfade.ShouldStart(duration - 9, duration, 4));
        Assert.False(Crossfade.ShouldStart(duration - 4.1, duration, 4));
        Assert.True(Crossfade.ShouldStart(duration - 4, duration, 4));
        Assert.True(Crossfade.ShouldStart(duration - 0.5, duration, 4));
    }

    [Fact]
    public void The_gains_cross_with_constant_power()
    {
        Assert.Equal((1.0, 0.0), Crossfade.Gains(0));
        var (outEnd, inEnd) = Crossfade.Gains(1);
        Assert.Equal(0, outEnd, 10);
        Assert.Equal(1, inEnd, 10);
        foreach (var p in new[] { 0.1, 0.25, 0.5, 0.75, 0.9 })
        {
            var (o, i) = Crossfade.Gains(p);
            Assert.Equal(1.0, o * o + i * i, 10);
        }
        var (o50, i50) = Crossfade.Gains(0.5);
        Assert.Equal(o50, i50, 10);
    }

    [Fact]
    public void Progress_outside_zero_to_one_is_limited_and_garbage_is_the_start()
    {
        Assert.Equal(Crossfade.Gains(1), Crossfade.Gains(5));
        Assert.Equal(Crossfade.Gains(0), Crossfade.Gains(-1));
        Assert.Equal(Crossfade.Gains(0), Crossfade.Gains(double.NaN));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(12)]
    public void Listed_lengths_are_valid(int seconds) => Assert.Equal(seconds, Crossfade.Validated(seconds));

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(60)]
    public void Other_lengths_are_refused(int seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Crossfade.Validated(seconds));
}
