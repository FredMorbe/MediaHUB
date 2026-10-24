using Microsoft.Data.Sqlite;
using MediaCenter.Core;
using static MediaCenter.Core.Tests.TestSupport;

namespace MediaCenter.Core.Tests;

public sealed class SubtitleAdjustmentTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-adj-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryDb _db;
    private readonly LibraryService _service;
    private readonly string _drive;

    public SubtitleAdjustmentTests()
    {
        _drive = Path.Combine(_base, "drive");
        Touch(_drive, @"Filme\Solo (2020).mkv");
        Touch(_drive, @"Filme\Duo (2021).mkv");
        var volumes = new FakeVolumes();
        volumes.Mounted.Add(Vol("AAAA0001", _drive));
        _db = new LibraryDb(Path.Combine(_base, "library.db"));
        _service = new LibraryService(_db, volumes, new FakeProbe());
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_base, recursive: true);
    }

    private long VideoId(string title) => _db.ListVideos().Single(v => v.TitleName == title).VideoId;

    [Fact]
    public async Task Without_a_saved_row_the_defaults_apply()
    {
        await _service.AddFolderAsync(_drive);
        var adjustment = _db.GetSubtitleAdjustment(VideoId("Solo"));

        Assert.True(adjustment.IsDefault);
        Assert.Equal((0.0, 1.0, 100), (adjustment.DelaySeconds, adjustment.Scale, adjustment.Position));
    }

    [Fact]
    public async Task Adjustments_are_saved_per_video_and_replaced_on_change()
    {
        await _service.AddFolderAsync(_drive);
        var solo = VideoId("Solo");
        var duo = VideoId("Duo");

        _db.SaveSubtitleAdjustment(solo, new SubtitleAdjustment(-2.5, 1.4, 90));
        _db.SaveSubtitleAdjustment(duo, new SubtitleAdjustment(0.3));
        _db.SaveSubtitleAdjustment(solo, new SubtitleAdjustment(-2.6, 1.4, 90));

        Assert.Equal(new SubtitleAdjustment(-2.6, 1.4, 90), _db.GetSubtitleAdjustment(solo));
        Assert.Equal(new SubtitleAdjustment(0.3), _db.GetSubtitleAdjustment(duo));
    }

    [Fact]
    public async Task Saving_the_defaults_removes_the_row()
    {
        await _service.AddFolderAsync(_drive);
        var solo = VideoId("Solo");
        _db.SaveSubtitleAdjustment(solo, new SubtitleAdjustment(1));

        _db.SaveSubtitleAdjustment(solo, SubtitleAdjustment.Default);

        Assert.True(_db.GetSubtitleAdjustment(solo).IsDefault);
    }

    [Theory]
    [InlineData(601.0, 1.0, 100)]
    [InlineData(-601.0, 1.0, 100)]
    [InlineData(double.NaN, 1.0, 100)]
    [InlineData(0.0, 0.1, 100)]
    [InlineData(0.0, 3.5, 100)]
    [InlineData(0.0, 1.0, 101)]
    [InlineData(0.0, 1.0, -1)]
    public async Task Values_outside_the_range_are_rejected_loudly_and_not_saved(double delay, double scale, int position)
    {
        await _service.AddFolderAsync(_drive);
        var solo = VideoId("Solo");

        Assert.Throws<ArgumentOutOfRangeException>(() => _db.SaveSubtitleAdjustment(solo, new SubtitleAdjustment(delay, scale, position)));

        Assert.True(_db.GetSubtitleAdjustment(solo).IsDefault);
    }

    [Fact]
    public async Task Adjustments_disappear_with_their_video()
    {
        await _service.AddFolderAsync(_drive);
        var solo = VideoId("Solo");
        _db.SaveSubtitleAdjustment(solo, new SubtitleAdjustment(2));
        var path = Path.Combine(_base, "library.db");

        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA foreign_keys = ON; DELETE FROM videos WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", solo);
            cmd.ExecuteNonQuery();
        }

        Assert.True(_db.GetSubtitleAdjustment(solo).IsDefault);
    }

    [Fact]
    public async Task A_schema_4_database_is_migrated_to_the_current_schema_keeping_all_data()
    {
        await _service.AddFolderAsync(_drive);
        var solo = VideoId("Solo");
        _db.AddGenre(_db.GetTitles().First().Id, "Drama");
        _db.Dispose();

        var path = Path.Combine(_base, "library.db");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TABLE playback_old (video_id INTEGER PRIMARY KEY REFERENCES videos(id) ON DELETE CASCADE, position_sec REAL NOT NULL, watched INTEGER NOT NULL, updated_utc TEXT NOT NULL); INSERT INTO playback_old SELECT video_id, position_sec, watched, updated_utc FROM playback WHERE profile_id = 1; DROP TABLE playback; ALTER TABLE playback_old RENAME TO playback; CREATE TABLE watchlist_old (title_id INTEGER PRIMARY KEY REFERENCES titles(id) ON DELETE CASCADE, added_utc TEXT NOT NULL); INSERT INTO watchlist_old SELECT title_id, added_utc FROM watchlist WHERE profile_id = 1; DROP TABLE watchlist; ALTER TABLE watchlist_old RENAME TO watchlist; CREATE TABLE podcast_progress_old (episode_id INTEGER PRIMARY KEY REFERENCES podcast_episodes(id) ON DELETE CASCADE, position_sec REAL NOT NULL, played INTEGER NOT NULL, updated_utc TEXT NOT NULL); INSERT INTO podcast_progress_old SELECT episode_id, position_sec, played, updated_utc FROM podcast_progress WHERE profile_id = 1; DROP TABLE podcast_progress; ALTER TABLE podcast_progress_old RENAME TO podcast_progress; DROP TABLE trakt_pushed; DROP TABLE episode_air; DROP TABLE series_schedule; DROP TABLE live_epg; DROP TABLE live_channels; DROP TABLE live_sources; DROP TABLE kids_titles; DROP TABLE profiles; DROP INDEX files_fingerprint; ALTER TABLE files DROP COLUMN fingerprint; DROP TABLE collection_titles; DROP TABLE collections; DROP TABLE episode_info; DROP TABLE title_info; DROP TABLE album_artists; DROP TABLE track_artists; DROP TABLE album_names; DROP TABLE track_names; DROP TABLE watchlist; DROP TABLE playlist_items; DROP TABLE playlists; DROP TABLE removed_files; DROP TABLE album_lookups; DROP TABLE online_lookups; DROP TABLE color_settings; DROP TABLE subtitle_settings; PRAGMA user_version = 4;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var migrated = new LibraryDb(path);

        Assert.Equal("Solo", migrated.ListVideos().Single(v => v.VideoId == solo).TitleName);
        Assert.Contains("Drama", migrated.GetAllTitleGenres().Values.SelectMany(g => g));
        migrated.SaveSubtitleAdjustment(solo, new SubtitleAdjustment(1.2));
        Assert.Equal(1.2, migrated.GetSubtitleAdjustment(solo).DelaySeconds);
    }
}
