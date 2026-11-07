using MediaCenter.Core;
using Microsoft.Data.Sqlite;
using static MediaCenter.Core.Tests.TestSupport;

namespace MediaCenter.Core.Tests;

public sealed class FavoritesTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-favorites-" + Guid.NewGuid().ToString("N"));
    private readonly string _drive;
    private readonly FakeVolumes _volumes = new();
    private readonly FakeProbe _probe = new();
    private readonly LibraryDb _db;
    private readonly LibraryService _library;

    public FavoritesTests()
    {
        _drive = Path.Combine(_base, "drive");
        Directory.CreateDirectory(_drive);
        _db = new LibraryDb(Path.Combine(_base, "library.db"));
        _volumes.Mounted.Add(Vol("AAAA0001", _drive));
        _library = new LibraryService(_db, _volumes, _probe);
        _probe.Respond = path => new ProbeResult(
            200, "mp3",
            new[] { new StreamInfo(0, "audio", "mp3", null, null, null, 2, null, null, true, false, null) },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["title"] = Path.GetFileNameWithoutExtension(path), ["artist"] = "Band", ["album"] = "Platte",
            });
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_base, recursive: true);
    }

    /// <summary>Legt Musikdateien an, scannt sie und liefert die Titel-Ids in der Reihenfolge der Namen.</summary>
    private async Task<long[]> TracksAsync(params string[] names)
    {
        foreach (var name in names) Touch(_drive, $@"Musik\{name}.mp3");
        await _library.AddFolderAsync(_drive);
        var byTitle = _db.ListTracks().ToDictionary(t => t.Title, t => t.TrackId);
        return names.Select(n => byTitle[n]).ToArray();
    }

    [Fact]
    public void A_new_database_has_no_favorites()
    {
        Assert.Empty(_db.GetFavoriteTrackIds());
    }

    [Fact]
    public async Task Marked_tracks_are_listed_newest_first_and_unmarking_removes_them()
    {
        var ids = await TracksAsync("a", "b", "c");

        foreach (var id in ids) _db.SetTrackFavorite(id, true);
        Assert.Equal(new[] { ids[2], ids[1], ids[0] }, _db.GetFavoriteTrackIds());

        _db.SetTrackFavorite(ids[1], false);
        Assert.Equal(new[] { ids[2], ids[0] }, _db.GetFavoriteTrackIds());
    }

    [Fact]
    public async Task Marking_twice_or_unmarking_a_track_that_is_not_a_favorite_changes_nothing()
    {
        var ids = await TracksAsync("a", "b");
        _db.SetTrackFavorite(ids[0], true);
        _db.SetTrackFavorite(ids[0], true);
        _db.SetTrackFavorite(ids[1], false);

        Assert.Equal(new[] { ids[0] }, _db.GetFavoriteTrackIds());
    }

    [Fact]
    public void Marking_a_track_that_does_not_exist_is_an_error()
    {
        Assert.Throws<SqliteException>(() => _db.SetTrackFavorite(9999, true));
        Assert.Empty(_db.GetFavoriteTrackIds());
    }

    [Fact]
    public async Task Favorites_belong_to_the_profile_and_go_with_it()
    {
        var ids = await TracksAsync("a", "b");
        _db.SetTrackFavorite(ids[0], true);

        var other = _db.CreateProfile("Zweiter", "#FF8800", kids: false);
        _db.SetActiveProfile(other);
        Assert.Empty(_db.GetFavoriteTrackIds());
        _db.SetTrackFavorite(ids[1], true);
        Assert.Equal(new[] { ids[1] }, _db.GetFavoriteTrackIds());

        _db.SetActiveProfile(LibraryDb.DefaultProfileId);
        Assert.Equal(new[] { ids[0] }, _db.GetFavoriteTrackIds());

        _db.DeleteProfile(other);
        Assert.Equal(new[] { ids[0] }, _db.GetFavoriteTrackIds());
    }

    [Fact]
    public async Task A_track_that_leaves_the_library_leaves_the_favorites()
    {
        var ids = await TracksAsync("a", "b");
        _db.SetTrackFavorite(ids[0], true);
        _db.SetTrackFavorite(ids[1], true);

        File.Delete(Path.Combine(_drive, @"Musik\a.mp3"));
        await _library.RescanAllAsync();
        _db.RemoveMissingFiles();

        Assert.Equal(new[] { ids[1] }, _db.GetFavoriteTrackIds());
    }

    [Fact]
    public void A_database_of_the_previous_schema_is_migrated()
    {
        var path = Path.Combine(_base, "old25.db");
        using (new LibraryDb(path)) { }
        SqliteConnection.ClearAllPools();
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DROP TABLE track_favorites; PRAGMA user_version = 25;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var migrated = new LibraryDb(path);

        Assert.Empty(migrated.GetFavoriteTrackIds());
    }
}
