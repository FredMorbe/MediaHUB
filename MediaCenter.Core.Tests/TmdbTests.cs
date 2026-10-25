using System.Net;
using System.Text;
using MediaCenter.Core;
using Microsoft.Data.Sqlite;
using static MediaCenter.Core.Tests.TestSupport;

namespace MediaCenter.Core.Tests;

/// <summary>Antwortet auf Pfade von api.themoviedb.org/3 (ohne Parameter) und merkt sich, was gefragt wurde.</summary>
internal sealed class TmdbHttp : HttpMessageHandler
{
    public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = new();

    /// <summary>Pfad (z. B. "search/movie"), Parameter und Authorization-Kopf jeder Anfrage.</summary>
    public List<(string Path, string Query, string? Auth)> Requests { get; } = new();

    public void Json(string path, string body) =>
        Routes[path] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public void Status(string path, HttpStatusCode code) => Routes[path] = () => new HttpResponseMessage(code);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        Assert.Equal("api.themoviedb.org", uri.Host);
        Assert.Equal("https", uri.Scheme);
        var path = uri.AbsolutePath["/3/".Length..];
        Requests.Add((path, uri.Query, request.Headers.Authorization?.ToString()));
        return Task.FromResult(Routes.TryGetValue(path, out var make) ? make() : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    public HttpClient Client() => new(this);
}

public sealed class TmdbClientTests
{
    private const string V3Key = "0123456789abcdef0123456789abcdef";

    private readonly TmdbHttp _http = new();

    private TmdbClient Client(string key = V3Key) => new(_http.Client(), key, TimeSpan.Zero);

    private static string Str(string? value) => value is null ? "null" : "\"" + value + "\"";

    private static string Movie(int id, string title, string original, string date, string? poster = "/p.jpg", string? backdrop = "/b.jpg") =>
        "{\"id\":" + id + ",\"title\":\"" + title + "\",\"original_title\":\"" + original + "\",\"release_date\":\"" + date
        + "\",\"poster_path\":" + Str(poster) + ",\"backdrop_path\":" + Str(backdrop) + "}";

    private static string Show(int id, string name, string original, string date) =>
        "{\"id\":" + id + ",\"name\":\"" + name + "\",\"original_name\":\"" + original + "\",\"first_air_date\":\"" + date
        + "\",\"poster_path\":\"/sp.jpg\",\"backdrop_path\":\"/sb.jpg\"}";

    private static string Results(params string[] items) => "{\"results\":[" + string.Join(",", items) + "]}";

    [Fact]
    public async Task A_movie_is_found_by_its_german_title_and_gives_poster_and_backdrop()
    {
        _http.Json("search/movie", Results(Movie(1, "Der Pate 2", "The Godfather Part II", "1974-12-12"), Movie(238, "Der Pate", "The Godfather", "1972-03-14")));

        var poster = await Client().Movies.FindAsync("Der Pate", 1972);
        var backdrop = await Client().Movies.FindBackdropAsync("Der Pate", 1972);

        Assert.Equal("Der Pate", poster!.ShowName);
        Assert.Equal("https://image.tmdb.org/t/p/w500/p.jpg", poster.ImageUrl);
        Assert.Equal("https://image.tmdb.org/t/p/w1280/b.jpg", backdrop!.ImageUrl);
    }

    [Fact]
    public async Task The_original_title_matches_too()
    {
        _http.Json("search/movie", Results(Movie(238, "Der Pate", "The Godfather", "1972-03-14")));

        Assert.NotNull(await Client().Movies.FindAsync("The Godfather", 1972));
    }

    [Fact]
    public async Task A_different_film_or_a_wrong_year_gives_nothing()
    {
        _http.Json("search/movie", Results(Movie(5, "Dark Matter", "Dark Matter", "2015-01-01"), Movie(6, "Der Pate", "The Godfather", "1972-03-14")));

        Assert.Null(await Client().Movies.FindAsync("Dark", null)); // ähnlich ist nicht gleich
        Assert.Null(await Client().Movies.FindAsync("Der Pate", 1999)); // kein Jahr passt: lieber kein Poster als ein falsches
        Assert.NotNull(await Client().Movies.FindAsync("Der Pate", 1973)); // ohne Jahr gefragt darf das Jahr um eins abweichen
    }

