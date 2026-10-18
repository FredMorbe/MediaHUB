using System.Net;
using MediaCenter.Core;

namespace MediaCenter.Core.Tests;

public sealed class MusicBrainzAlbumClientTests
{
    private const string Contact = "https://example.org/mediacenter";

    private readonly FakeHttp _http = new();

    private MusicBrainzAlbumClient Client() => new(_http.Client(), Contact, TimeSpan.Zero);

    private static string Group(string id, string title, params string[] artists) =>
        "{\"id\":\"" + id + "\",\"title\":\"" + title + "\",\"artist-credit\":[" + string.Join(",", artists.Select(a =>
            "{\"name\":\"" + a + "\",\"artist\":{\"name\":\"" + a + "\"}}")) + "]}";

    private static string Result(params string[] groups) => "{\"count\":" + groups.Length + ",\"release-groups\":[" + string.Join(",", groups) + "]}";

    private static string SearchUrl(string album, string artist) =>
        "https://musicbrainz.org/ws/2/release-group/?fmt=json&limit=10&query=" + Uri.EscapeDataString($"releasegroup:\"{album}\" AND artist:\"{artist}\"");

    private static string CoverUrl(string id) => $"https://coverartarchive.org/release-group/{id}/front-500";

    private const string Id1 = "f5093c06-23e3-404f-aeaa-40f72885ee3a";
    private const string Id2 = "11111111-2222-4333-8444-555555555555";
    private const string Id3 = "22222222-3333-4444-8555-666666666666";
    private const string Id4 = "33333333-4444-4555-8666-777777777777";

    private void Search(string album, string artist, string json) => _http.Text(SearchUrl(album, artist), json, "application/json");

    private void HasCover(string id) => _http.Bytes(CoverUrl(id), new byte[] { 1 });

    [Fact]
    public async Task An_exact_match_with_a_cover_gives_the_cover_archive_address()
    {
        Search("The Dark Side of the Moon", "Pink Floyd", Result(Group(Id1, "The Dark Side of the Moon", "Pink Floyd")));
        HasCover(Id1);

        var cover = await Client().FindAsync("Pink Floyd", "The Dark Side of the Moon");

        Assert.Equal("The Dark Side of the Moon", cover!.ShowName);
        Assert.Equal(CoverUrl(Id1), cover.ImageUrl);
    }

    [Fact]
    public async Task A_match_without_a_cover_is_skipped_for_the_next_one()
    {
        Search("Discovery", "Daft Punk", Result(Group(Id1, "Discovery", "Daft Punk"), Group(Id2, "Discovery", "Daft Punk")));
        HasCover(Id2); // Id1 hat kein Bild (404)

        var cover = await Client().FindAsync("Daft Punk", "Discovery");

        Assert.Equal(CoverUrl(Id2), cover!.ImageUrl);
        Assert.Equal(new[] { SearchUrl("Discovery", "Daft Punk"), CoverUrl(Id1), CoverUrl(Id2) }, _http.Requests);
    }

    [Fact]
    public async Task No_match_with_a_cover_gives_null_and_only_a_few_candidates_are_checked()
    {
        Search("Discovery", "Daft Punk", Result(
            Group(Id1, "Discovery", "Daft Punk"), Group(Id2, "Discovery", "Daft Punk"), Group(Id3, "Discovery", "Daft Punk"), Group(Id4, "Discovery", "Daft Punk")));

        Assert.Null(await Client().FindAsync("Daft Punk", "Discovery"));

        Assert.Equal(1 + 3, _http.Requests.Count); // die Suche und höchstens drei Prüfungen
    }

    [Theory]
    [InlineData("Discovery (Remastered)")]
    [InlineData("Discovery [Deluxe Edition]")]
    [InlineData("Discovery - EP")]
    [InlineData("DISCOVERY")]
    public async Task Additions_at_the_provider_and_case_do_not_matter(string providerTitle)
    {
        Search("Discovery", "Daft Punk", Result(Group(Id1, providerTitle, "Daft Punk")));
        HasCover(Id1);

        Assert.NotNull(await Client().FindAsync("Daft Punk", "Discovery"));
    }

