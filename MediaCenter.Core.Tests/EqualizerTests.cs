using MediaCenter.Core;

namespace MediaCenter.Core.Tests;

public sealed class EqualizerTests
{
    private static int[] Zeros() => new int[10];

    [Fact]
    public void Flat_gives_no_filter()
    {
        Assert.Null(Equalizer.ToMpvFilter(Zeros()));
    }

    [Fact]
    public void Only_bands_with_a_gain_become_filters_and_a_preamp_lowers_the_level_by_the_largest_boost()
    {
        var gains = Zeros();
        gains[0] = 6;
        gains[5] = -3;
        gains[9] = 2;

        var filter = Equalizer.ToMpvFilter(gains);

        Assert.Equal("lavfi=[volume=-6dB,equalizer=f=32:t=o:w=1:g=6,equalizer=f=1000:t=o:w=1:g=-3,equalizer=f=16000:t=o:w=1:g=2]", filter);
    }

    [Fact]
    public void Cutting_only_needs_no_preamp()
    {
        var gains = Zeros();
        gains[3] = -4;

        Assert.Equal("lavfi=[equalizer=f=250:t=o:w=1:g=-4]", Equalizer.ToMpvFilter(gains));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(11)]
    [InlineData(0)]
    public void The_wrong_number_of_bands_is_an_error(int count)
    {
        Assert.Throws<ArgumentException>(() => Equalizer.ToMpvFilter(new int[count]));
    }

    [Theory]
    [InlineData(13)]
    [InlineData(-13)]
    public void A_gain_out_of_range_is_an_error_not_clamped(int gain)
    {
        var gains = Zeros();
        gains[4] = gain;

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Equalizer.ToMpvFilter(gains));

        Assert.Contains("500 Hz", ex.Message);
    }

    [Fact]
    public void The_limits_themselves_are_allowed()
    {
        var gains = Zeros();
        gains[0] = Equalizer.MaxGain;
        gains[1] = Equalizer.MinGain;

        Assert.NotNull(Equalizer.ToMpvFilter(gains));
    }

    [Fact]
    public void Every_preset_is_valid_has_a_unique_id_and_flat_is_first_and_empty()
    {
        Assert.Equal(Equalizer.Presets.Count, Equalizer.Presets.Select(p => p.Id).Distinct().Count());
        Assert.All(Equalizer.Presets, p => Equalizer.Validated(p.Gains));
        Assert.Equal(Equalizer.Flat, Equalizer.Presets[0].Id);
        Assert.Null(Equalizer.ToMpvFilter(Equalizer.Presets[0].Gains));
        Assert.All(Equalizer.Presets.Skip(1), p => Assert.NotNull(Equalizer.ToMpvFilter(p.Gains)));
    }

    [Fact]
    public void A_preset_is_recognised_by_its_values_and_anything_else_is_custom()
    {
        var bass = Equalizer.PresetById("bass");
        var changed = bass.Gains.ToArray();
        changed[9] = 1;

        Assert.Equal("bass", Equalizer.MatchingPreset(bass.Gains));
        Assert.Equal("flat", Equalizer.MatchingPreset(Zeros()));
        Assert.Equal("custom", Equalizer.MatchingPreset(changed));
        Assert.Throws<ArgumentException>(() => Equalizer.PresetById("gibtsnicht"));
    }
}
