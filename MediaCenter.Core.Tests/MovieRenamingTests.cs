using MediaCenter.Core;
using Microsoft.Data.Sqlite;
using static MediaCenter.Core.Tests.TestSupport;

namespace MediaCenter.Core.Tests;

public sealed class MovieNamingTests
{
    [Theory]
    [InlineData("The Matrix Reloaded", 2003, 1920, 1080, "The_Matrix_Reloaded_(2003)_[1080p]")]
    [InlineData("The Matrix Reloaded", 2003, 1920, 800, "The_Matrix_Reloaded_(2003)_[1080p]")] // Breitbild: 1920×800 zählt als 1080p
    [InlineData("Dune", 2021, 3840, 2160, "Dune_(2021)_[2160p]")]
    [InlineData("Dune", 2021, 1280, 720, "Dune_(2021)_[720p]")]
    [InlineData("Alt", 1960, 720, 576, "Alt_(1960)_[576p]")]
    [InlineData("Alt", 1960, 640, 360, "Alt_(1960)_[480p]")]
    [InlineData("Unbekannt", 2000, null, null, "Unbekannt_(2000)")]
    [InlineData("Mission: Impossible", 1996, 1920, 1080, "Mission_-_Impossible_(1996)_[1080p]")]
    [InlineData("  Viele   Leerzeichen ", 2000, null, null, "Viele_Leerzeichen_(2000)")]
    public void Base_names_follow_title_year_resolution(string title, int year, int? width, int? height, string expected)
    {
        Assert.Equal(expected, MovieNaming.BaseName(title, year, width, height));
    }

    [Theory]
    [InlineData("Mission: Impossible", "Mission - Impossible")]
    [InlineData("Harry Potter and the Deathly Hallows: Part 1", "Harry Potter and the Deathly Hallows - Part 1")]
    [InlineData("What?", "What")]
    [InlineData("AC/DC: Let There Be Rock", "AC DC - Let There Be Rock")]
    [InlineData("  Viele   Leerzeichen  ", "Viele Leerzeichen")]
    [InlineData("Titel mit Punkt am Ende...", "Titel mit Punkt am Ende")]
    [InlineData("<Film> \"zwei\" | *drei*", "Film zwei drei")]
    public void Sanitize_removes_what_windows_does_not_allow(string title, string expected)
    {
        Assert.Equal(expected, MovieNaming.Sanitize(title));
    }

    [Fact]
    public void A_title_that_sanitizes_to_nothing_gives_no_name()
    {
        Assert.Null(MovieNaming.BaseName("???", 2000, 1920, 1080));
    }

    [Fact]
    public void Very_long_titles_are_cut()
    {
        Assert.True(MovieNaming.Sanitize(new string('a', 400)).Length <= 150);
    }
}

