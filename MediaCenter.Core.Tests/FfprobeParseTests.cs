using MediaCenter.Core;

namespace MediaCenter.Core.Tests;

public class FfprobeParseTests
{
    private const string Json = """
        {
          "streams": [
            { "index": 0, "codec_type": "video", "codec_name": "hevc", "profile": "Main 10", "width": 3840, "height": 2160,
              "color_transfer": "smpte2084", "disposition": { "default": 1, "forced": 0 } },
            { "index": 1, "codec_type": "audio", "codec_name": "eac3", "channels": 6,
              "tags": { "language": "ger", "title": "German" }, "disposition": { "default": 1, "forced": 0 } },
            { "index": 2, "codec_type": "subtitle", "codec_name": "subrip",
              "tags": { "LANGUAGE": "eng", "TITLE": "English [Forced]" }, "disposition": { "default": 0, "forced": 1 } }
          ],
          "format": { "format_name": "matroska,webm", "duration": "7951.104000" }
        }
        """;

    [Fact]
    public void Parses_streams_and_format()
    {
        var probe = FfprobeReader.Parse(Json);

        Assert.Equal(7951.104, probe.DurationSeconds);
        Assert.Equal("matroska,webm", probe.Container);
        Assert.Equal(3, probe.Streams.Count);
        Assert.Equal("hevc", probe.Video!.Codec);
        Assert.Equal((3840, 2160), (probe.Video.Width, probe.Video.Height));
        Assert.True(probe.IsHdr);
    }

    [Fact]
    public void Reads_tags_case_insensitively_and_dispositions()
    {
        var subtitle = FfprobeReader.Parse(Json).Streams[2];

        Assert.Equal("eng", subtitle.Language);
        Assert.Equal("English [Forced]", subtitle.Title);
        Assert.True(subtitle.IsForced);
        Assert.False(subtitle.IsDefault);
    }

    [Fact]
    public void Missing_duration_is_null_not_zero()
    {
        var probe = FfprobeReader.Parse("""{ "streams": [], "format": { "format_name": "avi" } }""");

        Assert.Null(probe.DurationSeconds);
        Assert.Null(probe.Video);
        Assert.False(probe.IsHdr);
    }
}
