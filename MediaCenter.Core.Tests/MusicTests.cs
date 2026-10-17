using MediaCenter.Core;
using Microsoft.Data.Sqlite;
using static MediaCenter.Core.Tests.TestSupport;

namespace MediaCenter.Core.Tests;

public sealed class TrackTagsTests
{
    private static IReadOnlyDictionary<string, string> Tags(params (string, string)[] pairs) =>
        new Dictionary<string, string>(pairs.ToDictionary(p => p.Item1, p => p.Item2), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Tags_win_and_numbers_like_3_of_12_and_dates_are_reduced()
    {
        var t = TrackTags.From(
            Tags(("TITLE", "Blue in Green"), ("artist", "Miles Davis"), ("album_artist", "Miles Davis"), ("album", "Kind of Blue"),
                ("track", "3/5"), ("disc", "1/2"), ("date", "1959-08-17"), ("genre", "Jazz")),
            @"D:\Musik\Kind of Blue\03 - something else.flac");

        Assert.Equal(new TrackTags("Blue in Green", "Miles Davis", "Miles Davis", "Kind of Blue", 3, 1, 1959, "Jazz"), t);
    }

    [Theory]
    [InlineData("07 - Roads.mp3", 7, "Roads")]
    [InlineData("07. Roads.mp3", 7, "Roads")]
    [InlineData("07_Roads.mp3", 7, "Roads")]
    [InlineData("07 Roads.mp3", 7, "Roads")]
    [InlineData("7 Nation Army.mp3", null, "7 Nation Army")]
    [InlineData("1999.mp3", null, "1999")]
    [InlineData("Roads.mp3", null, "Roads")]
    public void Without_tags_title_and_track_number_come_from_the_file_name(string file, int? number, string title)
    {
        var t = TrackTags.From(null, @"D:\Musik\Portishead - Dummy\" + file);

        Assert.Equal(title, t.Title);
        Assert.Equal(number, t.TrackNo);
        Assert.Equal("Portishead - Dummy", t.Album); // ohne Album-Tag dient der Ordner
        Assert.Null(t.Artist);
    }
}

public sealed class FfprobeTagTests
{
    [Fact]
    public void Container_tags_are_read_case_insensitively_and_beat_stream_tags()
    {
        var json = """
            { "streams": [ { "index": 0, "codec_type": "audio", "codec_name": "opus", "tags": { "TITLE": "Stream-Titel", "ARTIST": "Band" } } ],
              "format": { "duration": "200", "format_name": "ogg", "tags": { "title": "Container-Titel" } } }
            """;

        var probe = FfprobeReader.Parse(json);

        Assert.Equal("Container-Titel", probe.Tags!["TITLE"]);
        Assert.Equal("Band", probe.Tags["artist"]); // fehlt am Container, kommt vom Audiostream
        Assert.True(probe.HasAudio);
        Assert.Null(probe.Video);
    }
}

public sealed class MusicIndexTests
{
    private static TrackRow Track(long id, string title, string? artist, string? albumArtist, string? album, int? no, int? disc = null, int? year = null) =>
        new(id, id, 1, $"t{id}.mp3", false, title, artist, albumArtist, album, no, disc, year, null, 180);

    [Fact]
    public void Tracks_are_grouped_into_albums_ordered_by_disc_and_track_and_albums_by_artist_and_year()
    {
        var tracks = new[]
        {
            Track(1, "B-Seite", "Zebra", null, "Zweites", 2, year: 2010),
            Track(2, "A-Seite", "Zebra", null, "Zweites", 1, year: 2010),
            Track(3, "Eins", "Alpha", null, "Debüt", 1, year: 2001),
            Track(4, "Disc 2 Track 1", "Zebra", null, "Zweites", 1, disc: 2, year: 2010),
            Track(5, "Alt", "Zebra", null, "Erstes", 1, year: 2005),
        };

        var albums = MusicIndex.BuildAlbums(tracks);

        Assert.Equal(new[] { "Debüt", "Erstes", "Zweites" }, albums.Select(a => a.Title));
        Assert.Equal(new[] { "A-Seite", "B-Seite", "Disc 2 Track 1" }, albums[2].Tracks.Select(t => t.Title));
        Assert.Equal(2010, albums[2].Year);
    }

    [Fact]
    public void Album_artist_tag_keeps_a_compilation_together_and_spelling_does_not_split_an_album()
    {
        var tracks = new[]
        {
            Track(1, "x", "Eins", "Various Artists", "Sampler", 1),
            Track(2, "y", "Zwei", "Various Artists", "Sampler", 2),
            Track(3, "z", "The Band", null, "Live!", 1),
            Track(4, "w", "the band", null, "live", 2),
        };

        var albums = MusicIndex.BuildAlbums(tracks);

        Assert.Equal(2, albums.Count);
        Assert.Equal(2, albums.Single(a => a.Artist == "Various Artists").Tracks.Count);
        Assert.Equal(2, albums.Single(a => a.Title.StartsWith("Live", StringComparison.OrdinalIgnoreCase)).Tracks.Count);
    }

    [Fact]
    public void Tracks_without_tags_land_under_the_unknown_artist_and_album()
    {
        var albums = MusicIndex.BuildAlbums(new[] { Track(1, "x", null, null, null, null) });

        Assert.Equal(MusicIndex.UnknownArtist, albums.Single().Artist);
        Assert.Equal(MusicIndex.UnknownAlbum, albums.Single().Title);
    }

    [Fact]
    public void Artists_collect_their_albums()
    {
        var albums = MusicIndex.BuildAlbums(new[]
        {
            Track(1, "a", "Zebra", null, "Erstes", 1), Track(2, "b", "Zebra", null, "Zweites", 1), Track(3, "c", "Alpha", null, "Debüt", 1),
        });

        var artists = MusicIndex.BuildArtists(albums);

        Assert.Equal(new[] { "Alpha", "Zebra" }, artists.Select(a => a.Name));
        Assert.Equal(2, artists[1].Albums.Count);
    }
}

public sealed class MusicLibraryTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-music-" + Guid.NewGuid().ToString("N"));
    private readonly string _drive;
    private readonly string _cache;
    private readonly FakeVolumes _volumes = new();
    private readonly FakeProbe _probe = new();
    private readonly FakeImageTool _tool = new();
    private readonly LibraryDb _db;
    private readonly LibraryService _library;
    private readonly ArtworkService _art;

    public MusicLibraryTests()
    {
        _drive = Path.Combine(_base, "drive");
        _cache = Path.Combine(_base, "cache");
        Directory.CreateDirectory(_drive);
        _db = new LibraryDb(Path.Combine(_base, "library.db"));
        _volumes.Mounted.Add(Vol("AAAA0001", _drive));
        _library = new LibraryService(_db, _volumes, _probe);
        _art = new ArtworkService(_db, _tool, _probe, _cache);
        _probe.Respond = path => MediaExtensions.Audio.Contains(Path.GetExtension(path)) ? AudioProbe(path) : FakeProbe.VideoOnly;
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_base, recursive: true);
    }

