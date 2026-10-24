using MediaCenter.Core;
using Microsoft.Data.Sqlite;

namespace MediaCenter.Core.Tests;

internal static class Rows
{
    public static VideoRow Video(long id, long? titleId, TitleKind? kind = null, string? name = null, int? season = null, int? episode = null,
        double? duration = 3600, long fileId = 0) =>
        new(id, fileId == 0 ? id : fileId, 1, $"v{id}.mkv", false, titleId, kind, name, null, season, episode, false,
            name ?? "x", season is null ? VideoKind.Movie : VideoKind.Episode, duration, 1920, 1080, "h264", HdrFormat.None);

    public static PlaybackState Played(double position, bool watched, string whenUtc) => new(position, watched, whenUtc);
}

public sealed class WatchStatisticsTests
{
    private static readonly DateTime Now = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private static readonly TitleRow Movie = new(1, TitleKind.Movie, "Inception", 2010);
    private static readonly TitleRow Show = new(2, TitleKind.Series, "Dark", 2017);

    private static WatchStatistics Build(IReadOnlyList<VideoRow> videos, Dictionary<long, PlaybackState> playback, Dictionary<long, long>? sizes = null) =>
        WatchStatistics.Build(videos, new[] { Movie, Show },
            new Dictionary<long, List<string>> { [1] = new() { "Sci-Fi", "Thriller" }, [2] = new() { "Sci-Fi" } },
            playback, sizes ?? new Dictionary<long, long>(), Now, TimeZoneInfo.Utc);

    [Fact]
    public void Watched_videos_count_with_their_full_length_and_started_ones_with_their_position()
    {
        var videos = new[]
        {
            Rows.Video(10, 1, TitleKind.Movie, "Inception", duration: 7200),
            Rows.Video(20, 2, TitleKind.Series, "Dark", 1, 1, 3000),
            Rows.Video(21, 2, TitleKind.Series, "Dark", 1, 2, 3000),
            Rows.Video(22, 2, TitleKind.Series, "Dark", 1, 3, 3000),
        };
        var playback = new Dictionary<long, PlaybackState>
        {
            [10] = Rows.Played(0, true, "2026-06-10T10:00:00.0000000Z"),
            [20] = Rows.Played(0, true, "2026-05-02T10:00:00.0000000Z"),
            [21] = Rows.Played(1500, false, "2026-06-14T10:00:00.0000000Z"),
        };

        var stats = Build(videos, playback, new Dictionary<long, long> { [10] = 4_000_000_000, [20] = 1_000, [21] = 1_000, [22] = 1_000 });

        Assert.Equal(1, stats.Movies);
        Assert.Equal(1, stats.Series);
        Assert.Equal(4, stats.Videos);
        Assert.Equal((7200 + 9000) / 3600.0, stats.LibraryHours, 3);
        Assert.Equal(4_000_003_000, stats.LibraryBytes);
        Assert.Equal(1, stats.WatchedMovies);
        Assert.Equal(1, stats.WatchedEpisodes);
        Assert.Equal((7200 + 3000 + 1500) / 3600.0, stats.WatchedHours, 3);
        Assert.Equal((7200 + 1500) / 3600.0, stats.Last30DaysHours, 3); // die Folge vom 2. Mai liegt außerhalb
    }

    [Fact]
    public void Months_cover_the_last_twelve_with_watched_videos_in_the_month_of_their_last_entry()
    {
        var videos = new[] { Rows.Video(10, 1, TitleKind.Movie, "Inception", duration: 7200), Rows.Video(20, 2, TitleKind.Series, "Dark", 1, 1, 3600) };
        var playback = new Dictionary<long, PlaybackState>
        {
            [10] = Rows.Played(0, true, "2026-06-10T10:00:00.0000000Z"),
            [20] = Rows.Played(0, true, "2025-07-01T00:30:00.0000000Z"),
        };

        var months = Build(videos, playback).Months;

        Assert.Equal(12, months.Count);
        Assert.Equal((2025, 7), (months[0].Year, months[0].Month));
        Assert.Equal((2026, 6), (months[^1].Year, months[^1].Month));
        Assert.Equal(1, months[0].Videos);
        Assert.Equal(1.0, months[0].Hours, 3);
        Assert.Equal(2.0, months[^1].Hours, 3);
        Assert.All(months.Skip(1).Take(10), m => Assert.Equal(0, m.Videos));
    }

