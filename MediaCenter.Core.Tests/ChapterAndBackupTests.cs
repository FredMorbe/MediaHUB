using System.IO.Compression;
using System.Text.Json;
using MediaCenter.Core;
using Microsoft.Data.Sqlite;

namespace MediaCenter.Core.Tests;

public sealed class ChapterTests
{
    private static IReadOnlyList<Chapter> Parse(string json) => ChapterList.Parse(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Chapters_are_read_sorted_by_time_with_a_fallback_label()
    {
        var chapters = Parse("""[{"title":"Ende","time":3000.5},{"title":"","time":0},{"time":120}]""");

        Assert.Equal(new[] { 0.0, 120.0, 3000.5 }, chapters.Select(c => c.Start));
        Assert.Equal(new[] { "Kapitel 1", "Kapitel 2", "Ende" }, chapters.Select(c => c.Label));
        Assert.Equal(new[] { 0, 1, 2 }, chapters.Select(c => c.Index));
    }

    [Fact]
    public void A_chapter_without_a_time_is_a_loud_error()
    {
        Assert.Throws<JsonException>(() => Parse("""[{"title":"Kaputt"}]"""));
        Assert.Throws<JsonException>(() => ChapterList.Parse(JsonDocument.Parse("{}").RootElement));
    }

    [Fact]
    public void IndexAt_finds_the_chapter_a_position_lies_in()
    {
        var chapters = Parse("""[{"title":"A","time":10},{"title":"B","time":60},{"title":"C","time":120}]""");

        Assert.Equal(-1, ChapterList.IndexAt(chapters, 5));
        Assert.Equal(0, ChapterList.IndexAt(chapters, 10));
        Assert.Equal(1, ChapterList.IndexAt(chapters, 119.9));
        Assert.Equal(2, ChapterList.IndexAt(chapters, 5000));
        Assert.Equal(-1, ChapterList.IndexAt(Array.Empty<Chapter>(), 5));
    }

    [Fact]
    public void Next_and_previous_chapter_jump_like_a_dvd_player()
    {
        var chapters = Parse("""[{"title":"A","time":0},{"title":"B","time":60},{"title":"C","time":120}]""");

        Assert.Equal(60, ChapterList.NextStart(chapters, 10));
        Assert.Equal(120, ChapterList.NextStart(chapters, 60));
        Assert.Null(ChapterList.NextStart(chapters, 130));
        Assert.Equal(60, ChapterList.PreviousStart(chapters, 100)); // schon weiter als 3 s: an den Anfang des Kapitels
        Assert.Equal(0, ChapterList.PreviousStart(chapters, 61));   // gerade erst begonnen: zum vorigen
        Assert.Equal(0, ChapterList.PreviousStart(chapters, 1));    // erstes Kapitel: an den Anfang
        Assert.Null(ChapterList.PreviousStart(Parse("""[{"title":"A","time":30}]"""), 5));
    }

    [Theory]
    [InlineData("Intro")]
    [InlineData("Opening")]
    [InlineData("OP")]
    [InlineData("Opening Credits")]
    [InlineData("Vorspann")]
    [InlineData("Title Sequence")]
    [InlineData("  intro 2 ")]
    public void Intro_chapters_are_recognised_by_their_title(string title)
    {
        var chapters = Parse($$"""[{"title":"{{title}}","time":5},{"title":"Folge","time":95}]""");

        Assert.Equal((5.0, 95.0), ChapterList.FindIntro(chapters, 1400));
    }

    [Theory]
    [InlineData("Opening scene of the heist")]
    [InlineData("Chapter 1")]
    [InlineData("Introduction to the villain's plan")]
    public void Other_chapters_are_not_taken_for_an_intro(string title)
    {
        Assert.Null(ChapterList.FindIntro(Parse($$"""[{"title":"{{title}}","time":0},{"title":"X","time":60}]"""), 1400));
    }

    [Fact]
    public void A_last_chapter_intro_ends_at_the_duration_and_an_unknown_end_gives_no_intro()
    {
        var chapters = Parse("""[{"title":"Hauptteil","time":0},{"title":"Intro","time":50}]""");

        Assert.Equal((50.0, 90.0), ChapterList.FindIntro(chapters, 90));
        Assert.Null(ChapterList.FindIntro(chapters, 0));
    }
}

public sealed class BackupTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-backup-" + Guid.NewGuid().ToString("N"));
    private readonly string _data;
    private readonly LibraryDb _db;

