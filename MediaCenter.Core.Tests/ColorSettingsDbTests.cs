using Microsoft.Data.Sqlite;
using MediaCenter.Core;
using static MediaCenter.Core.Tests.TestSupport;

namespace MediaCenter.Core.Tests;

public sealed class ColorSettingsDbTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-color-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryDb _db;
    private readonly LibraryService _service;
    private readonly string _drive;

    public ColorSettingsDbTests()
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

        Assert.True(_db.GetColorSettings(VideoId("Solo")).IsDefault);
    }

    [Fact]
    public async Task Settings_are_saved_per_video_and_replaced_on_change()
    {
        await _service.AddFolderAsync(_drive);
        var solo = VideoId("Solo");
        var duo = VideoId("Duo");
        var warm = new ColorSettings(Brightness: 20, Contrast: -5, Saturation: 30, Hue: -10, Gamma: 15, Temperature: 40, Tint: -20, Sharpness: 25, Invert: true);

        _db.SaveColorSettings(solo, warm);
        _db.SaveColorSettings(duo, new ColorSettings(Brightness: -50));
        _db.SaveColorSettings(duo, new ColorSettings(Brightness: -60));

        Assert.Equal(warm, _db.GetColorSettings(solo));
        Assert.Equal(new ColorSettings(Brightness: -60), _db.GetColorSettings(duo));
    }

    [Fact]
    public async Task Saving_the_defaults_removes_the_row()
    {
        await _service.AddFolderAsync(_drive);
        var solo = VideoId("Solo");
        _db.SaveColorSettings(solo, new ColorSettings(Invert: true));

        _db.SaveColorSettings(solo, ColorSettings.Default);

        Assert.True(_db.GetColorSettings(solo).IsDefault);
    }

    [Fact]
    public async Task Values_outside_the_range_are_rejected_loudly_and_not_saved()
    {
        await _service.AddFolderAsync(_drive);
        var solo = VideoId("Solo");

        Assert.Throws<ArgumentOutOfRangeException>(() => _db.SaveColorSettings(solo, new ColorSettings(Brightness: 101)));
        Assert.Throws<ArgumentOutOfRangeException>(() => _db.SaveColorSettings(solo, new ColorSettings(Sharpness: -101)));

        Assert.True(_db.GetColorSettings(solo).IsDefault);
    }

    [Fact]
    public async Task Settings_disappear_with_their_video()
    {
        await _service.AddFolderAsync(_drive);
        var solo = VideoId("Solo");
        _db.SaveColorSettings(solo, new ColorSettings(Hue: 5));
        var path = Path.Combine(_base, "library.db");

        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA foreign_keys = ON; DELETE FROM videos WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", solo);
            cmd.ExecuteNonQuery();
        }

        Assert.True(_db.GetColorSettings(solo).IsDefault);
    }

    [Fact]
    public async Task A_schema_5_database_is_migrated_to_6_keeping_all_data()
    {
        await _service.AddFolderAsync(_drive);
        var solo = VideoId("Solo");
        _db.SaveSubtitleAdjustment(solo, new SubtitleAdjustment(1.5));
        _db.Dispose();

        var path = Path.Combine(_base, "library.db");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TABLE playback_old (video_id INTEGER PRIMARY KEY REFERENCES videos(id) ON DELETE CASCADE, position_sec REAL NOT NULL, watched INTEGER NOT NULL, updated_utc TEXT NOT NULL); INSERT INTO playback_old SELECT video_id, position_sec, watched, updated_utc FROM playback WHERE profile_id = 1; DROP TABLE playback; ALTER TABLE playback_old RENAME TO playback; CREATE TABLE watchlist_old (title_id INTEGER PRIMARY KEY REFERENCES titles(id) ON DELETE CASCADE, added_utc TEXT NOT NULL); INSERT INTO watchlist_old SELECT title_id, added_utc FROM watchlist WHERE profile_id = 1; DROP TABLE watchlist; ALTER TABLE watchlist_old RENAME TO watchlist; CREATE TABLE podcast_progress_old (episode_id INTEGER PRIMARY KEY REFERENCES podcast_episodes(id) ON DELETE CASCADE, position_sec REAL NOT NULL, played INTEGER NOT NULL, updated_utc TEXT NOT NULL); INSERT INTO podcast_progress_old SELECT episode_id, position_sec, played, updated_utc FROM podcast_progress WHERE profile_id = 1; DROP TABLE podcast_progress; ALTER TABLE podcast_progress_old RENAME TO podcast_progress; DROP TABLE kids_titles; DROP TABLE profiles; DROP INDEX files_fingerprint; ALTER TABLE files DROP COLUMN fingerprint; DROP TABLE collection_titles; DROP TABLE collections; DROP TABLE episode_info; DROP TABLE title_info; DROP TABLE album_artists; DROP TABLE track_artists; DROP TABLE album_names; DROP TABLE track_names; DROP TABLE watchlist; DROP TABLE playlist_items; DROP TABLE playlists; DROP TABLE removed_files; DROP TABLE album_lookups; DROP TABLE online_lookups; DROP TABLE color_settings; PRAGMA user_version = 5;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var migrated = new LibraryDb(path);

        Assert.Equal(1.5, migrated.GetSubtitleAdjustment(solo).DelaySeconds); // Daten aus Schema 5 bleiben
        migrated.SaveColorSettings(solo, new ColorSettings(Saturation: 10));
        Assert.Equal(10, migrated.GetColorSettings(solo).Saturation);
    }

    [Fact]
    public async Task The_lut_and_its_strength_are_saved_per_video()
    {
        await _service.AddFolderAsync(_drive);
        var solo = VideoId("Solo");
        var duo = VideoId("Duo");

        _db.SaveColorSettings(solo, new ColorSettings(Brightness: 5, Lut: "Kodak 2383", LutStrength: 40));
        _db.SaveColorSettings(duo, new ColorSettings(Lut: "Kodak 2383"));

        Assert.Equal(new ColorSettings(Brightness: 5, Lut: "Kodak 2383", LutStrength: 40), _db.GetColorSettings(solo));
        Assert.Equal("Kodak 2383", _db.GetColorSettings(duo).Lut);
        Assert.Equal(100, _db.GetColorSettings(duo).LutStrength);

        _db.SaveColorSettings(solo, ColorSettings.Default with { Brightness = 5 }); // LUT wieder aus
        Assert.Null(_db.GetColorSettings(solo).Lut);
    }

    [Fact]
    public async Task A_schema_13_database_gets_the_lut_columns_keeping_saved_colors()
    {
        await _service.AddFolderAsync(_drive);
        var solo = VideoId("Solo");
        _db.SaveColorSettings(solo, new ColorSettings(Saturation: 10));
        _db.Dispose();

        var path = Path.Combine(_base, "library.db");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TABLE playback_old (video_id INTEGER PRIMARY KEY REFERENCES videos(id) ON DELETE CASCADE, position_sec REAL NOT NULL, watched INTEGER NOT NULL, updated_utc TEXT NOT NULL); INSERT INTO playback_old SELECT video_id, position_sec, watched, updated_utc FROM playback WHERE profile_id = 1; DROP TABLE playback; ALTER TABLE playback_old RENAME TO playback; CREATE TABLE watchlist_old (title_id INTEGER PRIMARY KEY REFERENCES titles(id) ON DELETE CASCADE, added_utc TEXT NOT NULL); INSERT INTO watchlist_old SELECT title_id, added_utc FROM watchlist WHERE profile_id = 1; DROP TABLE watchlist; ALTER TABLE watchlist_old RENAME TO watchlist; CREATE TABLE podcast_progress_old (episode_id INTEGER PRIMARY KEY REFERENCES podcast_episodes(id) ON DELETE CASCADE, position_sec REAL NOT NULL, played INTEGER NOT NULL, updated_utc TEXT NOT NULL); INSERT INTO podcast_progress_old SELECT episode_id, position_sec, played, updated_utc FROM podcast_progress WHERE profile_id = 1; DROP TABLE podcast_progress; ALTER TABLE podcast_progress_old RENAME TO podcast_progress; DROP TABLE kids_titles; DROP TABLE profiles; DROP INDEX files_fingerprint; ALTER TABLE files DROP COLUMN fingerprint; DROP TABLE collection_titles; DROP TABLE collections; DROP TABLE episode_info; DROP TABLE title_info; ALTER TABLE color_settings DROP COLUMN lut; ALTER TABLE color_settings DROP COLUMN lut_strength; PRAGMA user_version = 13;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var migrated = new LibraryDb(path);

        Assert.Equal(new ColorSettings(Saturation: 10), migrated.GetColorSettings(solo));
        migrated.SaveColorSettings(solo, new ColorSettings(Saturation: 10, Lut: "Look", LutStrength: 70));
        Assert.Equal("Look", migrated.GetColorSettings(solo).Lut);
    }
}
