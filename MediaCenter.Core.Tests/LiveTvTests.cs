using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml;
using MediaCenter.Core;
using Microsoft.Data.Sqlite;

namespace MediaCenter.Core.Tests;

public sealed class M3uParserTests
{
    [Fact]
    public void Reads_name_group_guide_id_logo_and_number_and_a_comma_inside_quotes_does_not_split_the_name()
    {
        var playlist = M3uParser.Parse("""
            #EXTM3U url-tvg="https://epg.example/guide.xml.gz,https://other.example/g.xml"
            #EXTINF:-1 tvg-id="DasErste.de" tvg-name="Das Erste" tvg-logo="https://logo.example/a,b.png" tvg-chno="1" group-title="Vollprogramm, Öffentlich",Das Erste HD
            http://stream.example/live/1.ts
            """);

        var channel = Assert.Single(playlist.Channels);
        Assert.Equal(("Das Erste HD", "http://stream.example/live/1.ts", "Vollprogramm, Öffentlich", "DasErste.de", "https://logo.example/a,b.png", 1),
            (channel.Name, channel.Url, channel.Group, channel.TvgId, channel.LogoUrl, channel.Number));
        Assert.Equal("https://epg.example/guide.xml.gz", playlist.EpgUrl);
        Assert.Equal(0, playlist.Skipped);
    }

    [Fact]
    public void A_group_line_gives_the_group_and_a_plain_url_list_is_a_playlist_too()
    {
        var playlist = M3uParser.Parse("\uFEFF#EXTM3U\r\n#EXTINF:-1,Eins\r\n#EXTGRP:Nachrichten\r\nhttps://a.example/1.m3u8\r\n\r\nrtmp://b.example/live/zwei\r\n");

        Assert.Equal(new[] { "Eins", "b.example/live/zwei" }, playlist.Channels.Select(c => c.Name));
        Assert.Equal("Nachrichten", playlist.Channels[0].Group);
        Assert.Null(playlist.Channels[1].Group);
        Assert.Null(playlist.EpgUrl);
    }

    [Fact]
    public void An_entry_without_a_title_falls_back_to_tvg_name_and_then_to_the_address()
    {
        var playlist = M3uParser.Parse("#EXTINF:-1 tvg-name=\"Arte\",\nhttp://a.example/1\n#EXTINF:-1,\nhttp://b.example/path/2\n");

        Assert.Equal(new[] { "Arte", "b.example/path/2" }, playlist.Channels.Select(c => c.Name));
    }

    [Fact]
    public void Addresses_that_mpv_would_not_play_from_the_net_are_dropped_and_counted()
    {
        var playlist = M3uParser.Parse("""
            #EXTM3U
            #EXTINF:-1,Lokal
            file:///C:/Windows/win.ini
            #EXTINF:-1,Skript
            javascript:alert(1)
            #EXTINF:-1,Liste
            edl://%5
            #EXTINF:-1 tvg-logo="file:///C:/x.png",Gut
            udp://239.0.0.1:1234
            """);

        var channel = Assert.Single(playlist.Channels);
        Assert.Equal("Gut", channel.Name);
        Assert.Null(channel.LogoUrl); // ein Logo darf nicht auf eine lokale Datei zeigen
        Assert.Equal(3, playlist.Skipped);
    }

    [Fact]
    public void Options_for_vlc_and_comments_are_ignored_and_the_name_does_not_leak_to_the_next_entry()
    {
        var playlist = M3uParser.Parse("#EXTINF:-1,Eins\n#EXTVLCOPT:http-user-agent=X\n# Kommentar\nhttp://a.example/1\nhttp://b.example/2\n");

        Assert.Equal(new[] { "Eins", "b.example/2" }, playlist.Channels.Select(c => c.Name));
    }
}

public sealed class XmltvParserTests
{
    private static readonly DateTime From = new(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc);

