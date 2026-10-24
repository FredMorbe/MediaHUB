using System.Net;
using MediaCenter.Core;

namespace MediaCenter.Core.Tests;

public sealed class ScheduleTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHttp _http = new();
    private readonly LibraryDb _db;
    private readonly ScheduleService _service;

    public ScheduleTests()
    {
        Directory.CreateDirectory(_base);
        _db = new LibraryDb(Path.Combine(_base, "library.db"));
        _service = new ScheduleService(_db, new TvMazeClient(_http.Client(), TimeSpan.Zero));
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_base, recursive: true);
    }

    private const string ShowUrl = "https://api.tvmaze.com/shows/100?embed%5B%5D=episodes";

    private void RouteShow(string name = "Dark", string body = "")
    {
        _http.Text("https://api.tvmaze.com/search/shows?q=" + Uri.EscapeDataString(name),
            "[{\"score\":1,\"show\":{\"id\":100,\"name\":\"" + name + "\",\"premiered\":\"2017-12-01\",\"image\":null,\"summary\":null,\"rating\":{\"average\":null}}}]", "application/json");
        _http.Text(ShowUrl, body.Length > 0 ? body : Episodes(), "application/json");
    }

    private static string Episodes(string status = "Running") => """
        {"id":100,"name":"Dark","status":"__STATUS__","_embedded":{"episodes":[
          {"season":1,"number":1,"name":"Alt","airdate":"2026-09-01","airstamp":"2026-09-01T18:00:00+00:00"},
          {"season":1,"number":2,"name":"Gestern","airdate":"2026-10-24","airstamp":"2026-10-24T18:00:00+00:00"},
          {"season":1,"number":3,"name":"Morgen","airdate":"2026-10-26","airstamp":"2026-10-26T20:15:00+02:00"},
          {"season":1,"number":4,"name":"Ohne Uhrzeit","airdate":"2026-11-02","airstamp":null},
          {"season":1,"number":5,"name":"Ohne Termin","airdate":null,"airstamp":null},
          {"season":1,"number":null,"name":"Special","airdate":"2026-10-30","airstamp":"2026-10-30T18:00:00+00:00"},
          {"season":1,"number":6,"name":"Zu weit weg","airdate":"2027-06-01","airstamp":"2027-06-01T18:00:00+00:00"}]}}
        """.Replace("__STATUS__", status);

    private long AddSeries(string name = "Dark") => _db.CreateTitle(TitleKind.Series, name, 2017);

    private IReadOnlyList<TitleRow> Series() => _db.GetTitles().ToList();

    [Fact]
    public void Air_dates_use_the_time_zone_of_the_stamp_or_fall_back_to_the_day()
    {
        Assert.Equal((new DateTime(2026, 10, 26, 18, 15, 0, DateTimeKind.Utc), true), TvMazeClient.ParseAir("2026-10-26T20:15:00+02:00", null));
        Assert.Equal((new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc), false), TvMazeClient.ParseAir(null, "2026-11-02"));
        Assert.Null(TvMazeClient.ParseAir(null, null));
        Assert.Null(TvMazeClient.ParseAir("", "kein Datum"));
    }

    [Fact]
    public async Task Schedule_lists_numbered_episodes_with_a_date_and_the_show_status()
    {
        RouteShow();

        var schedule = await new TvMazeClient(_http.Client(), TimeSpan.Zero).FindScheduleAsync("Dark", 2017);

        Assert.Equal(100, schedule!.TvMazeId);
        Assert.Equal("Running", schedule.Status);
        Assert.True(schedule.IsActive);
        Assert.Equal(new[] { 1, 2, 3, 4, 6 }, schedule.Episodes.Select(e => e.Number).ToArray()); // ohne Termin und Sonderfolgen fehlen
        Assert.False(schedule.Episodes.Single(e => e.Number == 4).HasTime);
        Assert.False(new ShowSchedule(1, "X", "Ended", Array.Empty<ScheduledEpisode>()).IsActive);
    }

    [Fact]
    public async Task A_known_tvmaze_id_skips_the_search()
    {
        RouteShow();

        await new TvMazeClient(_http.Client(), TimeSpan.Zero).FindScheduleAsync("Dark", 2017, knownId: 100);

        Assert.DoesNotContain(_http.Requests, r => r.Contains("/search/shows"));
    }

    [Fact]
    public async Task Refresh_stores_the_schedule_and_the_calendar_splits_upcoming_from_missing()
    {
        RouteShow();
        var title = AddSeries();

        var result = await _service.RefreshAsync(Series(), Now);

        Assert.Equal(1, result.Checked);
        Assert.Null(result.Aborted);
        var calendar = _service.Calendar(_db.ListVideos(), Now, TimeSpan.FromHours(2));
        Assert.Equal(new[] { 3, 4 }, calendar.Upcoming.Select(r => r.Episode).ToArray());        // Folge 6 liegt jenseits von 60 Tagen
        Assert.Equal(new[] { 2 }, calendar.Missing.Select(r => r.Episode).ToArray());             // Folge 1 liegt 54 Tage zurück: nicht mehr „neu“
        Assert.Equal(title, calendar.Upcoming[0].TitleId);
    }

    [Fact]
    public void Episodes_in_the_library_are_not_missing_and_old_ones_are_not_new()
    {
        var rows = new[]
        {
            new ScheduleRow(1, "Dark", 1, 1, "Alt", Now.AddDays(-54), true, false),
            new ScheduleRow(1, "Dark", 1, 2, "Da", Now.AddDays(-2), true, false),
            new ScheduleRow(1, "Dark", 1, 3, "Fehlt", Now.AddDays(-1), true, false),
        };
        var videos = new[] { Video(1, 1, 2) };

        var calendar = EpisodeCalendar.Build(rows, videos, Now, TimeSpan.Zero);

        Assert.Equal(new[] { 3 }, calendar.Missing.Select(r => r.Episode).ToArray());
        Assert.Empty(calendar.Upcoming);
    }

    [Fact]
    public void A_dated_episode_counts_as_upcoming_until_its_day_is_over_in_local_time()
    {
        var today = new ScheduleRow(1, "Dark", 1, 1, null, new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc), HasTime: false, false);
        var yesterday = today with { Episode = 2, AirUtc = today.AirUtc.AddDays(-1) };

        var calendar = EpisodeCalendar.Build(new[] { today, yesterday }, Array.Empty<VideoRow>(), Now, TimeSpan.FromHours(2));

        Assert.Equal(new[] { 1 }, calendar.Upcoming.Select(r => r.Episode).ToArray());
        Assert.Equal(new[] { 2 }, calendar.Missing.Select(r => r.Episode).ToArray());
    }

    [Fact]
    public async Task Fresh_episodes_are_announced_once()
    {
        RouteShow();
        AddSeries();
        await _service.RefreshAsync(Series(), Now);

        var first = _service.TakeNewEpisodes(_db.ListVideos(), Now, TimeSpan.Zero);
        var second = _service.TakeNewEpisodes(_db.ListVideos(), Now, TimeSpan.Zero);

        Assert.Equal(new[] { 2 }, first.Select(r => r.Episode).ToArray()); // Folge 1 ist älter als vier Tage
        Assert.Empty(second);
    }

    [Fact]
    public async Task Notified_flags_survive_a_refresh()
    {
        RouteShow();
        AddSeries();
        await _service.RefreshAsync(Series(), Now);
        _service.TakeNewEpisodes(_db.ListVideos(), Now, TimeSpan.Zero);

        await _service.RefreshAsync(Series(), Now.AddDays(1)); // ist fällig: 12 Stunden vorbei

        Assert.Empty(_service.TakeNewEpisodes(_db.ListVideos(), Now.AddDays(1), TimeSpan.Zero));
    }

    [Fact]
    public async Task A_fresh_schedule_is_not_fetched_again_but_a_stale_one_is()
    {
        RouteShow();
        AddSeries();
        await _service.RefreshAsync(Series(), Now);
        var requests = _http.Requests.Count;

        await _service.RefreshAsync(Series(), Now.AddHours(11));
        Assert.Equal(requests, _http.Requests.Count);

        await _service.RefreshAsync(Series(), Now.AddHours(13));
        Assert.True(_http.Requests.Count > requests);
    }

    [Fact]
    public async Task An_ended_show_is_checked_rarely_and_an_unknown_one_after_two_weeks()
    {
        RouteShow(body: Episodes("Ended"));
        AddSeries();
        await _service.RefreshAsync(Series(), Now);
        var requests = _http.Requests.Count;

        await _service.RefreshAsync(Series(), Now.AddDays(20));
        Assert.Equal(requests, _http.Requests.Count);
        await _service.RefreshAsync(Series(), Now.AddDays(31));
        Assert.True(_http.Requests.Count > requests);

        Assert.Equal(TimeSpan.FromDays(14), EpisodeCalendar.RefreshInterval(new ScheduleCheck(1, null, null, Now)));
        Assert.Equal(TimeSpan.Zero, EpisodeCalendar.RefreshInterval(null));
    }

    [Fact]
    public async Task A_show_tvmaze_does_not_know_is_remembered_and_old_episodes_vanish()
    {
        RouteShow();
        var title = AddSeries();
        await _service.RefreshAsync(Series(), Now);
        Assert.NotEmpty(_db.ListSchedule(Now.AddYears(-1), Now.AddYears(2)));

        _http.Text("https://api.tvmaze.com/search/shows?q=Dark", "[]", "application/json");
        // erst nach Ablauf der Frist (die Serie läuft: 12 h) fragt der Dienst erneut; die Kennung ist bekannt, also ohne Suche
        _http.Status(ShowUrl, HttpStatusCode.NotFound);
        var result = await _service.RefreshAsync(Series(), Now.AddDays(1));

        Assert.NotNull(result.Aborted); // 404 für eine bekannte Kennung ist ein Abruffehler: der Lauf endet, der alte Plan bleibt
        Assert.NotEmpty(_db.ListSchedule(Now.AddYears(-1), Now.AddYears(2)));
        Assert.Equal(100, _db.GetScheduleCheck(title)!.TvMazeId);
    }

    [Fact]
    public async Task A_series_without_a_match_gets_a_check_entry_so_the_search_is_not_repeated_at_every_start()
    {
        _http.Text("https://api.tvmaze.com/search/shows?q=Unbekannt", "[]", "application/json");
        var title = AddSeries("Unbekannt");

        await _service.RefreshAsync(Series(), Now);
        var requests = _http.Requests.Count;
        await _service.RefreshAsync(Series(), Now.AddDays(1));

        Assert.Null(_db.GetScheduleCheck(title)!.TvMazeId);
        Assert.Equal(requests, _http.Requests.Count);
    }

    [Fact]
    public async Task When_tvmaze_is_down_the_run_stops_and_nothing_is_marked_as_checked()
    {
        _http.Status("https://api.tvmaze.com/search/shows?q=Dark", HttpStatusCode.InternalServerError);
        var title = AddSeries();

        var result = await _service.RefreshAsync(Series(), Now);

        Assert.NotNull(result.Aborted);
        Assert.Null(_db.GetScheduleCheck(title));
    }

    [Fact]
    public async Task A_too_big_answer_is_reported_and_not_retried_at_every_start()
    {
        _http.Text("https://api.tvmaze.com/search/shows?q=Dark",
            "[{\"score\":1,\"show\":{\"id\":100,\"name\":\"Dark\",\"premiered\":\"2017-12-01\",\"image\":null,\"summary\":null,\"rating\":{\"average\":null}}}]", "application/json");
        _http.Text(ShowUrl, "{\"id\":100,\"status\":\"Running\",\"_embedded\":{\"episodes\":[" + new string(' ', 13_000_000) + "]}}", "application/json");
        var title = AddSeries();

        var result = await _service.RefreshAsync(Series(), Now);

        Assert.Single(result.Failures);
        Assert.Null(result.Aborted);
        Assert.NotNull(_db.GetScheduleCheck(title));
    }

    [Fact]
    public async Task Movies_are_not_looked_up()
    {
        _db.CreateTitle(TitleKind.Movie, "Dark", 2017);

        var result = await _service.RefreshAsync(Series(), Now);

        Assert.Equal(0, result.Checked);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Removing_the_series_removes_its_schedule()
    {
        RouteShow();
        var title = AddSeries();
        await _service.RefreshAsync(Series(), Now);

        _db.RemoveTitle(title);

        Assert.Empty(_db.ListSchedule(Now.AddYears(-1), Now.AddYears(2)));
        Assert.Null(_db.GetScheduleCheck(title));
    }

    private static VideoRow Video(long titleId, int season, int episode) =>
        new(1, 1, 1, "x.mkv", false, titleId, TitleKind.Series, "Dark", null, season, episode, false, "Dark", VideoKind.Episode, 60, null, null, null, HdrFormat.None);
}