    [Fact]
    public async Task Without_a_sequel_number_the_first_film_is_not_taken_for_the_second()
    {
        _http.Json("search/movie", Results(Movie(1, "Ghostbusters", "Ghostbusters", "1984-06-07")));

        Assert.Null(await Client().Movies.FindAsync("Ghostbusters 2", null));
    }

    [Fact]
    public async Task Image_paths_from_the_answer_must_be_plain_file_names()
    {
        _http.Json("search/movie", Results(Movie(1, "Solo", "Solo", "2020-01-01", poster: "/../../evil.jpg", backdrop: "https://evil.example/x.jpg")));

        Assert.Null(await Client().Movies.FindAsync("Solo", 2020));
        Assert.Null(await Client().Movies.FindBackdropAsync("Solo", 2020));
    }

    [Fact]
    public async Task One_search_serves_poster_backdrop_and_info()
    {
        _http.Json("search/movie", Results(Movie(238, "Der Pate", "The Godfather", "1972-03-14")));
        _http.Json("movie/238", "{\"overview\":\"Die Familie.\"}");
        var client = Client();

        await client.Movies.FindAsync("Der Pate", 1972);
        await client.Movies.FindBackdropAsync("Der Pate", 1972);
        await client.Movies.FindInfoAsync("Der Pate", 1972);

        Assert.Single(_http.Requests, r => r.Path == "search/movie");
    }

    [Fact]
    public async Task Movie_info_has_the_german_plot_rating_director_cast_and_the_best_trailer()
    {
        _http.Json("search/movie", Results(Movie(238, "Der Pate", "The Godfather", "1972-03-14")));
        _http.Json("movie/238", """
            {"overview":"Die Familie Corleone.","vote_average":8.7,"vote_count":100,
             "credits":{"cast":[{"name":"Marlon Brando"},{"name":"Al Pacino"},{"name":"Al Pacino"}],
                        "crew":[{"job":"Producer","name":"Albert Ruddy"},{"job":"Director","name":"Francis Ford Coppola"}]},
             "videos":{"results":[
               {"site":"YouTube","type":"Teaser","key":"teaser1234","iso_639_1":"de","official":true,"published_at":"2020-01-01T00:00:00.000Z"},
               {"site":"Vimeo","type":"Trailer","key":"vimeo12345","iso_639_1":"de","official":true,"published_at":"2020-01-01T00:00:00.000Z"},
               {"site":"YouTube","type":"Trailer","key":"engOfficial","iso_639_1":"en","official":true,"published_at":"2021-01-01T00:00:00.000Z"},
               {"site":"YouTube","type":"Trailer","key":"deUnofficial","iso_639_1":"de","official":false,"published_at":"2022-01-01T00:00:00.000Z"},
               {"site":"YouTube","type":"Trailer","key":"bad/key!!","iso_639_1":"de","official":true,"published_at":"2023-01-01T00:00:00.000Z"},
               {"site":"YouTube","type":"Trailer","key":"deOfficial","iso_639_1":"de","official":true,"published_at":"2018-01-01T00:00:00.000Z"}]}}
            """);

        var info = await Client().Movies.FindInfoAsync("Der Pate", 1972);

        Assert.Equal("Die Familie Corleone.", info!.Plot);
        Assert.Equal(8.7, info.Rating);
        Assert.Equal("Francis Ford Coppola", info.Director);
        Assert.Equal("Marlon Brando, Al Pacino", info.Cast);
        Assert.Equal("deOfficial", info.TrailerKey);
        Assert.Empty(info.Episodes);
        Assert.Contains(_http.Requests, r => r.Path == "movie/238" && r.Query.Contains("language=de-DE") && r.Query.Contains("append_to_response=credits,videos"));
    }

    [Fact]
    public async Task A_rating_from_few_votes_and_an_empty_overview_are_left_out()
    {
        _http.Json("search/movie", Results(Movie(1, "Neu", "Neu", "2026-01-01")));
        _http.Json("movie/1", "{\"overview\":\"\",\"vote_average\":10.0,\"vote_count\":3}");

        // Nichts davon ist anzeigbar: der Dienst kennt den Film, hat aber keine Texte.
        Assert.Null(await Client().Movies.FindInfoAsync("Neu", 2026));
    }

