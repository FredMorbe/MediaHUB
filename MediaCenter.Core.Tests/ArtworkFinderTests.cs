using MediaCenter.Core;
using static MediaCenter.Core.Tests.TestSupport;

namespace MediaCenter.Core.Tests;

public sealed class ArtworkFinderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mc-art-" + Guid.NewGuid().ToString("N"));

    public ArtworkFinderTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string P(string rel) => Path.Combine(_root, rel);

    [Fact]
    public void Movie_uses_files_named_after_the_movie_first()
    {
        Touch(_root, @"Filme\Heat (1995).mkv");
        Touch(_root, @"Filme\Heat (1995)-poster.jpg");
        Touch(_root, @"Filme\Heat (1995)-fanart.png");
        Touch(_root, @"Filme\Other (2000).mkv");

        var art = SidecarFinder.ForMovie(P(@"Filme\Heat (1995).mkv"));

        Assert.Equal(P(@"Filme\Heat (1995)-poster.jpg"), art.Poster);
        Assert.Equal(P(@"Filme\Heat (1995)-fanart.png"), art.Landscape);
    }

    [Fact]
    public void Movie_folder_level_poster_counts_only_when_the_folder_holds_a_single_video()
    {
        Touch(_root, @"Single\Solo (2020).mkv");
        Touch(_root, @"Single\POSTER.JPG");
        Touch(_root, @"Single\fanart.jpg");
        Touch(_root, @"Shared\A (2001).mkv");
        Touch(_root, @"Shared\B (2002).mkv");
        Touch(_root, @"Shared\poster.jpg");

        var single = SidecarFinder.ForMovie(P(@"Single\Solo (2020).mkv"));
        var shared = SidecarFinder.ForMovie(P(@"Shared\A (2001).mkv"));

        Assert.Equal(P(@"Single\POSTER.JPG"), single.Poster);
        Assert.Equal(P(@"Single\fanart.jpg"), single.Landscape);
        Assert.Null(shared.Poster);
    }

    [Fact]
    public void Series_looks_one_level_above_season_folders()
    {
        Touch(_root, @"Show\Season 1\S01E01.mkv");
        Touch(_root, @"Show\Season 2\S02E01.mkv");
        Touch(_root, @"Show\folder.jpg");
        Touch(_root, @"Show\backdrop.jpg");

        var art = SidecarFinder.ForSeries(new[] { P(@"Show\Season 1\S01E01.mkv"), P(@"Show\Season 2\S02E01.mkv") });

        Assert.Equal(P(@"Show\folder.jpg"), art.Poster);
        Assert.Equal(P(@"Show\backdrop.jpg"), art.Landscape);
    }

    [Fact]
    public void Series_folder_shared_with_other_shows_gives_no_cover()
    {
        Touch(_root, @"Serien\Show A S01E01.mkv");
        Touch(_root, @"Serien\Show B S01E01.mkv");
        Touch(_root, @"Serien\poster.jpg");

        var art = SidecarFinder.ForSeries(new[] { P(@"Serien\Show A S01E01.mkv") });

        Assert.Null(art.Poster);
    }

    [Fact]
    public void Embedded_pictures_are_told_apart_by_aspect_ratio_not_by_name()
    {
        StreamInfo Pic(int index, int w, int h) => new(index, FfprobeReader.AttachedPictureType, "mjpeg", null, w, h, null, null, null, false, false, null);
        var probe = new ProbeResult(100, "matroska", new[]
        {
            FakeProbe.VideoOnly.Streams[0],
            Pic(1, 1280, 720),
            Pic(2, 600, 900),
            Pic(3, 300, 450),
        });

        var (poster, landscape) = EmbeddedArtwork.Choose(probe);

        Assert.Equal(2, poster!.Index);
        Assert.Equal(1, landscape!.Index);
    }

    [Fact]
    public void Embedded_picture_does_not_count_as_the_video_stream()
    {
        var json = """
            { "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "mjpeg", "width": 800, "height": 1200, "disposition": { "attached_pic": 1 } },
                { "index": 1, "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080, "disposition": { "attached_pic": 0 } }
              ], "format": { "duration": "10" } }
            """;

        var probe = FfprobeReader.Parse(json);

        Assert.Equal(1, probe.Video!.Index);
        Assert.Equal(1920, probe.Video.Width);
        Assert.Equal(0, probe.AttachedPictures.Single().Index);
    }
}
