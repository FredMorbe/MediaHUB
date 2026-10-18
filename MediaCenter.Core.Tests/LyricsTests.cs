using System.Net;
using MediaCenter.Core;
using static MediaCenter.Core.Tests.TestSupport;

namespace MediaCenter.Core.Tests;

public sealed class LyricsParserTests
{
    [Fact]
    public void Time_tags_become_seconds_and_lines_are_sorted()
    {
        var lyrics = LyricsParser.Parse("[00:20.50]zweite\n[00:05.25]erste\n[01:02]dritte\n[100:00.1]sehr spät", "test");

        Assert.Equal(new[] { "erste", "zweite", "dritte", "sehr spät" }, lyrics.Lines.Select(l => l.Text));
        Assert.Equal(new double?[] { 5.25, 20.5, 62, 6000.1 }, lyrics.Lines.Select(l => l.Time));
        Assert.True(lyrics.Synced);
    }

    [Fact]
    public void A_line_with_several_time_tags_appears_at_each_time()
    {
        var lyrics = LyricsParser.Parse("[00:10.00][00:50.00]Refrain\n[00:30.00]Strophe", "test");

        Assert.Equal(new[] { "Refrain", "Strophe", "Refrain" }, lyrics.Lines.Select(l => l.Text));
        Assert.Equal(new double?[] { 10, 30, 50 }, lyrics.Lines.Select(l => l.Time));
    }

    [Fact]
    public void Info_tags_and_lines_without_time_are_ignored_and_word_tags_are_removed()
    {
        var lyrics = LyricsParser.Parse("[ti:Titel]\n[ar:Band]\n[by:jemand]\nKopfzeile ohne Zeit\n[00:01.00]<00:01.00>Hallo <00:01.50>Welt", "test");

        Assert.Equal("Hallo Welt", Assert.Single(lyrics.Lines).Text);
    }

    [Theory]
    [InlineData("+500", 9.5)]
    [InlineData("-500", 10.5)]
    [InlineData("+20000", 0)] // nie vor null
    public void The_offset_tag_shifts_all_times(string offset, double expected)
    {
        var lyrics = LyricsParser.Parse($"[offset:{offset}]\n[00:10.00]Zeile", "test");

        Assert.Equal(expected, lyrics.Lines.Single().Time!.Value, 3);
    }

    [Fact]
    public void Empty_timed_lines_are_kept_as_pauses()
    {
        var lyrics = LyricsParser.Parse("[00:01.00]eins\n[00:05.00]\n[00:09.00]zwei", "test");

        Assert.Equal(new[] { "eins", "", "zwei" }, lyrics.Lines.Select(l => l.Text));
    }

    [Fact]
    public void Text_without_any_time_tag_is_plain_and_trimmed_at_the_edges_only()
    {
        var lyrics = LyricsParser.Parse("\n\nErste Zeile\n\nZweite Zeile   \n\n", "test");

        Assert.False(lyrics.Synced);
        Assert.Equal(new[] { "Erste Zeile", "", "Zweite Zeile" }, lyrics.Lines.Select(l => l.Text));
        Assert.All(lyrics.Lines, l => Assert.Null(l.Time));
    }

    [Fact]
    public void LineAt_finds_the_line_being_sung()
    {
        var lyrics = LyricsParser.Parse("[00:10.00]a\n[00:20.00]b\n[00:30.00]c", "test");

        Assert.Equal(-1, lyrics.LineAt(0));
        Assert.Equal(-1, lyrics.LineAt(9.99));
        Assert.Equal(0, lyrics.LineAt(10));
        Assert.Equal(0, lyrics.LineAt(19.9));
        Assert.Equal(1, lyrics.LineAt(20));
        Assert.Equal(2, lyrics.LineAt(500));
    }

    [Fact]
    public void Plain_text_has_no_current_line()
    {
        Assert.Equal(-1, LyricsParser.FromPlainText("a\nb", "test").LineAt(30));
    }
}

public sealed class LrcLibClientTests
{
    private const string Contact = "https://example.org/mediacenter";

    private readonly FakeHttp _http = new();

    private LrcLibClient Client() => new(_http.Client(), Contact);

    private const string Url = "https://lrclib.net/api/get?artist_name=Pink%20Floyd&track_name=Time&album_name=The%20Dark%20Side%20of%20the%20Moon&duration=413";

    [Fact]
    public async Task Synced_lyrics_are_preferred_over_plain_ones()
    {
        _http.Text(Url, "{\"instrumental\":false,\"plainLyrics\":\"Ticking away\",\"syncedLyrics\":\"[00:10.00]Ticking away\"}", "application/json");

        var text = await Client().FindAsync("Pink Floyd", "Time", "The Dark Side of the Moon", 412.6);

        Assert.Equal("[00:10.00]Ticking away", text);
    }

