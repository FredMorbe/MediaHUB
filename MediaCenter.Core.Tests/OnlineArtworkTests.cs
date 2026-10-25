using System.Net;
using MediaCenter.Core;
using Microsoft.Data.Sqlite;
using static MediaCenter.Core.Tests.TestSupport;

namespace MediaCenter.Core.Tests;

public sealed class TvMazeClientTests
{
    private readonly FakeHttp _http = new();

    private TvMazeClient Client() => new(_http.Client(), TimeSpan.Zero);

    private static string Json(params (string Name, string? Premiered, string? Image)[] shows) =>
        "[" + string.Join(",", shows.Select((s, i) =>
            "{\"score\":" + (1.5 - i * 0.1).ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"show\":{\"id\":" + (100 + i) + ",\"name\":\"" + s.Name + "\""
            + ",\"premiered\":" + (s.Premiered is null ? "null" : "\"" + s.Premiered + "\"")
            + ",\"image\":" + (s.Image is null ? "null" : "{\"medium\":\"" + s.Image + "m.jpg\",\"original\":\"" + s.Image + ".jpg\"}")
            + "}}")) + "]";

    private void RouteSearch(string query, string json) => _http.Text("https://api.tvmaze.com/search/shows?q=" + query, json, "application/json");

    [Fact]
    public async Task An_exact_name_match_gives_the_original_poster()
    {
        RouteSearch("Dark", Json(("Dark Matter", "2015-06-12", "https://static.tvmaze.com/dm"), ("Dark", "2017-12-01", "https://static.tvmaze.com/dark")));

        var poster = await Client().FindAsync("Dark", null);

        Assert.Equal("Dark", poster!.ShowName);
        Assert.Equal("https://static.tvmaze.com/dark.jpg", poster.ImageUrl);
    }

    [Fact]
    public async Task Case_punctuation_and_accents_do_not_matter()
    {
        RouteSearch("lindenstrasse", Json(("Lindenstraße", "1985-12-08", "https://static.tvmaze.com/l")));

        Assert.NotNull(await Client().FindAsync("lindenstrasse", null));
    }

    [Fact]
    public async Task The_year_separates_shows_with_the_same_name()
    {
        RouteSearch("Das%20Boot", Json(("Das Boot", "2018-11-23", "https://static.tvmaze.com/new"), ("Das Boot", "1985-01-01", "https://static.tvmaze.com/old")));

        Assert.Equal("https://static.tvmaze.com/old.jpg", (await Client().FindAsync("Das Boot", 1985))!.ImageUrl);
        Assert.Equal("https://static.tvmaze.com/new.jpg", (await Client().FindAsync("Das Boot", 2018))!.ImageUrl);
        Assert.Equal("https://static.tvmaze.com/new.jpg", (await Client().FindAsync("Das Boot", null))!.ImageUrl); // ohne Jahr: der bessere Suchtreffer
        Assert.Null(await Client().FindAsync("Das Boot", 1999)); // kein Jahr passt: lieber kein Poster als ein falsches
    }

    [Fact]
    public async Task A_german_title_is_found_through_the_alternative_names()
    {
        RouteSearch("Die%20Simpsons", Json(("The Simpsons", "1989-12-17", "https://static.tvmaze.com/s")));
        _http.Text("https://api.tvmaze.com/shows/100/akas", "[{\"name\":\"Los Simpson\"},{\"name\":\"Die Simpsons\"}]", "application/json");

        var poster = await Client().FindAsync("Die Simpsons", 1989);

        Assert.Equal("The Simpsons", poster!.ShowName);
    }

    [Fact]
    public async Task A_similar_but_different_show_is_not_taken()
    {
        RouteSearch("Dark", Json(("Dark Matter", "2015-06-12", "https://static.tvmaze.com/dm")));
        _http.Text("https://api.tvmaze.com/shows/100/akas", "[{\"name\":\"Dunkle Materie\"}]", "application/json");

        Assert.Null(await Client().FindAsync("Dark", null));
    }

    [Fact]
    public async Task Shows_without_an_image_are_skipped()
    {
        RouteSearch("Dark", Json(("Dark", "2017-12-01", null), ("Dark", "2017-12-02", "https://static.tvmaze.com/dark2")));

        Assert.Equal("https://static.tvmaze.com/dark2.jpg", (await Client().FindAsync("Dark", null))!.ImageUrl);
    }

    [Fact]
    public async Task Only_https_image_links_are_accepted()
    {
        RouteSearch("Dark", "[{\"score\":1,\"show\":{\"id\":1,\"name\":\"Dark\",\"premiered\":\"2017-12-01\",\"image\":{\"original\":\"file:///C:/Windows/win.ini\",\"medium\":\"http://static.tvmaze.com/x.jpg\"}}}]");

        Assert.Null(await Client().FindAsync("Dark", null));
    }

    [Fact]
    public async Task No_results_gives_null_and_an_error_status_is_loud()
    {
        RouteSearch("Nichts", "[]");
        _http.Status("https://api.tvmaze.com/search/shows?q=Kaputt", HttpStatusCode.InternalServerError);

        Assert.Null(await Client().FindAsync("Nichts", null));
        await Assert.ThrowsAsync<HttpRequestException>(() => Client().FindAsync("Kaputt", null));
    }

