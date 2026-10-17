using System.Diagnostics;
using MediaCenter.Core;
using static MediaCenter.Core.Tests.TestSupport;

namespace MediaCenter.Core.Tests;

/// <summary>Läuft gegen das echte ffmpeg/ffprobe (PATH oder MEDIACENTER_FFMPEG/MEDIACENTER_FFPROBE); fehlt es, schlagen die Tests laut fehl.</summary>
public sealed class FfmpegIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mc-ff-" + Guid.NewGuid().ToString("N"));
    private readonly FfmpegTool _ffmpeg = FfmpegTool.FromEnvironment();
    private readonly FfprobeReader _ffprobe = FfprobeReader.FromEnvironment();

    public FfmpegIntegrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static async Task RunFfmpeg(params string[] args)
    {
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("MEDIACENTER_FFMPEG") ?? "ffmpeg")
        {
            UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true,
        };
        foreach (var a in new[] { "-v", "error", "-y" }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, $"ffmpeg-Testmedien konnten nicht erzeugt werden: {stderr}");
    }

    /// <summary>Erzeugt ein Hochformat-Bild und eine kurze MKV, die dieses Bild als Anhang trägt.</summary>
    private async Task<(string Image, string Video)> MakeMedia(string videoName = "sample.mkv")
    {
        // Das Quellbild liegt bewusst nicht neben dem Video, sonst fände der Finder es als Ordner-Cover.
        Directory.CreateDirectory(Path.Combine(_dir, "assets"));
        var image = Path.Combine(_dir, "assets", "cover.jpg");
        var video = Path.Combine(_dir, videoName);
        await RunFfmpeg("-f", "lavfi", "-i", "color=c=0x3366aa:s=800x1200", "-frames:v", "1", image);
        await RunFfmpeg("-f", "lavfi", "-i", "testsrc=d=3:s=640x360:r=24", "-f", "lavfi", "-i", "sine=d=3",
            "-pix_fmt", "yuv420p", "-c:v", "libx264", "-c:a", "aac",
            "-attach", image, "-metadata:s:t:0", "mimetype=image/jpeg", "-metadata:s:t:0", "filename=cover.jpg", video);
        return (image, video);
    }

    private async Task<(int W, int H)> SizeOf(string imagePath)
    {
        var probe = await _ffprobe.ProbeAsync(imagePath);
        var s = probe.Streams.Single();
        return (s.Width!.Value, s.Height!.Value);
    }

    [Fact]
    public async Task Embedded_cover_is_not_mistaken_for_the_video_and_can_be_extracted_scaled()
    {
        var (_, video) = await MakeMedia();

        var probe = await _ffprobe.ProbeAsync(video);

        Assert.Equal((640, 360), (probe.Video!.Width!.Value, probe.Video.Height!.Value));
        var picture = Assert.Single(probe.AttachedPictures);
        Assert.Equal((800, 1200), (picture.Width!.Value, picture.Height!.Value));

        var output = Path.Combine(_dir, "out", "extracted.jpg");
        await _ffmpeg.ExtractStreamImageAsync(video, picture.Index, output, 400, 600);
        Assert.Equal((400, 600), await SizeOf(output));
    }

    [Fact]
    public async Task Tags_with_umlauts_and_other_non_ascii_text_survive_the_round_trip_through_ffprobe()
    {
        var file = Path.Combine(_dir, "Größe.mp3");
        await RunFfmpeg("-f", "lavfi", "-i", "sine=d=1", "-metadata", "title=Straße Größe – Łódź", "-metadata", "artist=Café Müller", file);

        var probe = await _ffprobe.ProbeAsync(file);

        Assert.Equal("Straße Größe – Łódź", probe.Tags!["title"]);
        Assert.Equal("Café Müller", probe.Tags["artist"]);
    }

    [Fact]
    public async Task Images_are_scaled_down_but_never_up()
    {
        var (image, _) = await MakeMedia();
        var small = Path.Combine(_dir, "small.jpg");
        await RunFfmpeg("-f", "lavfi", "-i", "color=c=red:s=100x150", "-frames:v", "1", small);

        await _ffmpeg.ScaleImageAsync(image, Path.Combine(_dir, "big-scaled.jpg"), 400, 600);
        await _ffmpeg.ScaleImageAsync(small, Path.Combine(_dir, "small-scaled.jpg"), 400, 600);

        Assert.Equal((400, 600), await SizeOf(Path.Combine(_dir, "big-scaled.jpg")));
        Assert.Equal((100, 150), await SizeOf(Path.Combine(_dir, "small-scaled.jpg")));
    }

    [Fact]
    public async Task Frame_grab_fits_the_requested_box_and_a_missing_input_fails_loudly()
    {
        var (_, video) = await MakeMedia();

        var output = Path.Combine(_dir, "frame.jpg");
        await _ffmpeg.GrabFrameAsync(video, 1, output, 480, 270);
        Assert.Equal((480, 270), await SizeOf(output));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _ffmpeg.GrabFrameAsync(Path.Combine(_dir, "gibt-es-nicht.mkv"), 1, Path.Combine(_dir, "x.jpg"), 480, 270));
        Assert.Contains("gibt-es-nicht.mkv", ex.Message); // die ffmpeg-Meldung nennt die fehlende Datei
        Assert.False(File.Exists(Path.Combine(_dir, "x.jpg")));
        Assert.DoesNotContain(Directory.EnumerateFiles(_dir), f => f.Contains(".tmp."));
    }

    [Fact]
    public async Task Frame_grab_cuts_black_letterbox_bars()
    {
        var video = Path.Combine(_dir, "bars.mkv");
        // 640x360 Bild in ein 640x480 Video mit je 60 Pixel schwarzem Rand oben und unten gesetzt.
        await RunFfmpeg("-f", "lavfi", "-i", "testsrc=d=6:s=640x360:r=24", "-vf", "pad=640:480:0:60:black",
            "-pix_fmt", "yuv420p", "-c:v", "libx264", video);

        var output = Path.Combine(_dir, "bars.jpg");
        await _ffmpeg.GrabFrameAsync(video, 1, output, 1280, 720);

        Assert.Equal((640, 360), await SizeOf(output));
    }

    [Fact]
    public async Task Frame_grab_of_a_video_without_bars_keeps_the_full_frame()
    {
        var (_, video) = await MakeMedia();
        var output = Path.Combine(_dir, "full.jpg");

        await _ffmpeg.GrabFrameAsync(video, 1, output, 1280, 720);

        Assert.Equal((640, 360), await SizeOf(output));
    }

    [Fact]
    public async Task Whole_chain_with_real_tools_stores_embedded_poster_and_frame_landscape()
    {
        await MakeMedia("Solo (2020).mkv");
        var drive = _dir;
        var volumes = new FakeVolumes();
        volumes.Mounted.Add(Vol("AAAA0001", drive));
        using var db = new LibraryDb(Path.Combine(_dir, "library.db"));
        var library = new LibraryService(db, volumes, _ffprobe);
        var cache = Path.Combine(_dir, "cache");
        var art = new ArtworkService(db, _ffmpeg, _ffprobe, cache);

        await library.AddFolderAsync(drive);
        var result = await art.RefreshAsync(library.RefreshVolumes());

        Assert.Empty(result.Failures);
        var title = db.GetTitles().Single();
        var poster = db.GetArtwork(ArtworkOwner.Title, title.Id, ArtworkKind.Poster)!;
        var landscape = db.GetArtwork(ArtworkOwner.Title, title.Id, ArtworkKind.Landscape)!;
        Assert.Equal(ArtworkSource.Embedded, poster.Source);
        Assert.Equal(ArtworkSource.Frame, landscape.Source);
        Assert.Equal((400, 600), await SizeOf(Path.Combine(cache, poster.File)));
        Assert.Equal((640, 360), await SizeOf(Path.Combine(cache, landscape.File)));

        // Die Datenbank zählt das eingebettete Bild nicht als Videospur der Datei.
        var video = db.ListVideos().Single();
        Assert.Equal(640, video.Width);
        Assert.Equal(360, video.Height);
    }
}
