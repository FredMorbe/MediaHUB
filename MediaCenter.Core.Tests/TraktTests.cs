using System.Net;
using System.Text;
using System.Text.Json;
using MediaCenter.Core;
using static MediaCenter.Core.Tests.TestSupport;

namespace MediaCenter.Core.Tests;

/// <summary>Trakt ohne Netz: antwortet der Reihe nach je „METHODE url“, merkt sich Köpfe und Inhalt jeder Anfrage.</summary>
internal sealed class TraktHttp : HttpMessageHandler
{
    public sealed record Call(HttpMethod Method, string Url, IReadOnlyDictionary<string, string> Headers, string Body);

    private readonly Dictionary<string, Queue<(HttpStatusCode Code, string Body)>> _routes = new();

    public List<Call> Calls { get; } = new();

    public void Route(HttpMethod method, string path, HttpStatusCode code, string body = "{}") =>
        Queue(method, path).Enqueue((code, body));

    public void Json(HttpMethod method, string path, string body) => Route(method, path, HttpStatusCode.OK, body);

    private Queue<(HttpStatusCode, string)> Queue(HttpMethod method, string path)
    {
        var key = $"{method} https://api.trakt.tv{path}";
        if (!_routes.TryGetValue(key, out var queue)) _routes[key] = queue = new();
        return queue;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Calls.Add(new Call(request.Method, request.RequestUri!.AbsoluteUri, headers, body));

        var key = $"{request.Method} {request.RequestUri!.AbsoluteUri}";
        if (!_routes.TryGetValue(key, out var queue) || queue.Count == 0) return new HttpResponseMessage(HttpStatusCode.NotFound);
        var (code, text) = queue.Count > 1 ? queue.Dequeue() : queue.Peek(); // die letzte Antwort bleibt
        return new HttpResponseMessage(code) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }

    public HttpClient Client() => new(this);
}

public sealed class TraktClientTests
{
    private static readonly DateTime Now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
    private readonly TraktHttp _http = new();

    private static TraktAccount Account(DateTime? expires = null) =>
        new("client-id", "client-secret", 1, "ich", new TraktTokens("alt-zugriff", "alt-erneuerung", expires ?? Now.AddDays(60)));

    private static string TokenJson(string access = "neu-zugriff") =>
        "{\"access_token\":\"" + access + "\",\"refresh_token\":\"neu-erneuerung\",\"expires_in\":7776000,\"created_at\":" + new DateTimeOffset(Now).ToUnixTimeSeconds() + "}";