    [Fact]
    public async Task Series_info_has_the_episode_texts_without_specials_and_without_placeholder_names()
    {
        _http.Json("search/tv", Results(Show(70523, "Dark", "Dark", "2017-12-01")));
        _http.Json("tv/70523", """
            {"overview":"Winden.","vote_average":8.4,"vote_count":5000,
             "aggregate_credits":{"cast":[{"name":"Louis Hofmann"},{"name":"Lisa Vicari"}]},
             "seasons":[{"season_number":0},{"season_number":1},{"season_number":2}],
             "videos":{"results":[]}}
            """);
        _http.Json("tv/70523/season/1", """{"episodes":[{"episode_number":1,"name":"Geheimnisse","overview":"Ein Junge verschwindet."},{"episode_number":2,"name":"Folge 2","overview":""}]}""");
        _http.Json("tv/70523/season/2", """{"episodes":[{"episode_number":1,"name":"Anfang","overview":null}]}""");

        var info = await Client().Series.FindInfoAsync("Dark", null);

        Assert.Equal("Winden.", info!.Plot);
        Assert.Equal("Louis Hofmann, Lisa Vicari", info.Cast);
        Assert.Null(info.Director);
        Assert.Null(info.TrailerKey);
        Assert.Equal(new[] { new EpisodeText(1, 1, "Geheimnisse", "Ein Junge verschwindet."), new EpisodeText(2, 1, "Anfang", null) }, info.Episodes);
        Assert.DoesNotContain(_http.Requests, r => r.Path == "tv/70523/season/0");
    }

    [Fact]
    public async Task A_v3_key_goes_into_the_address_and_a_v4_token_into_the_header()
    {
        _http.Json("search/movie", Results());

        await Client().Movies.FindAsync("Solo", null);
        var token = "eyJhbGciOiJIUzI1NiJ9.eyJhdWQiOiIxMjM0In0.sig-nature_";
        await Client(token).Movies.FindAsync("Solo", null);

        var v3 = _http.Requests[0];
        Assert.Contains("api_key=" + V3Key, v3.Query);
        Assert.Null(v3.Auth);
        var v4 = _http.Requests[1];
        Assert.DoesNotContain("api_key", v4.Query);
        Assert.Equal("Bearer " + token, v4.Auth);
    }

    [Fact]
    public async Task A_rejected_key_is_reported_without_revealing_it_and_a_missing_key_too()
    {
        _http.Status("search/movie", HttpStatusCode.Unauthorized);

        var rejected = await Assert.ThrowsAsync<PosterSourceUnavailableException>(() => Client().Movies.FindAsync("Solo", null));
        Assert.DoesNotContain(V3Key, rejected.Message);
        await Assert.ThrowsAsync<PosterSourceUnavailableException>(() => Client("").Movies.FindAsync("Solo", null));
    }

    [Fact]
    public async Task A_rate_limit_answer_is_retried_once_and_then_the_service_counts_as_unavailable()
    {
        var calls = 0;
        _http.Routes["search/movie"] = () =>
            ++calls == 1
                ? new HttpResponseMessage((HttpStatusCode)429) { Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero) } }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Results(Movie(1, "Solo", "Solo", "2020-01-01"))) };
        Assert.NotNull(await Client().Movies.FindAsync("Solo", 2020));
        Assert.Equal(2, calls);

        var again = 0;
        _http.Routes["search/movie"] = () =>
        {
            again++;
            return new HttpResponseMessage((HttpStatusCode)429) { Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero) } };
        };
        await Assert.ThrowsAsync<PosterSourceUnavailableException>(() => Client().Movies.FindAsync("Solo", 2020));
        Assert.Equal(2, again);
    }

    [Fact]
    public async Task A_server_error_is_loud_and_a_vanished_entry_gives_null()
    {
        _http.Json("search/movie", Results(Movie(7, "Solo", "Solo", "2020-01-01")));
        _http.Status("movie/7", HttpStatusCode.InternalServerError);
        await Assert.ThrowsAsync<HttpRequestException>(() => Client().Movies.FindInfoAsync("Solo", 2020));

        _http.Status("movie/7", HttpStatusCode.NotFound);
        Assert.Null(await Client().Movies.FindInfoAsync("Solo", 2020));
    }
}

