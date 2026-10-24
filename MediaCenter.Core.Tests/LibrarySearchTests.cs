using MediaCenter.Core;

namespace MediaCenter.Core.Tests;

public sealed class LibrarySearchTests
{
    private static TitleRow Movie(long id, string name, int? year = null) => new(id, TitleKind.Movie, name, year);

    private static readonly TitleRow Inception = Movie(1, "Inception", 2010);
    private static readonly TitleRow Dunkirk = Movie(2, "Dunkirk", 2017);
    private static readonly TitleRow Warrior = Movie(3, "Warrior", 2011);
    private static readonly TitleRow Tom = Movie(4, "Tom und Jerry");

    private static readonly Dictionary<long, string> Casts = new()
    {
        [1] = "Leonardo DiCaprio, Tom Hardy, Elliot Page",
        [2] = "Fionn Whitehead, Tom Hardy",
        [3] = "Tom Hardy, Joel Edgerton",
    };

    private static SearchResults Search(string query, IEnumerable<AlbumInfo>? albums = null) =>
        LibrarySearch.Run(query, new[] { Inception, Dunkirk, Warrior, Tom }, Casts, albums ?? Array.Empty<AlbumInfo>());

    [Theory]
    [InlineData("Dr. House", "dr house")]
    [InlineData("Lindenstraße", "lindenstrasse")]
    [InlineData("  Café   Ünder ", "cafe under")]
    [InlineData("Spider-Man: 2", "spider man 2")]
    public void Fold_ignores_case_accents_and_punctuation(string text, string expected) => Assert.Equal(expected, SearchText.Fold(text));

    [Fact]
    public void Every_term_must_occur_and_part_of_a_word_is_enough()
    {
        var terms = SearchText.Terms("nol chris");

        Assert.True(SearchText.Matches(terms, "Christopher Nolan"));
        Assert.False(SearchText.Matches(terms, "Christopher Walken"));
        Assert.False(SearchText.Matches(SearchText.Terms("  ...  "), "Irgendwas")); // ohne Suchwörter passt nichts
        Assert.False(SearchText.Matches(terms, null));
    }

    [Fact]
    public void An_empty_query_finds_nothing() => Assert.True(Search("   ").IsEmpty);

    [Fact]
    public void A_title_is_found_by_its_name_in_any_case()
    {
        var hits = Search("inCEPtion").Titles;

        var hit = Assert.Single(hits);
        Assert.Equal(Inception, hit.Title);
        Assert.Null(hit.Via);
    }

    [Fact]
    public void An_actor_search_finds_all_their_titles_and_names_the_actor()
    {
        var result = Search("tom hardy");

        Assert.Equal(new long[] { 2, 1, 3 }, result.Titles.Select(h => h.Title.Id)); // nach Name sortiert: Dunkirk, Inception, Warrior
        Assert.All(result.Titles, h => Assert.Equal("Tom Hardy", h.Via));
        var actor = Assert.Single(result.Actors);
        Assert.Equal(new ActorHit("Tom Hardy", 3), actor);
    }

    [Fact]
    public void Titles_whose_name_matches_come_before_titles_found_through_an_actor()
    {
        var result = Search("tom");

        Assert.Equal(4, result.Titles[0].Title.Id); // „Tom und Jerry“ passt mit dem Namen
        Assert.Null(result.Titles[0].Via);
        Assert.Equal(new long[] { 2, 1, 3 }, result.Titles.Skip(1).Select(h => h.Title.Id));
        Assert.Equal("Tom Hardy", result.Actors[0].Name);
    }

    [Fact]
    public void The_exact_name_ranks_before_names_that_only_contain_the_word()
    {
        var titles = new[] { Movie(10, "The Dark Knight"), Movie(11, "Dark"), Movie(12, "Dark City") };

        var hits = LibrarySearch.Run("dark", titles, new Dictionary<long, string>(), Array.Empty<AlbumInfo>()).Titles;

        Assert.Equal(new long[] { 11, 12, 10 }, hits.Select(h => h.Title.Id)); // genau, beginnt damit, kommt vor
    }