    private const string Guide = """
        <?xml version="1.0" encoding="UTF-8"?>
        <tv>
          <channel id="DasErste.de"><display-name lang="de">Das Erste</display-name><display-name>ARD</display-name></channel>
          <channel id="Arte.fr"><display-name>Arte</display-name></channel>
          <programme start="20261024180000 +0200" stop="20261024184500 +0200" channel="DasErste.de">
            <title lang="en">News</title><title lang="de">Tagesschau</title><desc lang="de">Die Nachrichten.</desc>
          </programme>
          <programme start="20261024164500 +0000" stop="20261024170000 +0000" channel="DasErste.de"><title>Wetter</title></programme>
          <programme start="20261023100000 +0000" stop="20261023110000 +0000" channel="DasErste.de"><title>Gestern</title></programme>
          <programme start="20261024100000 +0000" stop="20261024110000 +0000" channel="Arte.fr"><title>Nicht gefragt</title></programme>
          <programme start="kaputt" stop="20261024110000 +0000" channel="DasErste.de"><title>Zeit kaputt</title></programme>
          <programme start="20261024120000 +0000" stop="20261024130000 +0000" channel="DasErste.de"></programme>
        </tv>
        """;

    private static Stream Open(string xml) => XmltvParser.Open(Encoding.UTF8.GetBytes(xml));

    [Fact]
    public void Times_with_offset_without_offset_with_z_and_short_forms_become_utc_and_nonsense_gives_null()
    {
        Assert.Equal(new DateTime(2026, 10, 24, 16, 0, 0, DateTimeKind.Utc), XmltvParser.ParseTime("20261024180000 +0200"));
        Assert.Equal(new DateTime(2026, 10, 24, 18, 0, 0, DateTimeKind.Utc), XmltvParser.ParseTime("20261024180000"));
        Assert.Equal(new DateTime(2026, 10, 24, 18, 0, 0, DateTimeKind.Utc), XmltvParser.ParseTime("20261024180000Z"));
        Assert.Equal(new DateTime(2026, 10, 24, 23, 30, 0, DateTimeKind.Utc), XmltvParser.ParseTime("20261024180000 -0530"));
        Assert.Equal(new DateTime(2026, 10, 24, 17, 0, 0, DateTimeKind.Utc), XmltvParser.ParseTime("202610241800 +0100"));
        Assert.Null(XmltvParser.ParseTime("kaputt"));
        Assert.Null(XmltvParser.ParseTime("20261024180000 +99"));
        Assert.Null(XmltvParser.ParseTime(""));
        Assert.Null(XmltvParser.ParseTime(null));
    }

    [Fact]
    public void Channels_come_with_all_their_display_names()
    {
        using var stream = Open(Guide);

        var channels = XmltvParser.ReadChannels(stream);

        Assert.Equal(new[] { "DasErste.de", "Arte.fr" }, channels.Select(c => c.Id));
        Assert.Equal(new[] { "Das Erste", "ARD" }, channels[0].DisplayNames);
    }

    [Fact]
    public void Only_wanted_channels_and_programmes_in_the_window_are_kept_and_the_german_text_wins()
    {
        using var stream = Open(Guide);

        var result = XmltvParser.ReadProgrammes(stream, id => id == "DasErste.de", From, To);

        Assert.Equal(new[] { "Tagesschau", "Wetter" }, result.Items.Select(p => p.Title));
        var news = result.Items[0];
        Assert.Equal((new DateTime(2026, 10, 24, 16, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 24, 16, 45, 0, DateTimeKind.Utc), "Die Nachrichten."),
            (news.StartUtc, news.StopUtc, news.Description));
        Assert.Equal(2, result.Skipped); // unlesbare Zeit und fehlender Titel
    }

    [Fact]
    public void A_gzipped_guide_is_read_too()
    {
        var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(Encoding.UTF8.GetBytes(Guide));

        using var stream = XmltvParser.Open(packed.ToArray());

        Assert.Equal(2, XmltvParser.ReadChannels(stream).Count);
    }