    [Fact]
    public async Task Another_artist_or_another_album_is_not_taken_and_no_cover_is_requested()
    {
        Search("Discovery", "Daft Punk", Result(
            Group(Id1, "Discovery", "Discovery Kids"), Group(Id2, "Discovery Channel", "Daft Punk"), Group(Id3, "Homework", "Daft Punk")));

        Assert.Null(await Client().FindAsync("Daft Punk", "Discovery"));

        Assert.Single(_http.Requests); // nur die Suche
    }

    [Fact]
    public async Task Several_credited_artists_match_by_one_name_or_by_the_joined_name()
    {
        Search("Collab", "Alpha", Result(Group(Id1, "Collab", "Alpha", "Beta")));
        Search("Collab", "Alpha & Beta", "{\"release-groups\":[{\"id\":\"" + Id2 + "\",\"title\":\"Collab\",\"artist-credit\":[" +
            "{\"name\":\"Alpha\",\"joinphrase\":\" & \"},{\"name\":\"Beta\"}]}]}");
        HasCover(Id1);
        HasCover(Id2);

        Assert.Equal(CoverUrl(Id1), (await Client().FindAsync("Alpha", "Collab"))!.ImageUrl);
        Assert.Equal(CoverUrl(Id2), (await Client().FindAsync("Alpha & Beta", "Collab"))!.ImageUrl);
    }

    [Fact]
    public async Task Entries_without_a_valid_id_are_ignored()
    {
        Search("Discovery", "Daft Punk", Result(Group("../../etc", "Discovery", "Daft Punk"), Group("kein-guid", "Discovery", "Daft Punk")));

        Assert.Null(await Client().FindAsync("Daft Punk", "Discovery"));

        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task No_results_gives_null_and_an_error_status_is_loud_for_search_and_for_the_cover_check()
    {
        Search("B", "A", "{\"count\":0,\"release-groups\":[]}");
        _http.Status(SearchUrl("Kaputt", "A"), HttpStatusCode.ServiceUnavailable);
        Search("Cover", "A", Result(Group(Id1, "Cover", "A")));
        _http.Status(CoverUrl(Id1), HttpStatusCode.InternalServerError);

        Assert.Null(await Client().FindAsync("A", "B"));
        var search = await Assert.ThrowsAsync<HttpRequestException>(() => Client().FindAsync("A", "Kaputt"));
        var cover = await Assert.ThrowsAsync<HttpRequestException>(() => Client().FindAsync("A", "Cover"));

        Assert.Contains("503", search.Message);
        Assert.Contains("500", cover.Message);
    }

    [Fact]
    public async Task Quotes_and_backslashes_in_names_cannot_change_the_search_query()
    {
        // Die Suche ist nicht eingerichtet (404 = nichts gefunden); es zählt, was gesendet wurde.
        Assert.Null(await Client().FindAsync("A\\\" OR artist:\"B", "He said \"hi\""));

        var query = Uri.UnescapeDataString(Assert.Single(_http.Requests));
        Assert.DoesNotContain("\\", query);
        Assert.Equal(4, query.Count(c => c == '"')); // nur die vier Anführungszeichen der Abfrage selbst
    }

    [Fact]
    public async Task Missing_artist_album_or_contact_is_an_error_and_nothing_is_sent()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Client().FindAsync(" ", "B"));
        await Assert.ThrowsAsync<ArgumentException>(() => Client().FindAsync("A", ""));
        await Assert.ThrowsAsync<ArgumentException>(() => new MusicBrainzAlbumClient(_http.Client(), " ", TimeSpan.Zero).FindAsync("A", "B"));

        Assert.Empty(_http.Requests);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> UserAgents { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            UserAgents.Add(request.Headers.UserAgent.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"release-groups\":[]}") });
        }
    }

    [Fact]
    public async Task Every_request_names_the_program_and_a_contact()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MediaCenter/0.5.0");

        await new MusicBrainzAlbumClient(http, Contact, TimeSpan.Zero).FindAsync("A", "B");

        Assert.Equal($"MediaCenter/0.5.0 ({Contact})", Assert.Single(handler.UserAgents));
    }
}
