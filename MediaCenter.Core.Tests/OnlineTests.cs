using System.Net;
using System.Text;
using MediaCenter.Core;
using static MediaCenter.Core.Tests.TestSupport;

namespace MediaCenter.Core.Tests;

/// <summary>HTTP ohne Netz: antwortet je URL aus einer Tabelle, zählt die Aufrufe.</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = new();

    public List<string> Requests { get; } = new();

    public void Text(string url, string body, string contentType = "text/plain") =>
        Routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, contentType) };

    public void Bytes(string url, byte[] body) =>
        Routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    public void Status(string url, HttpStatusCode code) => Routes[url] = () => new HttpResponseMessage(code);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.AbsoluteUri;
        Requests.Add(url);
        return Task.FromResult(Routes.TryGetValue(url, out var make) ? make() : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    public HttpClient Client() => new(this);
}

public sealed class PodcastFeedParserTests
{
    private const string Feed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
          <channel>
            <title>Der Test-Podcast</title>
            <itunes:author>Erika Muster</itunes:author>
            <description>&lt;p&gt;Ein &lt;b&gt;Podcast&lt;/b&gt; &amp;amp; mehr.&lt;/p&gt;</description>
            <itunes:image href="https://example.org/cover.jpg"/>
            <item>
              <title>Folge 2</title>
              <guid>ep-2</guid>
              <pubDate>Tue, 14 Oct 2025 06:00:00 +0000</pubDate>
              <enclosure url="https://example.org/2.mp3" length="1" type="audio/mpeg"/>
              <itunes:duration>1:02:03</itunes:duration>
              <description>Zweite Folge</description>
            </item>
            <item>
              <title>Folge 1</title>
              <enclosure url="https://example.org/1.mp3" type="audio/mpeg"/>
              <itunes:duration>45:12</itunes:duration>
            </item>
            <item><title>Ohne Audio</title></item>
            <item><title>Lokale Datei</title><enclosure url="file:///C:/geheim.mp3" type="audio/mpeg"/></item>
          </channel>
        </rss>
        """;

    [Fact]
    public void Reads_channel_and_episodes_and_skips_items_without_a_web_audio_url()
    {
        var feed = PodcastFeedParser.Parse(Feed);

        Assert.Equal("Der Test-Podcast", feed.Title);
        Assert.Equal("Erika Muster", feed.Author);
        Assert.Equal("Ein Podcast & mehr.", feed.Description);
        Assert.Equal("https://example.org/cover.jpg", feed.ImageUrl);
        Assert.Equal(new[] { "Folge 2", "Folge 1" }, feed.Episodes.Select(e => e.Title));
        Assert.Equal("ep-2", feed.Episodes[0].Guid);
        Assert.Equal(new DateTime(2025, 10, 14, 6, 0, 0, DateTimeKind.Utc), feed.Episodes[0].PublishedUtc);
        Assert.Equal(3723, feed.Episodes[0].DurationSeconds);
        Assert.Equal("https://example.org/1.mp3", feed.Episodes[1].Guid); // ohne guid dient die Adresse
        Assert.Equal(2712, feed.Episodes[1].DurationSeconds);
    }

    [Theory]
    [InlineData("3600", 3600.0)]
    [InlineData("45:12", 2712.0)]
    [InlineData("1:02:03", 3723.0)]
    [InlineData("abc", null)]
    [InlineData("1:2:3:4", null)]
    public void Durations_come_in_three_formats(string text, double? seconds) =>
        Assert.Equal(seconds, PodcastFeedParser.ParseDuration(text));

    [Fact]
    public void A_feed_with_a_doctype_is_rejected_instead_of_reading_files()
    {
        var xxe = """
            <?xml version="1.0"?>
            <!DOCTYPE rss [ <!ENTITY secret SYSTEM "file:///C:/Windows/win.ini"> ]>
            <rss><channel><title>&secret;</title></channel></rss>
            """;

        var ex = Assert.Throws<InvalidDataException>(() => PodcastFeedParser.Parse(xxe));

        Assert.Contains("kein gültiges XML", ex.Message);
    }

    [Fact]
    public void A_page_that_is_not_a_feed_gives_a_clear_error()
    {
        Assert.Contains("kein Podcast-Feed", Assert.Throws<InvalidDataException>(() => PodcastFeedParser.Parse("<html><body/></html>")).Message);
        Assert.Contains("kein gültiges XML", Assert.Throws<InvalidDataException>(() => PodcastFeedParser.Parse("das ist kein xml")).Message);
    }
}

public sealed class OnlineClientTests
{
    [Fact]
    public void Radio_results_keep_only_playable_web_stations()
    {
        var json = """
            [ { "stationuuid": "u1", "name": " Radio Eins ", "url": "http://a/1", "url_resolved": "https://a/stream", "favicon": "https://a/icon.png",
                "tags": "pop,news", "country": "Germany", "codec": "MP3", "bitrate": 128, "votes": 5 },
              { "stationuuid": "u2", "name": "Nur URL", "url": "https://b/s", "favicon": "javascript:alert(1)" },
              { "stationuuid": "u3", "name": "Kaputt", "url": "rtmp://c/live" },
              { "stationuuid": "u4", "url": "https://d/s" } ]
            """;

        var stations = RadioBrowserClient.Parse(json);

        Assert.Equal(2, stations.Count);
        Assert.Equal(new RadioStation("u1", "Radio Eins", "https://a/stream", "https://a/icon.png", "Germany", "pop,news", "MP3", 128, 5), stations[0]);
        Assert.Equal("https://b/s", stations[1].Url);
        Assert.Null(stations[1].FaviconUrl); // kein Web-Schema
    }

    [Fact]
    public async Task Radio_search_falls_back_to_the_next_server_and_reports_when_none_answers()
    {
        var http = new FakeHttp();
        http.Status("https://all.api.radio-browser.info/json/stations/search?hidebroken=true&order=votes&reverse=true&limit=40&name=jazz", HttpStatusCode.BadGateway);
        http.Text("https://de1.api.radio-browser.info/json/stations/search?hidebroken=true&order=votes&reverse=true&limit=40&name=jazz",
            """[ { "stationuuid": "u", "name": "Jazz FM", "url": "https://j/s" } ]""");
        var client = new RadioBrowserClient(http.Client());

        var found = await client.SearchAsync("jazz");

        Assert.Equal("Jazz FM", found.Single().Name);
        Assert.Equal(2, http.Requests.Count);

        var none = new RadioBrowserClient(new FakeHttp().Client());
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => none.SearchAsync("jazz"));
        Assert.Contains("Kein radio-browser-Server", ex.Message);
    }

    [Fact]
    public async Task Radio_genre_search_sends_the_tag_in_lowercase_because_the_directory_is_case_sensitive()
    {
        var http = new FakeHttp();
        http.Text("https://all.api.radio-browser.info/json/stations/search?hidebroken=true&order=votes&reverse=true&limit=40&tag=hip%20hop",
            """[ { "stationuuid": "u", "name": "Beats", "url": "https://b/s" } ]""");

        var found = await new RadioBrowserClient(http.Client()).SearchAsync(null, "Hip Hop");

        Assert.Equal("Beats", found.Single().Name);
    }

    [Fact]
    public async Task Podcast_search_returns_only_results_with_a_web_feed()
    {
        var http = new FakeHttp();
        http.Text("https://itunes.apple.com/search?media=podcast&limit=25&term=radio%20lab", """
            { "resultCount": 3, "results": [
              { "collectionName": "Radiolab", "artistName": "WNYC", "feedUrl": "https://f/rss", "artworkUrl600": "https://f/600.jpg" },
              { "collectionName": "Ohne Feed" },
              { "collectionName": "Lokal", "feedUrl": "file:///x" } ] }
            """);

        var results = await new PodcastSearchClient(http.Client()).SearchAsync("radio lab");

        var hit = Assert.Single(results);
        Assert.Equal(new PodcastSearchResult("Radiolab", "WNYC", "https://f/rss", "https://f/600.jpg"), hit);
    }

    [Fact]
    public async Task Downloads_are_limited_in_size_and_to_web_addresses()
    {
        var http = new FakeHttp();
        http.Bytes("https://x/big", new byte[2_000]);

        await Assert.ThrowsAsync<InvalidDataException>(() => HttpFetch.GetBytesAsync(http.Client(), new Uri("https://x/big"), 1_000, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => HttpFetch.RequireWebUri("file:///C:/x"));
        Assert.Throws<ArgumentException>(() => HttpFetch.RequireWebUri("ftp://x/y"));
        Assert.Equal("https://x/y", HttpFetch.RequireWebUri("https://x/y").ToString());
    }
}

public sealed class PodcastAndRadioServiceTests : IDisposable
{
    private const string FeedUrl = "https://example.org/feed.xml";
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-pod-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHttp _http = new();
    private readonly FakeImageTool _tool = new();
    private readonly LibraryDb _db;
    private readonly PodcastService _podcasts;
    private readonly RadioService _radio;

    public PodcastAndRadioServiceTests()
    {
        Directory.CreateDirectory(_base);
        _db = new LibraryDb(Path.Combine(_base, "library.db"));
        var images = new RemoteImages(_http.Client(), _tool, Path.Combine(_base, "cache"));
        _podcasts = new PodcastService(_db, new PodcastFeedClient(_http.Client()), images);
        _radio = new RadioService(_db, images);
        _http.Bytes("https://example.org/cover.jpg", new byte[] { 1, 2, 3 });
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_base, recursive: true);
    }

    private static string Rss(params (string Guid, string Title)[] items) =>
        $"""
        <rss xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd"><channel><title>Podcast</title>
          <itunes:image href="https://example.org/cover.jpg"/>
          {string.Concat(items.Select(i => $"<item><guid>{i.Guid}</guid><title>{i.Title}</title><enclosure url=\"https://example.org/{i.Guid}.mp3\"/></item>"))}
        </channel></rss>
        """;

    [Fact]
    public async Task Subscribing_stores_podcast_episodes_and_cover()
    {
        _http.Text(FeedUrl, Rss(("a", "Folge A"), ("b", "Folge B")));

        var result = await _podcasts.SubscribeAsync(FeedUrl);

        Assert.Equal(2, result.Episodes);
        Assert.Null(result.Warning);
        var podcast = Assert.Single(_db.ListPodcasts());
        Assert.NotNull(podcast.ImageFile);
        Assert.True(File.Exists(Path.Combine(_base, "cache", podcast.ImageFile!)));
        Assert.Equal(2, _db.ListEpisodes(podcast.Id).Count);
    }

    [Fact]
    public async Task Refresh_adds_new_episodes_and_keeps_listening_progress()
    {
        _http.Text(FeedUrl, Rss(("a", "Folge A")));
        var id = (await _podcasts.SubscribeAsync(FeedUrl)).PodcastId;
        var episode = _db.ListEpisodes(id).Single();
        _db.SaveEpisodeProgress(episode.Id, 321, played: false);

        _http.Text(FeedUrl, Rss(("a", "Folge A (überarbeitet)"), ("b", "Folge B")));
        var refresh = Assert.Single(await _podcasts.RefreshAllAsync());

        Assert.Null(refresh.Error);
        Assert.Equal(1, refresh.NewEpisodes);
        var episodes = _db.ListEpisodes(id);
        Assert.Contains(episodes, e => e.Id == episode.Id && e.Title == "Folge A (überarbeitet)");
        Assert.Equal(321, _db.GetEpisodeProgress(id)[episode.Id].PositionSeconds);
    }

    [Fact]
    public async Task One_broken_feed_is_reported_and_does_not_stop_the_others()
    {
        _http.Text(FeedUrl, Rss(("a", "A")));
        _http.Text("https://example.org/second.xml", Rss(("c", "C")));
        await _podcasts.SubscribeAsync(FeedUrl);
        await _podcasts.SubscribeAsync("https://example.org/second.xml");
        _http.Status(FeedUrl, HttpStatusCode.InternalServerError);
        _http.Text("https://example.org/second.xml", Rss(("c", "C"), ("d", "D")));

        var results = await _podcasts.RefreshAllAsync();

        Assert.Contains(results, r => r.Podcast.FeedUrl == FeedUrl && r.Error!.Contains("500"));
        Assert.Contains(results, r => r.Podcast.FeedUrl.EndsWith("second.xml") && r.Error is null && r.NewEpisodes == 1);
    }

    [Fact]
    public async Task A_missing_cover_is_a_warning_not_a_failed_subscription()
    {
        _http.Text(FeedUrl, Rss(("a", "A")));
        _http.Status("https://example.org/cover.jpg", HttpStatusCode.NotFound);

        var result = await _podcasts.SubscribeAsync(FeedUrl);

        Assert.Contains("Bild konnte nicht geladen werden", result.Warning);
        Assert.Single(_db.ListPodcasts());
    }

    [Fact]
    public async Task A_non_feed_address_fails_loudly_and_nothing_is_stored()
    {
        _http.Text(FeedUrl, "<html>keine Folgen</html>");

        await Assert.ThrowsAsync<InvalidDataException>(() => _podcasts.SubscribeAsync(FeedUrl));
        await Assert.ThrowsAsync<ArgumentException>(() => _podcasts.SubscribeAsync("file:///C:/x.xml"));
        Assert.Empty(_db.ListPodcasts());
    }

    [Fact]
    public async Task Unsubscribing_removes_episodes_and_progress()
    {
        _http.Text(FeedUrl, Rss(("a", "A")));
        var id = (await _podcasts.SubscribeAsync(FeedUrl)).PodcastId;
        _db.SaveEpisodeProgress(_db.ListEpisodes(id).Single().Id, 10, played: true);

        _db.DeletePodcast(id);

        Assert.Empty(_db.ListPodcasts());
        Assert.Empty(_db.ListEpisodes(id));
        Assert.Empty(_db.GetEpisodeProgress(id));
    }

    [Fact]
    public async Task Saving_the_same_station_twice_updates_it_and_stores_the_logo()
    {
        _http.Bytes("https://r/icon.png", new byte[] { 9 });
        var station = new RadioStation("uuid-1", "Radio Eins", "https://r/stream", "https://r/icon.png", "DE", "pop", "MP3", 128);

        var (first, warning) = await _radio.SaveAsync(station);
        var (second, _) = await _radio.SaveAsync(station with { Name = "Radio Eins HD" });

        Assert.Null(warning);
        Assert.Equal(first, second);
        var saved = Assert.Single(_db.ListStations());
        Assert.Equal("Radio Eins HD", saved.Station.Name);
        Assert.True(File.Exists(Path.Combine(_base, "cache", saved.FaviconFile!)));
    }

    [Fact]
    public async Task A_custom_station_without_an_id_is_matched_by_its_address()
    {
        var (a, _) = await _radio.SaveAsync(new RadioStation(null, "Mein Sender", "https://s/stream", null, null, null, null, null));
        var (b, _) = await _radio.SaveAsync(new RadioStation(null, "Mein Sender neu", "https://s/stream", null, null, null, null, null));

        Assert.Equal(a, b);
        Assert.Equal("Mein Sender neu", _db.ListStations().Single().Station.Name);
    }
}
