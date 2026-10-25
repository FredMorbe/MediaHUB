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

public sealed class TrackFileNameTests
{
    [Theory]
    [InlineData("[HD 1080p] 少女時代 Girls' Generation - Day By Day (Daum Screen Saver).mp4", "少女時代 Girls' Generation", "Day By Day (Daum Screen Saver)")]
    [InlineData("Daft Punk - One More Time (Official Video).mp3", "Daft Punk", "One More Time")]
    [InlineData("07 - Portishead - Roads.flac", "Portishead", "Roads")]
    [InlineData("Daft_Punk_-_One_More_Time.mp3", "Daft Punk", "One More Time")]
    [InlineData("Jay-Z - 99 Problems [Lyrics].mp3", "Jay-Z", "99 Problems")]
    [InlineData("50 Cent - In da Club.mp3", "50 Cent", "In da Club")]
    [InlineData("Artist – Title – Live.mp3", "Artist", "Title – Live")]
    [InlineData("【MV】 IU - Good Day 【4K】.mp4", "IU", "Good Day")]
    public void Artist_and_title_are_read_from_artist_dash_title(string file, string artist, string title)
    {
        var parts = TrackFileName.Parse(@"D:\Musik\Past Times\" + file);

        Assert.Equal(new FileNameParts(artist, title), parts);
    }

    [Theory]
    [InlineData("07 - Roads.mp3", "Roads")]
    [InlineData("7 Nation Army.mp3", "7 Nation Army")]
    [InlineData("3 Doors Down.mp3", "3 Doors Down")]
    [InlineData("Jay-Z.mp3", "Jay-Z")]
    [InlineData("Artist - .mp3", "Artist -")]
    [InlineData("(Official Video).mp3", "(Official Video)")]
    [InlineData("Roads (Remastered 2009).mp3", "Roads (Remastered 2009)")]
    public void Without_artist_dash_title_only_the_title_is_cleaned(string file, string title)
    {
        var parts = TrackFileName.Parse(@"D:\Musik\" + file);

        Assert.Null(parts.Artist);
        Assert.Equal(title, parts.Title);
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
    public void Folder_with_one_album_name_and_several_artists_is_a_compilation_unless_one_artist_clearly_leads()
    {
        TrackRow InFolder(long id, string folder, string artist, string album = "Past Times", string? albumArtist = null) =>
            Track(id, "t", artist, albumArtist, album, null) with { RelPath = $@"{folder}\t{id}.mp3" };

        var tracks = new[]
        {
            InFolder(1, "Playlist", "2Pac - Topic"), InFolder(2, "Playlist", "Dido"), InFolder(3, "Playlist", "B.o.B"),
            InFolder(4, "Band", "Band"), InFolder(5, "Band", "Band"), InFolder(6, "Band", "Band"), InFolder(7, "Band", "Band feat. Gast"),
            InFolder(8, "Allein", "Solo"),
            InFolder(9, "Sampler", "Eins", albumArtist: "Various Artists"), InFolder(10, "Sampler", "Zwei", albumArtist: "Various Artists"),
            InFolder(11, "Zwei Alben", "Eins", album: "A"), InFolder(12, "Zwei Alben", "Zwei", album: "B"),
        };

        var found = MusicIndex.CompilationArtists(tracks);

        Assert.Equal(2, found.Count);
        Assert.Equal(MusicIndex.VariousArtists, found[MusicIndex.CompilationKey(tracks[0])]);
        Assert.Equal("Band", found[MusicIndex.CompilationKey(tracks[3])]);
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

    [Fact]
    public void Song_artists_group_by_the_artist_of_the_track_even_inside_a_compilation_album()
    {
        var tracks = new[]
        {
            Track(1, "Eins", "Alpha", "Verschiedene Interpreten", "Sampler", 1),
            Track(2, "Zwei", "alpha", "Verschiedene Interpreten", "Sampler", 2),
            Track(3, "Drei", "Zebra", "Verschiedene Interpreten", "Sampler", 3),
            Track(4, "Ohne", null, "Beta", "Solo", 1),
        };

        var artists = MusicIndex.BuildSongArtists(tracks);

        Assert.Equal(new[] { "Alpha", "Beta", "Zebra" }, artists.Select(a => a.Name));
        Assert.Equal(new long[] { 1, 2 }, artists[0].Tracks.Select(t => t.TrackId));
        Assert.Equal(new long[] { 4 }, artists[1].Tracks.Select(t => t.TrackId)); // ohne Titel-Künstler gilt der Album-Künstler
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
    public async Task Playlist_folder_with_many_artists_stays_one_album_that_can_be_renamed_and_reset()
    {
        Touch(_drive, @"Musik\Past Times\eins.mp3");
        Touch(_drive, @"Musik\Past Times\zwei.mp3");
        Touch(_drive, @"Musik\Past Times\drei.mp3");
        var artists = new Dictionary<string, string> { ["eins"] = "2Pac - Topic", ["zwei"] = "Dido", ["drei"] = "B.o.B" };
        _probe.Respond = path => new ProbeResult(200, "mp3", AudioProbe(path).Streams, new Dictionary<string, string>
        {
            ["title"] = Path.GetFileNameWithoutExtension(path), ["artist"] = artists[Path.GetFileNameWithoutExtension(path)], ["album"] = "Past Times",
        });
        await _library.AddFolderAsync(_drive);

        var album = MusicIndex.BuildAlbums(_db.ListTracks()).Single();

        Assert.Equal("Past Times", album.Title);
        Assert.Equal(MusicIndex.VariousArtists, album.Artist);
        Assert.Equal(3, album.Tracks.Count);
        Assert.Equal(new[] { "2Pac - Topic", "B.o.B", "Dido" }, album.Tracks.Select(t => t.Artist!).OrderBy(a => a));

        var newKey = _db.RenameAlbum(album.Tracks, "Alte Zeiten", "Sampler");
        var renamed = MusicIndex.BuildAlbums(_db.ListTracks()).Single();
        Assert.Equal(newKey, renamed.Key);
        Assert.Equal(("Alte Zeiten", "Sampler"), (renamed.Title, renamed.Artist));

        _db.ResetAlbum(renamed.Tracks);
        var reset = MusicIndex.BuildAlbums(_db.ListTracks()).Single();
        Assert.Equal(("Past Times", MusicIndex.VariousArtists), (reset.Title, reset.Artist));
        Assert.Equal(new[] { "2Pac - Topic", "B.o.B", "Dido" }, reset.Tracks.Select(t => t.Artist!).OrderBy(a => a));
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
    public async Task Renamed_track_keeps_its_own_name_through_a_rescan_and_can_be_reset()
    {
        Touch(_drive, @"Musik\Platte\eins.mp3");
        await _library.AddFolderAsync(_drive);
        var track = _db.ListTracks().Single();

        _db.RenameTrack(track, "  Erster Song ");

        var renamed = _db.ListTracks().Single();
        Assert.Equal("Erster Song", renamed.Title);
        Assert.Equal("eins", renamed.TagTitle);

        // Neu getaggt: der eigene Name bleibt, die Tags dahinter ändern sich.
        _probe.Respond = path => new ProbeResult(200, "mp3", AudioProbe(path).Streams,
            new Dictionary<string, string> { ["title"] = "eins (neu getaggt)", ["artist"] = "Band", ["album"] = "Platte" });
        File.WriteAllText(Path.Combine(_drive, @"Musik\Platte\eins.mp3"), "geändert und länger");
        await _library.RescanAllAsync();
        var rescanned = _db.ListTracks().Single();
        Assert.Equal("Erster Song", rescanned.Title);
        Assert.Equal("eins (neu getaggt)", rescanned.TagTitle);

        // Wer den Namen der Tags eintippt, hat keinen eigenen Namen mehr.
        _db.RenameTrack(rescanned, "eins (neu getaggt)");
        var back = _db.ListTracks().Single();
        Assert.Equal("eins (neu getaggt)", back.Title);
        Assert.Null(back.TagTitle);

        _db.RenameTrack(back, "Noch einer");
        _db.ResetTrack(back.TrackId);
        Assert.Equal("eins (neu getaggt)", _db.ListTracks().Single().Title);
        Assert.Throws<ArgumentException>(() => _db.RenameTrack(back, "   "));
    }

    [Fact]
    public async Task Renamed_album_moves_its_cover_covers_later_tracks_and_can_be_reset()
    {
        Touch(_drive, @"Musik\Platte\eins.mp3");
        Touch(_drive, @"Musik\Platte\zwei.mp3");
        await _library.AddFolderAsync(_drive);
        var oldKey = MusicIndex.BuildAlbums(_db.ListTracks()).Single().Key;
        _db.SetAlbumArt(oldKey, "embedded", "album-alt.jpg");

        _db.RenameAlbum(MusicIndex.BuildAlbums(_db.ListTracks()).Single().Tracks, "Neue Platte");

        var album = MusicIndex.BuildAlbums(_db.ListTracks()).Single();
        Assert.Equal("Neue Platte", album.Title);
        Assert.Equal(MusicIndex.AlbumKey("Band", "Neue Platte"), album.Key);
        Assert.All(album.Tracks, t => Assert.Equal("Platte", t.TagAlbum));
        Assert.Equal("album-alt.jpg", _db.GetAlbumArt(album.Key));
        Assert.Null(_db.GetAlbumArt(oldKey));

        // Ein Titel, der später dazukommt, gehört mit zum umbenannten Album.
        Touch(_drive, @"Musik\Platte\drei.mp3");
        await _library.RescanAllAsync();
        var withThird = MusicIndex.BuildAlbums(_db.ListTracks()).Single();
        Assert.Equal(3, withThird.Tracks.Count);
        Assert.Equal("Neue Platte", withThird.Title);

        _db.ResetAlbum(withThird.Tracks);

        var reset = MusicIndex.BuildAlbums(_db.ListTracks()).Single();
        Assert.Equal("Platte", reset.Title);
        Assert.Equal(oldKey, reset.Key);
        Assert.All(reset.Tracks, t => Assert.Null(t.TagAlbum));
        Assert.Equal("album-alt.jpg", _db.GetAlbumArt(oldKey));
    }

    [Fact]
    public async Task Album_renamed_to_an_existing_album_of_the_artist_merges_and_keeps_the_cover_of_the_target()
    {
        Touch(_drive, @"Musik\A\eins.mp3");
        Touch(_drive, @"Musik\B\zwei.mp3");
        _probe.Respond = path => new ProbeResult(200, "mp3", AudioProbe(path).Streams, new Dictionary<string, string>
        {
            ["title"] = Path.GetFileNameWithoutExtension(path), ["artist"] = "Band", ["album"] = Path.GetFileName(Path.GetDirectoryName(path))!,
        });
        await _library.AddFolderAsync(_drive);
        _db.SetAlbumArt(MusicIndex.AlbumKey("Band", "A"), "embedded", "a.jpg");
        _db.SetAlbumArt(MusicIndex.AlbumKey("Band", "B"), "embedded", "b.jpg");

        _db.RenameAlbum(MusicIndex.BuildAlbums(_db.ListTracks()).Single(a => a.Title == "A").Tracks, "B");

        var album = MusicIndex.BuildAlbums(_db.ListTracks()).Single();
        Assert.Equal(2, album.Tracks.Count);
        Assert.Equal("b.jpg", _db.GetAlbumArt(album.Key));
    }

    [Fact]
    public async Task Track_artist_is_changed_without_taking_the_track_out_of_its_album_and_can_be_reset()
    {
        Touch(_drive, @"Musik\Platte\eins.mp3");
        Touch(_drive, @"Musik\Platte\zwei.mp3");
        await _library.AddFolderAsync(_drive); // Tags: Künstler "Band", kein Album-Künstler
        var one = _db.ListTracks().Single(t => t.Title == "eins");

        _db.RenameTrack(one, "eins", "Band feat. Gast");

        var tracks = _db.ListTracks();
        var changed = tracks.Single(t => t.Title == "eins");
        Assert.Equal("Band feat. Gast", changed.Artist);
        Assert.Equal("Band", changed.TagArtist);
        var album = Assert.Single(MusicIndex.BuildAlbums(tracks)); // beide Titel stehen weiter im selben Album
        Assert.Equal("Band", album.Artist);
        Assert.Equal(2, album.Tracks.Count);

        // Der eigene Künstler übersteht einen neuen Scan.
        File.WriteAllText(Path.Combine(_drive, @"Musik\Platte\eins.mp3"), "geändert und länger");
        await _library.RescanAllAsync();
        Assert.Equal("Band feat. Gast", _db.ListTracks().Single(t => t.Title == "eins").Artist);

        // Leer oder der Name aus den Tags nimmt ihn zurück; null lässt ihn.
        _db.RenameTrack(_db.ListTracks().Single(t => t.Title == "eins"), "eins", null);
        Assert.Equal("Band feat. Gast", _db.ListTracks().Single(t => t.Title == "eins").Artist);
        _db.RenameTrack(_db.ListTracks().Single(t => t.Title == "eins"), "eins", "  ");
        var back = _db.ListTracks().Single(t => t.Title == "eins");
        Assert.Equal("Band", back.Artist);
        Assert.Null(back.TagArtist);
    }

    [Fact]
    public async Task Album_artist_moves_the_album_and_its_cover_and_covers_later_tracks_and_can_be_reset()
    {
        Touch(_drive, @"Musik\Platte\eins.mp3");
        Touch(_drive, @"Musik\Platte\zwei.mp3");
        await _library.AddFolderAsync(_drive);
        var oldKey = MusicIndex.AlbumKey("Band", "Platte");
        _db.SetAlbumArt(oldKey, "embedded", "album-alt.jpg");
        var tracks = MusicIndex.BuildAlbums(_db.ListTracks()).Single().Tracks;

        var newKey = _db.RenameAlbum(tracks, "Platte", "Die Band");

        var album = MusicIndex.BuildAlbums(_db.ListTracks()).Single();
        Assert.Equal("Die Band", album.Artist);
        Assert.Equal("Platte", album.Title);
        Assert.Equal(newKey, album.Key);
        Assert.Equal(MusicIndex.AlbumKey("Die Band", "Platte"), album.Key);
        Assert.Equal("album-alt.jpg", _db.GetAlbumArt(newKey));
        Assert.All(album.Tracks, t => Assert.Equal("Band", t.TagAlbumArtist));
        Assert.All(album.Tracks, t => Assert.Equal("Die Band", t.Artist)); // der Künstler gilt auch für jeden Titel
        Assert.All(album.Tracks, t => Assert.Equal("Band", t.TagArtist));

        // Nur den Namen ändern, den Künstler nicht (null): ein eigener Künstler eines Titels bleibt.
        _db.RenameTrack(album.Tracks[0], album.Tracks[0].Title, "Gast");
        _db.RenameAlbum(MusicIndex.BuildAlbums(_db.ListTracks()).Single().Tracks, "Platte (neu)");
        Assert.Contains(_db.ListTracks(), t => t.Artist == "Gast");
        _db.RenameAlbum(MusicIndex.BuildAlbums(_db.ListTracks()).Single().Tracks, "Platte");

        Touch(_drive, @"Musik\Platte\drei.mp3");
        await _library.RescanAllAsync();
        var withThird = MusicIndex.BuildAlbums(_db.ListTracks()).Single();
        Assert.Equal(3, withThird.Tracks.Count);
        Assert.Equal("Die Band", withThird.Artist);

        _db.RenameTrack(withThird.Tracks[0], "Eigener Name");
        var resetKey = _db.ResetAlbum(MusicIndex.BuildAlbums(_db.ListTracks()).Single().Tracks);

        var reset = MusicIndex.BuildAlbums(_db.ListTracks()).Single();
        Assert.All(reset.Tracks, t => Assert.NotEqual("Eigener Name", t.Title)); // auch die Namen der Titel sind zurück
        Assert.Equal(oldKey, resetKey);
        Assert.Equal("Band", reset.Artist);
        Assert.Equal("album-alt.jpg", _db.GetAlbumArt(oldKey));
        Assert.All(reset.Tracks, t => Assert.Null(t.TagAlbumArtist));
        Assert.All(reset.Tracks, t => Assert.Equal("Band", t.Artist)); // auch die Künstler der Titel sind zurück
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

    private sealed class FakeAlbumSource(string provider = "fakealbums") : IAlbumCoverSource
    {
        public string Provider => provider;

        public List<string> Asked { get; } = new();

        public Func<string, string, OnlinePoster?> Answer { get; set; } = (_, _) => null;

        public Task<OnlinePoster?> FindAsync(string artist, string album, CancellationToken ct = default)
        {
            Asked.Add($"{artist}|{album}");
            return Task.FromResult(Answer(artist, album));
        }
    }

    private FakeAlbumSource UseOnlineAlbums()
    {
        var source = new FakeAlbumSource { Answer = (_, album) => new OnlinePoster(album, "https://is1-ssl.mzstatic.com/cover.jpg") };
        UseSources(source);
        return source;
    }

    /// <summary>Stellt die Anbieter in dieser Reihenfolge ein; die Antworten der Anbieter bestimmen die Tests.</summary>
    private void UseSources(params FakeAlbumSource[] sources)
    {
        var http = new FakeHttp();
        http.Bytes("https://is1-ssl.mzstatic.com/cover.jpg", new byte[] { 1, 2, 3 });
        _art.Online = new OnlineArtwork(null, null, new RemoteImages(http.Client(), _tool, _cache), sources);
    }

    private static OnlinePoster Cover(string album) => new(album, "https://is1-ssl.mzstatic.com/cover.jpg");

    [Fact]
    public async Task The_next_provider_is_asked_only_when_the_first_finds_nothing()
    {
        Touch(_drive, @"Musik\Platte\eins.mp3");
        await _library.AddFolderAsync(_drive);
        var first = new FakeAlbumSource("first");
        var second = new FakeAlbumSource("second") { Answer = (_, album) => Cover(album) };
        UseSources(first, second);

        var result = await _art.RefreshAlbumsAsync(_library.RefreshVolumes());

        Assert.Equal(1, result.Created);
        Assert.Equal(new[] { "Band|Platte" }, first.Asked);
        Assert.Equal(new[] { "Band|Platte" }, second.Asked);
        Assert.Equal("online", _db.GetAllAlbumArt().Single().Value.Source);
    }

    [Fact]
    public async Task A_cover_from_the_first_provider_means_the_second_is_not_asked()
    {
        Touch(_drive, @"Musik\Platte\eins.mp3");
        await _library.AddFolderAsync(_drive);
        var first = new FakeAlbumSource("first") { Answer = (_, album) => Cover(album) };
        var second = new FakeAlbumSource("second") { Answer = (_, album) => Cover(album) };
        UseSources(first, second);

        await _art.RefreshAlbumsAsync(_library.RefreshVolumes());

        Assert.Single(first.Asked);
        Assert.Empty(second.Asked);
    }

    [Fact]
    public async Task A_provider_that_is_down_is_reported_and_skipped_while_the_next_one_still_works()
    {
        Touch(_drive, @"Musik\A\eins.mp3");
        Touch(_drive, @"Musik\B\eins.mp3");
        _probe.Respond = path => new ProbeResult(200, "mp3", AudioProbe(path).Streams,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["title"] = "x", ["artist"] = "Band", ["album"] = Path.GetFileName(Path.GetDirectoryName(path))!,
            });
        await _library.AddFolderAsync(_drive);
        var first = new FakeAlbumSource("first") { Answer = (_, _) => throw new HttpRequestException("first.example antwortete mit 503.") };
        var second = new FakeAlbumSource("second") { Answer = (_, album) => Cover(album) };
        UseSources(first, second);

        var result = await _art.RefreshAlbumsAsync(_library.RefreshVolumes());

        Assert.Single(first.Asked); // beim zweiten Album wird er nicht mehr gefragt
        Assert.Equal(2, second.Asked.Count);
        Assert.Equal(2, result.Created);
        var failure = Assert.Single(result.Failures);
        Assert.Contains("first", failure.Subject);
    }

    [Fact]
    public async Task Misses_are_remembered_for_each_provider_separately()
    {
        Touch(_drive, @"Musik\Platte\eins.mp3");
        await _library.AddFolderAsync(_drive);
        var first = new FakeAlbumSource("first");
        var second = new FakeAlbumSource("second");
        UseSources(first, second);
        var status = _library.RefreshVolumes();

        await _art.RefreshAlbumsAsync(status);
        await _art.RefreshAlbumsAsync(status);

        Assert.Single(first.Asked);
        Assert.Single(second.Asked);

        // Ein neuer Anbieter wird trotz der Absagen der anderen gefragt.
        var third = new FakeAlbumSource("third") { Answer = (_, album) => Cover(album) };
        UseSources(first, second, third);
        var result = await _art.RefreshAlbumsAsync(status);

        Assert.Equal(1, result.Created);
        Assert.Single(first.Asked);
        Assert.Single(second.Asked);
        Assert.Single(third.Asked);
    }

    [Fact]
    public async Task Album_without_a_local_picture_gets_its_cover_from_the_internet()
    {
        Touch(_drive, @"Musik\Platte\eins.mp3");
        await _library.AddFolderAsync(_drive);
        var source = UseOnlineAlbums();

        var result = await _art.RefreshAlbumsAsync(_library.RefreshVolumes());

        Assert.Empty(result.Failures);
        Assert.Equal(1, result.Created);
        Assert.Equal(new[] { "Band|Platte" }, source.Asked);
        var key = MusicIndex.BuildAlbums(_db.ListTracks()).Single().Key;
        Assert.True(File.Exists(_art.GetAlbumPath(key)));
        Assert.Equal("online", _db.GetAllAlbumArt()[key].Source);
        Assert.Contains(_tool.Calls, c => c.EndsWith(":480x480"));
    }

    [Fact]
    public async Task A_local_album_cover_is_never_replaced_by_the_internet_and_nothing_is_asked()
    {
        Touch(_drive, @"Musik\Platte\eins.mp3");
        Touch(_drive, @"Musik\Platte\cover.jpg");
        await _library.AddFolderAsync(_drive);
        var source = UseOnlineAlbums();

        await _art.RefreshAlbumsAsync(_library.RefreshVolumes());
        await _art.RefreshAlbumsAsync(_library.RefreshVolumes());

        Assert.Empty(source.Asked);
        Assert.Equal("sidecar", _db.GetAllAlbumArt().Single().Value.Source);
    }

    [Fact]
    public async Task Albums_without_a_known_artist_or_title_are_not_searched()
    {
        Touch(_drive, @"Musik\eins.mp3");
        _probe.Respond = path => new ProbeResult(200, "mp3", AudioProbe(path).Streams,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["title"] = "Lied" });
        await _library.AddFolderAsync(_drive);
        var source = UseOnlineAlbums();

        await _art.RefreshAlbumsAsync(_library.RefreshVolumes());

        Assert.Empty(source.Asked);
        Assert.Empty(_db.GetAllAlbumArt());
    }

    [Fact]
    public async Task A_search_without_result_is_remembered_until_forced()
    {
        Touch(_drive, @"Musik\Platte\eins.mp3");
        await _library.AddFolderAsync(_drive);
        var source = UseOnlineAlbums();
        source.Answer = (_, _) => null;
        var status = _library.RefreshVolumes();

        await _art.RefreshAlbumsAsync(status);
        await _art.RefreshAlbumsAsync(status);
        Assert.Single(source.Asked);

        await _art.RefreshAlbumsAsync(status, force: true);
        Assert.Equal(2, source.Asked.Count);
    }

    [Fact]
    public async Task An_unavailable_provider_is_reported_once_and_the_run_continues()
    {
        Touch(_drive, @"Musik\A\eins.mp3");
        Touch(_drive, @"Musik\B\eins.mp3");
        _probe.Respond = path => new ProbeResult(200, "mp3", AudioProbe(path).Streams,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["title"] = "x", ["artist"] = "Band", ["album"] = Path.GetFileName(Path.GetDirectoryName(path))!,
            });
        await _library.AddFolderAsync(_drive);
        var source = UseOnlineAlbums();
        source.Answer = (_, _) => throw new HttpRequestException("itunes.apple.com antwortete mit 403 Forbidden.");

        var result = await _art.RefreshAlbumsAsync(_library.RefreshVolumes());

        Assert.Single(source.Asked); // das zweite Album wird nicht mehr gefragt
        var failure = Assert.Single(result.Failures);
        Assert.Contains("fakealbums", failure.Subject);
        Assert.Contains("403", failure.Error);
        Assert.Empty(_db.GetAllAlbumArt());
    }

    [Fact]
    public void Database_at_schema_8_keeps_album_art_and_accepts_online_covers_afterwards()
    {
        var path = Path.Combine(_base, "migrate8.db");
        using (var db = new LibraryDb(path)) db.SetAlbumArt("band|platte", "embedded", "album-1.jpg");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE playback_old (video_id INTEGER PRIMARY KEY REFERENCES videos(id) ON DELETE CASCADE, position_sec REAL NOT NULL, watched INTEGER NOT NULL, updated_utc TEXT NOT NULL); INSERT INTO playback_old SELECT video_id, position_sec, watched, updated_utc FROM playback WHERE profile_id = 1; DROP TABLE playback; ALTER TABLE playback_old RENAME TO playback; CREATE TABLE watchlist_old (title_id INTEGER PRIMARY KEY REFERENCES titles(id) ON DELETE CASCADE, added_utc TEXT NOT NULL); INSERT INTO watchlist_old SELECT title_id, added_utc FROM watchlist WHERE profile_id = 1; DROP TABLE watchlist; ALTER TABLE watchlist_old RENAME TO watchlist; CREATE TABLE podcast_progress_old (episode_id INTEGER PRIMARY KEY REFERENCES podcast_episodes(id) ON DELETE CASCADE, position_sec REAL NOT NULL, played INTEGER NOT NULL, updated_utc TEXT NOT NULL); INSERT INTO podcast_progress_old SELECT episode_id, position_sec, played, updated_utc FROM podcast_progress WHERE profile_id = 1; DROP TABLE podcast_progress; ALTER TABLE podcast_progress_old RENAME TO podcast_progress; DROP TABLE profile_avatars; DROP TABLE audio_settings; DROP TABLE trakt_pushed; DROP TABLE episode_air; DROP TABLE series_schedule; DROP TABLE live_epg; DROP TABLE live_channels; DROP TABLE live_sources; DROP TABLE kids_titles; DROP TABLE profiles; DROP INDEX files_fingerprint; ALTER TABLE files DROP COLUMN fingerprint; DROP TABLE collection_titles; DROP TABLE collections; DROP TABLE episode_info; DROP TABLE title_info; DROP TABLE album_artists; DROP TABLE track_artists; DROP TABLE album_names; DROP TABLE track_names; DROP TABLE watchlist; DROP TABLE playlist_items; DROP TABLE playlists;
                DROP TABLE album_lookups;
                CREATE TABLE album_art_old (album_key TEXT PRIMARY KEY, source TEXT NOT NULL CHECK (source IN ('sidecar', 'embedded')), file TEXT NOT NULL, updated_utc TEXT NOT NULL);
                INSERT INTO album_art_old SELECT album_key, source, file, updated_utc FROM album_art;
                DROP TABLE album_art; ALTER TABLE album_art_old RENAME TO album_art;
                ALTER TABLE color_settings DROP COLUMN lut; ALTER TABLE color_settings DROP COLUMN lut_strength;
                PRAGMA user_version = 8;
                """;
            cmd.ExecuteNonQuery();
        }

        using var migrated = new LibraryDb(path);

        Assert.Equal("album-1.jpg", migrated.GetAlbumArt("band|platte"));
        migrated.SetAlbumArt("andere|platte", "online", "album-2.jpg");
        migrated.SetAlbumLookup("andere|platte", "itunes", "q", found: false);
        Assert.False(migrated.GetAlbumLookup("andere|platte", "itunes")!.Found);
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
                CREATE TABLE playback_old (video_id INTEGER PRIMARY KEY REFERENCES videos(id) ON DELETE CASCADE, position_sec REAL NOT NULL, watched INTEGER NOT NULL, updated_utc TEXT NOT NULL); INSERT INTO playback_old SELECT video_id, position_sec, watched, updated_utc FROM playback WHERE profile_id = 1; DROP TABLE playback; ALTER TABLE playback_old RENAME TO playback; CREATE TABLE watchlist_old (title_id INTEGER PRIMARY KEY REFERENCES titles(id) ON DELETE CASCADE, added_utc TEXT NOT NULL); INSERT INTO watchlist_old SELECT title_id, added_utc FROM watchlist WHERE profile_id = 1; DROP TABLE watchlist; ALTER TABLE watchlist_old RENAME TO watchlist; CREATE TABLE podcast_progress_old (episode_id INTEGER PRIMARY KEY REFERENCES podcast_episodes(id) ON DELETE CASCADE, position_sec REAL NOT NULL, played INTEGER NOT NULL, updated_utc TEXT NOT NULL); INSERT INTO podcast_progress_old SELECT episode_id, position_sec, played, updated_utc FROM podcast_progress WHERE profile_id = 1; DROP TABLE podcast_progress; ALTER TABLE podcast_progress_old RENAME TO podcast_progress; DROP TABLE profile_avatars; DROP TABLE audio_settings; DROP TABLE trakt_pushed; DROP TABLE episode_air; DROP TABLE series_schedule; DROP TABLE live_epg; DROP TABLE live_channels; DROP TABLE live_sources; DROP TABLE kids_titles; DROP TABLE profiles; DROP INDEX files_fingerprint; ALTER TABLE files DROP COLUMN fingerprint; DROP TABLE collection_titles; DROP TABLE collections; DROP TABLE episode_info; DROP TABLE title_info; DROP TABLE album_artists; DROP TABLE track_artists; DROP TABLE album_names; DROP TABLE track_names; DROP TABLE watchlist; DROP TABLE playlist_items; DROP TABLE playlists; DROP TABLE tracks; DROP TABLE album_art; DROP TABLE radio_stations; DROP TABLE podcast_progress;
                DROP TABLE podcast_episodes; DROP TABLE podcasts; DROP TABLE removed_files; DROP TABLE online_lookups; DROP TABLE album_lookups; DROP TABLE color_settings; DROP TABLE subtitle_settings; PRAGMA user_version = 3;
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