public sealed class MovieRenamingTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-rename-" + Guid.NewGuid().ToString("N"));
    private readonly string _drive;
    private readonly FakeVolumes _volumes = new();
    private readonly FakeProbe _probe = new();
    private readonly FakeInfo _info = new();
    private LibraryDb _db;
    private readonly LibraryService _library;

    private sealed class FakeInfo : IMovieInfoSource
    {
        public Dictionary<string, MovieInfo> Known { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Asked { get; } = new();

        public Task<MovieInfo?> LookupAsync(string title, int? year, CancellationToken ct = default)
        {
            Asked.Add($"{title}|{year}");
            return Task.FromResult(Known.TryGetValue(title, out var info) ? info : null);
        }
    }

    public MovieRenamingTests()
    {
        _drive = Path.Combine(_base, "drive");
        Directory.CreateDirectory(_drive);
        _db = new LibraryDb(Path.Combine(_base, "library.db"));
        _volumes.Mounted.Add(Vol("AAAA0001", _drive));
        _library = new LibraryService(_db, _volumes, _probe);
        _info.Known["the matrix reloaded"] = new MovieInfo("The Matrix Reloaded", 2003, "https://m.media-amazon.com/x.jpg");
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_base, recursive: true);
    }

    private const string Scene = "the.matrix.reloaded.2003.1080p.h264.hdr.blabla";
    private const string Proper = "The_Matrix_Reloaded_(2003)_[1080p]";

    private string Abs(string rel) => Path.Combine(_drive, rel);

    private async Task Scan() => await _library.AddFolderAsync(_drive);

    private VolumeStatus Status() => _library.RefreshVolumes();

    private Task<IReadOnlyList<RenameProposal>> Plan() => new MovieRenamePlanner(_info).PlanAsync(_db.ListVideos(), Status());

    private VideoRow Video(string fileStart) => _db.ListVideos().Single(v => Path.GetFileName(v.RelPath).StartsWith(fileStart, StringComparison.OrdinalIgnoreCase));

    private IReadOnlyList<RenameOutcome> Apply(IReadOnlyList<RenameProposal> proposals) => new MovieRenamer(_db).Apply(proposals, Status());

    [Fact]
    public async Task A_scene_name_gets_the_proper_title_year_and_resolution()
    {
        Touch(_drive, $@"Filme\{Scene}.mkv");
        await Scan();

        var proposal = Assert.Single(await Plan());

        Assert.Equal(RenameStatus.Ready, proposal.Status);
        Assert.Equal($@"Filme\{Proper}.mkv", proposal.NewRelPath);
        Assert.Equal("The Matrix Reloaded", proposal.Title);
        Assert.Equal(2003, proposal.Year);
        Assert.Equal(new[] { "the matrix reloaded|2003" }, _info.Asked);
    }

    [Fact]
    public async Task Applying_renames_the_file_and_its_companions_and_keeps_all_data_of_the_video()
    {
        Touch(_drive, $@"Filme\{Scene}.mkv");
        Touch(_drive, $@"Filme\{Scene}.de.srt");
        Touch(_drive, $@"Filme\{Scene}.nfo");
        Touch(_drive, $@"Filme\{Scene}-poster.jpg");
        Touch(_drive, $@"Filme\Subs\{Scene}.en.srt");
        Touch(_drive, @"Filme\ganz.anderer.film.srt");
        await Scan();
        var video = Video(Scene);
        var titleId = video.TitleId!.Value;
        _db.SavePlayback(video.VideoId, 600, watched: false);
        _db.SaveSubtitleAdjustment(video.VideoId, new SubtitleAdjustment(1.5));
        _db.SaveColorSettings(video.VideoId, new ColorSettings(Brightness: 20));
        _db.AddGenre(titleId, "Action");

        var outcome = Assert.Single(Apply(await Plan()));

        Assert.True(outcome.Success, outcome.Error);
        Assert.True(File.Exists(Abs($@"Filme\{Proper}.mkv")));
        Assert.True(File.Exists(Abs($@"Filme\{Proper}.de.srt")));
        Assert.True(File.Exists(Abs($@"Filme\{Proper}.nfo")));
        Assert.True(File.Exists(Abs($@"Filme\{Proper}-poster.jpg")));
        Assert.True(File.Exists(Abs($@"Filme\Subs\{Proper}.en.srt")));
        Assert.True(File.Exists(Abs(@"Filme\ganz.anderer.film.srt"))); // fremde Datei bleibt
        Assert.False(File.Exists(Abs($@"Filme\{Scene}.mkv")));
        Assert.Empty(Directory.EnumerateFiles(Abs("Filme"), $"{Scene}*", SearchOption.AllDirectories));

        var after = _db.ListVideos().Single();
        Assert.Equal(video.VideoId, after.VideoId); // dieselbe Video-ID …
        Assert.Equal(video.FileId, after.FileId);
        Assert.Equal($@"Filme\{Proper}.mkv", after.RelPath);
        Assert.Equal(600, _db.GetPlayback(video.VideoId)!.PositionSeconds); // … damit Fortschritt,
        Assert.Equal(1.5, _db.GetSubtitleAdjustment(video.VideoId).DelaySeconds); // Untertitel-Einstellungen,
        Assert.Equal(20, _db.GetColorSettings(video.VideoId).Brightness); // Farben und
        Assert.Equal(titleId, after.TitleId); // Titel samt Genres bleiben
        Assert.Equal("The Matrix Reloaded", after.TitleName); // der Titel hat jetzt die richtige Schreibweise
        Assert.Equal(2003, after.Year);
        Assert.Contains("Action", _db.GetGenres(titleId));
    }

    [Fact]
    public async Task A_new_scan_after_renaming_changes_nothing()
    {
        Touch(_drive, $@"Filme\{Scene}.mkv");
        await Scan();
        Apply(await Plan());
        var before = _db.ListVideos().Single();

        var scan = await _library.AddFolderAsync(_drive);

        Assert.Equal(0, scan.Scan.Added);
        Assert.Equal(0, scan.Scan.MarkedMissing);
        Assert.Equal(before, _db.ListVideos().Single());
    }

    [Fact]
    public async Task A_film_moves_to_an_existing_title_and_the_empty_old_title_is_merged_with_its_genres()
    {
        Touch(_drive, @"Filme\matrix.reloaded.2003.mkv");
        Touch(_drive, @"Filme\The Matrix Reloaded (2003) [720p].mkv");
        await Scan();
        _info.Known["matrix reloaded"] = _info.Known["the matrix reloaded"];
        var wrong = Video("matrix.reloaded").TitleId!.Value;
        var right = Video("The Matrix Reloaded").TitleId!.Value;
        Assert.NotEqual(wrong, right);
        _db.AddGenre(wrong, "Science-Fiction");

        var proposals = await Plan();
        var outcome = Assert.Single(Apply(proposals.Where(p => p.Video.RelPath.Contains("matrix.reloaded")).ToList()));

        Assert.True(outcome.Success, outcome.Error);
        Assert.All(_db.ListVideos(), v => Assert.Equal(right, v.TitleId));
        Assert.Single(_db.GetTitles());
        Assert.Contains("Science-Fiction", _db.GetGenres(right)); // die Genres des leeren Titels sind mitgewandert
    }

    [Fact]
    public async Task A_user_assignment_is_not_changed_by_renaming()
    {
        Touch(_drive, $@"Filme\{Scene}.mkv");
        await Scan();
        var video = Video(Scene);
        var mine = _db.CreateTitle(TitleKind.Movie, "Mein eigener Titel", 2003);
        _db.AssignVideo(video.VideoId, mine, null, null);
        _info.Known["Mein eigener Titel"] = _info.Known["the matrix reloaded"]; // die Suche läuft mit dem Namen des Titels, dem das Video gehört

        Assert.True(Assert.Single(Apply(await Plan())).Success);

        var after = _db.ListVideos().Single();
        Assert.Equal(mine, after.TitleId);
        Assert.Equal("Mein eigener Titel", after.TitleName);
    }

    [Fact]
    public async Task A_film_without_a_year_leaves_the_inbox_once_it_has_a_proper_name()
    {
        Touch(_drive, "Filme/matrix.reloaded.1080p.mkv");
        await Scan();
        Assert.True(Video("matrix").IsInbox);
        _info.Known["matrix reloaded"] = _info.Known["the matrix reloaded"];

        Assert.True(Assert.Single(Apply(await Plan())).Success);

        var after = _db.ListVideos().Single();
        Assert.False(after.IsInbox);
        Assert.Equal("The Matrix Reloaded", after.TitleName);
        Assert.Equal(2003, after.Year);
    }

    [Fact]
    public async Task A_single_file_source_follows_the_rename()
    {
        Touch(_drive, $@"Einzeln\{Scene}.mkv");
        await _library.AddFileAsync(Abs($@"Einzeln\{Scene}.mkv"));

        Assert.True(Assert.Single(Apply(await Plan())).Success);

        var source = Assert.Single(_db.GetSources());
        Assert.Equal($@"Einzeln\{Proper}.mkv", source.RelPath);
        var scan = await _library.RescanAllAsync();
        Assert.Equal(0, scan.Single().Result.MarkedMissing);
    }

    [Fact]
    public async Task Names_that_are_already_right_unknown_films_and_episodes_are_left_alone()
    {
        Touch(_drive, $@"Filme\{Proper}.mkv");
        Touch(_drive, @"Filme\voellig.unbekannt.1999.mkv");
        Touch(_drive, @"Serien\Show X\Season 1\Show.X.S01E01.mkv");
        await Scan();

        var proposals = await Plan();

        Assert.Equal(2, proposals.Count); // die Episode ist kein Kandidat
        Assert.Equal(RenameStatus.AlreadyCorrect, proposals.Single(p => p.Video.RelPath.Contains("Matrix")).Status);
        Assert.Equal(RenameStatus.NoMatch, proposals.Single(p => p.Video.RelPath.Contains("unbekannt")).Status);
        Assert.Empty(Apply(proposals)); // nichts umbenennbar
    }

    [Fact]
    public async Task An_existing_file_with_the_new_name_blocks_the_rename_and_so_do_two_films_with_the_same_new_name()
    {
        Touch(_drive, $@"Filme\{Scene}.mkv");
        Touch(_drive, $@"Ordner2\{Scene}.mkv");
        Touch(_drive, $@"Ordner2\{Proper}.mkv"); // gibt es dort schon
        await Scan();

        var proposals = await Plan();

        var blocked = proposals.Single(p => p.Video.RelPath.StartsWith("Ordner2") && p.Video.RelPath.Contains("blabla"));
        Assert.Equal(RenameStatus.TargetExists, blocked.Status);
        Assert.Contains(Proper, blocked.Detail);
        Assert.Equal(RenameStatus.Ready, proposals.Single(p => p.Video.RelPath.StartsWith("Filme")).Status);
        Assert.Equal(RenameStatus.AlreadyCorrect, proposals.Single(p => p.Video.RelPath.EndsWith($"{Proper}.mkv")).Status);

        Touch(_drive, @"Doppelt\the.matrix.reloaded.2003.1080p.a.mkv");
        Touch(_drive, @"Doppelt\the.matrix.reloaded.2003.1080p.b.mkv");
        await Scan();
        var doubles = (await Plan()).Where(p => p.Video.RelPath.StartsWith("Doppelt")).ToList();
        Assert.Equal(1, doubles.Count(p => p.Status == RenameStatus.Ready));
        Assert.Equal(1, doubles.Count(p => p.Status == RenameStatus.TargetExists));
    }

    [Fact]
    public async Task A_name_the_library_would_not_read_as_the_same_film_is_not_proposed()
    {
        Touch(_drive, $@"Filme\{Scene}.mkv");
        await Scan();
        _info.Known["the matrix reloaded"] = new MovieInfo("Show S01E01", 2003, null); // der Name würde als Episode gelesen

        var proposal = Assert.Single(await Plan());

        Assert.Equal(RenameStatus.Unreadable, proposal.Status);
        Assert.Empty(Apply(new[] { proposal }));
    }

    [Fact]
    public async Task Companions_of_another_film_in_the_same_folder_stay_where_they_are()
    {
        Touch(_drive, @"Filme\Film.2003.mkv");
        Touch(_drive, @"Filme\Film.2003.de.srt");
        Touch(_drive, @"Filme\Film.2003.Teil2.mkv");
        Touch(_drive, @"Filme\Film.2003.Teil2.de.srt");
        await Scan();
        _info.Known["film"] = new MovieInfo("Ein Film", 2003, null);

        var plan = await Plan();
        var first = plan.Single(p => p.Video.RelPath.EndsWith("Film.2003.mkv"));

        Assert.Equal(RenameStatus.Ready, first.Status);
        Assert.Equal(new[] { @"Filme\Film.2003.de.srt" }, first.SidecarMoves.Select(s => s.OldRel));
        Assert.Equal(@"Filme\Ein_Film_(2003)_[1080p].de.srt", first.SidecarMoves.Single().NewRel);
    }

    [Fact]
    public async Task When_a_companion_cannot_be_moved_everything_of_that_film_is_rolled_back()
    {
        Touch(_drive, $@"Filme\{Scene}.mkv");
        Touch(_drive, $@"Filme\{Scene}.de.srt");
        await Scan();
        var proposals = await Plan();
        File.WriteAllText(Abs($@"Filme\{Proper}.de.srt"), "taucht nach der Planung auf"); // blockiert den zweiten Schritt

        var outcome = Assert.Single(Apply(proposals));

        Assert.False(outcome.Success);
        Assert.Contains("Nichts wurde verändert", outcome.Error);
        Assert.True(File.Exists(Abs($@"Filme\{Scene}.mkv")));
        Assert.False(File.Exists(Abs($@"Filme\{Proper}.mkv")));
        Assert.Equal($@"Filme\{Scene}.mkv", _db.ListVideos().Single().RelPath);
        Assert.Equal("taucht nach der Planung auf", File.ReadAllText(Abs($@"Filme\{Proper}.de.srt"))); // nichts überschrieben
    }

    [Fact]
    public async Task When_the_library_refuses_the_new_path_the_files_are_moved_back()
    {
        Touch(_drive, $@"Filme\{Scene}.mkv");
        Touch(_drive, $@"Filme\{Proper}.mkv"); // gleich wieder entfernt: der Eintrag bleibt als „fehlend“ in der Bibliothek
        await Scan();
        File.Delete(Abs($@"Filme\{Proper}.mkv"));
        await Scan();
        var proposals = await Plan();
        Assert.Equal(RenameStatus.Ready, proposals.Single(p => p.Video.RelPath.Contains("blabla")).Status);

        var outcome = Assert.Single(Apply(proposals.Where(p => p.Status == RenameStatus.Ready).ToList()));

        Assert.False(outcome.Success);
        Assert.Contains("Nichts wurde verändert", outcome.Error);
        Assert.True(File.Exists(Abs($@"Filme\{Scene}.mkv")));
        Assert.False(File.Exists(Abs($@"Filme\{Proper}.mkv")));
    }

    [Fact]
    public async Task Undo_brings_back_the_old_names_in_the_files_and_in_the_library()
    {
        Touch(_drive, $@"Filme\{Scene}.mkv");
        Touch(_drive, $@"Filme\{Scene}.de.srt");
        await Scan();
        var videoId = Video(Scene).VideoId;
        var outcomes = Apply(await Plan());
        var records = outcomes.Select(o => o.Record!).ToList();

        var undo = new MovieRenamer(_db).Undo(records, Status());

        Assert.All(undo, u => Assert.True(u.Success, u.Error));
        Assert.True(File.Exists(Abs($@"Filme\{Scene}.mkv")));
        Assert.True(File.Exists(Abs($@"Filme\{Scene}.de.srt")));
        Assert.False(File.Exists(Abs($@"Filme\{Proper}.mkv")));
        var after = _db.ListVideos().Single();
        Assert.Equal(videoId, after.VideoId);
        Assert.Equal($@"Filme\{Scene}.mkv", after.RelPath);
    }

    [Fact]
    public async Task Undo_refuses_when_the_old_name_is_taken_again_and_reports_it()
    {
        Touch(_drive, $@"Filme\{Scene}.mkv");
        await Scan();
        var records = Apply(await Plan()).Select(o => o.Record!).ToList();
        File.WriteAllText(Abs($@"Filme\{Scene}.mkv"), "inzwischen neu angelegt");

        var undo = Assert.Single(new MovieRenamer(_db).Undo(records, Status()));

        Assert.False(undo.Success);
        Assert.True(File.Exists(Abs($@"Filme\{Proper}.mkv")));
        Assert.Equal("inzwischen neu angelegt", File.ReadAllText(Abs($@"Filme\{Scene}.mkv")));
    }

    [Fact]
    public async Task A_change_of_letter_case_only_is_possible()
    {
        Touch(_drive, @"Filme\the_matrix_reloaded_(2003)_[1080p].mkv");
        await Scan();

        var proposal = Assert.Single(await Plan());
        Assert.Equal(RenameStatus.Ready, proposal.Status);
        Assert.True(Assert.Single(Apply(new[] { proposal })).Success);

        Assert.Equal($"{Proper}.mkv", Path.GetFileName(Directory.GetFiles(Abs("Filme")).Single()));
        Assert.Empty(Directory.GetFiles(Abs("Filme"), "*.rename-tmp"));
    }

    [Fact]
    public async Task A_missing_drive_or_file_is_offline_and_not_touched()
    {
        Touch(_drive, $@"Filme\{Scene}.mkv");
        await Scan();
        File.Delete(Abs($@"Filme\{Scene}.mkv"));

        var proposal = Assert.Single(await Plan());

        Assert.Equal(RenameStatus.Offline, proposal.Status);
        Assert.Empty(_info.Asked); // ohne Datei wird auch nichts angefragt
    }

    [Fact]
    public void The_journal_roundtrips_and_a_broken_one_is_a_loud_error()
    {
        var path = Path.Combine(_base, "journal.json");
        Assert.Empty(RenameJournal.Load(path));
        var records = new[] { new RenameRecord(1, 2, @"a\old.mkv", @"a\new.mkv", new[] { new SidecarMove(@"a\old.srt", @"a\new.srt") }) };

        RenameJournal.Save(path, records);
        var loaded = RenameJournal.Load(path);

        Assert.Equal(@"a\new.mkv", loaded.Single().NewRel);
        Assert.Equal(@"a\new.srt", loaded.Single().Sidecars.Single().NewRel);

        File.WriteAllText(path, "{kaputt");
        Assert.Throws<InvalidDataException>(() => RenameJournal.Load(path));
        RenameJournal.Clear(path);
        Assert.Empty(RenameJournal.Load(path));
    }

    [Fact]
    public async Task Planning_stops_loudly_when_the_service_refuses()
    {
        Touch(_drive, $@"Filme\{Scene}.mkv");
        await Scan();
        var refusing = new RefusingInfo();

        await Assert.ThrowsAsync<PosterSourceUnavailableException>(() => new MovieRenamePlanner(refusing).PlanAsync(_db.ListVideos(), Status()));
    }

    // --- Von Hand gewählter Name ---------------------------------------------------------------------------

    [Fact]
    public async Task A_manual_name_renames_the_file_and_companions_and_keeps_the_video_and_follows_with_the_title()
    {
        Touch(_drive, $@"Filme\{Scene}.mkv");
        Touch(_drive, $@"Filme\{Scene}.de.srt");
        await Scan();
        var video = Video(Scene);
        _db.SavePlayback(video.VideoId, 600, watched: false);

        var proposal = MovieRenamePlanner.PlanManual(video, Status(), "Der Pate (1972)");

        Assert.Equal(RenameStatus.Ready, proposal.Status);
        Assert.Equal(@"Filme\Der Pate (1972).mkv", proposal.NewRelPath);
        Assert.Equal(@"Filme\Der Pate (1972).de.srt", proposal.SidecarMoves.Single().NewRel);
        Assert.True(Assert.Single(Apply(new[] { proposal })).Success);

        Assert.True(File.Exists(Abs(@"Filme\Der Pate (1972).mkv")));
        Assert.True(File.Exists(Abs(@"Filme\Der Pate (1972).de.srt")));
        Assert.False(File.Exists(Abs($@"Filme\{Scene}.mkv")));
        var after = _db.ListVideos().Single();
        Assert.Equal(video.VideoId, after.VideoId);
        Assert.Equal("Der Pate", after.TitleName);
        Assert.Equal(1972, after.Year);
        Assert.Equal(600, _db.GetPlayback(video.VideoId)!.PositionSeconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Film: Teil 2 (2001)")]
    [InlineData("Film/Teil (2001)")]
    [InlineData("Film (2001).")]
    [InlineData("Film (2001).mkv")]
    public async Task A_manual_name_that_is_no_usable_file_name_is_refused_loudly(string name)
    {
        Touch(_drive, $@"Filme\{Scene}.mkv");
        await Scan();

        Assert.Throws<ArgumentException>(() => MovieRenamePlanner.PlanManual(Video(Scene), Status(), name));
        Assert.True(File.Exists(Abs($@"Filme\{Scene}.mkv")));
    }

    [Theory]
    [InlineData("Der Pate", "Jahr")] // ohne Jahr würde der Film in die Inbox fallen
    [InlineData("Die Serie S01E02 (2001)", "Serienfolge")]
    public async Task A_manual_name_the_library_would_not_read_as_a_film_with_year_is_not_planned(string name, string expectedInDetail)
    {
        Touch(_drive, $@"Filme\{Scene}.mkv");
        await Scan();

        var proposal = MovieRenamePlanner.PlanManual(Video(Scene), Status(), name);

        Assert.Equal(RenameStatus.Unreadable, proposal.Status);
        Assert.Contains(expectedInDetail, proposal.Detail);
        Assert.Null(proposal.NewRelPath);
    }

    [Fact]
    public async Task A_manual_name_that_exists_already_the_same_name_and_a_missing_file_are_reported_not_applied()
    {
        Touch(_drive, $@"Filme\{Scene}.mkv");
        Touch(_drive, @"Filme\Belegt (2000).mkv");
        await Scan();
        var video = Video(Scene);

        Assert.Equal(RenameStatus.TargetExists, MovieRenamePlanner.PlanManual(video, Status(), "Belegt (2000)").Status);
        Assert.Equal(RenameStatus.AlreadyCorrect, MovieRenamePlanner.PlanManual(video, Status(), Scene).Status);

        File.Delete(Abs($@"Filme\{Scene}.mkv"));
        Assert.Equal(RenameStatus.Offline, MovieRenamePlanner.PlanManual(video, Status(), "Neu (2000)").Status);
    }

    [Fact]
    public async Task Undoing_a_manual_rename_gives_back_the_name_and_year_of_the_title_too()
    {
        Touch(_drive, $@"Filme\{Scene}.mkv");
        await Scan();
        var video = Video(Scene);
        var oldName = video.TitleName;
        var oldYear = video.Year;
        var proposal = MovieRenamePlanner.PlanManual(video, Status(), "Der Pate (1972)");
        var record = Assert.Single(Apply(new[] { proposal })).Record!;
        Assert.Equal("Der Pate", _db.ListVideos().Single().TitleName); // der Titel ist dem Dateinamen gefolgt

        var undo = Assert.Single(new MovieRenamer(_db).Undo(new[] { record }, Status()));

        Assert.True(undo.Success);
        Assert.True(File.Exists(Abs($@"Filme\{Scene}.mkv")));
        var after = _db.ListVideos().Single();
        Assert.Equal(video.VideoId, after.VideoId);
        Assert.Equal(oldName, after.TitleName);
        Assert.Equal(oldYear, after.Year);
    }

    [Fact]
    public async Task Undoing_leaves_a_title_alone_that_the_film_no_longer_belongs_to()
    {
        Touch(_drive, $@"Filme\{Scene}.mkv");
        Touch(_drive, @"Filme\Der Pate (1972).mkv");
        await Scan();
        var pate = Video("Der Pate");
        var video = Video(Scene);
        // Der Film wandert zum vorhandenen Titel „Der Pate“; sein alter Titel bleibt leer zurück und wird zusammengeführt.
        var record = Assert.Single(Apply(new[] { MovieRenamePlanner.PlanManual(video, Status(), "Der Pate (1972) Teil 2 (1974)") })).Record!;
        var titleOfPate = _db.ListVideos().Single(v => v.VideoId == pate.VideoId).TitleName;

        var undo = Assert.Single(new MovieRenamer(_db).Undo(new[] { record }, Status()));

        Assert.True(undo.Success);
        Assert.Equal(titleOfPate, _db.ListVideos().Single(v => v.VideoId == pate.VideoId).TitleName); // der andere Titel bleibt unangetastet
    }

    private sealed class RefusingInfo : IMovieInfoSource
    {
        public Task<MovieInfo?> LookupAsync(string title, int? year, CancellationToken ct = default) =>
            throw new PosterSourceUnavailableException("OMDb: Request limit reached!");
    }
}