    [Fact]
    public void A_document_type_declaration_is_refused()
    {
        using var stream = Open("<?xml version=\"1.0\"?><!DOCTYPE tv [<!ENTITY x \"y\">]><tv><channel id=\"a\"/></tv>");

        Assert.Throws<XmlException>(() => XmltvParser.ReadChannels(stream));
    }
}

public sealed class LiveTvDatabaseTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-live-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryDb _db;

    public LiveTvDatabaseTests()
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

    private static M3uChannel Channel(string name, string? group = null, string? tvg = null, string? url = null) =>
        new(name, url ?? $"http://stream.example/{name.Replace(' ', '_')}", group, tvg, null, null);

    private long Source(string location = "http://list.example/a.m3u", params M3uChannel[] channels)
    {
        var id = _db.AddLiveSource("Liste", location, null);
        _db.ReplaceLiveChannels(id, channels);
        return id;
    }

    [Fact]
    public void Channels_keep_the_order_of_the_list_and_duplicates_count_once()
    {
        var id = Source(channels: new[] { Channel("Zwei"), Channel("Eins"), Channel("Zwei") });

        Assert.Equal(new[] { "Zwei", "Eins" }, _db.ListLiveChannels(new LiveChannelFilter(SourceId: id)).Select(c => c.Name));
        Assert.Single(_db.ListLiveSources());
        Assert.Equal(2, _db.GetLiveSource(id)!.ChannelCount);
    }

    [Fact]
    public void A_reload_keeps_favorites_adds_new_channels_removes_gone_ones_and_follows_a_rename_by_address()
    {
        var id = Source(channels: new[]
        {
            Channel("Eins", url: "http://s.example/1"), Channel("Zwei", url: "http://s.example/2"), Channel("Alt", url: "http://s.example/3"),
        });
        var eins = _db.ListLiveChannels(new LiveChannelFilter()).Single(c => c.Name == "Eins");
        var zwei = _db.ListLiveChannels(new LiveChannelFilter()).Single(c => c.Name == "Zwei");
        _db.SetLiveFavorite(eins.Id, true);
        _db.SetLiveFavorite(zwei.Id, true);

        var result = _db.ReplaceLiveChannels(id, new[]
        {
            Channel("Neu", url: "http://s.example/9"), Channel("Zwei HD", url: "http://s.example/2"), Channel("Eins", url: "http://s.example/1", group: "News"),
        });

        Assert.Equal((3, 1, 1), (result.Total, result.Added, result.Removed));
        var channels = _db.ListLiveChannels(new LiveChannelFilter());
        Assert.Equal(new[] { "Neu", "Zwei HD", "Eins" }, channels.Select(c => c.Name));
        Assert.Equal(new[] { eins.Id, zwei.Id }, channels.Where(c => c.Favorite).Select(c => c.Id).Order()); // umbenanntes „Zwei HD“ ist derselbe Sender
        Assert.Equal("News", channels.Single(c => c.Name == "Eins").Group);
    }

    [Fact]
    public void Filters_by_group_favorites_and_a_forgiving_text_and_pages()
    {
        var id = Source(channels: new[]
        {
            Channel("Das Erste HD", "Vollprogramm"), Channel("ZDF", "Vollprogramm"), Channel("n-tv", "Nachrichten"), Channel("Köln 1"), Channel("Arte"),
        });
        var all = _db.ListLiveChannels(new LiveChannelFilter());
        _db.SetLiveFavorite(all.Single(c => c.Name == "ZDF").Id, true);

        Assert.Equal(new[] { "Das Erste HD", "ZDF" }, _db.ListLiveChannels(new LiveChannelFilter(Group: "vollprogramm")).Select(c => c.Name));
        Assert.Equal(new[] { "Köln 1", "Arte" }, _db.ListLiveChannels(new LiveChannelFilter(Group: "")).Select(c => c.Name)); // ohne Gruppe
        Assert.Equal(new[] { "ZDF" }, _db.ListLiveChannels(new LiveChannelFilter(FavoritesOnly: true)).Select(c => c.Name));
        Assert.Equal(new[] { "Das Erste HD" }, _db.ListLiveChannels(new LiveChannelFilter(Text: "das  erste")).Select(c => c.Name));
        Assert.Equal(new[] { "n-tv" }, _db.ListLiveChannels(new LiveChannelFilter(Text: "NTV")).Select(c => c.Name));
        Assert.Equal(1, _db.CountLiveChannels(new LiveChannelFilter(Text: "zdf", SourceId: id)));
        Assert.Equal(new[] { "ZDF", "n-tv" }, _db.ListLiveChannels(new LiveChannelFilter(), limit: 2, offset: 1).Select(c => c.Name));
        Assert.Equal(new[] { ("Vollprogramm", 2), ("Nachrichten", 1), ("", 2) }, _db.ListLiveGroups().Select(g => (g.Name, g.Count)));
    }

    [Fact]
    public void The_guide_gives_now_and_next_and_nothing_in_a_gap()
    {
        var id = Source(channels: new[] { Channel("Eins", tvg: "eins") });
        var t = new DateTime(2026, 10, 24, 18, 0, 0, DateTimeKind.Utc);
        _db.ReplaceLiveGuide(id, new[]
        {
            new XmltvProgramme("eins", t.AddHours(-1), t, "Vorher", null),
            new XmltvProgramme("eins", t, t.AddMinutes(30), "Jetzt", "Beschreibung"),
            new XmltvProgramme("eins", t.AddMinutes(30), t.AddHours(1), "Danach", null),
            new XmltvProgramme("eins", t.AddHours(3), t.AddHours(4), "Spät", null),
        });

        var during = _db.GetLiveNowNext(id, "eins", t.AddMinutes(10));
        Assert.Equal(("Jetzt", "Beschreibung", "Danach"), (during.Now!.Title, during.Now.Description, during.Next!.Title));

        var gap = _db.GetLiveNowNext(id, "eins", t.AddHours(2));
        Assert.Null(gap.Now);
        Assert.Equal("Spät", gap.Next!.Title);

        Assert.Null(_db.GetLiveNowNext(id, "eins", t.AddHours(9)).Next);
        Assert.Equal(new[] { "Jetzt", "Danach" }, _db.ListLiveProgrammes(id, "eins", t.AddMinutes(5), t.AddMinutes(40)).Select(p => p.Title));
        Assert.Equal(1, _db.PruneLiveGuide(t)); // nur „Vorher“ ist zu Ende; was läuft oder kommt, bleibt
    }

    [Fact]
    public void Removing_a_list_removes_its_channels_and_guide_and_the_same_address_cannot_be_added_twice()
    {
        var id = Source("http://list.example/a.m3u", Channel("Eins"));
        _db.ReplaceLiveGuide(id, new[] { new XmltvProgramme("eins", DateTime.UtcNow, DateTime.UtcNow.AddHours(1), "X", null) });

        Assert.Throws<InvalidOperationException>(() => _db.AddLiveSource("Nochmal", "HTTP://LIST.EXAMPLE/A.M3U", null));
        _db.RemoveLiveSource(id);

        Assert.Empty(_db.ListLiveChannels(new LiveChannelFilter()));
        Assert.Null(_db.GetLiveNowNext(id, "eins", DateTime.UtcNow).Now);
        Assert.Empty(_db.ListLiveSources());
    }
}

