using MediaCenter.Core;
using static MediaCenter.Core.Tests.TestSupport;

namespace MediaCenter.Core.Tests;

public sealed class LibraryQueryTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-q-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryDb _db;
    private readonly LibraryService _service;
    private readonly string _drive;

    public LibraryQueryTests()
    {
        _drive = Path.Combine(_base, "drive");
        Touch(_drive, @"Serien\Show X\Season 1\S01E01.mkv");
        Touch(_drive, @"Serien\Show X\Season 1\S01E02.mkv");
        Touch(_drive, @"Filme\Solo (2020).mkv");
        var volumes = new FakeVolumes();
        volumes.Mounted.Add(Vol("AAAA0001", _drive));
        _db = new LibraryDb(Path.Combine(_base, "library.db"));
        _service = new LibraryService(_db, volumes, new FakeProbe());
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_base, recursive: true);
    }

    private long VideoId(string title, int? episode = null) =>
        _db.ListVideos().Single(v => v.TitleName == title && v.Episode == episode).VideoId;

    [Fact]
    public async Task Continue_watching_lists_one_unfinished_video_per_title_newest_first()
    {
        await _service.AddFolderAsync(_drive);
        _db.SavePlayback(VideoId("Solo"), 30, watched: false);
        Thread.Sleep(5);
        _db.SavePlayback(VideoId("Show X", 1), 50, watched: false);
        Thread.Sleep(5);
        _db.SavePlayback(VideoId("Show X", 2), 10, watched: false);

        var items = _db.GetContinueWatching();

        Assert.Equal(2, items.Count);
        Assert.Equal(VideoId("Show X", 2), items[0].VideoId); // Serie: nur die zuletzt gesehene Folge
        Assert.Equal(2, items[0].Episode);
        Assert.Equal(VideoId("Solo"), items[1].VideoId);
        Assert.Equal(30, items[1].PositionSeconds);
        Assert.Equal(100, items[1].DurationSeconds);
    }

    [Fact]
    public async Task Continue_watching_skips_watched_unstarted_and_missing_videos()
    {
        await _service.AddFolderAsync(_drive);
        _db.SavePlayback(VideoId("Solo"), 90, watched: true);
        _db.SavePlayback(VideoId("Show X", 1), 0, watched: false);
        _db.SavePlayback(VideoId("Show X", 2), 40, watched: false);
        File.Delete(Path.Combine(_drive, @"Serien\Show X\Season 1\S01E02.mkv"));
        await _service.RescanAllAsync();

        Assert.Empty(_db.GetContinueWatching());
    }

    [Fact]
    public async Task All_title_genres_come_back_in_one_map()
    {
        await _service.AddFolderAsync(_drive);
        var series = _db.GetTitles().Single(t => t.Kind == TitleKind.Series).Id;
        var movie = _db.GetTitles().Single(t => t.Kind == TitleKind.Movie).Id;
        _db.AddGenre(series, "Krimi");
        _db.AddGenre(series, "Drama");
        _db.AddGenre(movie, "Drama");

        var map = _db.GetAllTitleGenres();

        Assert.Equal(new[] { "Drama", "Krimi" }, map[series]);
        Assert.Equal(new[] { "Drama" }, map[movie]);
    }
}