    [Fact]
    public void Genres_and_titles_are_ranked_by_watched_hours()
    {
        var videos = new[] { Rows.Video(10, 1, TitleKind.Movie, "Inception", duration: 7200), Rows.Video(20, 2, TitleKind.Series, "Dark", 1, 1, 3600) };
        var playback = new Dictionary<long, PlaybackState>
        {
            [10] = Rows.Played(0, true, "2026-06-10T10:00:00.0000000Z"),
            [20] = Rows.Played(0, true, "2026-06-11T10:00:00.0000000Z"),
        };

        var stats = Build(videos, playback);

        Assert.Equal(new[] { "Sci-Fi", "Thriller" }, stats.Genres.Select(g => g.Name));
        Assert.Equal(3.0, stats.Genres[0].Hours, 3);
        Assert.Equal(new[] { "Inception", "Dark" }, stats.TopTitles.Select(t => t.Name));
    }

    [Fact]
    public void An_empty_library_gives_zeros_and_empty_months()
    {
        var stats = Build(Array.Empty<VideoRow>(), new Dictionary<long, PlaybackState>());

        Assert.Equal(0, stats.Videos);
        Assert.Equal(0, stats.WatchedHours);
        Assert.Empty(stats.Genres);
        Assert.Equal(12, stats.Months.Count);
    }

    [Fact]
    public void A_watched_video_of_unknown_length_counts_as_watched_but_adds_no_time()
    {
        var videos = new[] { Rows.Video(10, 1, TitleKind.Movie, "Inception", duration: null) };
        var stats = Build(videos, new Dictionary<long, PlaybackState> { [10] = Rows.Played(0, true, "2026-06-10T10:00:00.0000000Z") });

        Assert.Equal(1, stats.WatchedMovies);
        Assert.Equal(0, stats.WatchedHours);
    }

    [Fact]
    public void History_lists_the_latest_first_and_leaves_out_videos_marked_unwatched()
    {
        var videos = new[]
        {
            Rows.Video(10, 1, TitleKind.Movie, "Inception"),
            Rows.Video(20, 2, TitleKind.Series, "Dark", 1, 1),
            Rows.Video(21, 2, TitleKind.Series, "Dark", 1, 2),
            Rows.Video(22, 2, TitleKind.Series, "Dark", 1, 3),
            Rows.Video(30, null),
        };
        var playback = new Dictionary<long, PlaybackState>
        {
            [10] = Rows.Played(0, true, "2026-06-10T10:00:00.0000000Z"),
            [20] = Rows.Played(0, false, "2026-06-14T10:00:00.0000000Z"), // ungesehen markiert
            [21] = Rows.Played(900, false, "2026-06-12T10:00:00.0000000Z"),
            [30] = Rows.Played(0, true, "2026-06-15T10:00:00.0000000Z"), // Inbox: kein Titel
        };

        var history = WatchStatistics.Recent(videos, new[] { Movie, Show }, playback, 10);

        Assert.Equal(new long[] { 21, 10 }, history.Select(h => h.VideoId));
        Assert.Equal("Dark", history[0].TitleName);
        Assert.Equal(900, history[0].PositionSeconds);
        Assert.Single(WatchStatistics.Recent(videos, new[] { Movie, Show }, playback, 1));
    }
}

