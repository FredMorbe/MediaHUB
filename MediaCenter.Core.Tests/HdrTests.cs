using MediaCenter.Core;
using static MediaCenter.Core.Tests.TestSupport;

namespace MediaCenter.Core.Tests;

public sealed class HdrTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true);
    }

    private static ProbeResult Probe(string? transfer, params string[] sideData)
    {
        var data = sideData.Length == 0 ? "" : $", \"side_data_list\": [{string.Join(",", sideData.Select(s => $"{{ \"side_data_type\": \"{s}\" }}"))}]";
        var transferJson = transfer is null ? "" : $", \"color_transfer\": \"{transfer}\"";
        return FfprobeReader.Parse($$"""
            { "streams": [ { "index": 0, "codec_type": "video", "codec_name": "hevc", "width": 3840, "height": 2160{{transferJson}}{{data}} } ],
              "format": { "format_name": "matroska,webm", "duration": "60" } }
            """);
    }

    [Fact]
    public void Pq_without_side_data_is_hdr10()
    {
        var probe = Probe("smpte2084", "Mastering display metadata", "Content light level metadata");

        Assert.Equal(HdrFormat.Hdr10, probe.Hdr);
        Assert.True(probe.IsHdr);
    }

    [Fact]
    public void Dynamic_metadata_makes_pq_hdr10_plus()
    {
        Assert.Equal(HdrFormat.Hdr10Plus, Probe("smpte2084", "HDR Dynamic Metadata SMPTE2094-40 (HDR10+)").Hdr);
    }

    [Fact]
    public void Hlg_is_hlg()
    {
        var probe = Probe("arib-std-b67");

        Assert.Equal(HdrFormat.Hlg, probe.Hdr);
        Assert.True(probe.IsHdr);
    }

    [Fact]
    public void Dolby_vision_with_hdr10_base_is_dolby_vision_and_tone_mappable()
    {
        var probe = Probe("smpte2084", "DOVI configuration record", "Mastering display metadata");

        Assert.Equal(HdrFormat.DolbyVision, probe.Hdr);
        Assert.True(probe.IsHdr);
    }

    [Fact]
    public void Dolby_vision_without_pq_base_is_not_tone_mappable()
    {
        // Profil 5: ffprobe meldet keinen PQ/HLG-Wert; ffmpeg kann das Bild nicht umrechnen, mpv schon.
        var probe = Probe(null, "DOVI configuration record");

        Assert.Equal(HdrFormat.DolbyVisionOnly, probe.Hdr);
        Assert.False(probe.IsHdr);
        Assert.Equal("Dolby Vision", probe.Hdr.Label());
    }

    [Fact]
    public void Sdr_with_mastering_metadata_stays_sdr()
    {
        var probe = Probe("bt709", "Mastering display metadata");

        Assert.Equal(HdrFormat.None, probe.Hdr);
        Assert.False(probe.IsHdr);
        Assert.Equal("", probe.Hdr.Label());
    }

    [Fact]
    public void Probe_without_video_is_sdr()
    {
        Assert.Equal(HdrFormat.None, FfprobeReader.Parse("""{ "streams": [], "format": { "format_name": "mp3" } }""").Hdr);
    }

    [Fact]
    public void Labels_cover_every_format()
    {
        foreach (var format in Enum.GetValues<HdrFormat>()) Assert.NotNull(format.Label());

        Assert.Equal("HDR10+", HdrFormat.Hdr10Plus.Label());
        Assert.Equal("HLG", HdrFormat.Hlg.Label());
        Assert.Equal("HDR", HdrFormat.Unspecified.Label());
    }

    [Fact]
    public void Stored_value_one_from_the_old_scan_means_unspecified_hdr()
    {
        Assert.Equal(HdrFormat.Unspecified, HdrFormats.FromStored(1));
        Assert.True(HdrFormat.Unspecified.IsToneMappable());
        Assert.Throws<InvalidDataException>(() => HdrFormats.FromStored(99));
    }

    [Fact]
    public async Task Scan_stores_the_format_and_rescan_keeps_it()
    {
        Directory.CreateDirectory(_base);
        var drive = Path.Combine(_base, "drive");
        Touch(drive, @"Filme\Dune (2021).mkv");
        Touch(drive, @"Filme\Heat (1995).mkv");
        var volumes = new FakeVolumes();
        volumes.Mounted.Add(Vol("HDR00001", drive));
        var probe = new FakeProbe
        {
            Respond = path => path.Contains("Dune") ? Probe("smpte2084", "DOVI configuration record") : FakeProbe.VideoOnly,
        };
        using var db = new LibraryDb(Path.Combine(_base, "library.db"));
        var service = new LibraryService(db, volumes, probe);

        await service.AddFolderAsync(drive);

        var videos = db.ListVideos();
        Assert.Equal(HdrFormat.DolbyVision, videos.Single(v => v.TitleName == "Dune").Hdr);
        Assert.Equal(HdrFormat.None, videos.Single(v => v.TitleName == "Heat").Hdr);
        Assert.True(videos.Single(v => v.TitleName == "Dune").IsHdr);
    }

    [Fact]
    public void Output_follows_the_setting_and_falls_back_to_sdr_with_filters()
    {
        var plain = ColorSettings.Default;

        Assert.Equal("auto", HdrOutputs.ColorspaceHint(HdrOutput.Auto, plain));
        Assert.Equal("no", HdrOutputs.ColorspaceHint(HdrOutput.Sdr, plain));
        Assert.Equal("no", HdrOutputs.ColorspaceHint(HdrOutput.Auto, plain with { Lut = "Look" }));
        Assert.Equal("no", HdrOutputs.ColorspaceHint(HdrOutput.Auto, plain with { Invert = true }));
        Assert.Equal("no", HdrOutputs.ColorspaceHint(HdrOutput.Auto, plain with { Sharpness = 2 }));
        // Brightness & Co. rechnet mpv im Bildaufbau selbst: HDR bleibt.
        Assert.Equal("auto", HdrOutputs.ColorspaceHint(HdrOutput.Auto, plain with { Brightness = 10, Gamma = -5 }));
    }

    [Fact]
    public void Filter_note_applies_only_when_hdr_output_was_wanted()
    {
        var withLut = ColorSettings.Default with { Lut = "Look" };

        Assert.True(HdrOutputs.FilterForcesSdr(HdrOutput.Auto, withLut));
        Assert.False(HdrOutputs.FilterForcesSdr(HdrOutput.Sdr, withLut));
        Assert.False(HdrOutputs.FilterForcesSdr(HdrOutput.Auto, ColorSettings.Default));
    }
}
