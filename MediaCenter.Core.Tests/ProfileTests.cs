using MediaCenter.Core;
using Microsoft.Data.Sqlite;
using static MediaCenter.Core.Tests.TestSupport;

namespace MediaCenter.Core.Tests;

public sealed class ProfilePinTests
{
    [Theory]
    [InlineData("1234")]
    [InlineData("00000000")]
    public void Four_to_eight_digits_are_valid(string pin) => Assert.Equal(pin, ProfilePin.Validated(pin));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("123456789")]
    [InlineData("12a4")]
    [InlineData("12 34")]
    [InlineData("１２３４")] // Ziffern in Vollbreite
    public void Everything_else_is_refused(string? pin) => Assert.Throws<ArgumentException>(() => ProfilePin.Validated(pin));

    [Fact]
    public void The_hash_verifies_the_right_pin_only_and_every_pin_gets_its_own_salt()
    {
        var (salt, hash) = ProfilePin.Create("4711");
        var (salt2, hash2) = ProfilePin.Create("4711");

        Assert.True(ProfilePin.Verify("4711", salt, hash));
        Assert.False(ProfilePin.Verify("4712", salt, hash));
        Assert.False(ProfilePin.Verify("", salt, hash));
        Assert.False(ProfilePin.Verify(null, salt, hash));
        Assert.NotEqual(salt, salt2);
        Assert.NotEqual(hash, hash2);
        Assert.DoesNotContain("4711", hash);
    }
}

