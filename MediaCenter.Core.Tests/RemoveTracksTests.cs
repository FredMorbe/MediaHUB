using MediaCenter.Core;
using Microsoft.Data.Sqlite;
using static MediaCenter.Core.Tests.TestSupport;

namespace MediaCenter.Core.Tests;

public sealed class RemoveTracksTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-removetracks-" + Guid.NewGuid().ToString("N"));
    private readonly string _drive;
    private readonly FakeVolumes _volumes = new();
    private readonly FakeProbe _probe = new();
    private readonly LibraryDb _db;
    private readonly LibraryService _library;

    public RemoveTracksTests()
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

    private async Task<long[]> TracksAsync(params string[] names)
    {
        foreach (var name in names) Touch(_drive, $@"Musik\{name}.mp3");
        await _library.AddFolderAsync(_drive);
        var byTitle = _db.ListTracks().ToDictionary(t => t.Title, t => t.TrackId);
        return names.Select(n => byTitle[n]).ToArray();
    }

    [Fact]
    public async Task A_removed_track_leaves_the_library_but_its_file_stays_on_disk()
    {
        var ids = await TracksAsync("a", "b", "c");

        Assert.Equal(1, _db.RemoveTracks(new[] { ids[1] }));

        Assert.Equal(new[] { "a", "c" }, _db.ListTracks().Select(t => t.Title).OrderBy(t => t));
        Assert.True(File.Exists(Path.Combine(_drive, @"Musik\b.mp3")));
        Assert.Equal(1, _db.CountRemovedFiles());
    }

    [Fact]
    public async Task A_rescan_does_not_bring_a_removed_track_back_until_the_list_is_cleared()
    {
        var ids = await TracksAsync("a", "b");
        _db.RemoveTracks(new[] { ids[0] });

        await _library.RescanAllAsync();
        Assert.Equal(new[] { "b" }, _db.ListTracks().Select(t => t.Title));

        Assert.Equal(1, _db.ClearRemovedFiles());
        await _library.RescanAllAsync();
        Assert.Equal(new[] { "a", "b" }, _db.ListTracks().Select(t => t.Title).OrderBy(t => t));
    }

    [Fact]
    public async Task Favorites_and_playlist_places_of_a_removed_track_go_with_it()
    {
        var ids = await TracksAsync("a", "b");
        _db.SetTrackFavorite(ids[0], true);
        _db.SetTrackFavorite(ids[1], true);
        var list = _db.CreatePlaylist("Mix");
        _db.AddTracksToPlaylist(list, ids);

        _db.RemoveTracks(new[] { ids[0] });

        Assert.Equal(new[] { ids[1] }, _db.GetFavoriteTrackIds());
        Assert.Equal(new[] { ids[1] }, _db.ListPlaylistItems()[list]);
        Assert.Single(_db.ListPlaylists()); // die Playlist selbst bleibt
    }

    [Fact]
    public async Task Several_tracks_can_be_removed_at_once_and_an_unknown_id_removes_nothing()
    {
        var ids = await TracksAsync("a", "b", "c");

        var ex = Assert.Throws<InvalidOperationException>(() => _db.RemoveTracks(new[] { ids[0], 9999 }));
        Assert.Contains("9999", ex.Message);
        Assert.Equal(3, _db.ListTracks().Count); // alles oder nichts
        Assert.Equal(0, _db.CountRemovedFiles());

        Assert.Equal(2, _db.RemoveTracks(new[] { ids[0], ids[2], ids[0] }));
        Assert.Equal(new[] { "b" }, _db.ListTracks().Select(t => t.Title));
    }

    [Fact]
    public async Task A_single_added_file_is_forgotten_as_source_too()
    {
        Touch(_drive, @"Einzeln\x.mp3");
        await _library.AddFileAsync(Path.Combine(_drive, @"Einzeln\x.mp3"));
        var id = _db.ListTracks().Single().TrackId;

        _db.RemoveTracks(new[] { id });
        await _library.RescanAllAsync();

        Assert.Empty(_db.ListTracks());
        Assert.True(File.Exists(Path.Combine(_drive, @"Einzeln\x.mp3")));
    }
}
