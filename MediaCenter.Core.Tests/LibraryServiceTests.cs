using MediaCenter.Core;
using static MediaCenter.Core.Tests.TestSupport;

namespace MediaCenter.Core.Tests;

public sealed class LibraryServiceTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-" + Guid.NewGuid().ToString("N"));
    private readonly FakeVolumes _volumes = new();
    private readonly LibraryDb _db;
    private readonly LibraryService _service;
    private readonly string _driveA;

    public LibraryServiceTests()
    {
        Directory.CreateDirectory(_base);
        _db = new LibraryDb(Path.Combine(_base, "library.db"));
        _service = new LibraryService(_db, _volumes, new FakeProbe());

        _driveA = Path.Combine(_base, "driveA");
        Touch(_driveA, @"Serien\Test Show\Season 1\S01E01.mkv");
        Touch(_driveA, @"Serien\Test Show\Season 1\S01E02.mkv");
        Touch(_driveA, @"Filme\Inception (2010).mkv");
        Touch(_driveA, @"Filme\Heat.mkv");
        _volumes.Mounted.Add(Vol("AAAA0001", _driveA));
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_base, recursive: true);
    }

    private async Task AddDriveAFolders() => await _service.AddFolderAsync(_driveA);

    [Fact]
    public async Task Add_folder_sorts_episodes_movies_and_uncertain_files_into_the_inbox()
    {
        await AddDriveAFolders();

        var videos = _db.ListVideos();
        var episodes = videos.Where(v => v.TitleKind == TitleKind.Series).ToList();
        Assert.Equal(2, episodes.Count);
        Assert.All(episodes, e => Assert.Equal("Test Show", e.TitleName));
        Assert.Equal(new int?[] { 1, 2 }, episodes.Select(e => e.Episode).ToArray());
        Assert.Single(videos, v => v.TitleKind == TitleKind.Movie && v.TitleName == "Inception" && v.Year == 2010);
        var inbox = Assert.Single(videos, v => v.IsInbox);
        Assert.Equal("Heat", inbox.ParsedTitle);
    }

    [Fact]
    public async Task Rescan_never_overwrites_user_data()
    {
        await AddDriveAFolders();
        var series = _db.ListVideos().First(v => v.TitleKind == TitleKind.Series).TitleId!.Value;
        _db.AddGenre(series, "Drama");
        _db.RenameTitle(series, "Meine Serie");
        var heat = _db.ListVideos().Single(v => v.IsInbox);
        var heatTitle = _db.CreateTitle(TitleKind.Movie, "Heat", 1995);
        _db.AssignVideo(heat.VideoId, heatTitle, null, null);
        _db.SavePlayback(heat.VideoId, 123.5, watched: false);

        File.WriteAllText(Path.Combine(_driveA, @"Filme\Heat.mkv"), "geändert und länger");
        File.WriteAllText(Path.Combine(_driveA, @"Serien\Test Show\Season 1\S01E01.mkv"), "auch geändert");
        var result = (await _service.RescanAllAsync()).Single().Result;

        Assert.Equal(2, result.Updated);
        var videos = _db.ListVideos();
        Assert.Equal(new[] { "Drama" }, _db.GetGenres(series));
        Assert.All(videos.Where(v => v.TitleKind == TitleKind.Series), v => Assert.Equal("Meine Serie", v.TitleName));
        var heatNow = videos.Single(v => v.VideoId == heat.VideoId);
        Assert.Equal(heatTitle, heatNow.TitleId);
        Assert.True(heatNow.Locked);
        Assert.Equal(123.5, _db.GetPlayback(heat.VideoId)!.PositionSeconds);
    }

    [Fact]
    public async Task Unplugged_drive_keeps_all_entries_and_is_reported_offline()
    {
        await AddDriveAFolders();
        var before = _db.ListVideos();
        _volumes.Mounted.Clear();

        var results = await _service.RescanAllAsync();

        Assert.True(results.Single().Result.Offline);
        var after = _db.ListVideos();
        Assert.Equal(before.Count, after.Count);
        Assert.DoesNotContain(after, v => v.Missing);
    }

    [Fact]
    public async Task Replugged_drive_with_new_letter_is_recognized_and_paths_resolve_to_the_new_letter()
    {
        await AddDriveAFolders();
        var driveB = Path.Combine(_base, "driveB");
        Directory.Move(_driveA, driveB);
        _volumes.Mounted.Clear();
        _volumes.Mounted.Add(Vol("AAAA0001", driveB));

        var result = (await _service.RescanAllAsync()).Single().Result;

        Assert.False(result.Offline);
        Assert.Equal(4, result.Unchanged);
        Assert.Equal(0, result.Added);
        Assert.Single(_db.GetVolumes());
        var status = _service.RefreshVolumes();
        var video = _db.ListVideos().First();
        Assert.StartsWith(driveB, status.ResolvePath(video.VolumeId, video.RelPath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Another_drive_taking_over_the_same_letter_does_not_wipe_the_library()
    {
        await AddDriveAFolders();
        Directory.Delete(_driveA, recursive: true);
        Directory.CreateDirectory(_driveA);
        _volumes.Mounted.Clear();
        _volumes.Mounted.Add(Vol("ZZZZ9999", _driveA));

        var result = (await _service.RescanAllAsync()).Single().Result;

        Assert.True(result.Offline);
        Assert.Equal(4, _db.ListVideos().Count);
        Assert.DoesNotContain(_db.ListVideos(), v => v.Missing);
        Assert.Single(_db.GetVolumes());
    }

    [Fact]
    public async Task Deleted_file_is_only_marked_missing_until_cleanup()
    {
        await AddDriveAFolders();
        var series = _db.ListVideos().First(v => v.TitleKind == TitleKind.Series).TitleId!.Value;
        _db.AddGenre(series, "Drama");
        File.Delete(Path.Combine(_driveA, @"Filme\Inception (2010).mkv"));
        File.Delete(Path.Combine(_driveA, @"Serien\Test Show\Season 1\S01E01.mkv"));
        File.Delete(Path.Combine(_driveA, @"Serien\Test Show\Season 1\S01E02.mkv"));

        var result = (await _service.RescanAllAsync()).Single().Result;

        Assert.Equal(3, result.MarkedMissing);
        Assert.Equal(4, _db.ListVideos().Count);
        Assert.Equal(3, _db.ListVideos().Count(v => v.Missing));

        Assert.Equal(3, _db.RemoveMissingFiles());
        var remaining = _db.ListVideos();
        Assert.Single(remaining);
        Assert.Equal(new[] { "Drama" }, _db.GetGenres(series)); // Titel mit Nutzerdaten bleibt erhalten
    }

    [Fact]
    public async Task File_that_returns_loses_its_missing_flag()
    {
        await AddDriveAFolders();
        var path = Path.Combine(_driveA, @"Filme\Heat.mkv");
        var content = File.ReadAllBytes(path);
        var mtime = File.GetLastWriteTimeUtc(path);
        File.Delete(path);
        await _service.RescanAllAsync();
        Assert.Contains(_db.ListVideos(), v => v.Missing);

        File.WriteAllBytes(path, content);
        File.SetLastWriteTimeUtc(path, mtime);
        var result = (await _service.RescanAllAsync()).Single().Result;

        Assert.Equal(0, result.Added);
        Assert.DoesNotContain(_db.ListVideos(), v => v.Missing);
    }

    [Fact]
    public async Task Ambiguous_drive_requires_confirmation_until_the_user_relinks_it()
    {
        await AddDriveAFolders();
        var reformatted = Vol("AAAA0001", _driveA, capacity: 5000, fs: "exFAT");
        _volumes.Mounted.Clear();
        _volumes.Mounted.Add(reformatted);

        var ex = await Assert.ThrowsAsync<VolumeConfirmationRequiredException>(() => _service.AddFolderAsync(Path.Combine(_driveA, "Filme")));
        Assert.Single(_service.RefreshVolumes().Unresolved);
        Assert.True((await _service.RescanAllAsync()).Single().Result.Offline);

        _db.RelinkVolume(ex.Match.Candidates.Single().Id, reformatted);

        Assert.False((await _service.RescanAllAsync()).First().Result.Offline);
        Assert.Empty(_service.RefreshVolumes().Unresolved);
    }

    [Fact]
    public async Task Add_single_file_creates_a_file_source()
    {
        var extra = Path.Combine(_base, "driveA", "Extra", "Movie.2020.mkv");
        Touch(_driveA, @"Extra\Movie.2020.mkv");

        await _service.AddFileAsync(extra);

        var source = Assert.Single(_db.GetSources());
        Assert.Equal(SourceKind.File, source.Kind);
        Assert.Equal(@"Extra\Movie.2020.mkv", source.RelPath);
        Assert.Single(_db.ListVideos());
    }

    [Fact]
    public async Task Merging_titles_moves_videos_genres_and_keys()
    {
        Touch(_driveA, @"Serien\Show A (US)\S01E01.mkv");
        await AddDriveAFolders();
        var videos = _db.ListVideos();
        var a = videos.First(v => v.TitleName == "Test Show").TitleId!.Value;
        var b = videos.First(v => v.TitleName == "Show A (US)").TitleId!.Value;
        _db.AddGenre(b, "Krimi");

        _db.MergeTitles(fromId: b, intoId: a);

        var after = _db.ListVideos();
        Assert.DoesNotContain(after, v => v.TitleName == "Show A (US)");
        Assert.Equal(3, after.Count(v => v.TitleId == a));
        Assert.Equal(new[] { "Krimi" }, _db.GetGenres(a));

        // Ein späterer Scan derselben Datei landet weiter beim zusammengeführten Titel.
        File.WriteAllText(Path.Combine(_driveA, @"Serien\Show A (US)\S01E01.mkv"), "neu");
        await _service.RescanAllAsync();
        Assert.Equal(3, _db.ListVideos().Count(v => v.TitleId == a));
    }

    [Fact]
    public void Movies_and_series_cannot_be_merged()
    {
        var movie = _db.CreateTitle(TitleKind.Movie, "Heat", 1995);
        var series = _db.CreateTitle(TitleKind.Series, "Heat");

        Assert.Throws<InvalidOperationException>(() => _db.MergeTitles(movie, series));
    }

    [Fact]
    public void Same_series_name_with_different_punctuation_maps_to_one_title()
    {
        Assert.Equal(
            LibraryDb.TitleKey(TitleKind.Series, "Mr. Robot", null),
            LibraryDb.TitleKey(TitleKind.Series, "mr robot", null));
    }
}
