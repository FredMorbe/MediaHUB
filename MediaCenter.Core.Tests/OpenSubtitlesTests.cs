using System.Net;
using System.Text;
using System.Text.Json;
using MediaCenter.Core;

namespace MediaCenter.Core.Tests;

public sealed class OpenSubtitlesTests
{
    /// <summary>Antwortet je Methode und Pfad; merkt sich Anfragen samt Kopfzeilen und Inhalt.</summary>
    private sealed class Server : HttpMessageHandler
    {
        public sealed record Seen(HttpMethod Method, string Url, string? Authorization, string? ApiKey, string? UserAgent, string? Body);

        public List<Seen> Requests { get; } = new();

        public Func<Seen, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var seen = new Seen(request.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Api-Key", out var key) ? key.Single() : null, request.Headers.UserAgent.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
            Requests.Add(seen);
            return Respond(seen);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string SearchResult = """
        {"total_count":3,"data":[
          {"id":"1","type":"subtitle","attributes":{"language":"de","download_count":50,"release":"Film.2010.WEB","moviehash_match":false,"hearing_impaired":false,
            "feature_details":{"title":"Inception","year":2010},"files":[{"file_id":111,"file_name":"a.srt"}]}},
          {"id":"2","type":"subtitle","attributes":{"language":"de","download_count":5,"release":"Film.2010.BluRay","moviehash_match":true,"hearing_impaired":true,
            "feature_details":{"title":"Inception","year":2010},"files":[{"file_id":222,"file_name":"b.srt"}]}},
          {"id":"3","type":"subtitle","attributes":{"language":"de","download_count":900,"release":"KI","ai_translated":true,"files":[{"file_id":333}]}},
          {"id":"4","type":"subtitle","attributes":{"language":"de","files":[]}}
        ]}
        """;

    private static (OpenSubtitlesClient Client, Server Server) Create()
    {
        var server = new Server();
        return (new OpenSubtitlesClient(new HttpClient(server), "GEHEIMER-KEY", "Mediacenter/1.0"), server);
    }

    [Fact]
    public async Task Search_sends_a_sorted_lowercase_query_with_the_key_and_ranks_hash_matches_first()
    {
        var (client, server) = Create();
        server.Respond = _ => Json(SearchResult);

        var hits = await client.SearchAsync(new SubtitleQuery("Inception", 2010, null, null, "de", "8e245d9679d31e12"));

        var request = Assert.Single(server.Requests);
        Assert.Equal("https://api.opensubtitles.com/api/v1/subtitles?languages=de&moviehash=8e245d9679d31e12&query=inception&type=movie&year=2010", request.Url);
        Assert.Equal("GEHEIMER-KEY", request.ApiKey);
        Assert.Equal("Mediacenter/1.0", request.UserAgent);
        Assert.Null(request.Authorization);
        Assert.Equal(new long[] { 222, 111, 333 }, hits.Select(h => h.FileId)); // Hash-Treffer, dann beliebt, Maschinenübersetzung zuletzt
        Assert.True(hits[0].HashMatch);
        Assert.True(hits[0].HearingImpaired);
        Assert.True(hits[2].Machine);
        Assert.Equal("Film.2010.BluRay", hits[0].Release);
        Assert.Equal(2010, hits[0].Year);
    }

    [Fact]
    public async Task Search_for_an_episode_uses_season_and_episode_and_no_year()
    {
        var (client, server) = Create();
        server.Respond = _ => Json("""{"data":[]}""");

        var hits = await client.SearchAsync(new SubtitleQuery("Dark", 2017, 2, 5, "en", null));

        Assert.Empty(hits);
        Assert.Equal("https://api.opensubtitles.com/api/v1/subtitles?episode_number=5&languages=en&query=dark&season_number=2&type=episode", server.Requests.Single().Url);
    }

    [Fact]
    public async Task Titles_with_special_characters_are_escaped()
    {
        var (client, server) = Create();
        server.Respond = _ => Json("""{"data":[]}""");

        await client.SearchAsync(new SubtitleQuery("Tom & Jerry: Der Film", null, null, null, "de", null));

        Assert.Contains("query=tom%20%26%20jerry%3A%20der%20film", server.Requests.Single().Url);
    }

    [Fact]
    public async Task Login_then_download_uses_the_token_and_fetches_the_returned_link()
    {
        var (client, server) = Create();
        server.Respond = seen => seen.Url switch
        {
            "https://api.opensubtitles.com/api/v1/login" => Json("""{"token":"JWT-TOKEN","base_url":"vip-api.opensubtitles.com","user":{"allowed_downloads":20}}"""),
            "https://vip-api.opensubtitles.com/api/v1/download" => Json("""{"link":"https://dl.opensubtitles.com/x/a.srt","file_name":"a.srt","remaining":19}"""),
            "https://dl.opensubtitles.com/x/a.srt" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("1\n00:00:01,000 --> 00:00:02,000\nHallo\n") },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };

        await client.LoginAsync("nutzer", "passwort");
        var subtitle = await client.DownloadAsync(222);

        Assert.True(client.LoggedIn);
        Assert.Equal("a.srt", subtitle.FileName);
        Assert.Equal(19, subtitle.Remaining);
        Assert.Contains("Hallo", Encoding.UTF8.GetString(subtitle.Content));
        var download = server.Requests.Single(r => r.Url.EndsWith("/download"));
        Assert.Equal("Bearer JWT-TOKEN", download.Authorization);
        Assert.Contains("\"file_id\":222", download.Body);
        var login = server.Requests[0];
        Assert.Contains("\"username\":\"nutzer\"", login.Body);
        // Der Link kommt von einem Auslieferungsdienst: kein Schlüssel und kein Token dorthin.
        var fetch = server.Requests.Single(r => r.Url.StartsWith("https://dl."));
        Assert.Null(fetch.Authorization);
        Assert.Null(fetch.ApiKey);
    }

