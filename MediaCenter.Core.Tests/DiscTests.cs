using MediaCenter.Core;
using static MediaCenter.Core.Tests.TestSupport;

namespace MediaCenter.Core.Tests;

public sealed class DiscTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-" + Guid.NewGuid().ToString("N"));

    public DiscTests() => Directory.CreateDirectory(_base);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_base, recursive: true);
    }

    private string Dir(string rel)
    {
        var path = Path.Combine(_base, rel);
        Directory.CreateDirectory(path);
        return path;
    }

    private string File_(string rel, int size = 10)
    {
        var path = Path.Combine(_base, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    private string Dvd(string root)
    {
        File_($@"{root}\VIDEO_TS\VIDEO_TS.IFO");
        File_($@"{root}\VIDEO_TS\VTS_01_1.VOB", 5000);
        return Path.Combine(_base, root);
    }

    private string BluRay(string root)
    {
        File_($@"{root}\BDMV\index.bdmv");
        File_($@"{root}\BDMV\STREAM\00000.m2ts", 9000);
        return Path.Combine(_base, root);
    }

    [Fact]
    public void Dvd_and_bluray_folders_are_recognised_by_their_index_files_ignoring_case()
    {
        Assert.Equal(DiscKind.Dvd, DiscDetector.OfFolder(Dvd("Dune")));
        Assert.Equal(DiscKind.BluRay, DiscDetector.OfFolder(BluRay("Heat")));
        File_(@"Klein\video_ts\video_ts.ifo");
        Assert.Equal(DiscKind.Dvd, DiscDetector.OfFolder(Path.Combine(_base, "Klein")));
        File_(@"Klein2\bdmv\INDEX.BDMV");
        Assert.Equal(DiscKind.BluRay, DiscDetector.OfFolder(Path.Combine(_base, "Klein2")));
    }

    [Fact]
    public void Folders_without_the_index_file_or_missing_folders_are_no_disc()
    {
        Dir(@"Leer\VIDEO_TS");
        File_(@"Teil\BDMV\STREAM\00000.m2ts");

        Assert.Equal(DiscKind.None, DiscDetector.OfFolder(Path.Combine(_base, "Leer")));
        Assert.Equal(DiscKind.None, DiscDetector.OfFolder(Path.Combine(_base, "Teil")));
        Assert.Equal(DiscKind.None, DiscDetector.OfFolder(Path.Combine(_base, "gibt-es-nicht")));
    }

    [Fact]
    public void A_folder_that_is_itself_called_video_ts_counts_as_a_dvd()
    {
        Dvd("Film");

        Assert.Equal(DiscKind.Dvd, DiscDetector.OfFolder(Path.Combine(_base, "Film", "VIDEO_TS")));
    }

    [Fact]
    public void Enumerate_lists_a_disc_folder_as_one_entry_and_does_not_look_inside()
    {
        Dvd(@"Filme\Dune (2021)");
        BluRay(@"Filme\Heat (1995)");
        File_(@"Filme\Alien (1979).mkv");
        File_(@"Filme\Aliens (1986).iso");
        File_(@"Filme\Notizen.txt");
        File_(@"Filme\Serie\S01E01.mp4");

        var entries = DiscDetector.Enumerate(Path.Combine(_base, "Filme")).OrderBy(e => e.Path).ToList();

        Assert.Equal(
            new[] { "Alien (1979).mkv", "Aliens (1986).iso", "Dune (2021)", "Heat (1995)", "S01E01.mp4" },
            entries.Select(e => Path.GetFileName(e.Path)).ToArray());
        Assert.Equal(DiscKind.Dvd, entries.Single(e => e.Path.EndsWith("Dune (2021)")).Disc);
        Assert.Equal(DiscKind.BluRay, entries.Single(e => e.Path.EndsWith("Heat (1995)")).Disc);
        Assert.True(entries.Single(e => e.Path.EndsWith(".iso")).IsImage);
        Assert.False(entries.Single(e => e.Path.EndsWith(".mkv")).IsDisc);
        Assert.DoesNotContain(entries, e => e.Path.EndsWith(".m2ts") || e.Path.EndsWith(".VOB"));
    }

    [Fact]
    public void A_source_that_is_a_disc_folder_gives_that_one_entry()
    {
        var root = Dvd("Dune");

        var entry = Assert.Single(DiscDetector.Enumerate(root));

        Assert.Equal((root, DiscKind.Dvd), (entry.Path, entry.Disc));
    }

    [Fact]
    public void Signature_covers_all_files_and_changes_when_one_changes()
    {
        var root = BluRay("Heat");
        var before = DiscDetector.Signature(root);
        Assert.Equal(10 + 9000, before.Size);

        File.WriteAllBytes(Path.Combine(root, "BDMV", "STREAM", "00001.m2ts"), new byte[100]);
        var after = DiscDetector.Signature(root);

        Assert.Equal(before.Size + 100, after.Size);
    }

    [Fact]
    public void Fingerprint_survives_moving_the_folder_but_not_changing_its_files()
    {
        var root = Dvd(@"A\Dune");
        var fingerprint = DiscDetector.Fingerprint(root);

        Directory.CreateDirectory(Path.Combine(_base, "B"));
        Directory.Move(root, Path.Combine(_base, "B", "Anderer Name"));

        Assert.Equal(fingerprint, DiscDetector.Fingerprint(Path.Combine(_base, "B", "Anderer Name")));
        File.WriteAllBytes(Path.Combine(_base, "B", "Anderer Name", "VIDEO_TS", "VTS_01_2.VOB"), new byte[3]);
        Assert.NotEqual(fingerprint, DiscDetector.Fingerprint(Path.Combine(_base, "B", "Anderer Name")));
    }

    [Fact]
    public void Containers_round_trip_and_unknown_ones_are_no_disc()
    {
        Assert.Equal(DiscKind.Dvd, DiscDetector.FromContainer(DiscDetector.ContainerOf(DiscKind.Dvd)));
        Assert.Equal(DiscKind.BluRay, DiscDetector.FromContainer(DiscDetector.ContainerOf(DiscKind.BluRay)));
        Assert.Equal(DiscKind.None, DiscDetector.FromContainer("matroska,webm"));
        Assert.Equal(DiscKind.None, DiscDetector.FromContainer(null));
        Assert.Null(DiscDetector.ContainerOf(DiscKind.None));
    }

    [Fact]
    public void Mpv_gets_the_device_and_the_longest_title()
    {
        Assert.Equal(("dvd-device", "dvd://longest"), DiscInput.ForMpv(DiscKind.Dvd));
        Assert.Equal(("bluray-device", "bd://longest"), DiscInput.ForMpv(DiscKind.BluRay));
        Assert.Throws<ArgumentOutOfRangeException>(() => DiscInput.ForMpv(DiscKind.None));
    }

    [Fact]
    public void Ffmpeg_input_uses_the_dvdvideo_format_or_the_bluray_protocol_with_forward_slashes()
    {
        var dvd = DiscInput.SplitFfmpeg(DiscInput.ForFfmpeg(DiscKind.Dvd, @"D:\Filme\Dune"));
        Assert.Equal(new[] { "-f", "dvdvideo" }, dvd.FormatArgs);
        Assert.Equal(@"D:\Filme\Dune", dvd.Input);

        var bluray = DiscInput.SplitFfmpeg(DiscInput.ForFfmpeg(DiscKind.BluRay, @"D:\Filme\Heat"));
        Assert.Empty(bluray.FormatArgs);
        Assert.Equal("bluray:D:/Filme/Heat", bluray.Input);

        var plain = DiscInput.SplitFfmpeg(DiscInput.ForFfmpeg(DiscKind.None, @"D:\Filme\a.mkv"));
        Assert.Empty(plain.FormatArgs);
        Assert.Equal(@"D:\Filme\a.mkv", plain.Input);
    }

    [Fact]
    public async Task A_folder_without_a_readable_disc_is_a_clear_error_from_the_real_ffprobe()
    {
        var folder = Dir("Keine Disc");
        File_(@"Keine Disc\notiz.txt");

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => FfprobeReader.FromEnvironment().ProbeDiscAsync(folder, DiscKind.None));

        Assert.Contains("keine lesbare DVD oder Blu-ray", ex.Message);
        Assert.Contains("DVD:", ex.Message);
        Assert.Contains("Blu-ray:", ex.Message);
    }

    [Theory]
    [InlineData(@"C:\Filme\Dune.iso", true)]
    [InlineData(@"C:\Filme\Dune.ISO", true)]
    [InlineData(@"C:\Filme\Dune.mkv", false)]
    public void Images_are_recognised_by_extension(string path, bool expected) => Assert.Equal(expected, DiscDetector.IsImage(path));

    [Theory]
    [InlineData(@"C:\Filme\Film.iso", true)]
    [InlineData(@"C:\Filme\Film\VIDEO_TS\VTS_01_1.VOB", true)]
    [InlineData(@"C:\Filme\Film\VIDEO_TS\VIDEO_TS.IFO", true)]
    [InlineData(@"C:\Filme\Film\BDMV\index.bdmv", true)]
    [InlineData(@"C:\Filme\Film\BDMV\PLAYLIST\00000.mpls", true)]
    [InlineData(@"C:\Filme\Film\notizen.txt", false)]
    public void The_folder_watcher_notices_disc_files(string path, bool relevant) => Assert.Equal(relevant, FolderWatcher.IsRelevant(path));
}

