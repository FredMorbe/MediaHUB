using MediaCenter.Core;
using Microsoft.Data.Sqlite;
using static MediaCenter.Core.Tests.TestSupport;

namespace MediaCenter.Core.Tests;

/// <summary>Filme, Serien und Videos aus der Bibliothek nehmen: die Dateien auf der Platte bleiben, ein Scan holt sie nicht zurück.</summary>
public sealed class RemoveFromLibraryTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-remove-" + Guid.NewGuid().ToString("N"));
    private readonly string _drive;
    private readonly FakeVolumes _volumes = new();
    private LibraryDb _db;
    private LibraryService _service;

    public RemoveFromLibraryTests()
    {
        Directory.CreateDirectory(_base);
        _db = new LibraryDb(Path.Combine(_base, "library.db"));
        _service = new LibraryService(_db, _volumes, new FakeProbe());

        _drive = Path.Combine(_base, "drive");
        Touch(_drive, @"Serien\Test Show\Season 1\S01E01.mkv");
        Touch(_drive, @"Serien\Test Show\Season 1\S01E02.mkv");
        Touch(_drive, @"Filme\Inception (2010).mkv");
        Touch(_drive, @"Filme\Heat.mkv"); // ohne Jahr: Inbox
        _volumes.Mounted.Add(Vol("AAAA0001", _drive));
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_base, recursive: true);
    }

    private string Abs(string rel) => Path.Combine(_drive, rel);

    private VideoRow Video(string fileStart) => _db.ListVideos().Single(v => Path.GetFileName(v.RelPath).StartsWith(fileStart, StringComparison.OrdinalIgnoreCase));

    private async Task Add() => await _service.AddFolderAsync(_drive);

    [Fact]
    public async Task Removing_a_movie_takes_it_and_its_data_out_of_the_library_but_leaves_the_file()
    {
        await Add();
        var movie = Video("Inception");
        _db.AddGenre(movie.TitleId!.Value, "Thriller");
        _db.SavePlayback(movie.VideoId, 600, watched: false);

        var removed = _db.RemoveTitle(movie.TitleId.Value);

        Assert.Equal(1, removed);
        Assert.DoesNotContain(_db.GetTitles(), t => t.Name == "Inception");
        Assert.DoesNotContain(_db.ListVideos(), v => v.VideoId == movie.VideoId);
        Assert.Null(_db.GetPlayback(movie.VideoId));
        Assert.True(File.Exists(Abs(@"Filme\Inception (2010).mkv")));
    }

    [Fact]
    public async Task A_rescan_does_not_bring_a_removed_file_back_and_does_not_touch_the_others()
    {
        await Add();
        _db.RemoveTitle(Video("Inception").TitleId!.Value);

        var result = (await _service.RescanAllAsync()).Single().Result;

        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.MarkedMissing);
        Assert.DoesNotContain(_db.ListVideos(), v => v.RelPath.Contains("Inception"));
        Assert.Equal(3, _db.ListVideos().Count); // zwei Folgen und Heat
    }

    [Fact]
    public async Task Removing_the_last_episode_removes_the_series_with_its_genres_but_one_episode_leaves_it()
    {
        await Add();
        var first = Video("S01E01");
        var seriesId = first.TitleId!.Value;
        _db.AddGenre(seriesId, "Drama");

        _db.RemoveVideos(new[] { first.VideoId });
        Assert.Contains(_db.GetTitles(), t => t.Id == seriesId);
        Assert.Equal(new[] { "Drama" }, _db.GetGenres(seriesId));

        _db.RemoveVideos(new[] { Video("S01E02").VideoId });
        Assert.DoesNotContain(_db.GetTitles(), t => t.Id == seriesId);
        Assert.Empty(_db.GetGenres(seriesId));
    }

    [Fact]
    public async Task Removing_a_series_removes_all_its_episodes()
    {
        await Add();
        var seriesId = Video("S01E01").TitleId!.Value;

        Assert.Equal(2, _db.RemoveTitle(seriesId));

        Assert.DoesNotContain(_db.ListVideos(), v => v.TitleId == seriesId);
        Assert.DoesNotContain(_db.GetTitles(), t => t.Id == seriesId);
        Assert.True(File.Exists(Abs(@"Serien\Test Show\Season 1\S01E01.mkv")));
        Assert.True(File.Exists(Abs(@"Serien\Test Show\Season 1\S01E02.mkv")));
    }

    [Fact]
    public async Task A_title_without_any_video_can_be_removed_too()
    {
        var empty = _db.CreateTitle(TitleKind.Movie, "Leer", 2000);

        Assert.Equal(0, _db.RemoveTitle(empty));

        Assert.DoesNotContain(_db.GetTitles(), t => t.Id == empty);
        await Add(); // nur damit der Test dieselben Dateien wie die anderen hat
    }

    [Fact]
    public async Task A_video_from_the_inbox_can_be_removed_and_stays_away_after_a_rescan()
    {
        await Add();
        var heat = Assert.Single(_db.ListVideos(), v => v.IsInbox);

        _db.RemoveVideos(new[] { heat.VideoId });
        await _service.RescanAllAsync();

        Assert.DoesNotContain(_db.ListVideos(), v => v.IsInbox);
    }

    [Fact]
    public async Task Adding_a_removed_file_explicitly_brings_it_back()
    {
        await Add();
        _db.RemoveTitle(Video("Inception").TitleId!.Value);

        await _service.AddFileAsync(Abs(@"Filme\Inception (2010).mkv"));

        Assert.Contains(_db.ListVideos(), v => v.TitleName == "Inception");
        Assert.Equal(0, _db.CountRemovedFiles());
    }

    [Fact]
    public async Task Clearing_the_list_lets_the_next_scan_take_everything_back()
    {
        await Add();
        _db.RemoveTitle(Video("Inception").TitleId!.Value);
        _db.RemoveTitle(Video("S01E01").TitleId!.Value);
        Assert.Equal(3, _db.CountRemovedFiles());

        Assert.Equal(3, _db.ClearRemovedFiles());
        var result = (await _service.RescanAllAsync()).Single().Result;

        Assert.Equal(3, result.Added);
        Assert.Equal(0, _db.CountRemovedFiles());
        Assert.Contains(_db.ListVideos(), v => v.TitleName == "Inception");
    }

    [Fact]
    public async Task Removing_a_single_file_source_removes_the_source_too()
    {
        await _service.AddFileAsync(Abs(@"Filme\Inception (2010).mkv"));
        var video = Video("Inception");
        Assert.Single(_db.GetSources(), s => s.Kind == SourceKind.File);

        _db.RemoveVideos(new[] { video.VideoId });

        Assert.Empty(_db.GetSources());
        Assert.Empty(_db.ListVideos());
    }

    [Fact]
    public async Task An_unknown_video_stops_everything_and_removes_nothing()
    {
        await Add();
        var movie = Video("Inception");

        Assert.Throws<InvalidOperationException>(() => _db.RemoveVideos(new[] { movie.VideoId, 99999L }));

        Assert.Contains(_db.ListVideos(), v => v.VideoId == movie.VideoId);
        Assert.Equal(0, _db.CountRemovedFiles());
    }

    [Fact]
    public async Task A_library_of_the_previous_schema_gets_the_list_and_keeps_its_data()
    {
        await Add();
        var count = _db.ListVideos().Count;
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        var path = Path.Combine(_base, "library.db");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DROP TABLE collection_titles; DROP TABLE collections; DROP TABLE episode_info; DROP TABLE title_info; DROP TABLE album_artists; DROP TABLE track_artists; DROP TABLE album_names; DROP TABLE track_names; DROP TABLE watchlist; DROP TABLE playlist_items; DROP TABLE playlists; DROP TABLE removed_files; DROP TABLE album_lookups; ALTER TABLE color_settings DROP COLUMN lut; ALTER TABLE color_settings DROP COLUMN lut_strength; PRAGMA user_version = 7;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        _db = new LibraryDb(path);
        _service = new LibraryService(_db, _volumes, new FakeProbe());

        Assert.Equal(count, _db.ListVideos().Count);
        Assert.Equal(0, _db.CountRemovedFiles());
        Assert.Equal(1, _db.RemoveVideos(new[] { Video("Inception").VideoId }));
    }
}
