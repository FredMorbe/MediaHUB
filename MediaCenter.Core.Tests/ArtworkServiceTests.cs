using MediaCenter.Core;
using static MediaCenter.Core.Tests.TestSupport;

namespace MediaCenter.Core.Tests;

public sealed class ArtworkServiceTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-artsvc-" + Guid.NewGuid().ToString("N"));
    private readonly string _drive;
    private readonly string _cache;
    private readonly FakeVolumes _volumes = new();
    private readonly FakeProbe _probe = new();
    private readonly FakeImageTool _tool = new();
    private readonly LibraryDb _db;
    private readonly LibraryService _library;
    private readonly ArtworkService _art;

    public ArtworkServiceTests()
    {
        _drive = Path.Combine(_base, "drive");
        _cache = Path.Combine(_base, "cache");
        Directory.CreateDirectory(_drive);
        _db = new LibraryDb(Path.Combine(_base, "library.db"));
        _volumes.Mounted.Add(Vol("AAAA0001", _drive));
        _library = new LibraryService(_db, _volumes, _probe);
        _art = new ArtworkService(_db, _tool, _probe, _cache);
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_base, recursive: true);
    }

    private async Task Scan() => await _library.AddFolderAsync(_drive);

    private Task<ArtworkResult> Refresh(bool force = false) => _art.RefreshAsync(_library.RefreshVolumes(), force);

    private TitleRow Title(string name) => _db.GetTitles().Single(t => t.Name == name);

    private ArtworkRow? Art(string title, ArtworkKind kind) => _db.GetArtwork(ArtworkOwner.Title, Title(title).Id, kind);

    [Fact]
    public async Task Sidecar_files_win_and_no_frame_is_grabbed()
    {
        Touch(_drive, @"Filme\Solo\Solo (2020).mkv");
        Touch(_drive, @"Filme\Solo\poster.jpg");
        Touch(_drive, @"Filme\Solo\fanart.jpg");
        await Scan();

        var result = await Refresh();

        Assert.Empty(result.Failures);
        Assert.Equal(ArtworkSource.Sidecar, Art("Solo", ArtworkKind.Poster)!.Source);
        Assert.Equal(ArtworkSource.Sidecar, Art("Solo", ArtworkKind.Landscape)!.Source);
        Assert.DoesNotContain(_tool.Calls, c => c.StartsWith("frame:"));
        Assert.Contains(_tool.Calls, c => c.StartsWith("scale:poster.jpg:400x600"));
        Assert.Contains(_tool.Calls, c => c.StartsWith("scale:fanart.jpg:1280x720"));
    }

    [Fact]
    public async Task Embedded_pictures_are_used_when_no_sidecar_exists()
    {
        Touch(_drive, @"Filme\Solo (2020).mkv");
        await Scan();
        StreamInfo Pic(int index, int w, int h) => new(index, FfprobeReader.AttachedPictureType, "mjpeg", null, w, h, null, null, null, false, false, null);
        _probe.Respond = _ => new ProbeResult(100, "matroska", new[] { FakeProbe.VideoOnly.Streams[0], Pic(2, 800, 1200), Pic(3, 1280, 720) });

        await Refresh();

        Assert.Equal(ArtworkSource.Embedded, Art("Solo", ArtworkKind.Poster)!.Source);
        Assert.Equal(ArtworkSource.Embedded, Art("Solo", ArtworkKind.Landscape)!.Source);
        Assert.Contains("extract:Solo (2020).mkv#2:400x600", _tool.Calls);
        Assert.Contains("extract:Solo (2020).mkv#3:1280x720", _tool.Calls);
    }

    [Fact]
    public async Task Without_any_image_the_landscape_is_a_frame_at_ten_percent_and_the_poster_stays_empty()
    {
        Touch(_drive, @"Filme\Solo (2020).mkv");
        await Scan();

        await Refresh();

        Assert.Null(Art("Solo", ArtworkKind.Poster));
        Assert.Equal(ArtworkSource.Frame, Art("Solo", ArtworkKind.Landscape)!.Source);
        Assert.Contains("frame:Solo (2020).mkv@10:1280x720", _tool.Calls); // 100 s Laufzeit → 10 s
    }

    [Fact]
    public async Task Series_get_a_title_cover_and_a_thumbnail_per_episode()
    {
        Touch(_drive, @"Serien\Show X\Season 1\S01E01.mkv");
        Touch(_drive, @"Serien\Show X\Season 1\S01E02.mkv");
        Touch(_drive, @"Serien\Show X\folder.jpg");
        await Scan();

        await Refresh();

        var title = Title("Show X");
        Assert.Equal(ArtworkSource.Sidecar, Art("Show X", ArtworkKind.Poster)!.Source);
        Assert.Equal(ArtworkSource.Frame, Art("Show X", ArtworkKind.Landscape)!.Source);
        var episodes = _db.GetTitleVideos(title.Id);
        Assert.All(episodes, e => Assert.Equal(ArtworkSource.Frame, _db.GetArtwork(ArtworkOwner.Video, e.VideoId, ArtworkKind.Landscape)!.Source));
        Assert.Equal(2, _tool.Calls.Count(c => c.EndsWith(":480x270")));
    }

    [Fact]
    public async Task Second_refresh_does_nothing_and_user_artwork_survives_force()
    {
        Touch(_drive, @"Filme\Solo (2020).mkv");
        var mine = Path.Combine(_base, "mein-bild.png");
        File.WriteAllText(mine, "bild");
        await Scan();
        await Refresh();
        await _art.SetUserArtworkAsync(ArtworkOwner.Title, Title("Solo").Id, ArtworkKind.Landscape, mine);
        _tool.Calls.Clear();

        var again = await Refresh();
        var forced = await Refresh(force: true);

        Assert.Equal(0, again.Created);
        Assert.Equal(0, forced.Created);
        Assert.Empty(_tool.Calls);
        Assert.Equal(ArtworkSource.User, Art("Solo", ArtworkKind.Landscape)!.Source);
    }

    [Fact]
    public async Task Clearing_user_artwork_lets_the_automatic_chain_refill_it()
    {
        Touch(_drive, @"Filme\Solo (2020).mkv");
        var mine = Path.Combine(_base, "mein-bild.png");
        File.WriteAllText(mine, "bild");
        await Scan();
        await _art.SetUserArtworkAsync(ArtworkOwner.Title, Title("Solo").Id, ArtworkKind.Landscape, mine);

        _art.ClearArtwork(ArtworkOwner.Title, Title("Solo").Id, ArtworkKind.Landscape);
        Assert.Null(Art("Solo", ArtworkKind.Landscape));
        await Refresh();

        Assert.Equal(ArtworkSource.Frame, Art("Solo", ArtworkKind.Landscape)!.Source);
    }

    [Fact]
    public async Task Force_replaces_automatic_artwork_with_a_newly_added_sidecar()
    {
        Touch(_drive, @"Filme\Solo\Solo (2020).mkv");
        await Scan();
        await Refresh();
        Assert.Equal(ArtworkSource.Frame, Art("Solo", ArtworkKind.Landscape)!.Source);

        Touch(_drive, @"Filme\Solo\fanart.jpg");
        await Refresh();
        Assert.Equal(ArtworkSource.Frame, Art("Solo", ArtworkKind.Landscape)!.Source); // ohne force bleibt alles

        await Refresh(force: true);
        Assert.Equal(ArtworkSource.Sidecar, Art("Solo", ArtworkKind.Landscape)!.Source);
    }

    [Fact]
    public async Task Cache_file_deleted_by_hand_is_recreated_without_force()
    {
        Touch(_drive, @"Filme\Solo (2020).mkv");
        await Scan();
        await Refresh();
        var path = _art.GetPath(ArtworkOwner.Title, Title("Solo").Id, ArtworkKind.Landscape)!;
        File.Delete(path);

        var result = await Refresh();

        Assert.True(result.Created > 0);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Offline_titles_are_skipped_and_keep_their_artwork()
    {
        Touch(_drive, @"Filme\Solo (2020).mkv");
        await Scan();
        await Refresh();
        _volumes.Mounted.Clear();
        _tool.Calls.Clear();

        var result = await Refresh(force: true);

        Assert.Equal(1, result.Unreachable);
        Assert.Empty(_tool.Calls);
        Assert.NotNull(Art("Solo", ArtworkKind.Landscape));
    }

    [Fact]
    public async Task One_failing_title_does_not_stop_the_others_and_is_reported()
    {
        Touch(_drive, @"Filme\Gut (2020).mkv");
        Touch(_drive, @"Filme\Kaputt (2021).mkv");
        await Scan();
        _tool.FailWhenCallContains = "Kaputt";

        var result = await Refresh();

        var failure = Assert.Single(result.Failures);
        Assert.Equal("Kaputt", failure.Subject);
        Assert.NotNull(Art("Gut", ArtworkKind.Landscape));
        Assert.Null(Art("Kaputt", ArtworkKind.Landscape));
    }

    [Fact]
    public async Task Best_cover_for_a_title_falls_back_from_poster_to_landscape_to_nothing()
    {
        Touch(_drive, @"Filme\Solo (2020).mkv");
        await Scan();
        var id = Title("Solo").Id;
        Assert.Null(_art.GetBestForTitle(id));

        await Refresh();
        Assert.Equal(ArtworkKind.Landscape, _art.GetBestForTitle(id)!.Value.Kind);

        var mine = Path.Combine(_base, "poster.png");
        File.WriteAllText(mine, "bild");
        await _art.SetUserArtworkAsync(ArtworkOwner.Title, id, ArtworkKind.Poster, mine);
        Assert.Equal(ArtworkKind.Poster, _art.GetBestForTitle(id)!.Value.Kind);
    }

    [Fact]
    public async Task Prune_removes_artwork_of_titles_that_no_longer_exist()
    {
        Touch(_drive, @"Filme\Solo (2020).mkv");
        await Scan();
        await Refresh();
        var file = _art.GetPath(ArtworkOwner.Title, Title("Solo").Id, ArtworkKind.Landscape)!;
        File.Delete(Path.Combine(_drive, @"Filme\Solo (2020).mkv"));
        await _library.RescanAllAsync();
        _db.RemoveMissingFiles();

        var pruned = _art.PruneOrphans();

        Assert.True(pruned >= 1);
        Assert.False(File.Exists(file));
        Assert.Empty(_db.GetAllArtwork());
    }

    [Fact]
    public async Task Flat_folder_with_several_movies_does_not_use_a_shared_poster()
    {
        Touch(_drive, @"Filme\A (2001).mkv");
        Touch(_drive, @"Filme\B (2002).mkv");
        Touch(_drive, @"Filme\poster.jpg");
        await Scan();

        await Refresh();

        Assert.Null(Art("A", ArtworkKind.Poster));
        Assert.Null(Art("B", ArtworkKind.Poster));
    }
}