public sealed class CollectionTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-collections-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryDb _db;

    public CollectionTests()
    {
        Directory.CreateDirectory(_base);
        _db = new LibraryDb(Path.Combine(_base, "library.db"));
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_base, recursive: true);
    }

    private long Movie(string name, int? year = null) => _db.CreateTitle(TitleKind.Movie, name, year);

    [Fact]
    public void Collections_hold_movies_in_order_of_year_and_name()
    {
        var reloaded = Movie("Matrix Reloaded", 2003);
        var first = Movie("Matrix", 1999);
        var noYear = Movie("Matrix Extra");
        var id = _db.CreateCollection("Matrix", new[] { reloaded, noYear, first });

        Assert.Equal(new[] { first, reloaded, noYear }, _db.ListCollectionItems()[id]);
        Assert.Equal("Matrix", Assert.Single(_db.ListCollections()).Name);
    }

    [Fact]
    public void Names_are_unique_ignoring_case_and_validated()
    {
        _db.CreateCollection("Matrix");

        Assert.Throws<ArgumentException>(() => _db.CreateCollection("matrix"));
        Assert.Throws<ArgumentException>(() => _db.CreateCollection("   "));
        Assert.Throws<ArgumentException>(() => _db.CreateCollection(new string('x', LibraryDb.MaxCollectionNameLength + 1)));
        var other = _db.CreateCollection("Alien");
        Assert.Throws<ArgumentException>(() => _db.RenameCollection(other, "MATRIX"));
        _db.RenameCollection(other, "Aliens");
        Assert.Equal(new[] { "Aliens", "Matrix" }, _db.ListCollections().Select(c => c.Name));
    }

    [Fact]
    public void Only_movies_can_be_added_and_a_failed_add_changes_nothing()
    {
        var movie = Movie("Film", 2000);
        var series = _db.CreateTitle(TitleKind.Series, "Serie");
        var id = _db.CreateCollection("Reihe");

        Assert.Throws<ArgumentException>(() => _db.AddTitlesToCollection(id, new[] { movie, series }));
        Assert.Throws<ArgumentException>(() => _db.AddTitlesToCollection(id, new long[] { 9999 }));
        Assert.Empty(_db.ListCollectionItems());
        Assert.Throws<InvalidOperationException>(() => _db.AddTitlesToCollection(12345, new[] { movie }));
    }

    [Fact]
    public void Adding_twice_counts_only_new_movies_and_removing_empties_the_collection()
    {
        var a = Movie("A", 2000);
        var b = Movie("B", 2001);
        var id = _db.CreateCollection("Reihe", new[] { a });

        Assert.Equal(1, _db.AddTitlesToCollection(id, new[] { a, b }));
        _db.RemoveTitleFromCollection(id, a);
        _db.RemoveTitleFromCollection(id, b);

        Assert.Empty(_db.ListCollectionItems());
        Assert.Single(_db.ListCollections()); // leere Sammlung bleibt bestehen
    }

    [Fact]
    public void Deleting_a_collection_keeps_the_movies_and_removing_a_movie_takes_it_out_of_its_collections()
    {
        var a = Movie("A", 2000);
        var b = Movie("B", 2001);
        var keep = _db.CreateCollection("Bleibt", new[] { a, b });
        var gone = _db.CreateCollection("Weg", new[] { a });

        _db.DeleteCollection(gone);
        Assert.Equal(2, _db.GetTitles().Count);
        Assert.Throws<InvalidOperationException>(() => _db.DeleteCollection(gone));

        _db.RemoveTitle(a);
        Assert.Equal(new[] { b }, _db.ListCollectionItems()[keep]);
    }

    [Fact]
    public void Merging_two_titles_moves_the_collection_membership_to_the_target()
    {
        var target = Movie("Film", 2000);
        var duplicate = Movie("Film (Kopie)", 2000);
        var id = _db.CreateCollection("Reihe", new[] { duplicate });

        _db.MergeTitles(duplicate, target);

        Assert.Equal(new[] { target }, _db.ListCollectionItems()[id]);
    }

    private static TitleRow T(long id, string name, int? year = null) => new(id, TitleKind.Movie, name, year);

    [Fact]
    public void Suggestions_group_sequels_and_subtitled_films_by_their_base_name()
    {
        var titles = new[]
        {
            T(1, "The Matrix", 1999), T(2, "The Matrix Reloaded", 2003), T(3, "The Matrix Revolutions", 2003),
            T(4, "Iron Man", 2008), T(5, "Iron Man 2", 2010), T(6, "Iron Man 3", 2013),
            T(7, "Die Hard: With a Vengeance", 1995), T(8, "Die Hard 2", 1990), T(9, "Die Hard", 1988),
            T(10, "Inception", 2010), T(11, "Man of Steel", 2013), T(12, "Man on Fire", 2004),
        };

        var suggestions = CollectionSuggestions.Find(titles, Array.Empty<CollectionRow>(), new Dictionary<long, List<long>>());

        Assert.Equal(new[] { "Die Hard", "Iron Man", "The Matrix" }, suggestions.Select(s => s.Name));
        Assert.Equal(new long[] { 9, 8, 7 }, suggestions[0].TitleIds); // nach Jahr: 1988, 1990, 1995 → Id 9, 8, 7
        Assert.Equal(new long[] { 1, 2, 3 }, suggestions[2].TitleIds);
    }

    [Fact]
    public void Series_and_lone_films_are_not_suggested_and_existing_collections_are_respected()
    {
        var titles = new[]
        {
            T(1, "Iron Man", 2008), T(2, "Iron Man 2", 2010), T(3, "Iron Man 3", 2013),
            new TitleRow(4, TitleKind.Series, "Dark", null), new TitleRow(5, TitleKind.Series, "Dark 2", null),
        };
        var collections = new[] { new CollectionRow(7, "iron man", "x") };

        var all = CollectionSuggestions.Find(titles, Array.Empty<CollectionRow>(), new Dictionary<long, List<long>>());
        var withCollection = CollectionSuggestions.Find(titles, collections, new Dictionary<long, List<long>> { [7] = new() { 1, 2 } });
        var full = CollectionSuggestions.Find(titles, collections, new Dictionary<long, List<long>> { [7] = new() { 1, 2, 3 } });

        Assert.Equal("Iron Man", Assert.Single(all).Name);
        Assert.Empty(withCollection); // nur noch ein Film übrig
        Assert.Empty(full);
    }

    [Theory]
    [InlineData("Iron Man 2", "Iron Man")]
    [InlineData("Rocky IV", "Rocky")]
    [InlineData("Star Wars: Episode V", "Star Wars")]
    [InlineData("Alien - Das unheimliche Wesen", "Alien")]
    [InlineData("Ocean's Eleven Teil 2", "Ocean's Eleven")]
    [InlineData("12", "12")]
    public void BaseName_drops_subtitles_and_counts(string name, string expected)
    {
        Assert.Equal(expected, CollectionSuggestions.BaseName(name));
    }
}