    [Fact]
    public async Task Device_auth_returns_the_code_to_show_and_sends_the_client_id()
    {
        _http.Json(HttpMethod.Post, "/oauth/device/code",
            "{\"device_code\":\"d123\",\"user_code\":\"5055CC52\",\"verification_url\":\"https://trakt.tv/activate\",\"expires_in\":600,\"interval\":5}");

        var code = await TraktClient.StartDeviceAuthAsync(_http.Client(), "client-id");

        Assert.Equal("5055CC52", code.UserCode);
        Assert.Equal("https://trakt.tv/activate", code.VerificationUrl);
        Assert.Equal(TimeSpan.FromSeconds(600), code.ExpiresIn);
        var call = Assert.Single(_http.Calls);
        Assert.Equal("client-id", call.Headers["trakt-api-key"]);
        Assert.Equal("2", call.Headers["trakt-api-version"]);
        Assert.Contains("\"client_id\":\"client-id\"", call.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task An_unknown_client_id_is_named_as_the_problem(HttpStatusCode status)
    {
        _http.Route(HttpMethod.Post, "/oauth/device/code", status);

        var ex = await Assert.ThrowsAsync<TraktAuthException>(() => TraktClient.StartDeviceAuthAsync(_http.Client(), "falsch"));

        Assert.Contains("Client-ID", ex.Message);
    }

    [Fact]
    public async Task Waiting_for_the_token_polls_until_the_user_confirmed()
    {
        _http.Route(HttpMethod.Post, "/oauth/device/token", HttpStatusCode.BadRequest);
        _http.Route(HttpMethod.Post, "/oauth/device/token", HttpStatusCode.BadRequest);
        _http.Route(HttpMethod.Post, "/oauth/device/token", HttpStatusCode.OK, TokenJson());
        var delays = new List<TimeSpan>();
        var code = new TraktDeviceCode("d123", "ABC", "https://trakt.tv/activate", TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5));

        var tokens = await TraktClient.WaitForDeviceTokenAsync(_http.Client(), Account(), code, Now, (span, _) => { delays.Add(span); return Task.CompletedTask; });

        Assert.Equal("neu-zugriff", tokens.AccessToken);
        Assert.Equal("neu-erneuerung", tokens.RefreshToken);
        Assert.Equal(Now.AddSeconds(7_776_000), tokens.ExpiresUtc);
        Assert.Equal(3, delays.Count);
        Assert.Contains("\"client_secret\":\"client-secret\"", _http.Calls[0].Body);
        Assert.Contains("\"code\":\"d123\"", _http.Calls[0].Body);
    }

    [Fact]
    public async Task Slow_down_lengthens_the_interval()
    {
        _http.Route(HttpMethod.Post, "/oauth/device/token", HttpStatusCode.TooManyRequests);
        _http.Route(HttpMethod.Post, "/oauth/device/token", HttpStatusCode.OK, TokenJson());
        var delays = new List<TimeSpan>();
        var code = new TraktDeviceCode("d", "A", "https://trakt.tv/activate", TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5));

        await TraktClient.WaitForDeviceTokenAsync(_http.Client(), Account(), code, Now, (span, _) => { delays.Add(span); return Task.CompletedTask; });

        Assert.Equal(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(6) }, delays);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "kennt den Code nicht")]
    [InlineData(HttpStatusCode.Conflict, "schon benutzt")]
    [InlineData(HttpStatusCode.Gone, "abgelaufen")]
    [InlineData((HttpStatusCode)418, "abgelehnt")]
    [InlineData(HttpStatusCode.Unauthorized, "Client-ID")]
    public async Task Refusals_while_waiting_are_clear_auth_errors(HttpStatusCode status, string fragment)
    {
        _http.Route(HttpMethod.Post, "/oauth/device/token", status);
        var code = new TraktDeviceCode("d", "A", "https://trakt.tv/activate", TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5));

        var ex = await Assert.ThrowsAsync<TraktAuthException>(() =>
            TraktClient.WaitForDeviceTokenAsync(_http.Client(), Account(), code, Now, (_, _) => Task.CompletedTask));

        Assert.Contains(fragment, ex.Message);
    }

    [Fact]
    public async Task A_code_nobody_confirms_expires()
    {
        _http.Route(HttpMethod.Post, "/oauth/device/token", HttpStatusCode.BadRequest);
        var code = new TraktDeviceCode("d", "A", "https://trakt.tv/activate", TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<TraktAuthException>(() =>
            TraktClient.WaitForDeviceTokenAsync(_http.Client(), Account(), code, Now, (_, _) => Task.CompletedTask));

        Assert.Equal(4, _http.Calls.Count);
    }

    [Fact]
    public async Task Tokens_that_expire_soon_are_refreshed_and_reported()
    {
        _http.Json(HttpMethod.Post, "/oauth/token", TokenJson("erneuert"));
        TraktTokens? saved = null;
        var client = new TraktClient(_http.Client(), Account(Now.AddDays(3)), t => saved = t);

        await client.EnsureFreshAsync(Now);

        Assert.Equal("erneuert", saved!.AccessToken);
        Assert.Equal("erneuert", client.Tokens!.AccessToken);
        Assert.Contains("\"grant_type\":\"refresh_token\"", _http.Calls.Single().Body);
        Assert.Contains("\"refresh_token\":\"alt-erneuerung\"", _http.Calls.Single().Body);
    }

    [Fact]
    public async Task Tokens_that_last_long_are_left_alone()
    {
        var client = new TraktClient(_http.Client(), Account(Now.AddDays(30)));

        await client.EnsureFreshAsync(Now);

        Assert.Empty(_http.Calls);
    }

    [Fact]
    public async Task A_refused_refresh_asks_to_connect_again()
    {
        _http.Route(HttpMethod.Post, "/oauth/token", HttpStatusCode.Unauthorized);
        var client = new TraktClient(_http.Client(), Account(Now.AddDays(1)));

        var ex = await Assert.ThrowsAsync<TraktAuthException>(() => client.EnsureFreshAsync(Now));

        Assert.Contains("neu verbinden", ex.Message);
    }

    [Fact]
    public async Task Watched_movies_and_shows_are_read_with_title_year_and_episodes()
    {
        _http.Json(HttpMethod.Get, "/sync/watched/movies",
            "[{\"plays\":1,\"last_watched_at\":\"2025-01-02T03:04:05.000Z\",\"movie\":{\"title\":\"Inception\",\"year\":2010,\"ids\":{\"trakt\":1}}},{\"movie\":{\"title\":null}}]");
        _http.Json(HttpMethod.Get, "/sync/watched/shows",
            "[{\"show\":{\"title\":\"Test Show\",\"year\":2020},\"seasons\":[{\"number\":1,\"episodes\":[{\"number\":1,\"last_watched_at\":\"2025-02-01T00:00:00.000Z\"},{\"number\":2}]},{\"number\":2,\"episodes\":[{\"number\":5}]}]}]");
        var client = new TraktClient(_http.Client(), Account());

        var movies = await client.GetWatchedMoviesAsync();
        var shows = await client.GetWatchedShowsAsync();

        var movie = Assert.Single(movies);
        Assert.Equal(("Inception", 2010), (movie.Title, movie.Year));
        Assert.Equal(new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc), movie.WatchedUtc);
        var show = Assert.Single(shows);
        Assert.Equal(new[] { (1, 1), (1, 2), (2, 5) }, show.Episodes.Select(e => (e.Season, e.Episode)).ToArray());
        Assert.All(_http.Calls, c => Assert.Equal("Bearer alt-zugriff", c.Headers["Authorization"]));
    }

    [Fact]
    public async Task Adding_history_sends_titles_with_year_and_episodes_grouped_by_season_and_reports_what_trakt_did_not_find()
    {
        _http.Json(HttpMethod.Post, "/sync/history",
            "{\"added\":{\"movies\":1,\"episodes\":2},\"not_found\":{\"movies\":[{\"title\":\"Unbekannt\"}],\"shows\":[],\"seasons\":[],\"episodes\":[]}}");
        var client = new TraktClient(_http.Client(), Account());

        var result = await client.AddHistoryAsync(
            new[] { new TraktMovieWatch("Heat", 1995, new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc)), new TraktMovieWatch("Unbekannt", null, Now) },
            new[] { new TraktShowWatch("Test Show", 2020, new[] { new TraktWatchedEpisode(1, 2, Now), new TraktWatchedEpisode(1, 1, Now), new TraktWatchedEpisode(2, 1, Now) }) });

        Assert.Equal(3, result.Added);
        Assert.Equal(new[] { "Unbekannt" }, result.NotFound);
        using var body = JsonDocument.Parse(_http.Calls.Single().Body);
        var movies = body.RootElement.GetProperty("movies");
        Assert.Equal("Heat", movies[0].GetProperty("title").GetString());
        Assert.Equal(1995, movies[0].GetProperty("year").GetInt32());
        Assert.Equal("2025-03-04T05:06:07.000Z", movies[0].GetProperty("watched_at").GetString());
        Assert.False(movies[1].TryGetProperty("year", out _)); // ohne Jahr: nichts schicken statt null
        var seasons = body.RootElement.GetProperty("shows")[0].GetProperty("seasons");
        Assert.Equal(2, seasons.GetArrayLength());
        Assert.Equal(new[] { 1, 2 }, seasons[0].GetProperty("episodes").EnumerateArray().Select(e => e.GetProperty("number").GetInt32()).ToArray());
    }

    [Fact]
    public async Task Nothing_to_add_makes_no_request()
    {
        var client = new TraktClient(_http.Client(), Account());

        var result = await client.AddHistoryAsync(Array.Empty<TraktMovieWatch>(), Array.Empty<TraktShowWatch>());

        Assert.Equal(0, result.Added);
        Assert.Empty(_http.Calls);
    }

    [Fact]
    public async Task An_unauthorized_call_refreshes_once_and_retries()
    {
        _http.Route(HttpMethod.Get, "/sync/watched/movies", HttpStatusCode.Unauthorized);
        _http.Route(HttpMethod.Get, "/sync/watched/movies", HttpStatusCode.OK, "[]");
        _http.Json(HttpMethod.Post, "/oauth/token", TokenJson("zweiter"));
        var client = new TraktClient(_http.Client(), Account());

        var movies = await client.GetWatchedMoviesAsync();

        Assert.Empty(movies);
        Assert.Equal("Bearer zweiter", _http.Calls.Last().Headers["Authorization"]);
    }

    [Fact]
    public async Task A_server_error_is_a_http_error_not_an_auth_error()
    {
        _http.Route(HttpMethod.Get, "/sync/watched/movies", HttpStatusCode.InternalServerError);
        var client = new TraktClient(_http.Client(), Account());

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetWatchedMoviesAsync());
    }

    [Fact]
    public async Task The_username_comes_from_the_account_settings()
    {
        _http.Json(HttpMethod.Get, "/users/settings", "{\"user\":{\"username\":\"fredbird\"}}");

        Assert.Equal("fredbird", await new TraktClient(_http.Client(), Account()).GetUsernameAsync());
    }
}