    [Fact]
    public async Task Plain_lyrics_are_used_when_there_are_no_synced_ones()
    {
        _http.Text(Url, "{\"plainLyrics\":\"Ticking away\",\"syncedLyrics\":null}", "application/json");

        Assert.Equal("Ticking away", await Client().FindAsync("Pink Floyd", "Time", "The Dark Side of the Moon", 413));
    }

    [Fact]
    public async Task An_instrumental_piece_a_missing_entry_and_empty_lyrics_give_null()
    {
        _http.Text(Url, "{\"instrumental\":true,\"plainLyrics\":\"x\"}", "application/json");
        _http.Text("https://lrclib.net/api/get?artist_name=A&track_name=Leer", "{\"plainLyrics\":\"  \",\"syncedLyrics\":\"\"}", "application/json");

        Assert.Null(await Client().FindAsync("Pink Floyd", "Time", "The Dark Side of the Moon", 413));
        Assert.Null(await Client().FindAsync("A", "Leer", null, null));
        Assert.Null(await Client().FindAsync("Niemand", "Nichts", null, null)); // 404
    }

    [Fact]
    public async Task Album_and_duration_are_left_out_when_unknown()
    {
        await Client().FindAsync("A B", "C", null, 0);

        Assert.Equal("https://lrclib.net/api/get?artist_name=A%20B&track_name=C", Assert.Single(_http.Requests));
    }

    [Fact]
    public async Task An_error_status_is_loud()
    {
        _http.Status(Url, HttpStatusCode.ServiceUnavailable);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Client().FindAsync("Pink Floyd", "Time", "The Dark Side of the Moon", 413));

        Assert.Contains("503", ex.Message);
    }

    [Fact]
    public async Task Missing_artist_title_or_contact_is_an_error_and_nothing_is_sent()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Client().FindAsync(" ", "T", null, null));
        await Assert.ThrowsAsync<ArgumentException>(() => Client().FindAsync("A", "", null, null));
        await Assert.ThrowsAsync<ArgumentException>(() => new LrcLibClient(_http.Client(), " ").FindAsync("A", "T", null, null));

        Assert.Empty(_http.Requests);
    }
}