public sealed class LiveTvServiceTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-livesvc-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHttp _http = new();
    private readonly LibraryDb _db;
    private readonly LiveTvService _service;

    private static readonly DateTime Now = new(2026, 10, 24, 18, 0, 0, DateTimeKind.Utc);

    public LiveTvServiceTests()
    {
        Directory.CreateDirectory(_base);
        _db = new LibraryDb(Path.Combine(_base, "library.db"));
        _service = new LiveTvService(_db, _http.Client());
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_base, recursive: true);
    }

    private const string Playlist = """
        #EXTM3U url-tvg="http://epg.example/guide.xml"
        #EXTINF:-1 tvg-id="DasErste.de" group-title="Vollprogramm",DE | Das Erste HD
        http://stream.example/1
        #EXTINF:-1 group-title="Vollprogramm",ZDF HD
        http://stream.example/2
        #EXTINF:-1,Ohne Führer
        http://stream.example/3
        """;

    private static string GuideXml(DateTime now) =>
        "<tv><channel id=\"DasErste.de\"><display-name>Das Erste</display-name></channel><channel id=\"zdf.de\"><display-name>ZDF</display-name></channel>"
        + $"<programme start=\"{now.AddMinutes(-30):yyyyMMddHHmmss} +0000\" stop=\"{now.AddMinutes(30):yyyyMMddHHmmss} +0000\" channel=\"DasErste.de\"><title>Tagesschau</title></programme>"
        + $"<programme start=\"{now.AddMinutes(30):yyyyMMddHHmmss} +0000\" stop=\"{now.AddMinutes(60):yyyyMMddHHmmss} +0000\" channel=\"DASERSTE.DE\"><title>Wetter</title></programme>"
        + $"<programme start=\"{now.AddMinutes(-30):yyyyMMddHHmmss} +0000\" stop=\"{now.AddMinutes(30):yyyyMMddHHmmss} +0000\" channel=\"zdf.de\"><title>heute</title></programme>"
        + $"<programme start=\"{now.AddMinutes(-30):yyyyMMddHHmmss} +0000\" stop=\"{now.AddMinutes(30):yyyyMMddHHmmss} +0000\" channel=\"unbekannt\"><title>Fremd</title></programme></tv>";

    [Fact]
    public async Task Adding_a_list_stores_its_channels_and_takes_the_guide_address_from_the_list()
    {
        _http.Text("http://list.example/tv.m3u", Playlist);

        var (source, refresh) = await _service.AddSourceAsync(null, "http://list.example/tv.m3u", null);

        Assert.Equal(("list.example", "http://epg.example/guide.xml", 3), (source.Name, source.EpgLocation, source.ChannelCount));
        Assert.Equal((3, 3, 0), (refresh.Total, refresh.Added, refresh.Removed));
    }

    [Fact]
    public async Task A_list_without_playable_channels_a_missing_file_and_a_duplicate_are_loud_and_store_nothing()
    {
        _http.Text("http://list.example/leer.m3u", "#EXTM3U\n#EXTINF:-1,Nur lokal\nfile:///C:/x.mp4\n");
        _http.Text("http://list.example/tv.m3u", Playlist);

        await Assert.ThrowsAsync<InvalidDataException>(() => _service.AddSourceAsync(null, "http://list.example/leer.m3u", null));
        await Assert.ThrowsAsync<FileNotFoundException>(() => _service.AddSourceAsync(null, Path.Combine(_base, "gibt-es-nicht.m3u"), null));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.AddSourceAsync(null, "http://list.example/tv.m3u", "ftp://epg.example/x.xml"));
        Assert.Empty(_db.ListLiveSources());

        await _service.AddSourceAsync("Meine", "http://list.example/tv.m3u", null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.AddSourceAsync("Nochmal", "http://list.example/tv.m3u", null));
    }

    [Fact]
    public async Task A_list_from_a_file_and_a_gzipped_list_work()
    {
        var file = Path.Combine(_base, "tv.m3u");
        await File.WriteAllTextAsync(file, Playlist);
        var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(Encoding.UTF8.GetBytes(Playlist));
        _http.Bytes("http://list.example/tv.m3u.gz", packed.ToArray());

        var (fromFile, _) = await _service.AddSourceAsync(null, file, null);
        var (fromWeb, _) = await _service.AddSourceAsync(null, "http://list.example/tv.m3u.gz", null);

        Assert.Equal("tv", fromFile.Name);
        Assert.Equal(3, fromWeb.ChannelCount);
    }

    [Fact]
    public async Task A_reload_keeps_favorites_and_an_empty_answer_does_not_empty_the_list()
    {
        _http.Text("http://list.example/tv.m3u", Playlist);
        var (source, _) = await _service.AddSourceAsync(null, "http://list.example/tv.m3u", null);
        var zdf = _db.ListLiveChannels(new LiveChannelFilter()).Single(c => c.Name == "ZDF HD");
        _db.SetLiveFavorite(zdf.Id, true);

        _http.Text("http://list.example/tv.m3u", Playlist.Replace("Ohne Führer", "Neu"));
        var refresh = await _service.RefreshSourceAsync(source.Id);

        Assert.Equal((3, 0, 0), (refresh.Total, refresh.Added, refresh.Removed));
        Assert.True(_db.GetLiveChannel(zdf.Id)!.Favorite);

        _http.Text("http://list.example/tv.m3u", "");
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.RefreshSourceAsync(source.Id));
        Assert.Equal(3, _db.GetLiveSource(source.Id)!.ChannelCount);
    }

    [Fact]
    public async Task The_guide_is_matched_by_id_and_by_name_without_quality_tags_and_country_prefix()
    {
        _http.Text("http://list.example/tv.m3u", Playlist);
        _http.Text("http://epg.example/guide.xml", GuideXml(Now));
        var (source, _) = await _service.AddSourceAsync(null, "http://list.example/tv.m3u", null);

        var result = await _service.RefreshGuideAsync(source.Id, Now);

        Assert.Equal((3, 2, 3), (result.Channels, result.WithGuide, result.Programmes)); // „Fremd“ gehört keinem Sender; „Ohne Führer“ hat keinen Eintrag
        var channels = _db.ListLiveChannels(new LiveChannelFilter()).ToDictionary(c => c.Name);
        Assert.Equal("DasErste.de", channels["DE | Das Erste HD"].EpgId); // über die tvg-id
        Assert.Equal("zdf.de", channels["ZDF HD"].EpgId); // über den Namen
        Assert.Null(channels["Ohne Führer"].EpgId);

        var erste = _db.GetLiveNowNext(source.Id, "DasErste.de", Now);
        Assert.Equal(("Tagesschau", "Wetter"), (erste.Now!.Title, erste.Next!.Title)); // die Sendung mit anderer Schreibweise der Kennung zählt mit
    }

    [Fact]
    public async Task A_gzipped_guide_from_a_file_works_and_a_list_without_guide_says_so()
    {
        _http.Text("http://list.example/tv.m3u", Playlist.Replace("#EXTM3U url-tvg=\"http://epg.example/guide.xml\"", "#EXTM3U"));
        var (source, _) = await _service.AddSourceAsync(null, "http://list.example/tv.m3u", null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RefreshGuideAsync(source.Id, Now));

        var file = Path.Combine(_base, "guide.xml.gz");
        using (var output = File.Create(file))
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest)) gzip.Write(Encoding.UTF8.GetBytes(GuideXml(Now)));
        _db.SetLiveGuideLocation(source.Id, file);

        var result = await _service.RefreshGuideAsync(source.Id, Now);

        Assert.Equal(2, result.WithGuide);
    }

    [Fact]
    public async Task Error_messages_do_not_contain_the_address_with_its_credentials()
    {
        _http.Status("http://list.example/get.php?username=anna&password=geheim123", HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => _service.AddSourceAsync(null, "http://list.example/get.php?username=anna&password=geheim123", null));

        Assert.DoesNotContain("geheim123", ex.Message);
        Assert.DoesNotContain("anna", ex.Message);
    }

    [Fact]
    public void Channel_names_are_compared_without_quality_tags_and_country_prefix()
    {
        Assert.Equal(LiveTvService.NameKey("Das Erste"), LiveTvService.NameKey("DE | Das Erste HD"));
        Assert.Equal(LiveTvService.NameKey("ZDF"), LiveTvService.NameKey("zdf FHD"));
        Assert.NotEqual(LiveTvService.NameKey("ZDF"), LiveTvService.NameKey("ZDF neo"));
        Assert.Equal("de", LiveTvService.NameKey("DE")); // ein Name, der nur aus dem Kürzel besteht, bleibt
    }
}