public sealed class TraktSyncTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-" + Guid.NewGuid().ToString("N"));
    private readonly TraktHttp _http = new();
    private readonly FakeVolumes _volumes = new();
    private readonly LibraryDb _db;
    private readonly LibraryService _service;

    public TraktSyncTests()
    {
        Directory.CreateDirectory(_base);
        _db = new LibraryDb(Path.Combine(_base, "library.db"));
        _service = new LibraryService(_db, _volumes, new FakeProbe());
        var drive = Path.Combine(_base, "drive");
        Touch(drive, @"Filme\Inception (2010).mkv");
        Touch(drive, @"Filme\Heat (1995).mkv");
        Touch(drive, @"Serien\Test Show\Season 1\S01E01.mkv");
        Touch(drive, @"Serien\Test Show\Season 1\S01E02.mkv");
        _volumes.Mounted.Add(Vol("TRAKT001", drive));
        _service.AddFolderAsync(drive).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_base, recursive: true);
    }

    private TraktClient Client() => new(_http.Client(), new TraktAccount("id", "secret", 1, "ich", new TraktTokens("zugriff", "erneuerung", DateTime.UtcNow.AddDays(60))));

    private Task<TraktSyncResult> Sync() =>
        new TraktSync(_db).SyncAsync(Client(), _db.GetTitles(), _db.ListVideos(), _db.GetAllPlayback());

    private long VideoOf(string title, int? episode = null) => _db.ListVideos().Single(v => v.TitleName == title && v.Episode == episode).VideoId;

    private bool Watched(long videoId) => _db.GetAllPlayback().TryGetValue(videoId, out var state) && state.Watched;

    private void Remote(string movies = "[]", string shows = "[]")
    {
        _http.Json(HttpMethod.Get, "/sync/watched/movies", movies);
        _http.Json(HttpMethod.Get, "/sync/watched/shows", shows);
    }

    [Fact]
    public async Task Watched_on_trakt_becomes_watched_in_the_library_and_nothing_goes_back()
    {
        Remote(
            movies: "[{\"movie\":{\"title\":\"Inception\",\"year\":2010}},{\"movie\":{\"title\":\"Nicht in der Bibliothek\",\"year\":1999}}]",
            shows: "[{\"show\":{\"title\":\"Test Show\"},\"seasons\":[{\"number\":1,\"episodes\":[{\"number\":2}]}]}]");

        var result = await Sync();

        Assert.True(Watched(VideoOf("Inception")));
        Assert.True(Watched(VideoOf("Test Show", 2)));
        Assert.False(Watched(VideoOf("Test Show", 1)));
        Assert.Equal(2, result.MarkedWatched);
        Assert.Equal(1, result.Unmatched);
        Assert.Equal(0, result.Pushed);
        Assert.DoesNotContain(_http.Calls, c => c.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Watched_in_the_library_but_missing_on_trakt_is_sent_once()
    {
        Remote();
        _http.Json(HttpMethod.Post, "/sync/history", "{\"added\":{\"movies\":1},\"not_found\":{\"movies\":[],\"shows\":[],\"seasons\":[],\"episodes\":[]}}");  // Filmpaket
        _http.Json(HttpMethod.Post, "/sync/history", "{\"added\":{\"episodes\":1},\"not_found\":{\"movies\":[],\"shows\":[],\"seasons\":[],\"episodes\":[]}}"); // Serienpaket
        _db.SavePlayback(VideoOf("Heat"), 0, watched: true);
        _db.SavePlayback(VideoOf("Test Show", 1), 0, watched: true);

        var first = await Sync();
        var second = await Sync();

        Assert.Equal(2, first.Pushed);
        Assert.Equal(0, second.Pushed);
        var posts = _http.Calls.Where(c => c.Method == HttpMethod.Post).ToList();
        Assert.Equal(2, posts.Count); // je Film- und Serienpaket ein Abruf, nur beim ersten Abgleich
        Assert.Contains(posts, p => p.Body.Contains("\"title\":\"Heat\"") && p.Body.Contains("\"year\":1995"));
        Assert.Contains(posts, p => p.Body.Contains("\"title\":\"Test Show\"") && p.Body.Contains("\"number\":1"));
    }

    [Fact]
    public async Task Already_known_to_trakt_is_not_sent_again()
    {
        Remote(movies: "[{\"movie\":{\"title\":\"Heat\",\"year\":1995}}]");
        _db.SavePlayback(VideoOf("Heat"), 0, watched: true);

        var result = await Sync();

        Assert.Equal(0, result.Pushed);
        Assert.DoesNotContain(_http.Calls, c => c.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Titles_trakt_does_not_know_are_reported_and_not_retried_every_time()
    {
        Remote();
        _http.Json(HttpMethod.Post, "/sync/history", "{\"added\":{\"movies\":0,\"episodes\":0},\"not_found\":{\"movies\":[{\"title\":\"Heat\"}],\"shows\":[],\"seasons\":[],\"episodes\":[]}}");
        _db.SavePlayback(VideoOf("Heat"), 0, watched: true);

        var first = await Sync();
        var second = await Sync();

        Assert.Equal(new[] { "Heat" }, first.NotFound);
        Assert.Equal(0, second.Pushed);
        Assert.Single(_http.Calls, c => c.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task A_different_year_is_a_different_film()
    {
        Remote(movies: "[{\"movie\":{\"title\":\"Inception\",\"year\":1999}}]");

        var result = await Sync();

        Assert.False(Watched(VideoOf("Inception")));
        Assert.Equal(1, result.Unmatched);
    }

    [Fact]
    public async Task Clearing_the_markers_lets_the_next_sync_send_everything_again()
    {
        Remote();
        _http.Json(HttpMethod.Post, "/sync/history", "{\"added\":{\"movies\":1},\"not_found\":{}}");
        _db.SavePlayback(VideoOf("Heat"), 0, watched: true);
        await Sync();

        _db.ClearTraktPushed();
        var again = await Sync();

        Assert.Equal(1, again.Pushed);
    }
}