    [Fact]
    public async Task A_rate_limit_answer_is_retried_once()
    {
        var calls = 0;
        _http.Routes["https://api.tvmaze.com/search/shows?q=Dark"] = () =>
            ++calls == 1
                ? new HttpResponseMessage((HttpStatusCode)429) { Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero) } }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json(("Dark", "2017-12-01", "https://static.tvmaze.com/d"))) };

        Assert.NotNull(await Client().FindAsync("Dark", null));
        Assert.Equal(2, calls);

        // Dauerhaft 429: nach dem einen Wiederholen gibt der Client auf.
        var again = 0;
        _http.Routes["https://api.tvmaze.com/search/shows?q=Dark"] = () =>
        {
            again++;
            return new HttpResponseMessage((HttpStatusCode)429) { Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero) } };
        };
        await Assert.ThrowsAsync<HttpRequestException>(() => Client().FindAsync("Dark", null));
        Assert.Equal(2, again);
    }

    [Theory]
    [InlineData("The Matrix Reloaded", "Matrix Reloaded")]
    [InlineData("Ghostbusters II", "Ghostbusters 2")]
    [InlineData("Der Herr der Ringe", "Herr der Ringe")]
    [InlineData("Dr. House", "dr house")]
    [InlineData("Lindenstraße", "LINDENSTRASSE")]
    [InlineData("Rocky IV", "rocky 4")]
    public void Match_key_treats_these_titles_as_the_same(string a, string b)
    {
        Assert.Equal(PosterNames.MatchKey(a), PosterNames.MatchKey(b));
    }

    [Theory]
    [InlineData("Matrix Reloaded", "Matrix Revolutions")]
    [InlineData("Ghostbusters", "Ghostbusters 2")]
    [InlineData("The", "The Matrix")]
    [InlineData("Dark", "Dark Matter")]
    public void Match_key_keeps_different_titles_apart(string a, string b)
    {
        Assert.NotEqual(PosterNames.MatchKey(a), PosterNames.MatchKey(b));
    }

    [Theory]
    [InlineData("Ghostbusters 2", "Ghostbusters II")]
    [InlineData("Rocky IV", "Rocky 4")]
    [InlineData("Toy Story 3", "Toy Story III")]
    [InlineData("Matrix 2: Reloaded", "Matrix II: Reloaded")]
    public void Number_spelling_is_swapped_between_arabic_and_roman(string title, string expected)
    {
        Assert.Equal(expected, PosterNames.WithSwappedNumbers(title));
    }

    [Theory]
    [InlineData("Blade Runner 2049")]
    [InlineData("Matrix Reloaded")]
    [InlineData("Rocky 1")]
    [InlineData("Malcolm X")]
    [InlineData("Rocky I")]
    public void Titles_without_a_swappable_number_stay_as_they_are_for_swapping(string title)
    {
        Assert.Null(PosterNames.WithSwappedNumbers(title));
    }

    [Theory]
    [InlineData("Matrix 2 Reloaded", "Matrix Reloaded")]
    [InlineData("Rocky IV Der Kampf des Jahrhunderts", "Rocky Der Kampf des Jahrhunderts")]
    [InlineData("Matrix 2: Reloaded", "Matrix Reloaded")]
    [InlineData("Ghostbusters II", "Ghostbusters")]
    public void Sequel_numbers_are_dropped_only_as_separate_words(string title, string expected)
    {
        Assert.Equal(expected, PosterNames.WithoutSequelNumbers(title));
    }

    [Theory]
    [InlineData("Blade Runner 2049")]   // vierstellig: ein Jahr im Titel, keine Folgenummer
    [InlineData("Matrix Reloaded")]
    [InlineData("2")]                   // nichts bliebe übrig
    [InlineData("Malcolm X")]           // X ist hier ein Name, keine Zahl
    [InlineData("Se7en")]
    public void Titles_without_a_separate_sequel_number_stay_as_they_are(string title)
    {
        Assert.Null(PosterNames.WithoutSequelNumbers(title));
    }

    [Fact]
    public void Normalize_strips_everything_but_letters_and_digits()
    {
        Assert.Equal("drhouse", PosterNames.Normalize("Dr. House"));
        Assert.Equal("lindenstrasse", PosterNames.Normalize("Lindenstraße"));
        Assert.Equal("cafe24", PosterNames.Normalize("Café 24"));
        Assert.Equal("", PosterNames.Normalize("...")); // leer: wird nie gesucht
    }
}

public sealed class OmdbClientTests
{
    private const string Key = "geheim123";
    private readonly FakeHttp _http = new();

    private OmdbClient Client(string key = Key) => new(_http.Client(), key, TimeSpan.Zero);