    private static ProbeResult AudioProbe(string path, params StreamInfo[] extra) => new(
        200, "mp3",
        new[] { new StreamInfo(0, "audio", "mp3", null, null, null, 2, null, null, true, false, null) }.Concat(extra).ToList(),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["title"] = Path.GetFileNameWithoutExtension(path), ["artist"] = "Band", ["album"] = "Platte", ["track"] = "1",
        });

    [Fact]
    public async Task Scan_stores_audio_files_as_tracks_and_leaves_videos_alone()
    {
        Touch(_drive, @"Musik\Platte\eins.mp3");
        Touch(_drive, @"Musik\Platte\zwei.flac");
        Touch(_drive, @"Filme\Solo (2020).mkv");

        var (_, scan) = await _library.AddFolderAsync(_drive);

        Assert.Equal(3, scan.Added);
        Assert.Empty(scan.Failures);
        var tracks = _db.ListTracks();
        Assert.Equal(new[] { "eins", "zwei" }, tracks.Select(t => t.Title).OrderBy(t => t));
        Assert.All(tracks, t => Assert.Equal("Band", t.Artist));
        Assert.Equal(200, tracks[0].DurationSeconds);
        Assert.Single(_db.ListVideos()); // nur der Film
    }

    [Fact]
    public async Task Rescan_updates_tags_in_place_and_marks_deleted_tracks_missing_without_losing_the_others()
    {
        Touch(_drive, @"Musik\eins.mp3");
        Touch(_drive, @"Musik\zwei.mp3");
        await _library.AddFolderAsync(_drive);
        var idBefore = _db.ListTracks().Single(t => t.Title == "eins").TrackId;

        _probe.Respond = path => new ProbeResult(200, "mp3", AudioProbe(path).Streams,
            new Dictionary<string, string> { ["title"] = Path.GetFileNameWithoutExtension(path) + " (neu getaggt)" });
        File.WriteAllText(Path.Combine(_drive, @"Musik\eins.mp3"), "geändert und länger");
        File.Delete(Path.Combine(_drive, @"Musik\zwei.mp3"));
        await _library.RescanAllAsync();

        var tracks = _db.ListTracks();
        var one = tracks.Single(t => t.Title.StartsWith("eins"));
        Assert.Equal(idBefore, one.TrackId);
        Assert.Equal("eins (neu getaggt)", one.Title);
        Assert.False(one.Missing);
        Assert.True(tracks.Single(t => t.Title == "zwei").Missing);
    }

    [Fact]
    public async Task Audio_file_without_an_audio_stream_is_reported_as_a_failure()
    {
        Touch(_drive, @"Musik\kaputt.mp3");
        _probe.Respond = _ => FakeProbe.VideoOnly;

        var (_, scan) = await _library.AddFolderAsync(_drive);

        var failure = Assert.Single(scan.Failures);
        Assert.Contains("kaputt.mp3", failure.Path);
        Assert.Contains("Audiostream", failure.Error);
        Assert.Empty(_db.ListTracks());
    }

    [Fact]
    public async Task Album_cover_comes_from_the_folder_when_all_tracks_share_it()
    {
        Touch(_drive, @"Musik\Platte\eins.mp3");
        Touch(_drive, @"Musik\Platte\zwei.mp3");
        Touch(_drive, @"Musik\Platte\Folder.JPG");
        await _library.AddFolderAsync(_drive);

        var result = await _art.RefreshAlbumsAsync(_library.RefreshVolumes());

        Assert.Empty(result.Failures);
        Assert.Equal(1, result.Created);
        var album = MusicIndex.BuildAlbums(_db.ListTracks()).Single();
        Assert.True(File.Exists(_art.GetAlbumPath(album.Key)));
        Assert.Contains(_tool.Calls, c => c.StartsWith("scale:Folder.JPG:480x480"));
    }

    [Fact]
    public async Task Album_spread_over_folders_does_not_use_a_folder_cover_but_an_embedded_picture()
    {
        Touch(_drive, @"Musik\CD1\eins.mp3");
        Touch(_drive, @"Musik\CD2\zwei.mp3");
        Touch(_drive, @"Musik\CD1\cover.jpg");
        var picture = new StreamInfo(1, FfprobeReader.AttachedPictureType, "mjpeg", null, 500, 500, null, null, null, false, false, null);
        _probe.Respond = path => AudioProbe(path, picture);
        await _library.AddFolderAsync(_drive);

        await _art.RefreshAlbumsAsync(_library.RefreshVolumes());

        Assert.DoesNotContain(_tool.Calls, c => c.StartsWith("scale:cover.jpg"));
        Assert.Single(_tool.Calls, c => c.StartsWith("extract:") && c.EndsWith("#1:480x480"));
    }

    [Fact]
    public async Task Album_without_any_picture_stays_empty_and_a_second_run_does_nothing()
    {
        Touch(_drive, @"Musik\eins.mp3");
        await _library.AddFolderAsync(_drive);
        var status = _library.RefreshVolumes();

        var first = await _art.RefreshAlbumsAsync(status);
        var second = await _art.RefreshAlbumsAsync(status);

        Assert.Equal(0, first.Created);
        Assert.Equal(0, second.Created);
        Assert.Empty(_db.GetAllAlbumArt());
    }

    [Fact]
    public async Task Pruning_removes_album_art_of_albums_that_no_longer_exist()
    {
        Touch(_drive, @"Musik\Platte\eins.mp3");
        Touch(_drive, @"Musik\Platte\cover.png");
        await _library.AddFolderAsync(_drive);
        await _art.RefreshAlbumsAsync(_library.RefreshVolumes());
        var file = _art.GetAlbumPath(MusicIndex.BuildAlbums(_db.ListTracks()).Single().Key)!;
        File.Delete(Path.Combine(_drive, @"Musik\Platte\eins.mp3"));
        await _library.RescanAllAsync();
        _db.RemoveMissingFiles();

        _art.PruneOrphans();

        Assert.False(File.Exists(file));
        Assert.Empty(_db.GetAllAlbumArt());
    }

    [Fact]
    public async Task Database_at_schema_3_is_migrated_to_the_current_schema_without_losing_data()
    {
        Touch(_drive, @"Filme\Solo (2020).mkv");
        var path = Path.Combine(_base, "migrate.db");
        using (var db = new LibraryDb(path))
        {
            await new LibraryService(db, _volumes, _probe).AddFolderAsync(_drive);
            db.AddGenre(db.GetTitles().Single().Id, "Drama");
        }
        // Zustand von Schema 3 herstellen: die neuen Tabellen weg, Version zurück.
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                DROP TABLE tracks; DROP TABLE album_art; DROP TABLE radio_stations; DROP TABLE podcast_progress;
                DROP TABLE podcast_episodes; DROP TABLE podcasts; DROP TABLE subtitle_settings; PRAGMA user_version = 3;
                """;
            cmd.ExecuteNonQuery();
        }

        using var migrated = new LibraryDb(path);

        // Bestehende Daten sind noch da, die neuen Tabellen benutzbar.
        Assert.Equal("Solo", migrated.ListVideos().Single().TitleName);
        Assert.Equal(new[] { "Drama" }, migrated.GetGenres(migrated.GetTitles().Single().Id));
        migrated.SaveStation(new RadioStation(null, "Test", "https://example.org/stream", null, null, null, null, null));
        var fileId = migrated.ListVideos().Single().FileId;
        migrated.ApplyTrack(fileId, TrackTags.From(null, "x.mp3"));
        Assert.Single(migrated.ListStations());
        Assert.Single(migrated.ListTracks());
    }
}
