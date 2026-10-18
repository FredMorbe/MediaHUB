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
            cmd.CommandText = "DROP TABLE removed_files; DROP TABLE album_lookups; DROP TABLE online_lookups; DROP TABLE color_settings; PRAGMA user_version = 5;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var migrated = new LibraryDb(path);

        Assert.Equal(1.5, migrated.GetSubtitleAdjustment(solo).DelaySeconds); // Daten aus Schema 5 bleiben
        migrated.SaveColorSettings(solo, new ColorSettings(Saturation: 10));
        Assert.Equal(10, migrated.GetColorSettings(solo).Saturation);
    }
}