    private void Route(string query, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        _http.Routes[$"https://www.omdbapi.com/?apikey={Key}&type=movie&{query}"] = () =>
            new HttpResponseMessage(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private static string Movie(string title, string year, string poster) =>
        "{\"Title\":\"" + title + "\",\"Year\":\"" + year + "\",\"Poster\":\"" + poster + "\",\"Response\":\"True\"}";

    [Fact]
    public async Task An_exact_title_and_year_gives_the_poster()
    {
        Route("t=Inception&y=2010", Movie("Inception", "2010", "https://m.media-amazon.com/images/M/x._V1_UX380.jpg"));

        var poster = await Client().FindAsync("Inception", 2010);

        Assert.Equal("Inception", poster!.ShowName);
        Assert.Equal("https://m.media-amazon.com/images/M/x._V1_UX380.jpg", poster.ImageUrl);
    }

    [Fact]
    public async Task Without_a_year_the_exact_title_is_enough_and_case_and_punctuation_do_not_matter()
    {
        Route("t=ghostbusters%20ii", Movie("Ghostbusters II", "1989", "https://m.media-amazon.com/images/M/g.jpg"));

        Assert.NotNull(await Client().FindAsync("ghostbusters ii", null));
    }

    [Fact]
    public async Task A_different_title_from_a_fuzzy_match_is_rejected()
    {
        // So antwortet OMDb auf „Der Pate“: ein Eintrag, der anders heißt.
        Route("t=Der%20Pate&y=1972", Movie("Teaser Bonus-Episode 61: Der Pate (1972)", "1972", "https://m.media-amazon.com/images/M/x.jpg"));
        Route("t=Der%20Pate", Movie("Teaser Bonus-Episode 61: Der Pate (1972)", "1972", "https://m.media-amazon.com/images/M/x.jpg"));

        Assert.Null(await Client().FindAsync("Der Pate", 1972));
    }

    [Fact]
    public async Task A_different_year_is_rejected()
    {
        Route("t=Dune&y=1984", "{\"Response\":\"False\",\"Error\":\"Movie not found!\"}");
        Route("t=Dune", Movie("Dune", "2021", "https://m.media-amazon.com/images/M/d.jpg")); // ohne Jahr gefragt: 37 Jahre daneben

        Assert.Null(await Client().FindAsync("Dune", 1984));
    }

    private void NotFound(string query) => Route(query, "{\"Response\":\"False\",\"Error\":\"Movie not found!\"}");

    private int Asked => _http.Requests.Count;

    [Fact]
    public async Task A_leading_article_does_not_matter_in_either_direction()
    {
        // OMDb liefert für „Matrix Reloaded“ den Film „The Matrix Reloaded“.
        Route("t=Matrix%20Reloaded&y=2003", Movie("The Matrix Reloaded", "2003", "https://m.media-amazon.com/images/M/m.jpg"));
        Route("t=The%20Matrix%20Reloaded&y=2003", Movie("The Matrix Reloaded", "2003", "https://m.media-amazon.com/images/M/m.jpg"));

        Assert.Equal("The Matrix Reloaded", (await Client().FindAsync("Matrix Reloaded", 2003))!.ShowName);
        Assert.Equal("The Matrix Reloaded", (await Client().FindAsync("The Matrix Reloaded", 2003))!.ShowName);
    }

    [Fact]
    public async Task A_sequel_number_the_service_does_not_know_is_dropped_in_a_later_attempt()
    {
        NotFound("t=Matrix%202%20Reloaded&y=2003");
        NotFound("t=Matrix%20II%20Reloaded&y=2003");
        Route("t=Matrix%20Reloaded&y=2003", Movie("The Matrix Reloaded", "2003", "https://m.media-amazon.com/images/M/m.jpg"));

        var poster = await Client().FindAsync("Matrix 2 Reloaded", 2003);

        Assert.Equal("The Matrix Reloaded", poster!.ShowName);
        Assert.Equal(3, Asked);
    }

    [Fact]
    public async Task Without_a_year_the_dropped_number_variant_needs_at_least_two_words()
    {
        NotFound("t=Matrix%202%20Reloaded");
        NotFound("t=Matrix%20II%20Reloaded");
        Route("t=Matrix%20Reloaded", Movie("The Matrix Reloaded", "2003", "https://m.media-amazon.com/images/M/m.jpg"));
        NotFound("t=Ghostbusters%202");
        NotFound("t=Ghostbusters%20II");
        Route("t=Ghostbusters", Movie("Ghostbusters", "1984", "https://m.media-amazon.com/images/M/g.jpg"));

        Assert.NotNull(await Client().FindAsync("Matrix 2 Reloaded", null));
        Assert.Null(await Client().FindAsync("Ghostbusters 2", null)); // sonst bekäme der zweite Film das Poster des ersten
    }

    [Fact]
    public async Task Roman_and_arabic_numbers_are_the_same_title()
    {
        Route("t=Ghostbusters%202&y=1989", Movie("Ghostbusters II", "1989", "https://m.media-amazon.com/images/M/g2.jpg"));

        Assert.Equal("Ghostbusters II", (await Client().FindAsync("Ghostbusters 2", 1989))!.ShowName);
        Assert.Equal(1, Asked);
    }

    [Fact]
    public async Task The_other_number_spelling_is_tried_when_the_service_only_knows_one()
    {
        NotFound("t=Ghostbusters%202&y=1989");
        Route("t=Ghostbusters%20II&y=1989", Movie("Ghostbusters II", "1989", "https://m.media-amazon.com/images/M/g2.jpg"));
        NotFound("t=Rocky%20IV&y=1985");
        Route("t=Rocky%204&y=1985", Movie("Rocky 4", "1985", "https://m.media-amazon.com/images/M/r.jpg"));

        Assert.Equal("Ghostbusters II", (await Client().FindAsync("Ghostbusters 2", 1989))!.ShowName);
        Assert.Equal("Rocky 4", (await Client().FindAsync("Rocky IV", 1985))!.ShowName);
    }

    [Fact]
    public async Task A_year_that_is_off_by_one_is_found_by_the_attempt_without_year_but_two_years_off_is_not()
    {
        NotFound("t=Dune&y=2020");
        Route("t=Dune", Movie("Dune", "2021", "https://m.media-amazon.com/images/M/d.jpg"));
        NotFound("t=Dune&y=2019");

        Assert.NotNull(await Client().FindAsync("Dune", 2020));
        Assert.Null(await Client().FindAsync("Dune", 2019));
    }

    [Fact]
    public async Task At_most_four_requests_are_made_per_film()
    {
        NotFound("t=Matrix%202%20Reloaded&y=2003");
        NotFound("t=Matrix%20II%20Reloaded&y=2003");
        NotFound("t=Matrix%20Reloaded&y=2003");
        NotFound("t=Matrix%202%20Reloaded");

        Assert.Null(await Client().FindAsync("Matrix 2 Reloaded", 2003));
        Assert.Equal(4, Asked);
    }

    [Fact]
    public async Task Lookup_returns_title_and_year_even_without_a_poster()
    {
        Route("t=Matrix%20Reloaded&y=2003", Movie("The Matrix Reloaded", "2003", "N/A"));

        var info = await Client().LookupAsync("Matrix Reloaded", 2003);

        Assert.Equal(new MovieInfo("The Matrix Reloaded", 2003, null), info);
        Assert.Null(await Client().FindAsync("Matrix Reloaded", 2003)); // als Poster-Quelle gibt es dann nichts
    }

    [Fact]
    public async Task A_found_film_without_a_poster_ends_the_search()
    {
        Route("t=Dune&y=2021", Movie("Dune", "2021", "N/A"));

        Assert.Null(await Client().FindAsync("Dune", 2021));
        Assert.Equal(1, Asked);
    }

    [Theory]
    [InlineData("N/A")]
    [InlineData("http://m.media-amazon.com/images/M/x.jpg")]
    [InlineData("file:///C:/Windows/win.ini")]
    public async Task A_missing_or_unsafe_poster_link_gives_no_poster(string poster)
    {
        Route("t=Dune&y=2021", Movie("Dune", "2021", poster));

        Assert.Null(await Client().FindAsync("Dune", 2021));
    }

    [Fact]
    public async Task Movie_not_found_gives_null()
    {
        Route("t=Nichtsda", "{\"Response\":\"False\",\"Error\":\"Movie not found!\"}");

        Assert.Null(await Client().FindAsync("Nichtsda", null));
    }

    [Theory]
    [InlineData("Invalid API key!", HttpStatusCode.Unauthorized)]
    [InlineData("Request limit reached!", HttpStatusCode.OK)]
    [InlineData("No API key provided.", HttpStatusCode.OK)]
    public async Task A_refused_request_is_a_loud_unavailable_error_without_the_key(string error, HttpStatusCode status)
    {
        Route("t=Dune", "{\"Response\":\"False\",\"Error\":\"" + error + "\"}", status);

        var ex = await Assert.ThrowsAsync<PosterSourceUnavailableException>(() => Client().FindAsync("Dune", null));

        Assert.Contains(error, ex.Message);
        Assert.DoesNotContain(Key, ex.Message);
    }

    [Fact]
    public async Task A_server_error_is_an_http_error_whose_message_does_not_contain_the_key()
    {
        Route("t=Dune", "boom", HttpStatusCode.InternalServerError);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Client().FindAsync("Dune", null));

        Assert.Contains("500", ex.Message);
        Assert.DoesNotContain(Key, ex.Message);
    }

    [Fact]
    public async Task Without_a_key_nothing_is_sent()
    {
        await Assert.ThrowsAsync<PosterSourceUnavailableException>(() => Client(key: " ").FindAsync("Dune", null));

        Assert.Empty(_http.Requests);
    }
}

public sealed class ITunesAlbumClientTests
{
    private readonly FakeHttp _http = new();