    [Fact]
    public async Task A_foreign_base_url_from_the_login_is_ignored_so_the_token_stays_with_opensubtitles()
    {
        var (client, server) = Create();
        server.Respond = seen => seen.Url.EndsWith("/login")
            ? Json("""{"token":"T","base_url":"boese.example.org"}""")
            : Json("""{"link":"https://dl.opensubtitles.com/a.srt","file_name":"a.srt"}""");

        await client.LoginAsync("u", "p");
        try { await client.DownloadAsync(1); } catch (OpenSubtitlesException) { /* der Link selbst ist hier nicht Thema */ }

        Assert.All(server.Requests.Where(r => r.Authorization is not null), r => Assert.StartsWith("https://api.opensubtitles.com/", r.Url));
    }

    [Fact]
    public async Task Download_without_login_is_refused_before_any_request()
    {
        var (client, server) = Create();

        var ex = await Assert.ThrowsAsync<OpenSubtitlesException>(() => client.DownloadAsync(1));

        Assert.Contains("Anmeldung", ex.Message);
        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "abgelehnt")]
    [InlineData(HttpStatusCode.NotAcceptable, "Tageslimit")]
    [InlineData(HttpStatusCode.TooManyRequests, "Zu viele")]
    public async Task Errors_are_explained_in_german_without_leaking_the_key_or_password(HttpStatusCode status, string expected)
    {
        var (client, server) = Create();
        server.Respond = _ => Json("""{"message":"Fehlertext vom Server"}""", status);

        var ex = await Assert.ThrowsAsync<OpenSubtitlesException>(() => client.LoginAsync("nutzer", "GEHEIMES-PASSWORT"));

        Assert.Contains(expected, ex.Message);
        Assert.Contains("Fehlertext vom Server", ex.Message);
        Assert.DoesNotContain("GEHEIMES-PASSWORT", ex.Message);
        Assert.DoesNotContain("GEHEIMER-KEY", ex.Message);
    }

    [Fact]
    public async Task Missing_key_or_credentials_fail_loudly()
    {
        var server = new Server();
        var noKey = new OpenSubtitlesClient(new HttpClient(server), "", "x/1");

        await Assert.ThrowsAsync<OpenSubtitlesException>(() => noKey.SearchAsync(new SubtitleQuery("A", null, null, null, "de", null)));
        var (client, _) = Create();
        await Assert.ThrowsAsync<OpenSubtitlesException>(() => client.LoginAsync("", "x"));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task An_oversized_download_is_refused()
    {
        var (client, server) = Create();
        server.Respond = seen => seen.Url switch
        {
            var u when u.EndsWith("/login") => Json("""{"token":"T"}"""),
            var u when u.EndsWith("/download") => Json("""{"link":"https://dl.opensubtitles.com/big.srt"}"""),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[(int)SubtitleStore.MaxBytes + 1]) },
        };
        await client.LoginAsync("u", "p");

        await Assert.ThrowsAsync<OpenSubtitlesException>(() => client.DownloadAsync(1));
    }

    [Fact]
    public void The_movie_hash_is_size_plus_the_sums_of_the_first_and_last_64_KiB()
    {
        var path = Path.Combine(Path.GetTempPath(), "mc-hash-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            var data = new byte[200_000];
            for (var i = 0; i < data.Length; i++) data[i] = (byte)(i * 31 + 7);
            File.WriteAllBytes(path, data);

            ulong expected = (ulong)data.Length;
            for (var i = 0; i < 65536; i += 8) expected += BitConverter.ToUInt64(data, i);
            for (var i = data.Length - 65536; i < data.Length; i += 8) expected += BitConverter.ToUInt64(data, i);

            Assert.Equal(expected.ToString("x16"), OpenSubtitlesClient.MovieHash(path));
            Assert.Equal(16, OpenSubtitlesClient.MovieHash(path).Length);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Files_below_128_KiB_have_no_hash()
    {
        var path = Path.Combine(Path.GetTempPath(), "mc-hash-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllBytes(path, new byte[1000]);
            Assert.Throws<InvalidDataException>(() => OpenSubtitlesClient.MovieHash(path));
        }
        finally { File.Delete(path); }
    }
}
