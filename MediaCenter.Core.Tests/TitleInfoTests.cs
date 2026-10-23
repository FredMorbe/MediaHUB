using MediaCenter.Core;
using Microsoft.Data.Sqlite;
using static MediaCenter.Core.Tests.TestSupport;

namespace MediaCenter.Core.Tests;

public sealed class OmdbInfoTests
{
    private const string Key = "geheim123";
    private readonly FakeHttp _http = new();

    private OmdbClient Client() => new(_http.Client(), Key, TimeSpan.Zero);

    private void Route(string query, string json) =>
        _http.Text($"https://www.omdbapi.com/?apikey={Key}&type=movie&{query}", json, "application/json");

    [Fact]
    public async Task Plot_rating_director_and_cast_come_from_the_full_plot_request()
    {
        Route("t=Inception&y=2010&plot=full",
            "{\"Title\":\"Inception\",\"Year\":\"2010\",\"Director\":\"Christopher Nolan\",\"Actors\":\"Leonardo DiCaprio, Elliot Page\","
            + "\"Plot\":\"A thief who steals secrets.\",\"imdbRating\":\"8.8\",\"Poster\":\"N/A\",\"Response\":\"True\"}");

        var info = await Client().FindInfoAsync("Inception", 2010);

        Assert.Equal("A thief who steals secrets.", info!.Plot);
        Assert.Equal(8.8, info.Rating);
        Assert.Equal("Christopher Nolan", info.Director);
        Assert.Equal("Leonardo DiCaprio, Elliot Page", info.Cast);
        Assert.Empty(info.Episodes);
    }

    [Fact]
    public async Task Fields_OMDb_does_not_know_are_null()
    {
        Route("t=Dune&y=2021&plot=full",
            "{\"Title\":\"Dune\",\"Year\":\"2021\",\"Director\":\"N/A\",\"Actors\":\"N/A\",\"Plot\":\"N/A\",\"imdbRating\":\"N/A\",\"Poster\":\"N/A\",\"Response\":\"True\"}");

        var info = await Client().FindInfoAsync("Dune", 2021);

        Assert.NotNull(info); // der Film ist erkannt, es gibt nur nichts Anzuzeigendes
        Assert.True(info!.IsEmpty);
        Assert.Null(info.Plot);
        Assert.Null(info.Rating);
    }

    [Fact]
    public async Task A_different_film_gives_no_info()
    {
        var other = "{\"Title\":\"Teaser Bonus-Episode 61: Der Pate (1972)\",\"Year\":\"1972\",\"Plot\":\"Podcast\",\"Response\":\"True\"}";
        Route("t=Der%20Pate&y=1972&plot=full", other);
        Route("t=Der%20Pate&plot=full", other);

        Assert.Null(await Client().FindInfoAsync("Der Pate", 1972));
    }
}

public sealed class TvMazeInfoTests
{
    private readonly FakeHttp _http = new();

    private TvMazeClient Client() => new(_http.Client(), TimeSpan.Zero);

    private const string ShowUrl = "https://api.tvmaze.com/shows/100?embed%5B%5D=episodes&embed%5B%5D=cast";

    private void RouteSearch(string query, string json) => _http.Text("https://api.tvmaze.com/search/shows?q=" + query, json, "application/json");