    public BackupTests()
    {
        _data = Path.Combine(_base, "data");
        Directory.CreateDirectory(_data);
        _db = new LibraryDb(Path.Combine(_data, "library.db"));
        File.WriteAllText(Path.Combine(_data, "settings.json"), "{\"Mode\":\"dark\"}");
        Directory.CreateDirectory(Path.Combine(_data, "artwork"));
        File.WriteAllText(Path.Combine(_data, "artwork", "title-1-poster.jpg"), "poster");
        Directory.CreateDirectory(Path.Combine(_data, "subtitles", "7"));
        File.WriteAllText(Path.Combine(_data, "subtitles", "7", "Film.de.srt"), "untertitel");
        // Das gehört nicht ins Archiv: Zwischenspeicher und Schlüssel.
        File.WriteAllText(Path.Combine(_data, "omdb.key"), "GEHEIM");
        Directory.CreateDirectory(Path.Combine(_data, "previews"));
        File.WriteAllText(Path.Combine(_data, "previews", "1.mp4"), "clip");
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_base, recursive: true);
    }

    private string Zip => Path.Combine(_base, "sicherung.zip");

    [Fact]
    public void A_backup_holds_the_database_settings_and_user_files_but_no_caches_or_keys()
    {
        _db.CreateTitle(TitleKind.Movie, "Inception", 2010);

        var manifest = BackupService.Create(_db, _data, Zip, "9.9.9", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        using var archive = ZipFile.OpenRead(Zip);
        var names = archive.Entries.Select(e => e.FullName).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "artwork/title-1-poster.jpg", "backup.json", "library.db", "settings.json", "subtitles/7/Film.de.srt" }, names);
        Assert.Equal(LibraryDb.CurrentSchema, manifest.Schema);
        Assert.Equal("9.9.9", manifest.AppVersion);
        Assert.Equal(4, manifest.Files); // ohne backup.json selbst
        Assert.Equal(manifest, BackupService.Inspect(Zip));
        Assert.False(File.Exists(Zip + ".tmp"));
    }

    [Fact]
    public void The_backup_contains_a_consistent_copy_of_the_database()
    {
        _db.CreateTitle(TitleKind.Movie, "Inception", 2010);
        BackupService.Create(_db, _data, Zip, "1.0");

        var copy = Path.Combine(_base, "copy");
        ZipFile.ExtractToDirectory(Zip, copy);
        using var restored = new LibraryDb(Path.Combine(copy, "library.db"));
        Assert.Equal("Inception", Assert.Single(restored.GetTitles()).Name);
    }

    [Fact]
    public void Restoring_is_prepared_first_and_swapped_in_at_the_next_start_keeping_the_old_state()
    {
        var title = _db.CreateTitle(TitleKind.Movie, "Alt", 2000);
        BackupService.Create(_db, _data, Zip, "1.0");

        // Nach der Sicherung ändert sich alles.
        _db.CreateTitle(TitleKind.Movie, "Neu", 2024);
        File.WriteAllText(Path.Combine(_data, "settings.json"), "{\"Mode\":\"light\"}");
        File.Delete(Path.Combine(_data, "artwork", "title-1-poster.jpg"));
        File.WriteAllText(Path.Combine(_data, "artwork", "neu.jpg"), "neu");

        BackupService.StageRestore(Zip, _data);
        Assert.True(BackupService.HasPendingRestore(_data));
        Assert.Equal(2, _db.GetTitles().Count); // bis zum Neustart ändert sich nichts

        _db.Dispose();
        SqliteConnection.ClearAllPools();
        var previous = BackupService.ApplyPendingRestore(_data, new DateTime(2026, 5, 6, 7, 8, 9));

        Assert.False(BackupService.HasPendingRestore(_data));
        Assert.EndsWith("vor-wiederherstellung-20260506-070809", previous);
        using (var restored = new LibraryDb(Path.Combine(_data, "library.db")))
            Assert.Equal(title, Assert.Single(restored.GetTitles()).Id);
        SqliteConnection.ClearAllPools();
        Assert.Equal("{\"Mode\":\"dark\"}", File.ReadAllText(Path.Combine(_data, "settings.json")));
        Assert.True(File.Exists(Path.Combine(_data, "artwork", "title-1-poster.jpg")));
        Assert.False(File.Exists(Path.Combine(_data, "artwork", "neu.jpg")));
        // Der bisherige Stand liegt unversehrt daneben.
        Assert.Equal("{\"Mode\":\"light\"}", File.ReadAllText(Path.Combine(previous!, "settings.json")));
        Assert.True(File.Exists(Path.Combine(previous!, "artwork", "neu.jpg")));
        Assert.True(File.Exists(Path.Combine(previous!, "library.db")));
        // Der Schlüssel und die Zwischenspeicher bleiben unberührt.
        Assert.Equal("GEHEIM", File.ReadAllText(Path.Combine(_data, "omdb.key")));
        Assert.True(File.Exists(Path.Combine(_data, "previews", "1.mp4")));
    }

    [Fact]
    public void Applying_without_a_prepared_restore_does_nothing()
    {
        Assert.Null(BackupService.ApplyPendingRestore(_data));
        Assert.Empty(Directory.GetDirectories(_data, BackupService.PreviousPrefix + "*"));
    }

    [Fact]
    public void An_incomplete_prepared_restore_is_set_aside_with_a_loud_error()
    {
        Directory.CreateDirectory(Path.Combine(_data, BackupService.PendingFolder));
        File.WriteAllText(Path.Combine(_data, BackupService.PendingFolder, "backup.json"), "{}");

        var ex = Assert.Throws<InvalidDataException>(() => BackupService.ApplyPendingRestore(_data));

        Assert.Contains("unvollständig", ex.Message);
        Assert.False(BackupService.HasPendingRestore(_data));
        Assert.True(File.Exists(Path.Combine(_data, "library.db"))); // die Daten sind unverändert
    }

    [Fact]
    public void Foreign_or_damaged_archives_are_refused_without_leaving_a_pending_restore()
    {
        var notABackup = Path.Combine(_base, "fremd.zip");
        using (var zip = ZipFile.Open(notABackup, ZipArchiveMode.Create)) zip.CreateEntry("hallo.txt");
        Assert.Throws<InvalidDataException>(() => BackupService.Inspect(notABackup));

        var notAZip = Path.Combine(_base, "text.zip");
        File.WriteAllText(notAZip, "kein zip");
        Assert.Throws<InvalidDataException>(() => BackupService.StageRestore(notAZip, _data));
        Assert.False(BackupService.HasPendingRestore(_data));
    }

    [Fact]
    public void A_backup_from_a_newer_version_and_unexpected_entries_are_refused()
    {
        BackupService.Create(_db, _data, Zip, "1.0");
        var newer = Path.Combine(_base, "neuer.zip");
        File.Copy(Zip, newer);
        using (var archive = ZipFile.Open(newer, ZipArchiveMode.Update))
        {
            archive.GetEntry("backup.json")!.Delete();
            using var writer = new StreamWriter(archive.CreateEntry("backup.json").Open());
            writer.Write(JsonSerializer.Serialize(new BackupManifest(BackupService.FormatVersion, LibraryDb.CurrentSchema + 1, "9", DateTime.UtcNow, 1)));
        }
        var tooNew = Assert.Throws<InvalidDataException>(() => BackupService.Inspect(newer));
        Assert.Contains("neueren Version", tooNew.Message);

        var evil = Path.Combine(_base, "boese.zip");
        File.Copy(Zip, evil);
        using (var archive = ZipFile.Open(evil, ZipArchiveMode.Update)) archive.CreateEntry("artwork/../../draussen.txt");
        Assert.Throws<InvalidDataException>(() => BackupService.Inspect(evil));
        using (var archive = ZipFile.Open(evil, ZipArchiveMode.Update)) archive.GetEntry("artwork/../../draussen.txt")!.Delete();
        using (var archive = ZipFile.Open(evil, ZipArchiveMode.Update)) archive.CreateEntry("windows.exe");
        Assert.Throws<InvalidDataException>(() => BackupService.Inspect(evil));
    }

    [Fact]
    public void Backing_up_to_an_existing_file_does_not_overwrite_it()
    {
        var target = Path.Combine(_base, "vorhanden.db");
        File.WriteAllText(target, "wichtig");

        Assert.Throws<IOException>(() => _db.BackupTo(target));
        Assert.Equal("wichtig", File.ReadAllText(target));
    }

    [Fact]
    public void A_new_backup_replaces_the_old_file_only_when_it_is_complete()
    {
        File.WriteAllText(Zip, "alte Sicherung");
        BackupService.Create(_db, _data, Zip, "1.0");

        Assert.NotNull(BackupService.Inspect(Zip));
    }
}