public sealed class TmdbArtworkTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-tmdb-" + Guid.NewGuid().ToString("N"));
    private readonly string _drive;
    private readonly string _cache;
    private readonly FakeVolumes _volumes = new();
    private readonly FakeProbe _probe = new();
    private readonly FakeImageTool _tool = new();
    private readonly FakeHttp _http = new();
    private readonly FakeRich _rich = new();
    private readonly FakePlain _plain = new();
    private readonly LibraryDb _db;
    private readonly LibraryService _library;
    private readonly ArtworkService _art;

    private sealed class FakeRich(string provider = "fakerich") : IRichTitleSource
    {
        public string Provider => provider;

        public Func<OnlinePoster?> Poster { get; set; } = () => null;

        public Func<OnlinePoster?> Backdrop { get; set; } = () => null;

        public Func<OnlineTitleInfo?> Info { get; set; } = () => null;

        public int PosterCalls { get; private set; }

        public int BackdropCalls { get; private set; }

        public int InfoCalls { get; private set; }

        public Task<OnlinePoster?> FindAsync(string title, int? year, CancellationToken ct = default)
        {
            PosterCalls++;
            return Task.FromResult(Poster());
        }

        public Task<OnlinePoster?> FindBackdropAsync(string title, int? year, CancellationToken ct = default)
        {
            BackdropCalls++;
            return Task.FromResult(Backdrop());
        }

        public Task<OnlineTitleInfo?> FindInfoAsync(string title, int? year, CancellationToken ct = default)
        {
            InfoCalls++;
            return Task.FromResult(Info());
        }
    }

    private sealed class FakePlain : IPosterSource, ITitleInfoSource
    {
        public string Provider => "fakeplain";

        public Func<OnlinePoster?> Poster { get; set; } = () => null;

        public Func<OnlineTitleInfo?> Info { get; set; } = () => null;

        public int InfoCalls { get; private set; }

        public Task<OnlinePoster?> FindAsync(string title, int? year, CancellationToken ct = default) => Task.FromResult(Poster());

        public Task<OnlineTitleInfo?> FindInfoAsync(string title, int? year, CancellationToken ct = default)
        {
            InfoCalls++;
            return Task.FromResult(Info());
        }
    }

    public TmdbArtworkTests()
    {
        _drive = Path.Combine(_base, "drive");
        _cache = Path.Combine(_base, "cache");
        Directory.CreateDirectory(_drive);
        _db = new LibraryDb(Path.Combine(_base, "library.db"));
        _volumes.Mounted.Add(Vol("AAAA0001", _drive));
        _library = new LibraryService(_db, _volumes, _probe);
        _art = new ArtworkService(_db, _tool, _probe, _cache) { Online = WithRich() };
        _http.Bytes("https://img.test/p.jpg", new byte[] { 1 });
        _http.Bytes("https://img.test/b.jpg", new byte[] { 2 });
        Touch(_drive, @"Filme\Solo (2020).mkv");
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_base, recursive: true);
    }

    private OnlineArtwork WithRich(bool rich = true) =>
        new(_plain, _plain, new RemoteImages(_http.Client(), _tool, _cache), null, _plain, _plain, rich ? _rich : null, rich ? _rich : null);

    private async Task Scan() => await _library.AddFolderAsync(_drive);

    private Task<ArtworkResult> Refresh(bool force = false) => _art.RefreshAsync(_library.RefreshVolumes(), force);

    private TitleRow Solo => _db.GetTitles().Single(t => t.Name == "Solo");

    private ArtworkRow? Poster => _db.GetArtwork(ArtworkOwner.Title, Solo.Id, ArtworkKind.Poster);

    private ArtworkRow? Landscape => _db.GetArtwork(ArtworkOwner.Title, Solo.Id, ArtworkKind.Landscape);

    private static OnlinePoster Image(string file) => new("Solo", "https://img.test/" + file);

    private static OnlineTitleInfo Info(string? plot, double? rating = 7.1, string? trailer = null) =>
        new("Solo", plot, rating, "Regie", "A, B", [], trailer);

    [Fact]
    public async Task The_rich_source_gives_poster_backdrop_info_and_trailer_and_the_plain_ones_stay_quiet()
    {
        _rich.Poster = () => Image("p.jpg");
        _rich.Backdrop = () => Image("b.jpg");
        _rich.Info = () => Info("Deutsche Handlung", trailer: "abcDEF123");
        _plain.Info = () => Info("English plot");
        await Scan();

        var result = await Refresh();

        Assert.Empty(result.Failures);
        Assert.Equal(ArtworkSource.Online, Poster!.Source);
        Assert.Equal(ArtworkSource.Online, Landscape!.Source);
        Assert.Contains(_tool.Calls, c => c.StartsWith("scale:") && c.EndsWith(":1280x720") && c.Contains(".download"));
        var info = _db.GetTitleInfo(Solo.Id)!;
        Assert.Equal(("fakerich", "Deutsche Handlung", "abcDEF123"), (info.Provider, info.Plot, info.TrailerKey));
        Assert.Equal(0, _plain.InfoCalls);
    }

    [Fact]
    public async Task The_plain_sources_step_in_where_the_rich_one_finds_nothing()
    {
        _plain.Poster = () => Image("p.jpg");
        _plain.Info = () => Info("English plot");
        await Scan();

        var result = await Refresh();

        Assert.Empty(result.Failures);
        Assert.Equal(ArtworkSource.Online, Poster!.Source);
        Assert.Equal(ArtworkSource.Frame, Landscape!.Source);
        Assert.Equal("fakeplain", _db.GetTitleInfo(Solo.Id)!.Provider);
        // Absagen werden je Dienst und Art gemerkt und nicht sofort wieder erfragt.
        Assert.False(_db.GetOnlineLookup(Solo.Id, "fakerich")!.Found);
        Assert.False(_db.GetOnlineLookup(Solo.Id, "backdrop:fakerich")!.Found);
        Assert.False(_db.GetOnlineLookup(Solo.Id, "info:fakerich")!.Found);
        await Refresh();
        Assert.Equal((1, 1, 1), (_rich.PosterCalls, _rich.BackdropCalls, _rich.InfoCalls));
    }

    [Fact]
    public async Task A_failing_rich_source_is_reported_once_and_the_plain_ones_take_over()
    {
        _rich.Poster = () => throw new PosterSourceUnavailableException("TMDB: Der Schlüssel wurde abgelehnt.");
        _plain.Poster = () => Image("p.jpg");
        _plain.Info = () => Info("English plot");
        await Scan();

        var result = await Refresh();

        var failure = Assert.Single(result.Failures);
        Assert.Contains("fakerich", failure.Subject);
        Assert.Contains("abgelehnt", failure.Error);
        Assert.Equal(ArtworkSource.Online, Poster!.Source);
        Assert.Equal("fakeplain", _db.GetTitleInfo(Solo.Id)!.Provider);
        Assert.Equal(0, _rich.InfoCalls); // der Dienst ist für diesen Durchlauf abgeschrieben
    }

    [Fact]
    public async Task A_missing_plot_is_filled_in_by_the_next_service()
    {
        _rich.Info = () => Info(null, rating: 8.0);
        _plain.Info = () => Info("English plot", rating: 6.0);
        await Scan();

        await Refresh();

        var info = _db.GetTitleInfo(Solo.Id)!;
        Assert.Equal(("fakerich", "English plot", 8.0), (info.Provider, info.Plot, info.Rating));
    }

    [Fact]
    public async Task Texts_and_backdrops_from_before_are_upgraded_without_force_once_a_key_is_there()
    {
        _art.Online = WithRich(rich: false);
        _plain.Info = () => Info("English plot", rating: 6.0);
        await Scan();
        await Refresh();
        Assert.Equal("fakeplain", _db.GetTitleInfo(Solo.Id)!.Provider);
        Assert.Equal(ArtworkSource.Frame, Landscape!.Source);

        _art.Online = WithRich();
        _rich.Backdrop = () => Image("b.jpg");
        _rich.Info = () => Info("Deutsche Handlung", rating: 8.0);
        await Refresh();

        var info = _db.GetTitleInfo(Solo.Id)!;
        Assert.Equal(("fakerich", "Deutsche Handlung", 8.0), (info.Provider, info.Plot, info.Rating));
        Assert.Equal(ArtworkSource.Online, Landscape!.Source);
        Assert.Equal(1, _plain.InfoCalls); // die englischen Texte wurden nicht noch einmal geholt
    }

    [Fact]
    public async Task An_upgrade_keeps_the_old_plot_when_the_rich_source_has_none()
    {
        _art.Online = WithRich(rich: false);
        _plain.Info = () => Info("English plot", rating: 6.0);
        await Scan();
        await Refresh();

        _art.Online = WithRich();
        _rich.Info = () => Info(null, rating: 8.0);
        await Refresh();

        var info = _db.GetTitleInfo(Solo.Id)!;
        Assert.Equal(("fakerich", "English plot", 8.0), (info.Provider, info.Plot, info.Rating));
    }

    [Fact]
    public async Task Self_written_texts_and_own_backdrops_are_never_touched()
    {
        _plain.Info = () => Info("English plot");
        await Scan();
        _db.SetUserTitleInfo(Solo.Id, "Mein Text", null, null);
        var own = Path.Combine(_base, "own.jpg");
        File.WriteAllText(own, "x");
        await _art.SetUserArtworkAsync(ArtworkOwner.Title, Solo.Id, ArtworkKind.Landscape, own);
        _rich.Backdrop = () => Image("b.jpg");
        _rich.Info = () => Info("Deutsche Handlung");

        await Refresh();
        await Refresh(force: true);

        Assert.Equal("Mein Text", _db.GetTitleInfo(Solo.Id)!.Plot);
        Assert.Equal(ArtworkSource.User, Landscape!.Source);
        Assert.Equal(0, _rich.InfoCalls);
    }

    [Fact]
    public async Task Without_any_info_source_there_is_no_info_to_refresh()
    {
        _art.Online = new OnlineArtwork(null, null, new RemoteImages(_http.Client(), _tool, _cache));
        await Scan();

        var result = await _art.RefreshInfoAsync(Solo.Id);

        Assert.False(result.Found);
        Assert.Contains("TMDB", result.Problem);
    }
}