public sealed class ProfileTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-profile-" + Guid.NewGuid().ToString("N"));
    private readonly FakeVolumes _volumes = new();
    private readonly LibraryDb _db;
    private readonly LibraryService _service;
    private readonly string _drive;

    public ProfileTests()
    {
        Directory.CreateDirectory(_base);
        _db = new LibraryDb(Path.Combine(_base, "library.db"));
        _service = new LibraryService(_db, _volumes, new FakeProbe());
        _drive = Path.Combine(_base, "drive");
        Touch(_drive, @"Filme\Alien (1979).mkv");
        Touch(_drive, @"Filme\Heat (1995).mkv");
        _volumes.Mounted.Add(Vol("AAAA0001", _drive));
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_base, recursive: true);
    }

    private async Task<(long Alien, long Heat)> Library()
    {
        await _service.AddFolderAsync(_drive);
        var videos = _db.ListVideos();
        return (videos.Single(v => v.ParsedTitle == "Alien").VideoId, videos.Single(v => v.ParsedTitle == "Heat").VideoId);
    }

    private long AdultWithPin(string name = "Papa", string pin = "1234")
    {
        var id = _db.CreateProfile(name, "#F25C54", kids: false);
        _db.SetPin(id, pin);
        return id;
    }

    [Fact]
    public void A_new_database_has_one_adult_profile_without_pin_and_it_is_active()
    {
        var profile = Assert.Single(_db.ListProfiles());

        Assert.Equal(LibraryDb.DefaultProfileId, profile.Id);
        Assert.Equal("Ich", profile.Name);
        Assert.False(profile.Kids);
        Assert.False(profile.HasPin);
        Assert.Equal(LibraryDb.DefaultProfileId, _db.ActiveProfileId);
    }

    [Fact]
    public async Task Progress_and_the_list_belong_to_the_active_profile()
    {
        var (alien, heat) = await Library();
        var title = _db.CreateTitle(TitleKind.Movie, "Merk", 2000);
        _db.SavePlayback(alien, 100, watched: false);
        _db.SetOnWatchlist(title, true);

        var other = _db.CreateProfile("Zweite", "#7BC950", kids: false);
        _db.SetActiveProfile(other);

        Assert.Null(_db.GetPlayback(alien));
        Assert.Empty(_db.GetAllPlayback());
        Assert.Empty(_db.GetContinueWatching());
        Assert.Empty(_db.GetWatchlist());

        _db.SavePlayback(heat, 50, watched: true);
        _db.SetOnWatchlist(title, true);
        _db.SetOnWatchlist(title, false); // nimmt nur die Merkliste dieses Profils herunter

        _db.SetActiveProfile(LibraryDb.DefaultProfileId);
        Assert.Equal(100, _db.GetPlayback(alien)!.PositionSeconds);
        Assert.Null(_db.GetPlayback(heat));
        Assert.Equal(new[] { title }, _db.GetWatchlist());
        Assert.Equal(new[] { alien }, _db.GetContinueWatching().Select(c => c.VideoId));
    }

    [Fact]
    public async Task Watched_marks_are_per_profile_too()
    {
        var (alien, _) = await Library();
        var other = _db.CreateProfile("Zweite", "#7BC950", kids: false);

        _db.SetVideosWatched(new[] { alien }, watched: true);
        _db.SetActiveProfile(other);

        Assert.Null(_db.GetPlayback(alien));
    }

    [Fact]
    public void Podcast_progress_is_per_profile()
    {
        var podcast = _db.SavePodcast("https://example.org/feed", new PodcastFeed("P", null, null, null, new[] { new FeedEpisode("g1", "Folge 1", null, "https://example.org/1.mp3", 600, null) }));
        var episode = _db.ListEpisodes(podcast).Single().Id;
        _db.SaveEpisodeProgress(episode, 120, played: false);

        var other = _db.CreateProfile("Zweite", "#7BC950", kids: false);
        _db.SetActiveProfile(other);
        Assert.Empty(_db.GetEpisodeProgress(podcast));
        _db.SaveEpisodeProgress(episode, 30, played: true);

        Assert.True(_db.GetEpisodeProgress(podcast)[episode].Played);
        _db.SetActiveProfile(LibraryDb.DefaultProfileId);
        Assert.Equal(120, _db.GetEpisodeProgress(podcast)[episode].PositionSeconds);
    }

    [Fact]
    public void Merging_titles_moves_the_list_entries_of_every_profile_and_the_kids_flag()
    {
        var from = _db.CreateTitle(TitleKind.Movie, "Alt", 2000);
        var into = _db.CreateTitle(TitleKind.Movie, "Neu", 2000);
        var other = _db.CreateProfile("Zweite", "#7BC950", kids: false);
        _db.SetOnWatchlist(from, true);
        _db.SetActiveProfile(other);
        _db.SetOnWatchlist(from, true);
        _db.SetKidsTitles(new[] { from }, on: true);

        _db.MergeTitles(from, into);

        Assert.Equal(new[] { into }, _db.GetWatchlist());
        _db.SetActiveProfile(LibraryDb.DefaultProfileId);
        Assert.Equal(new[] { into }, _db.GetWatchlist());
        Assert.Contains(into, _db.GetKidsTitleIds());
    }

    [Fact]
    public async Task Deleting_a_profile_removes_its_data_and_nothing_else()
    {
        var (alien, _) = await Library();
        var other = _db.CreateProfile("Zweite", "#7BC950", kids: false);
        _db.SetActiveProfile(other);
        _db.SavePlayback(alien, 10, watched: false);
        _db.SetActiveProfile(LibraryDb.DefaultProfileId);
        _db.SavePlayback(alien, 99, watched: false);

        _db.DeleteProfile(other);

        Assert.Single(_db.ListProfiles());
        Assert.Equal(99, _db.GetPlayback(alien)!.PositionSeconds);
    }

    [Fact]
    public void Profile_names_are_trimmed_unique_ignoring_case_and_limited()
    {
        var id = _db.CreateProfile("  Anna  ", "#7BC950", kids: false);

        Assert.Equal("Anna", _db.GetProfile(id).Name);
        Assert.Throws<InvalidOperationException>(() => _db.CreateProfile("ANNA", "#7BC950", kids: false));
        Assert.Throws<InvalidOperationException>(() => _db.RenameProfile(id, "ich"));
        _db.RenameProfile(id, "anna"); // der eigene Name in anderer Schreibweise ist erlaubt
        Assert.Throws<ArgumentException>(() => _db.CreateProfile("   ", "#7BC950", kids: false));
        Assert.Throws<ArgumentException>(() => _db.CreateProfile(new string('x', LibraryDb.MaxProfileNameLength + 1), "#7BC950", kids: false));
        Assert.Throws<ArgumentException>(() => _db.CreateProfile("a\nb", "#7BC950", kids: false));
        Assert.Throws<ArgumentException>(() => _db.CreateProfile("Farbe", "rot", kids: false));
    }

    [Fact]
    public void A_kids_profile_needs_an_adult_with_a_pin_first()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => _db.CreateProfile("Kind", "#FF9F45", kids: true));
        Assert.Contains("PIN", ex.Message);

        _db.SetPin(LibraryDb.DefaultProfileId, "1234");
        var kid = _db.CreateProfile("Kind", "#FF9F45", kids: true);

        Assert.True(_db.GetProfile(kid).Kids);
    }

    [Fact]
    public void A_kids_profile_gets_no_pin_and_loses_one_when_it_becomes_a_kids_profile()
    {
        _db.SetPin(LibraryDb.DefaultProfileId, "1234");
        var kid = _db.CreateProfile("Kind", "#FF9F45", kids: true);
        Assert.Throws<InvalidOperationException>(() => _db.SetPin(kid, "9999"));

        var teen = AdultWithPin("Teen", "5555");
        _db.SetProfileKids(teen, true);

        Assert.False(_db.GetProfile(teen).HasPin);
    }

    [Fact]
    public void The_last_adult_with_a_pin_cannot_lose_it_or_be_deleted_while_kids_profiles_exist()
    {
        _db.SetPin(LibraryDb.DefaultProfileId, "1234");
        var kid = _db.CreateProfile("Kind", "#FF9F45", kids: true);
        var mama = _db.CreateProfile("Mama", "#9D6BFF", kids: false); // Erwachsene ohne PIN
        _db.SetActiveProfile(mama);

        var ex = Assert.Throws<InvalidOperationException>(() => _db.DeleteProfile(LibraryDb.DefaultProfileId));
        Assert.Contains("PIN", ex.Message);
        Assert.True(_db.GetProfile(LibraryDb.DefaultProfileId).HasPin);

        _db.DeleteProfile(kid);
        _db.DeleteProfile(LibraryDb.DefaultProfileId); // ohne Kinderprofile ist es erlaubt
        Assert.Equal(new[] { mama }, _db.ListProfiles().Select(p => p.Id));
    }

    [Fact]
    public void A_second_adult_with_a_pin_lets_the_first_one_go()
    {
        _db.SetPin(LibraryDb.DefaultProfileId, "1234");
        _db.CreateProfile("Kind", "#FF9F45", kids: true);
        var papa = AdultWithPin("Papa", "5555");
        _db.SetActiveProfile(papa);

        _db.SetPin(LibraryDb.DefaultProfileId, null);
        _db.DeleteProfile(LibraryDb.DefaultProfileId);

        Assert.DoesNotContain(_db.ListProfiles(), p => p.Id == LibraryDb.DefaultProfileId);
    }

    [Fact]
    public void With_kids_profiles_the_single_pin_stays_and_the_last_adult_stays()
    {
        _db.SetPin(LibraryDb.DefaultProfileId, "1234");
        _db.CreateProfile("Kind", "#FF9F45", kids: true);

        Assert.Throws<InvalidOperationException>(() => _db.SetPin(LibraryDb.DefaultProfileId, null));
        Assert.Throws<InvalidOperationException>(() => _db.SetProfileKids(LibraryDb.DefaultProfileId, true)); // wäre kein Erwachsener mehr da

        // Ein zweiter Erwachsener ohne PIN darf Kind werden: „Ich“ behält die PIN und bleibt Erwachsener.
        var second = _db.CreateProfile("Mama", "#9D6BFF", kids: false);
        _db.SetProfileKids(second, true);
        Assert.True(_db.GetProfile(second).Kids);
    }

    [Fact]
    public void The_active_profile_and_the_last_profile_cannot_be_deleted()
    {
        var other = _db.CreateProfile("Zweite", "#7BC950", kids: false);
        _db.SetActiveProfile(other);

        Assert.Throws<InvalidOperationException>(() => _db.DeleteProfile(other));
        _db.DeleteProfile(LibraryDb.DefaultProfileId);
        Assert.Throws<InvalidOperationException>(() => _db.DeleteProfile(other));
        Assert.Throws<InvalidOperationException>(() => _db.DeleteProfile(999));
    }

    [Fact]
    public void Pins_are_verified_and_can_be_removed_or_replaced()
    {
        var id = LibraryDb.DefaultProfileId;
        Assert.False(_db.VerifyPin(id, "1234")); // ohne PIN gibt es nichts zu bestätigen

        _db.SetPin(id, "1234");
        Assert.True(_db.GetProfile(id).HasPin);
        Assert.True(_db.VerifyPin(id, "1234"));
        Assert.False(_db.VerifyPin(id, "0000"));
        Assert.False(_db.VerifyPin(id, null));

        _db.SetPin(id, "5678");
        Assert.False(_db.VerifyPin(id, "1234"));
        Assert.True(_db.VerifyPin(id, "5678"));

        _db.SetPin(id, null);
        Assert.False(_db.GetProfile(id).HasPin);
        Assert.Throws<ArgumentException>(() => _db.SetPin(id, "12"));
        Assert.Throws<InvalidOperationException>(() => _db.VerifyPin(42, "1234"));
    }

    [Fact]
    public void Kids_titles_can_be_released_withdrawn_and_unknown_ones_are_refused_atomically()
    {
        var a = _db.CreateTitle(TitleKind.Movie, "A", 2000);
        var b = _db.CreateTitle(TitleKind.Series, "B");

        _db.SetKidsTitles(new[] { a, b, a }, on: true);
        Assert.Equal(new[] { a, b }.ToHashSet(), _db.GetKidsTitleIds());

        _db.SetKidsTitles(new[] { a }, on: false);
        Assert.Equal(new[] { b }.ToHashSet(), _db.GetKidsTitleIds());

        Assert.Throws<InvalidOperationException>(() => _db.SetKidsTitles(new[] { a, 9999 }, on: true));
        Assert.DoesNotContain(a, _db.GetKidsTitleIds()); // nichts wurde halb gesetzt
    }

    [Fact]
    public void Kids_flags_follow_a_deleted_title()
    {
        var a = _db.CreateTitle(TitleKind.Movie, "A", 2000);
        _db.SetKidsTitles(new[] { a }, on: true);

        _db.RemoveTitle(a);

        Assert.Empty(_db.GetKidsTitleIds());
    }

    [Fact]
    public void Setting_an_unknown_profile_active_fails_and_keeps_the_current_one()
    {
        Assert.Throws<InvalidOperationException>(() => _db.SetActiveProfile(77));
        Assert.Equal(LibraryDb.DefaultProfileId, _db.ActiveProfileId);
    }

    [Fact]
    public async Task A_schema_18_database_is_migrated_and_all_old_data_belongs_to_the_first_profile()
    {
        var (alien, _) = await Library();
        var title = _db.CreateTitle(TitleKind.Movie, "Merk", 2000);
        _db.SavePlayback(alien, 321, watched: false);
        _db.SetOnWatchlist(title, true);
        _db.Dispose();

        var path = Path.Combine(_base, "library.db");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE playback_old (video_id INTEGER PRIMARY KEY REFERENCES videos(id) ON DELETE CASCADE, position_sec REAL NOT NULL, watched INTEGER NOT NULL, updated_utc TEXT NOT NULL);
                INSERT INTO playback_old SELECT video_id, position_sec, watched, updated_utc FROM playback WHERE profile_id = 1; DROP TABLE playback; ALTER TABLE playback_old RENAME TO playback;
                CREATE TABLE watchlist_old (title_id INTEGER PRIMARY KEY REFERENCES titles(id) ON DELETE CASCADE, added_utc TEXT NOT NULL);
                INSERT INTO watchlist_old SELECT title_id, added_utc FROM watchlist WHERE profile_id = 1; DROP TABLE watchlist; ALTER TABLE watchlist_old RENAME TO watchlist;
                CREATE TABLE podcast_progress_old (episode_id INTEGER PRIMARY KEY REFERENCES podcast_episodes(id) ON DELETE CASCADE, position_sec REAL NOT NULL, played INTEGER NOT NULL, updated_utc TEXT NOT NULL);
                INSERT INTO podcast_progress_old SELECT episode_id, position_sec, played, updated_utc FROM podcast_progress WHERE profile_id = 1; DROP TABLE podcast_progress; ALTER TABLE podcast_progress_old RENAME TO podcast_progress;
                DROP TABLE trakt_pushed; DROP TABLE episode_air; DROP TABLE series_schedule; DROP TABLE live_epg; DROP TABLE live_channels; DROP TABLE live_sources; DROP TABLE kids_titles; DROP TABLE profiles; ALTER TABLE title_info DROP COLUMN trailer;
                PRAGMA user_version = 18;
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var migrated = new LibraryDb(path);

        var profile = Assert.Single(migrated.ListProfiles());
        Assert.Equal("Ich", profile.Name);
        Assert.Equal(321, migrated.GetPlayback(alien)!.PositionSeconds);
        Assert.Equal(new[] { title }, migrated.GetWatchlist());
        migrated.SavePlayback(alien, 5, watched: true); // der neue Schlüssel funktioniert
        Assert.True(migrated.GetPlayback(alien)!.Watched);
    }
}