    private ITunesAlbumClient Client() => new(_http.Client(), TimeSpan.Zero);

    private static string Json(params (string Album, string Artist)[] items) =>
        "{\"resultCount\":" + items.Length + ",\"results\":[" + string.Join(",", items.Select((a, i) =>
            "{\"wrapperType\":\"collection\",\"collectionName\":\"" + a.Album + "\",\"artistName\":\"" + a.Artist + "\""
            + ",\"artworkUrl100\":\"https://is1-ssl.mzstatic.com/image/thumb/x" + i + "/100x100bb.jpg\"}")) + "]}";

    private void Route(string term, string json) =>
        _http.Text("https://itunes.apple.com/search?media=music&entity=album&limit=10&term=" + term, json, "application/json");

    [Fact]
    public async Task An_exact_artist_and_album_match_gives_the_large_cover()
    {
        Route("Daft%20Punk%20Discovery", Json(("Random Access Memories", "Daft Punk"), ("Discovery", "Daft Punk")));

        var cover = await Client().FindAsync("Daft Punk", "Discovery");

        Assert.Equal("Discovery", cover!.ShowName);
        Assert.Equal("https://is1-ssl.mzstatic.com/image/thumb/x1/600x600bb.jpg", cover.ImageUrl);
    }

    [Theory]
    [InlineData("Discovery (Remastered)")]
    [InlineData("Discovery [Deluxe Edition]")]
    [InlineData("Discovery - EP")]
    [InlineData("DISCOVERY")]
    public async Task Additions_at_the_provider_and_case_do_not_matter(string providerName)
    {
        Route("Daft%20Punk%20Discovery", Json((providerName, "Daft Punk")));

        Assert.NotNull(await Client().FindAsync("Daft Punk", "Discovery"));
    }

