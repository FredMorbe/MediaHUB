using MediaCenter.Core;
using Microsoft.Data.Sqlite;
using static MediaCenter.Core.Tests.TestSupport;

namespace MediaCenter.Core.Tests;

public sealed class PlaylistTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-playlist-" + Guid.NewGuid().ToString("N"));
    private readonly string _drive;
    private readonly FakeVolumes _volumes = new();
    private readonly FakeProbe _probe = new();
    private readonly LibraryDb _db;
    private readonly LibraryService _library;

    public PlaylistTests()
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

    private IReadOnlyList<long> Items(long playlistId) => _db.ListPlaylistItems().GetValueOrDefault(playlistId) ?? new List<long>();

    [Fact]
    public void A_new_database_has_no_playlists()
    {
        Assert.Empty(_db.ListPlaylists());
        Assert.Empty(_db.ListPlaylistItems());
    }

    [Fact]
    public void Created_playlists_are_listed_by_name_and_the_name_is_trimmed()
    {
        _db.CreatePlaylist("  Zebra ");
        _db.CreatePlaylist("alpha");

        Assert.Equal(new[] { "alpha", "Zebra" }, _db.ListPlaylists().Select(p => p.Name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_name_is_refused(string name)
    {
        var ex = Assert.Throws<ArgumentException>(() => _db.CreatePlaylist(name));

        Assert.Contains("leer", ex.Message);
    }

    [Fact]
    public void A_name_over_the_limit_is_refused()
    {
        Assert.Throws<ArgumentException>(() => _db.CreatePlaylist(new string('x', LibraryDb.MaxPlaylistNameLength + 1)));
        _db.CreatePlaylist(new string('x', LibraryDb.MaxPlaylistNameLength)); // genau am Limit geht
    }

    [Fact]
    public void Names_are_unique_ignoring_case_for_create_and_rename()
    {
        var rock = _db.CreatePlaylist("Rock");
        var jazz = _db.CreatePlaylist("Jazz");

        var create = Assert.Throws<ArgumentException>(() => _db.CreatePlaylist("ROCK"));
        var rename = Assert.Throws<ArgumentException>(() => _db.RenamePlaylist(jazz, "rock"));

        Assert.Contains("ROCK", create.Message);
        Assert.Contains("rock", rename.Message);
        _db.RenamePlaylist(rock, "ROCK"); // die eigene Schreibweise ändern ist erlaubt
        Assert.Equal(new[] { "Jazz", "ROCK" }, _db.ListPlaylists().Select(p => p.Name));
    }

    [Fact]
    public void Renaming_or_deleting_a_missing_playlist_fails_loudly()
    {
        Assert.Throws<InvalidOperationException>(() => _db.RenamePlaylist(99, "x"));
        Assert.Throws<InvalidOperationException>(() => _db.DeletePlaylist(99));
        Assert.Throws<InvalidOperationException>(() => _db.AddTracksToPlaylist(99, new long[] { 1 }));
    }

    [Fact]
    public async Task Tracks_are_appended_in_order_and_duplicates_are_skipped()
    {
        var t = await TracksAsync("a", "b", "c");
        var list = _db.CreatePlaylist("Mix");

        Assert.Equal(2, _db.AddTracksToPlaylist(list, new[] { t[2], t[0] }));
        Assert.Equal(1, _db.AddTracksToPlaylist(list, new[] { t[0], t[1], t[1] })); // a ist schon drin, b nur einmal

        Assert.Equal(new[] { t[2], t[0], t[1] }, Items(list));
    }

    [Fact]
    public async Task Adding_a_track_that_does_not_exist_fails_loudly_and_adds_nothing()
    {
        var t = await TracksAsync("a");
        var list = _db.CreatePlaylist("Mix");

        Assert.Throws<SqliteException>(() => _db.AddTracksToPlaylist(list, new[] { t[0], 99999 }));

        Assert.Empty(Items(list)); // die Transaktion nimmt auch den ersten Titel zurück
    }

    [Fact]
    public async Task Removing_a_track_keeps_the_order_of_the_others()
    {
        var t = await TracksAsync("a", "b", "c");
        var list = _db.CreatePlaylist("Mix");
        _db.AddTracksToPlaylist(list, t);

        _db.RemoveTrackFromPlaylist(list, t[1]);
        _db.AddTracksToPlaylist(list, new[] { t[1] }); // kommt wieder ans Ende

        Assert.Equal(new[] { t[0], t[2], t[1] }, Items(list));
    }

    [Theory]
    [InlineData(0, new[] { 2, 0, 1, 3 })]   // Titel c nach ganz oben
    [InlineData(1, new[] { 0, 2, 1, 3 })]
    [InlineData(3, new[] { 0, 1, 3, 2 })]   // ans Ende
    [InlineData(-5, new[] { 2, 0, 1, 3 })]  // außerhalb: nächster gültiger Platz
    [InlineData(99, new[] { 0, 1, 3, 2 })]
    public async Task A_track_can_be_moved_to_any_position(int newIndex, int[] expected)
    {
        var t = await TracksAsync("a", "b", "c", "d");
        var list = _db.CreatePlaylist("Mix");
        _db.AddTracksToPlaylist(list, t);

        _db.MoveTrackInPlaylist(list, t[2], newIndex);

        Assert.Equal(expected.Select(i => t[i]), Items(list));
    }

    [Fact]
    public async Task Moving_a_track_that_is_not_in_the_playlist_fails_loudly()
    {
        var t = await TracksAsync("a", "b");
        var list = _db.CreatePlaylist("Mix");
        _db.AddTracksToPlaylist(list, new[] { t[0] });

        Assert.Throws<InvalidOperationException>(() => _db.MoveTrackInPlaylist(list, t[1], 0));
    }

    [Fact]
    public async Task Deleting_a_playlist_keeps_its_tracks_in_the_library_and_other_playlists()
    {
        var t = await TracksAsync("a", "b");
        var one = _db.CreatePlaylist("Eins");
        var two = _db.CreatePlaylist("Zwei");
        _db.AddTracksToPlaylist(one, t);
        _db.AddTracksToPlaylist(two, t);

        _db.DeletePlaylist(one);

        Assert.Equal(2, _db.ListTracks().Count);
        Assert.Equal(new[] { two }, _db.ListPlaylists().Select(p => p.Id));
        Assert.Equal(t, Items(two));
        Assert.Empty(Items(one));
    }

    [Fact]
    public async Task A_track_removed_with_its_file_disappears_from_the_playlists()
    {
        var t = await TracksAsync("a", "b");
        var list = _db.CreatePlaylist("Mix");
        _db.AddTracksToPlaylist(list, t);

        File.Delete(Path.Combine(_drive, @"Musik\a.mp3"));
        await _library.RescanAllAsync();
        _db.RemoveMissingFiles();

        Assert.Equal(new[] { t[1] }, Items(list));
        Assert.Single(_db.ListPlaylists()); // die Playlist selbst bleibt
    }

    [Fact]
    public async Task Rescanning_keeps_playlist_entries_because_track_ids_stay_stable()
    {
        var t = await TracksAsync("a", "b");
        var list = _db.CreatePlaylist("Mix");
        _db.AddTracksToPlaylist(list, t);

        await _library.RescanAllAsync();

        Assert.Equal(t, Items(list));
    }

    [Fact]
    public async Task BuildPlaylists_joins_tracks_in_playlist_order_and_drops_unknown_ids()
    {
        var t = await TracksAsync("a", "b", "c");
        var list = _db.CreatePlaylist("Mix");
        var empty = _db.CreatePlaylist("Leer");
        _db.AddTracksToPlaylist(list, new[] { t[2], t[0] });
        var items = _db.ListPlaylistItems();
        items[list].Add(424242); // eine Id, die es nicht (mehr) gibt

        var playlists = MusicIndex.BuildPlaylists(_db.ListPlaylists(), items, _db.ListTracks());

        Assert.Equal(new[] { "Leer", "Mix" }, playlists.Select(p => p.Name));
        Assert.Equal(new[] { "c", "a" }, playlists.Single(p => p.Id == list).Tracks.Select(x => x.Title));
        Assert.Empty(playlists.Single(p => p.Id == empty).Tracks);
    }

    [Fact]
    public void Database_at_schema_9_keeps_its_data_and_gets_working_playlists()
    {
        var path = Path.Combine(_base, "migrate9.db");
        using (var db = new LibraryDb(path)) db.SetAlbumArt("band|platte", "online", "album-1.jpg");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DROP TABLE episode_info; DROP TABLE title_info; DROP TABLE album_artists; DROP TABLE track_artists; DROP TABLE album_names; DROP TABLE track_names; DROP TABLE watchlist; DROP TABLE playlist_items; DROP TABLE playlists; ALTER TABLE color_settings DROP COLUMN lut; ALTER TABLE color_settings DROP COLUMN lut_strength; PRAGMA user_version = 9;";
            cmd.ExecuteNonQuery();
        }

        using var migrated = new LibraryDb(path);

        Assert.Equal("album-1.jpg", migrated.GetAlbumArt("band|platte"));
        migrated.CreatePlaylist("Neu");
        Assert.Equal("Neu", migrated.ListPlaylists().Single().Name);
        Assert.Empty(migrated.ListPlaylistItems());
    }
}