public sealed class DuplicateFinderTests
{
    private static readonly TitleRow Movie = new(1, TitleKind.Movie, "Inception", 2010);
    private static readonly TitleRow Show = new(2, TitleKind.Series, "Dark", 2017);

    [Fact]
    public void A_movie_in_two_files_and_an_episode_in_two_files_are_found_with_the_space_to_win()
    {
        var videos = new[]
        {
            Rows.Video(10, 1, TitleKind.Movie, "Inception", fileId: 100),
            Rows.Video(11, 1, TitleKind.Movie, "Inception", fileId: 101),
            Rows.Video(20, 2, TitleKind.Series, "Dark", 1, 1, fileId: 200),
            Rows.Video(21, 2, TitleKind.Series, "Dark", 1, 1, fileId: 201),
            Rows.Video(22, 2, TitleKind.Series, "Dark", 1, 2, fileId: 202),
        };
        var sizes = new Dictionary<long, long> { [100] = 8_000, [101] = 3_000, [200] = 500, [201] = 700, [202] = 600 };

        var groups = DuplicateFinder.Find(videos, new[] { Movie, Show }, sizes);

        Assert.Equal(2, groups.Count);
        Assert.Equal(DuplicateKind.MovieFiles, groups[0].Kind);
        Assert.Equal("Inception (2010)", groups[0].Label);
        Assert.Equal(3_000, groups[0].ReclaimableBytes);
        Assert.Equal(DuplicateKind.EpisodeFiles, groups[1].Kind);
        Assert.Equal("Dark  S01E01", groups[1].Label);
        Assert.Equal(500, groups[1].ReclaimableBytes);
    }

    [Fact]
    public void Titles_with_the_same_name_are_listed_oldest_first_and_other_kinds_do_not_mix()
    {
        var titles = new[]
        {
            new TitleRow(1, TitleKind.Movie, "Dune", 2021), new TitleRow(2, TitleKind.Movie, "Dune", 1984),
            new TitleRow(3, TitleKind.Series, "Dune", null), new TitleRow(4, TitleKind.Movie, "Heat", 1995),
        };

        var group = Assert.Single(DuplicateFinder.Find(Array.Empty<VideoRow>(), titles, new Dictionary<long, long>()));

        Assert.Equal(DuplicateKind.SimilarTitles, group.Kind);
        Assert.Equal(new long[] { 2, 1 }, group.Titles.Select(t => t.Id));
    }

    [Fact]
    public void A_clean_library_has_no_duplicates()
    {
        var videos = new[] { Rows.Video(10, 1, TitleKind.Movie, "Inception"), Rows.Video(20, 2, TitleKind.Series, "Dark", 1, 1), Rows.Video(21, 2, TitleKind.Series, "Dark", 1, 2) };

        Assert.Empty(DuplicateFinder.Find(videos, new[] { Movie, Show }, new Dictionary<long, long>()));
    }
}