    [Fact]
    public async Task A_cover_of_another_artist_or_another_album_is_not_taken()
    {
        Route("Daft%20Punk%20Discovery", Json(("Discovery", "Discovery Kids"), ("Discovery Channel", "Daft Punk"), ("Homework", "Daft Punk")));

        Assert.Null(await Client().FindAsync("Daft Punk", "Discovery"));
    }

    [Fact]
    public async Task Only_https_cover_links_are_accepted()
    {
        Route("A%20B", "{\"results\":[{\"collectionName\":\"B\",\"artistName\":\"A\",\"artworkUrl100\":\"http://x.example/100x100bb.jpg\"}]}");

        Assert.Null(await Client().FindAsync("A", "B"));
    }

    [Fact]
    public async Task No_results_gives_null_and_an_error_status_is_loud()
    {
        Route("A%20B", "{\"resultCount\":0,\"results\":[]}");
        _http.Status("https://itunes.apple.com/search?media=music&entity=album&limit=10&term=A%20Kaputt", HttpStatusCode.Forbidden);

        Assert.Null(await Client().FindAsync("A", "B"));
        await Assert.ThrowsAsync<HttpRequestException>(() => Client().FindAsync("A", "Kaputt"));
    }

    [Fact]
    public async Task Missing_artist_or_album_is_an_error()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Client().FindAsync(" ", "B"));
        await Assert.ThrowsAsync<ArgumentException>(() => Client().FindAsync("A", ""));
        Assert.Empty(_http.Requests);
    }
}