public sealed class TmdbDatabaseTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-tmdbdb-" + Guid.NewGuid().ToString("N"));

    public TmdbDatabaseTests() => Directory.CreateDirectory(_base);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_base, recursive: true);
    }

    [Fact]
    public void The_trailer_is_stored_with_the_info_and_survives_the_user_editing_the_texts()
    {
        using var db = new LibraryDb(Path.Combine(_base, "library.db"));
        var title = db.CreateTitle(TitleKind.Movie, "Solo", 2020);

        db.SetTitleInfo(title, "tmdb", new OnlineTitleInfo("Solo", "Handlung", 7.0, null, null, [], "abcDEF123"));
        Assert.Equal("abcDEF123", db.GetTitleInfo(title)!.TrailerKey);

        db.SetUserTitleInfo(title, "Mein Text", null, null);
        Assert.Equal("abcDEF123", db.GetTitleInfo(title)!.TrailerKey);

        db.SetTitleInfo(title, "omdb", new OnlineTitleInfo("Solo", "Plot", 7.0, null, null, []));
        Assert.Null(db.GetTitleInfo(title)!.TrailerKey);
    }

    [Fact]
    public void A_schema_19_database_gets_the_trailer_column()
    {
        var path = Path.Combine(_base, "library.db");
        long title;
        using (var db = new LibraryDb(path))
        {
            title = db.CreateTitle(TitleKind.Movie, "Solo", 2020);
            db.SetTitleInfo(title, "omdb", new OnlineTitleInfo("Solo", "Plot", 7.0, null, null, []));
        }
        SqliteConnection.ClearAllPools();
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DROP TABLE profile_avatars; DROP TABLE audio_settings; DROP TABLE trakt_pushed; DROP TABLE episode_air; DROP TABLE series_schedule; DROP TABLE live_epg; DROP TABLE live_channels; DROP TABLE live_sources; ALTER TABLE title_info DROP COLUMN trailer; PRAGMA user_version = 19;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var migrated = new LibraryDb(path);

        Assert.Equal("Plot", migrated.GetTitleInfo(title)!.Plot);
        Assert.Null(migrated.GetTitleInfo(title)!.TrailerKey);
        migrated.SetTitleInfo(title, "tmdb", new OnlineTitleInfo("Solo", "Handlung", 7.0, null, null, [], "abcDEF123"));
        Assert.Equal("abcDEF123", migrated.GetTitleInfo(title)!.TrailerKey);
    }
}