    private static string Search(string name, string? summary = null, double? rating = null) =>
        "[{\"score\":1,\"show\":{\"id\":100,\"name\":\"" + name + "\",\"premiered\":\"2017-12-01\",\"image\":null"
        + ",\"summary\":" + (summary is null ? "null" : "\"" + summary + "\"")
        + ",\"rating\":{\"average\":" + (rating?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null") + "}}}]";

    [Fact]
    public async Task Plot_rating_cast_and_episode_texts_are_read_without_html()
    {
        RouteSearch("Dark", Search("Dark", "<p>A <b>missing</b> child &amp; a cave.</p>", 8.7));
        _http.Text(ShowUrl, """
            {"id":100,"_embedded":{
              "episodes":[
                {"season":1,"number":1,"name":"Secrets","summary":"<p>Winden mourns.</p>"},
                {"season":1,"number":2,"name":"Lies","summary":null},
                {"season":1,"number":null,"name":"Special","summary":"<p>Not a numbered episode.</p>"}],
              "cast":[
                {"person":{"name":"Louis Hofmann"}},{"person":{"name":"Louis Hofmann"}},{"person":{"name":"Lisa Vicari"}},
                {"person":{"name":"A"}},{"person":{"name":"B"}},{"person":{"name":"C"}},{"person":{"name":"D"}},{"person":{"name":"E"}}]}}
            """, "application/json");

        var info = await Client().FindInfoAsync("Dark", null); // die Serie hat kein Bild: für Infotexte reicht der Name

        Assert.Equal("A missing child & a cave.", info!.Plot);
        Assert.Equal(8.7, info.Rating);
        Assert.Null(info.Director);
        Assert.Equal("Louis Hofmann, Lisa Vicari, A, B, C, D", info.Cast); // doppelte Namen einmal, höchstens sechs
        Assert.Equal(new[] { new EpisodeText(1, 1, "Secrets", "Winden mourns."), new EpisodeText(1, 2, "Lies", null) }, info.Episodes);
    }

    [Fact]
    public async Task A_german_title_is_found_through_the_alternative_names_for_info_too()
    {
        RouteSearch("Die%20Simpsons", Search("The Simpsons", "<p>Family.</p>"));
        _http.Text("https://api.tvmaze.com/shows/100/akas", "[{\"name\":\"Die Simpsons\"}]", "application/json");
        _http.Text(ShowUrl, "{\"id\":100,\"_embedded\":{\"episodes\":[],\"cast\":[]}}", "application/json");

        Assert.Equal("Family.", (await Client().FindInfoAsync("Die Simpsons", null))!.Plot);
    }

    [Fact]
    public async Task A_show_with_nothing_to_show_and_a_different_show_give_null()
    {
        RouteSearch("Leer", Search("Leer"));
        _http.Text(ShowUrl, "{\"id\":100,\"_embedded\":{\"episodes\":[],\"cast\":[]}}", "application/json");
        RouteSearch("Dark", Search("Dark Matter", "<p>Space.</p>"));
        _http.Text("https://api.tvmaze.com/shows/100/akas", "[]", "application/json");

        Assert.Null(await Client().FindInfoAsync("Leer", null));
        Assert.Null(await Client().FindInfoAsync("Dark", null));
    }

    [Fact]
    public async Task An_error_status_is_loud()
    {
        RouteSearch("Dark", Search("Dark"));
        _http.Status(ShowUrl, System.Net.HttpStatusCode.InternalServerError);

        await Assert.ThrowsAsync<HttpRequestException>(() => Client().FindInfoAsync("Dark", null));
    }
}