public sealed class LyricsServiceTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-lyrics-" + Guid.NewGuid().ToString("N"));
    private readonly string _music;
    private readonly string _cache;
    private readonly FakeProbe _probe = new();
    private readonly FakeSource _source = new();

    private sealed class FakeSource : ILyricsSource
    {
        public string Provider => "Fake";

        public List<string> Asked { get; } = new();

        public Func<string?> Answer { get; set; } = () => null;

        public Task<string?> FindAsync(string artist, string title, string? album, double? durationSeconds, CancellationToken ct = default)
        {
            Asked.Add($"{artist}|{title}|{album}|{durationSeconds}");
            return Task.FromResult(Answer());
        }
    }

    public LyricsServiceTests()
    {
        _music = Path.Combine(_base, "Musik");
        _cache = Path.Combine(_base, "lyrics");
        Directory.CreateDirectory(_music);
        File.WriteAllText(Path.Combine(_music, "lied.mp3"), "x");
    }

    public void Dispose() => Directory.Delete(_base, recursive: true);

    private string Audio => Path.Combine(_music, "lied.mp3");

    private LyricsService Service(ILyricsSource? online = null) => new(_probe, online, _cache);

    private static ProbeResult WithTags(params (string, string)[] tags) => new(
        100, "mp3", Array.Empty<StreamInfo>(), new Dictionary<string, string>(tags.ToDictionary(t => t.Item1, t => t.Item2), StringComparer.OrdinalIgnoreCase));

    private Task<Lyrics?> Get(LyricsService service, long id = 1) => service.GetAsync(id, Audio, "Band", "Lied", "Platte", 200);

    [Fact]
    public async Task A_lrc_file_next_to_the_track_wins_over_everything()
    {
        File.WriteAllText(Path.Combine(_music, "lied.lrc"), "[00:01.00]aus der Datei");
        _probe.Respond = _ => WithTags(("lyrics", "aus dem Tag"));
        _source.Answer = () => "aus dem Internet";

        var lyrics = await Get(Service(_source));

        Assert.Equal("aus der Datei", lyrics!.Lines.Single().Text);
        Assert.Equal("Datei (.lrc)", lyrics.Source);
        Assert.Empty(_source.Asked);
    }

    [Fact]
    public async Task A_file_that_is_not_utf8_is_read_as_latin1_instead_of_breaking_umlauts()
    {
        File.WriteAllBytes(Path.Combine(_music, "lied.lrc"), System.Text.Encoding.Latin1.GetBytes("[00:01.00]Müde Möwen über Straße"));

        var lyrics = await Get(Service());

        Assert.Equal("Müde Möwen über Straße", lyrics!.Lines.Single().Text);
    }

    [Fact]
    public async Task A_file_with_utf8_bom_is_read_cleanly()
    {
        File.WriteAllBytes(Path.Combine(_music, "lied.lrc"), new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes("[00:01.00]Grüße")).ToArray());

        Assert.Equal("Grüße", (await Get(Service()))!.Lines.Single().Text);
    }

    [Fact]
    public async Task An_embedded_lyrics_tag_is_used_when_there_is_no_file_even_with_a_language_suffix()
    {
        _probe.Respond = _ => WithTags(("title", "Lied"), ("LYRICS-eng", "[00:02.00]eingebettet"));

        var lyrics = await Get(Service(_source));

        Assert.Equal("eingebettet", lyrics!.Lines.Single().Text);
        Assert.Equal("Tag", lyrics.Source);
        Assert.Empty(_source.Asked);
    }

    [Fact]
    public async Task An_empty_file_or_tag_does_not_count_as_lyrics()
    {
        File.WriteAllText(Path.Combine(_music, "lied.lrc"), " \n\n");
        _probe.Respond = _ => WithTags(("lyrics", "   "));

        Assert.Null(await Get(Service()));
    }

    [Fact]
    public async Task Without_an_online_source_nothing_is_cached_and_null_comes_back()
    {
        Assert.Null(await Get(Service()));

        Assert.False(Directory.Exists(_cache));
    }

    [Fact]
    public async Task Online_lyrics_are_fetched_once_cached_and_the_track_data_is_passed_on()
    {
        _source.Answer = () => "[00:03.00]aus dem Netz";
        var service = Service(_source);

        var first = await Get(service);
        var second = await Get(service);

        Assert.Equal("aus dem Netz", first!.Lines.Single().Text);
        Assert.Equal("Fake", first.Source);
        Assert.Equal("aus dem Netz", second!.Lines.Single().Text);
        Assert.Equal(new[] { "Band|Lied|Platte|200" }, _source.Asked);
        Assert.True(File.Exists(Path.Combine(_cache, "1.txt")));
    }

    [Fact]
    public async Task A_miss_is_remembered_until_it_is_two_weeks_old()
    {
        var service = Service(_source);

        Assert.Null(await Get(service));
        Assert.Null(await Get(service));
        Assert.Single(_source.Asked);

        File.SetLastWriteTimeUtc(Path.Combine(_cache, "1.none"), DateTime.UtcNow - LyricsService.NoMatchRetry - TimeSpan.FromHours(1));
        _source.Answer = () => "jetzt da";

        Assert.NotNull(await Get(service));
        Assert.Equal(2, _source.Asked.Count);
        Assert.False(File.Exists(Path.Combine(_cache, "1.none"))); // die Absage ist durch den Treffer ersetzt
    }

    [Fact]
    public async Task A_failing_provider_is_loud_and_leaves_no_marker_behind()
    {
        var service = Service(new ThrowingSource());

        await Assert.ThrowsAsync<HttpRequestException>(() => Get(service));

        Assert.False(File.Exists(Path.Combine(_cache, "1.none")));
    }

    private sealed class ThrowingSource : ILyricsSource
    {
        public string Provider => "Kaputt";

        public Task<string?> FindAsync(string artist, string title, string? album, double? durationSeconds, CancellationToken ct = default) =>
            throw new HttpRequestException("lrclib.net antwortete mit 503.");
    }

    [Fact]
    public async Task Without_an_artist_the_internet_is_not_asked_but_files_still_work()
    {
        _source.Answer = () => "[00:03.00]Text";
        var service = Service(_source);

        Assert.Null(await service.GetAsync(1, Audio, " ", "Lied", null, 200));
        Assert.Empty(_source.Asked);

        File.WriteAllText(Path.Combine(_music, "lied.lrc"), "[00:01.00]aus der Datei");
        Assert.NotNull(await service.GetAsync(1, Audio, "", "Lied", null, 200));
    }

    [Fact]
    public async Task Switching_the_internet_off_keeps_what_is_cached()
    {
        _source.Answer = () => "[00:03.00]Text";
        var service = Service(_source);
        await Get(service);

        service.Online = null;

        Assert.NotNull(await Get(service));
        Assert.Null(await Get(service, id: 2)); // nichts im Zwischenspeicher, nichts gefragt
        Assert.Single(_source.Asked);
    }

    [Fact]
    public async Task Different_tracks_do_not_share_a_cache_entry()
    {
        _source.Answer = () => "[00:03.00]Text";
        var service = Service(_source);

        await Get(service, id: 1);
        await Get(service, id: 2);

        Assert.Equal(2, _source.Asked.Count);
    }
}