/// <summary>Liest „Discs“ ohne ffprobe: antwortet aus einer Tabelle je Pfad.</summary>
internal sealed class FakeDiscProbe : IMediaProbe, IDiscProbe
{
    public Func<string, DiscKind, (DiscKind, ProbeResult)> Respond { get; set; } = (path, hint) => (hint == DiscKind.None ? DiscKind.Dvd : hint, Probe(hint == DiscKind.None ? DiscKind.Dvd : hint));

    public List<string> Discs { get; } = new();

    public static ProbeResult Probe(DiscKind kind) => new(
        5400, DiscDetector.ContainerOf(kind), new[] { new StreamInfo(0, "video", "mpeg2video", "Main", 720, 576, null, null, null, true, false, "bt709") });

    public Task<ProbeResult> ProbeAsync(string file, CancellationToken ct = default) => Task.FromResult(FakeProbe.VideoOnly);

    public Task<(DiscKind Kind, ProbeResult Result)> ProbeDiscAsync(string path, DiscKind hint, CancellationToken ct = default)
    {
        Discs.Add(path);
        return Task.FromResult(Respond(path, hint));
    }
}

public sealed class DiscScanTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-" + Guid.NewGuid().ToString("N"));
    private readonly FakeVolumes _volumes = new();
    private readonly FakeDiscProbe _probe = new();
    private readonly LibraryDb _db;
    private readonly LibraryService _service;
    private readonly string _drive;

    public DiscScanTests()
    {
        Directory.CreateDirectory(_base);
        _db = new LibraryDb(Path.Combine(_base, "library.db"));
        _service = new LibraryService(_db, _volumes, _probe);
        _drive = Path.Combine(_base, "drive");
        Touch(_drive, @"Filme\Dune (2021)\VIDEO_TS\VIDEO_TS.IFO");
        Touch(_drive, @"Filme\Dune (2021)\VIDEO_TS\VTS_01_1.VOB");
        Touch(_drive, @"Filme\Heat (1995)\BDMV\index.bdmv");
        Touch(_drive, @"Filme\Heat (1995)\BDMV\STREAM\00000.m2ts");
        Touch(_drive, @"Filme\Alien (1979).iso");
        Touch(_drive, @"Filme\Aliens (1986).mkv");
        _volumes.Mounted.Add(Vol("DISC0001", _drive));
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_base, recursive: true);
    }

    [Fact]
    public async Task Discs_become_movies_with_their_kind_and_the_files_inside_are_not_scanned_on_their_own()
    {
        var (_, scan) = await _service.AddFolderAsync(Path.Combine(_drive, "Filme"));

        Assert.Equal(4, scan.Added);
        Assert.Empty(scan.Failures);
        var videos = _db.ListVideos();
        Assert.Equal(4, videos.Count);
        Assert.Equal(DiscKind.Dvd, videos.Single(v => v.TitleName == "Dune").Disc);
        Assert.Equal(DiscKind.BluRay, videos.Single(v => v.TitleName == "Heat").Disc);
        Assert.Equal(DiscKind.Dvd, videos.Single(v => v.TitleName == "Alien").Disc);   // das Abbild: Art kommt von der Probe
        Assert.Equal(DiscKind.None, videos.Single(v => v.TitleName == "Aliens").Disc);
        Assert.Equal(5400, videos.Single(v => v.TitleName == "Dune").DurationSeconds);
        Assert.Equal(
            new[] { Path.Combine(_drive, @"Filme\Alien (1979).iso"), Path.Combine(_drive, @"Filme\Dune (2021)"), Path.Combine(_drive, @"Filme\Heat (1995)") },
            _probe.Discs.Order().ToArray());
        Assert.Equal(new[] { 720 }, videos.Where(v => v.Disc != DiscKind.None).Select(v => v.Width!.Value).Distinct().ToArray());
    }

    [Fact]
    public async Task A_rescan_leaves_unchanged_discs_alone_and_updates_a_changed_one()
    {
        await _service.AddFolderAsync(Path.Combine(_drive, "Filme"));
        _probe.Discs.Clear();

        var same = (await _service.RescanAllAsync()).Single().Result;
        Assert.Equal((0, 0, 4), (same.Added, same.Updated, same.Unchanged));
        Assert.Empty(_probe.Discs);

        File.WriteAllBytes(Path.Combine(_drive, @"Filme\Dune (2021)\VIDEO_TS\VTS_01_2.VOB"), new byte[50]);
        var changed = (await _service.RescanAllAsync()).Single().Result;
        Assert.Equal((0, 1, 3), (changed.Added, changed.Updated, changed.Unchanged));
        Assert.Equal(new[] { Path.Combine(_drive, @"Filme\Dune (2021)") }, _probe.Discs.ToArray());
    }

    [Fact]
    public async Task A_moved_disc_folder_keeps_its_user_data()
    {
        await _service.AddFolderAsync(Path.Combine(_drive, "Filme"));
        var dune = _db.ListVideos().Single(v => v.TitleName == "Dune");
        _db.SavePlayback(dune.VideoId, 1234, watched: false);

        Directory.CreateDirectory(Path.Combine(_drive, "Neu"));
        Directory.Move(Path.Combine(_drive, @"Filme\Dune (2021)"), Path.Combine(_drive, @"Neu\Dune (2021)"));
        await _service.AddFolderAsync(Path.Combine(_drive, "Neu"));
        var result = (await _service.RescanAllAsync()).Sum(r => r.Result.Moved);

        Assert.True(result >= 0);
        var moved = _db.ListVideos().Single(v => v.TitleName == "Dune");
        Assert.Equal(dune.VideoId, moved.VideoId);
        Assert.Equal(1234, _db.GetPlayback(moved.VideoId)!.PositionSeconds);
    }

    [Fact]
    public async Task An_image_no_probe_can_read_is_a_scan_failure_naming_the_file()
    {
        _probe.Respond = (path, hint) => path.EndsWith(".iso") ? throw new InvalidDataException("'Alien (1979).iso' ist keine lesbare DVD oder Blu-ray") : (hint, FakeDiscProbe.Probe(hint));

        var (_, scan) = await _service.AddFolderAsync(Path.Combine(_drive, "Filme"));

        Assert.Equal(3, scan.Added);
        var failure = Assert.Single(scan.Failures);
        Assert.EndsWith("Alien (1979).iso", failure.Path);
        Assert.Contains("keine lesbare DVD", failure.Error);
    }

    [Fact]
    public async Task A_scan_without_a_disc_probe_says_so_instead_of_skipping_discs_silently()
    {
        var service = new LibraryService(_db, _volumes, new FakeProbe());

        var (_, scan) = await service.AddFolderAsync(Path.Combine(_drive, "Filme"));

        Assert.Equal(1, scan.Added); // nur die MKV
        Assert.Equal(3, scan.Failures.Count);
        Assert.All(scan.Failures, f => Assert.Contains("DVD und Blu-ray", f.Error));
    }

    [Fact]
    public async Task A_disc_that_disappears_is_marked_missing_not_deleted()
    {
        await _service.AddFolderAsync(Path.Combine(_drive, "Filme"));

        Directory.Delete(Path.Combine(_drive, @"Filme\Heat (1995)"), recursive: true);
        var result = (await _service.RescanAllAsync()).Single().Result;

        Assert.Equal(1, result.MarkedMissing);
        Assert.Equal(4, _db.ListVideos().Count);
    }
}