public sealed class TitleInfoServiceTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-info-" + Guid.NewGuid().ToString("N"));
    private readonly string _drive;
    private readonly string _cache;
    private readonly FakeVolumes _volumes = new();
    private readonly FakeProbe _probe = new();
    private readonly FakeImageTool _tool = new();
    private readonly FakeHttp _http = new();
    private readonly FakeInfo _series = new("fakeseries");
    private readonly FakeInfo _movies = new("fakemovies");
    private readonly LibraryDb _db;
    private readonly LibraryService _library;
    private readonly ArtworkService _art;

    private sealed class FakeInfo(string provider) : ITitleInfoSource
    {
        public string Provider => provider;

        public List<string> Asked { get; } = new();

        public Func<string, int?, OnlineTitleInfo?> Answer { get; set; } = (_, _) => null;

        public Task<OnlineTitleInfo?> FindInfoAsync(string title, int? year, CancellationToken ct = default)
        {
            Asked.Add($"{title}|{year}");
            return Task.FromResult(Answer(title, year));
        }
    }

    public TitleInfoServiceTests()
    {
        _drive = Path.Combine(_base, "drive");
        _cache = Path.Combine(_base, "cache");
        Directory.CreateDirectory(_drive);
        _db = new LibraryDb(Path.Combine(_base, "library.db"));
        _volumes.Mounted.Add(Vol("AAAA0001", _drive));
        _library = new LibraryService(_db, _volumes, _probe);
        _art = new ArtworkService(_db, _tool, _probe, _cache)
        {
            Online = new OnlineArtwork(null, null, new RemoteImages(_http.Client(), _tool, _cache), null, _series, _movies),
        };
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

    private static OnlineTitleInfo Info(string plot, params EpisodeText[] episodes) => new("x", plot, 7.5, "Regie", "A, B", episodes);

    private void Series(string show = "Show X")
    {
        Touch(_drive, $@"Serien\{show}\Season 1\S01E01.mkv");
        Touch(_drive, $@"Serien\{show}\Season 1\S01E02.mkv");
    }

    [Fact]
    public async Task A_movie_and_a_series_get_their_info_from_their_own_source()
    {
        Touch(_drive, @"Filme\Solo (2020).mkv");
        Series();
        _movies.Answer = (_, _) => Info("Ein Film");
        _series.Answer = (_, _) => Info("Eine Serie", new EpisodeText(1, 1, "Pilot", "Es beginnt."), new EpisodeText(1, 2, "Zwei", null));
        await Scan();

        var result = await Refresh();

        Assert.Empty(result.Failures);
        Assert.Equal(new[] { "Solo|2020" }, _movies.Asked);
        Assert.Equal(new[] { "Show X|" }, _series.Asked);
        var movie = _db.GetTitleInfo(Title("Solo").Id)!;
        Assert.Equal(("fakemovies", "Ein Film", 7.5, "Regie", "A, B"), (movie.Provider, movie.Plot, movie.Rating, movie.Director, movie.Cast));
        Assert.Empty(_db.GetEpisodeInfos(Title("Solo").Id));
        Assert.Equal("Eine Serie", _db.GetTitleInfo(Title("Show X").Id)!.Plot);
        Assert.Equal(new[] { new EpisodeText(1, 1, "Pilot", "Es beginnt."), new EpisodeText(1, 2, "Zwei", null) }, _db.GetEpisodeInfos(Title("Show X").Id));
    }

    [Fact]
    public async Task A_title_with_info_is_not_asked_again_but_force_replaces_it()
    {
        Series();
        _series.Answer = (_, _) => Info("Alt", new EpisodeText(1, 1, "Pilot", null), new EpisodeText(1, 9, "Weg", null));
        await Scan();
        await Refresh();

        await Refresh();
        Assert.Single(_series.Asked);

        _series.Answer = (_, _) => Info("Neu", new EpisodeText(1, 1, "Pilot neu", null));
        await Refresh(force: true);

        Assert.Equal(2, _series.Asked.Count);
        Assert.Equal("Neu", _db.GetTitleInfo(Title("Show X").Id)!.Plot);
        Assert.Equal(new[] { new EpisodeText(1, 1, "Pilot neu", null) }, _db.GetEpisodeInfos(Title("Show X").Id)); // die alte Folge 9 ist weg
    }

    [Fact]
    public async Task No_match_is_remembered_and_force_asks_again()
    {
        Series();
        await Scan();

        await Refresh();
        await Refresh();
        Assert.Single(_series.Asked);
        Assert.False(_db.GetOnlineLookup(Title("Show X").Id, "info:fakeseries")!.Found);
        Assert.Null(_db.GetTitleInfo(Title("Show X").Id));

        _series.Answer = (_, _) => Info("Jetzt da");
        await Refresh(force: true);

        Assert.Equal("Jetzt da", _db.GetTitleInfo(Title("Show X").Id)!.Plot);
    }

    [Fact]
    public async Task An_info_without_anything_to_show_counts_as_no_match()
    {
        Series();
        _series.Answer = (_, _) => new OnlineTitleInfo("x", null, null, null, null, []);
        await Scan();

        await Refresh();

        Assert.Null(_db.GetTitleInfo(Title("Show X").Id));
        Assert.False(_db.GetOnlineLookup(Title("Show X").Id, "info:fakeseries")!.Found);
    }

    [Fact]
    public async Task A_failing_service_is_reported_once_and_not_asked_again()
    {
        Touch(_drive, @"Filme\Solo (2020).mkv");
        Touch(_drive, @"Filme\Duo (2021).mkv");
        _movies.Answer = (_, _) => throw new PosterSourceUnavailableException("OMDb: Request limit reached!");
        await Scan();

        var result = await Refresh();

        Assert.Single(_movies.Asked);
        var failure = Assert.Single(result.Failures);
        Assert.Contains("fakemovies", failure.Subject);
        Assert.Contains("Request limit reached!", failure.Error);
        Assert.Null(_db.GetOnlineLookup(Title("Solo").Id, "info:fakemovies")); // ein Ausfall zählt nicht als „nichts gefunden“
    }

    [Fact]
    public async Task Without_the_internet_nothing_is_asked()
    {
        Series();
        _series.Answer = (_, _) => Info("Egal");
        _art.Online = null;
        await Scan();

        await Refresh();

        Assert.Empty(_series.Asked);
        Assert.Null(_db.GetTitleInfo(Title("Show X").Id));
    }

    [Fact]
    public async Task Removing_a_title_removes_its_info()
    {
        Series();
        _series.Answer = (_, _) => Info("Text", new EpisodeText(1, 1, "Pilot", null));
        await Scan();
        await Refresh();
        var id = Title("Show X").Id;

        _db.RemoveTitle(id);

        Assert.Null(_db.GetTitleInfo(id));
        Assert.Empty(_db.GetEpisodeInfos(id));
    }
}