    [Fact]
    public void Titles_without_a_cast_are_only_found_by_name()
    {
        var result = LibrarySearch.Run("hardy", new[] { Inception }, new Dictionary<long, string>(), Array.Empty<AlbumInfo>());

        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void Several_actors_matching_a_part_of_a_name_are_listed_with_their_title_count()
    {
        var result = Search("ar");

        Assert.Equal(new ActorHit("Tom Hardy", 3), result.Actors[0]); // „Hardy“ und „Leonardo“ passen, Tom Hardy hat die meisten Titel
        Assert.Contains(result.Actors, a => a.Name == "Leonardo DiCaprio");
        Assert.True(result.Actors.Count <= LibrarySearch.MaxActors);
        Assert.Equal(result.Actors.Select(a => a.Titles).OrderByDescending(n => n), result.Actors.Select(a => a.Titles)); // die meisten Titel zuerst
    }

    private static TrackRow Track(string title) => new(1, 1, 1, "x.mp3", false, title, null, null, null, null, null, null, null, null);

    [Fact]
    public void An_album_is_found_by_title_artist_or_one_of_its_songs()
    {
        var discovery = new AlbumInfo("k1", "Discovery", "Daft Punk", 2001, new[] { Track("One More Time"), Track("Aerodynamic") });
        var homework = new AlbumInfo("k2", "Homework", "Daft Punk", 1997, new[] { Track("Around the World") });
        var albums = new[] { discovery, homework };

        Assert.Equal(new[] { discovery }, Search("discovery", albums).Albums.Select(h => h.Album));
        Assert.Equal(new[] { discovery, homework }, Search("daft punk", albums).Albums.Select(h => h.Album));
        var bySong = Assert.Single(Search("aerodynamic", albums).Albums);
        Assert.Equal(new AlbumHit(discovery, "Aerodynamic"), bySong);
        Assert.Empty(Search("nirvana", albums).Albums);
    }
}

public sealed class WatchStateTests
{
    private static VideoRow Video(long id) => new(id, id, 1, $"v{id}.mkv", false, 1, TitleKind.Series, "S", null, 1, (int)id, false, "S", VideoKind.Episode, 60, 720, 1080, "h264", false);

    private static Dictionary<long, PlaybackState> Playback(params (long Id, double Position, bool Watched)[] rows) =>
        rows.ToDictionary(r => r.Id, r => new PlaybackState(r.Position, r.Watched, "2026-01-01T00:00:00Z"));

    [Fact]
    public void No_progress_is_unwatched() =>
        Assert.Equal(WatchState.Unwatched, WatchStates.Of(new[] { Video(1), Video(2) }, Playback()));

    [Fact]
    public void A_title_without_videos_is_unwatched() =>
        Assert.Equal(WatchState.Unwatched, WatchStates.Of(Array.Empty<VideoRow>(), Playback()));

    [Fact]
    public void All_videos_watched_is_watched() =>
        Assert.Equal(WatchState.Watched, WatchStates.Of(new[] { Video(1), Video(2) }, Playback((1, 0, true), (2, 0, true))));

    [Fact]
    public void One_watched_episode_or_a_started_one_is_in_progress()
    {
        var videos = new[] { Video(1), Video(2), Video(3) };

        Assert.Equal(WatchState.InProgress, WatchStates.Of(videos, Playback((1, 0, true))));
        Assert.Equal(WatchState.InProgress, WatchStates.Of(videos, Playback((2, 120, false))));
        Assert.Equal(WatchState.Unwatched, WatchStates.Of(videos, Playback((2, 0, false)))); // eine Zeile ohne Stelle heißt: ungesehen
    }
}

public sealed class TitleOrderingTests
{
    private static readonly TitleRow A = new(1, TitleKind.Movie, "Alpha", 2000);
    private static readonly TitleRow B = new(2, TitleKind.Movie, "beta", 2020);
    private static readonly TitleRow C = new(3, TitleKind.Movie, "Gamma", null);
    private static readonly TitleRow D = new(4, TitleKind.Movie, "Delta", 2020);
    private static readonly TitleRow[] All = { C, A, D, B };

    private static IEnumerable<long> Ids(TitleSort sort, Dictionary<long, double>? ratings = null, Dictionary<long, int>? heights = null) =>
        TitleOrdering.Order(All, sort, ratings ?? new(), heights ?? new()).Select(t => t.Id);

    [Fact]
    public void By_name_ignoring_case() => Assert.Equal(new long[] { 1, 2, 4, 3 }, Ids(TitleSort.Name));

    [Fact]
    public void By_year_newest_first_then_name_and_no_year_last() => Assert.Equal(new long[] { 2, 4, 1, 3 }, Ids(TitleSort.Year));

    [Fact]
    public void By_added_the_highest_id_first() => Assert.Equal(new long[] { 4, 3, 2, 1 }, Ids(TitleSort.Added));

    [Fact]
    public void By_rating_best_first_and_unrated_last() =>
        Assert.Equal(new long[] { 4, 1, 2, 3 }, Ids(TitleSort.Rating, new Dictionary<long, double> { [1] = 7.5, [4] = 8.1 }));

    [Fact]
    public void By_resolution_highest_first_and_unknown_last() =>
        Assert.Equal(new long[] { 3, 2, 1, 4 }, Ids(TitleSort.Resolution, heights: new Dictionary<long, int> { [3] = 2160, [2] = 1080, [1] = 720 }));
}