public sealed class OnlineArtworkServiceTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-onart-" + Guid.NewGuid().ToString("N"));
    private readonly string _drive;
    private readonly string _cache;
    private readonly FakeVolumes _volumes = new();
    private readonly FakeProbe _probe = new();
    private readonly FakeImageTool _tool = new();
    private readonly FakeHttp _http = new();
    private readonly FakeFinder _finder = new();
    private readonly FakeFinder _movieFinder = new("fakemovies");
    private LibraryDb _db;
    private readonly LibraryService _library;
    private readonly ArtworkService _art;

    private sealed class FakeFinder(string provider = "fake") : IPosterSource
    {
        public string Provider => provider;

        public List<string> Asked { get; } = new();

        public Func<string, int?, OnlinePoster?> Answer { get; set; } = (_, _) => null;

        public Task<OnlinePoster?> FindAsync(string title, int? year, CancellationToken ct = default)
        {
            Asked.Add($"{title}|{year}");
            return Task.FromResult(Answer(title, year));
        }
    }

    public OnlineArtworkServiceTests()
    {
        _drive = Path.Combine(_base, "drive");
        _cache = Path.Combine(_base, "cache");
        Directory.CreateDirectory(_drive);
        _db = new LibraryDb(Path.Combine(_base, "library.db"));
        _volumes.Mounted.Add(Vol("AAAA0001", _drive));
        _library = new LibraryService(_db, _volumes, _probe);
        _art = new ArtworkService(_db, _tool, _probe, _cache)
        {
            Online = new OnlineArtwork(_finder, _movieFinder, new RemoteImages(_http.Client(), _tool, _cache)),
        };
        _http.Bytes("https://static.tvmaze.com/poster.jpg", new byte[] { 1, 2, 3 });
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_base, recursive: true);
    }

    private async Task Scan() => await _library.AddFolderAsync(_drive);

    private Task<ArtworkResult> Refresh(bool force = false) => _art.RefreshAsync(_library.RefreshVolumes(), force);

    private TitleRow Title(string name) => _db.GetTitles().Single(t => t.Name == name);

    private ArtworkRow? Poster(string title) => _db.GetArtwork(ArtworkOwner.Title, Title(title).Id, ArtworkKind.Poster);

    private ArtworkRow? Landscape(string title) => _db.GetArtwork(ArtworkOwner.Title, Title(title).Id, ArtworkKind.Landscape);

    private static OnlinePoster Found(string name = "Show X") => new(name, "https://static.tvmaze.com/poster.jpg");

    private void TwoEpisodes(string show = "Show X")
    {
        Touch(_drive, $@"Serien\{show}\Season 1\S01E01.mkv");
        Touch(_drive, $@"Serien\{show}\Season 1\S01E02.mkv");
    }

    [Fact]
    public async Task A_series_without_a_local_poster_gets_the_online_poster()
    {
        TwoEpisodes();
        _finder.Answer = (_, _) => Found();
        await Scan();

        var result = await Refresh();

        Assert.Empty(result.Failures);
        var poster = Poster("Show X")!;
        Assert.Equal(ArtworkSource.Online, poster.Source);
        Assert.Contains("scale:", File.ReadAllText(Path.Combine(_cache, poster.File)));
        Assert.Contains(_tool.Calls, c => c.StartsWith("scale:") && c.EndsWith(":400x600") && c.Contains(".download"));
        Assert.Equal(ArtworkSource.Frame, Landscape("Show X")!.Source); // das Querformat bleibt ein Standbild
    }

    [Fact]
    public async Task Movies_are_not_asked_at_the_series_source()
    {
        Touch(_drive, @"Filme\Solo (2020).mkv");
        _finder.Answer = (_, _) => Found();
        await Scan();

        await Refresh();

        Assert.Empty(_finder.Asked);
        Assert.Equal(new[] { "Solo|2020" }, _movieFinder.Asked);
        Assert.Null(Poster("Solo")); // der Filmdienst hat nichts geliefert
    }

    [Fact]
    public async Task A_movie_gets_its_poster_from_the_movie_source_and_a_series_is_not_asked_there()
    {
        Touch(_drive, @"Filme\Solo (2020).mkv");
        TwoEpisodes();
        _movieFinder.Answer = (_, _) => Found("Solo");
        _finder.Answer = (_, _) => Found();
        await Scan();

        var result = await Refresh();

        Assert.Empty(result.Failures);
        Assert.Equal(ArtworkSource.Online, Poster("Solo")!.Source);
        Assert.Equal(ArtworkSource.Online, Poster("Show X")!.Source);
        Assert.Equal(new[] { "Solo|2020" }, _movieFinder.Asked);
        Assert.Equal(new[] { "Show X|" }, _finder.Asked);
        Assert.Equal(ArtworkSource.Frame, Landscape("Solo")!.Source);
    }

    [Fact]
    public async Task Without_a_movie_source_movies_stay_offline()
    {
        Touch(_drive, @"Filme\Solo (2020).mkv");
        _art.Online = _art.Online! with { Movies = null };
        await Scan();

        await Refresh();

        Assert.Empty(_movieFinder.Asked);
        Assert.Null(Poster("Solo"));
    }

    [Fact]
    public async Task A_failing_provider_is_reported_once_and_does_not_stop_the_other()
    {
        Touch(_drive, @"Filme\Solo (2020).mkv");
        Touch(_drive, @"Filme\Duo (2021).mkv");
        TwoEpisodes();
        _movieFinder.Answer = (_, _) => throw new PosterSourceUnavailableException("OMDb: Request limit reached!");
        _finder.Answer = (_, _) => Found();
        await Scan();

        var result = await Refresh();

        Assert.Single(_movieFinder.Asked); // nach dem Ausfall wird OMDb nicht weiter gefragt
        var failure = Assert.Single(result.Failures);
        Assert.Contains("fakemovies", failure.Subject);
        Assert.Contains("Request limit reached!", failure.Error);
        Assert.Equal(ArtworkSource.Online, Poster("Show X")!.Source); // TVmaze arbeitet weiter
        Assert.Equal(ArtworkSource.Frame, Landscape("Duo")!.Source);
        Assert.Null(_db.GetOnlineLookup(Title("Solo").Id, "fakemovies")); // ein Ausfall zählt nicht als „nichts gefunden“
    }

    [Fact]
    public async Task A_local_poster_wins_and_the_internet_is_not_asked()
    {
        TwoEpisodes();
        Touch(_drive, @"Serien\Show X\folder.jpg");
        _finder.Answer = (_, _) => Found();
        await Scan();

        await Refresh();

        Assert.Empty(_finder.Asked);
        Assert.Equal(ArtworkSource.Sidecar, Poster("Show X")!.Source);
    }

    [Fact]
    public async Task A_poster_chosen_by_the_user_is_never_replaced_not_even_by_force()
    {
        TwoEpisodes();
        _finder.Answer = (_, _) => Found();
        await Scan();
        var own = Path.Combine(_base, "own.jpg");
        File.WriteAllText(own, "mein bild");
        await _art.SetUserArtworkAsync(ArtworkOwner.Title, Title("Show X").Id, ArtworkKind.Poster, own);

        await Refresh(force: true);

        Assert.Equal(ArtworkSource.User, Poster("Show X")!.Source);
        Assert.Empty(_finder.Asked);
    }

    [Fact]
    public async Task A_found_online_poster_is_stored_locally_and_not_fetched_again()
    {
        TwoEpisodes();
        _finder.Answer = (_, _) => Found();
        await Scan();

        await Refresh();
        await Refresh();

        Assert.Equal(ArtworkSource.Online, Poster("Show X")!.Source);
        Assert.True(File.Exists(Path.Combine(_cache, Poster("Show X")!.File)));
        Assert.Single(_finder.Asked);
        Assert.Single(_http.Requests, r => r.EndsWith("poster.jpg"));
    }

    [Fact]
    public async Task A_poster_from_a_web_address_is_saved_as_the_users_own()
    {
        TwoEpisodes();
        _finder.Answer = (_, _) => Found();
        await Scan();
        await Refresh();
        _http.Bytes("https://m.media-amazon.com/images/M/x@._V1_.jpg", new byte[] { 9, 9 });
        var remote = new RemoteImages(_http.Client(), _tool, _cache);

        await _art.SetUserArtworkFromUrlAsync(remote, ArtworkOwner.Title, Title("Show X").Id, ArtworkKind.Poster, "https://m.media-amazon.com/images/M/x@._V1_.jpg");
        await Refresh(force: true);

        Assert.Equal(ArtworkSource.User, Poster("Show X")!.Source);
        Assert.True(File.Exists(Path.Combine(_cache, Poster("Show X")!.File)));
    }

    [Fact]
    public async Task Without_an_online_source_nothing_is_asked()
    {
        TwoEpisodes();
        _art.Online = null;
        await Scan();

        await Refresh();

        Assert.Null(Poster("Show X"));
    }

    [Fact]
    public async Task A_search_without_result_is_not_repeated_until_forced()
    {
        TwoEpisodes();
        await Scan();

        await Refresh();
        await Refresh();
        Assert.Single(_finder.Asked);

        await Refresh(force: true);
        Assert.Equal(2, _finder.Asked.Count);
        Assert.Null(Poster("Show X"));
    }

    [Fact]
    public async Task A_changed_title_is_searched_again()
    {
        TwoEpisodes();
        await Scan();
        await Refresh();

        _db.SetOnlineLookup(Title("Show X").Id, "fake", "Altes Suchwort|", found: false); // wie nach einer Umbenennung: andere Anfrage als jetzt
        await Refresh();

        Assert.Equal(2, _finder.Asked.Count);
    }

    [Fact]
    public async Task A_miss_from_an_older_search_method_is_retried()
    {
        TwoEpisodes();
        await Scan();
        await Refresh();
        Assert.Single(_finder.Asked);

        _db.SetOnlineLookup(Title("Show X").Id, "fake", "Show X||v1", found: false); // so sah der Merker mit dem älteren Verfahren aus
        await Refresh();

        Assert.Equal(2, _finder.Asked.Count);
    }

    [Fact]
    public async Task An_old_miss_is_retried_after_two_weeks()
    {
        TwoEpisodes();
        await Scan();
        await Refresh();
        var path = Path.Combine(_base, "library.db");
        _db.Dispose();
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE online_lookups SET checked_utc = $old";
            cmd.Parameters.AddWithValue("$old", DateTime.UtcNow.AddDays(-15).ToString("O"));
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        _db = new LibraryDb(path);
        var art = new ArtworkService(_db, _tool, _probe, _cache) { Online = _art.Online };

        await art.RefreshAsync(new LibraryService(_db, _volumes, _probe).RefreshVolumes());

        Assert.Equal(2, _finder.Asked.Count);
    }

    [Fact]
    public async Task When_the_internet_is_down_there_is_one_loud_failure_and_the_other_artwork_is_still_made()
    {
        TwoEpisodes("Show A");
        TwoEpisodes("Show B");
        _finder.Answer = (_, _) => throw new HttpRequestException("Kein Netz");
        await Scan();

        var result = await Refresh();

        Assert.Single(_finder.Asked); // nach dem ersten Fehler wird nicht weiter gefragt
        var failure = Assert.Single(result.Failures);
        Assert.Contains("nicht verfügbar", failure.Error);
        Assert.Contains("Kein Netz", failure.Error);
        Assert.Equal(ArtworkSource.Frame, Landscape("Show A")!.Source);
        Assert.Equal(ArtworkSource.Frame, Landscape("Show B")!.Source);
        Assert.Null(_db.GetOnlineLookup(Title("Show A").Id, "fake")); // ein Netzfehler zählt nicht als „nichts gefunden“
    }

    [Fact]
    public async Task A_broken_image_fails_only_that_series()
    {
        TwoEpisodes("Show A");
        TwoEpisodes("Show B");
        _finder.Answer = (title, _) => title == "Show A" ? new OnlinePoster("Show A", "https://static.tvmaze.com/zu-gross.jpg") : Found("Show B");
        _http.Routes["https://static.tvmaze.com/zu-gross.jpg"] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[9_000_000]) };
        await Scan();

        var result = await Refresh();

        var failure = Assert.Single(result.Failures);
        Assert.Equal("Show A", failure.Subject);
        Assert.Null(Poster("Show A"));
        Assert.Equal(ArtworkSource.Online, Poster("Show B")!.Source);
        Assert.NotNull(Landscape("Show A")); // das Standbild entsteht trotzdem
    }

    [Fact]
    public async Task Force_prefers_a_poster_that_has_appeared_locally_since()
    {
        TwoEpisodes();
        _finder.Answer = (_, _) => Found();
        await Scan();
        await Refresh();
        Assert.Equal(ArtworkSource.Online, Poster("Show X")!.Source);
        Touch(_drive, @"Serien\Show X\folder.jpg");

        await Refresh(force: true);

        Assert.Equal(ArtworkSource.Sidecar, Poster("Show X")!.Source);
    }

    [Fact]
    public async Task A_schema_6_database_is_migrated_keeping_the_artwork_and_allowing_online()
    {
        TwoEpisodes();
        Touch(_drive, @"Serien\Show X\folder.jpg");
        await Scan();
        await Refresh();
        var id = Title("Show X").Id;
        _db.Dispose();

        var path = Path.Combine(_base, "library.db");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE playback_old (video_id INTEGER PRIMARY KEY REFERENCES videos(id) ON DELETE CASCADE, position_sec REAL NOT NULL, watched INTEGER NOT NULL, updated_utc TEXT NOT NULL); INSERT INTO playback_old SELECT video_id, position_sec, watched, updated_utc FROM playback WHERE profile_id = 1; DROP TABLE playback; ALTER TABLE playback_old RENAME TO playback; CREATE TABLE watchlist_old (title_id INTEGER PRIMARY KEY REFERENCES titles(id) ON DELETE CASCADE, added_utc TEXT NOT NULL); INSERT INTO watchlist_old SELECT title_id, added_utc FROM watchlist WHERE profile_id = 1; DROP TABLE watchlist; ALTER TABLE watchlist_old RENAME TO watchlist; CREATE TABLE podcast_progress_old (episode_id INTEGER PRIMARY KEY REFERENCES podcast_episodes(id) ON DELETE CASCADE, position_sec REAL NOT NULL, played INTEGER NOT NULL, updated_utc TEXT NOT NULL); INSERT INTO podcast_progress_old SELECT episode_id, position_sec, played, updated_utc FROM podcast_progress WHERE profile_id = 1; DROP TABLE podcast_progress; ALTER TABLE podcast_progress_old RENAME TO podcast_progress; DROP TABLE profile_avatars; DROP TABLE audio_settings; DROP TABLE trakt_pushed; DROP TABLE episode_air; DROP TABLE series_schedule; DROP TABLE live_epg; DROP TABLE live_channels; DROP TABLE live_sources; DROP TABLE kids_titles; DROP TABLE profiles; DROP INDEX files_fingerprint; ALTER TABLE files DROP COLUMN fingerprint; DROP TABLE collection_titles; DROP TABLE collections; DROP TABLE episode_info; DROP TABLE title_info; DROP TABLE album_artists; DROP TABLE track_artists; DROP TABLE album_names; DROP TABLE track_names; DROP TABLE watchlist; DROP TABLE playlist_items;
                DROP TABLE playlists;
                DROP TABLE removed_files;
                DROP TABLE album_lookups;
                DROP TABLE online_lookups;
                CREATE TABLE artwork_old AS SELECT * FROM artwork;
                DROP TABLE artwork;
                CREATE TABLE artwork (
                    owner_kind TEXT NOT NULL CHECK (owner_kind IN ('title', 'video')),
                    owner_id INTEGER NOT NULL,
                    kind TEXT NOT NULL CHECK (kind IN ('poster', 'landscape')),
                    source TEXT NOT NULL CHECK (source IN ('user', 'sidecar', 'embedded', 'frame')),
                    file TEXT NOT NULL,
                    updated_utc TEXT NOT NULL,
                    PRIMARY KEY (owner_kind, owner_id, kind)
                );
                INSERT INTO artwork SELECT * FROM artwork_old;
                DROP TABLE artwork_old;
                ALTER TABLE color_settings DROP COLUMN lut; ALTER TABLE color_settings DROP COLUMN lut_strength;
                PRAGMA user_version = 6;
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var migrated = new LibraryDb(path);

        Assert.Equal(ArtworkSource.Sidecar, migrated.GetArtwork(ArtworkOwner.Title, id, ArtworkKind.Poster)!.Source);
        Assert.Equal(ArtworkSource.Frame, migrated.GetArtwork(ArtworkOwner.Title, id, ArtworkKind.Landscape)!.Source);
        migrated.SetArtwork(ArtworkOwner.Title, id, ArtworkKind.Poster, ArtworkSource.Online, "x.jpg");
        Assert.Equal(ArtworkSource.Online, migrated.GetArtwork(ArtworkOwner.Title, id, ArtworkKind.Poster)!.Source);
        migrated.SetOnlineLookup(id, "tvmaze", "Show X|", found: false);
        Assert.False(migrated.GetOnlineLookup(id, "tvmaze")!.Found);
    }
}
