using MediaCenter.Core;
using Microsoft.Data.Sqlite;
using static MediaCenter.Core.Tests.TestSupport;

namespace MediaCenter.Core.Tests;

public sealed class FileFingerprintTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-" + Guid.NewGuid().ToString("N"));

    public FileFingerprintTests() => Directory.CreateDirectory(_base);

    public void Dispose() => Directory.Delete(_base, recursive: true);

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_base, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] Bytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    [Fact]
    public void The_same_content_gives_the_same_fingerprint_under_any_name_and_place()
    {
        var content = Bytes(500_000, 1);
        var a = Write("a.mkv", content);
        Directory.CreateDirectory(Path.Combine(_base, "unter"));
        var b = Write(Path.Combine("unter", "ganz anders.mkv"), content);

        Assert.Equal(FileFingerprint.Compute(a), FileFingerprint.Compute(b));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(300_000)]
    [InlineData(250_000)]
    public void A_change_in_the_start_the_middle_or_the_end_changes_the_fingerprint(int position)
    {
        var content = Bytes(500_000, 2);
        var original = FileFingerprint.Compute(Write("o.mkv", content));

        var changed = (byte[])content.Clone();
        changed[position == 300_000 ? content.Length - 5 : position == 250_000 ? content.Length / 2 : position] ^= 0xFF;

        Assert.NotEqual(original, FileFingerprint.Compute(Write("c.mkv", changed)));
    }

    [Fact]
    public void A_different_length_changes_the_fingerprint_even_with_equal_blocks()
    {
        var shortFile = FileFingerprint.Compute(Write("s.bin", new byte[1000]));
        var longFile = FileFingerprint.Compute(Write("l.bin", new byte[1001]));

        Assert.NotEqual(shortFile, longFile);
    }

    [Fact]
    public void Small_and_empty_files_work()
    {
        Assert.NotEmpty(FileFingerprint.Compute(Write("e.bin", [])));
        Assert.NotEmpty(FileFingerprint.Compute(Write("k.bin", [1, 2, 3])));
    }

    [Fact]
    public void A_file_that_is_open_for_writing_elsewhere_can_still_be_read()
    {
        var path = Write("w.mkv", Bytes(10_000, 3));
        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        Assert.NotEmpty(FileFingerprint.Compute(path));
    }

    [Fact]
    public void A_missing_file_fails_loudly()
    {
        Assert.Throws<FileNotFoundException>(() => FileFingerprint.Compute(Path.Combine(_base, "nicht da.mkv")));
    }
}

public sealed class MovedFileTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-" + Guid.NewGuid().ToString("N"));
    private readonly FakeVolumes _volumes = new();
    private readonly LibraryDb _db;
    private readonly LibraryService _service;
    private readonly string _drive;
    private int _seed;

    public MovedFileTests()
    {
        Directory.CreateDirectory(_base);
        _db = new LibraryDb(Path.Combine(_base, "library.db"));
        _service = new LibraryService(_db, _volumes, new FakeProbe());
        _drive = Path.Combine(_base, "drive");
        Directory.CreateDirectory(_drive);
        _volumes.Mounted.Add(Vol("AAAA0001", _drive));
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_base, recursive: true);
    }

    /// <summary>Jede Datei bekommt anderen Inhalt, sonst hätten alle denselben Fingerabdruck.</summary>
    private string Movie(string rel)
    {
        var path = Path.Combine(_drive, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new byte[200_000];
        new Random(++_seed).NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    private long VideoOf(string name) => _db.ListVideos().Single(v => v.RelPath.EndsWith(name, StringComparison.OrdinalIgnoreCase)).VideoId;

    [Fact]
    public async Task A_moved_file_keeps_its_video_and_all_user_data()
    {
        var original = Movie(@"Filme\Alt\Heat (1995).mkv");
        Movie(@"Filme\Alien (1979).mkv");
        await _service.AddFolderAsync(Path.Combine(_drive, "Filme"));
        var heat = VideoOf("Heat (1995).mkv");
        _db.SavePlayback(heat, 1234, watched: false);
        _db.SaveSubtitleAdjustment(heat, new SubtitleAdjustment(2.5));

        Directory.CreateDirectory(Path.Combine(_drive, @"Filme\Neu"));
        File.Move(original, Path.Combine(_drive, @"Filme\Neu\Heat (1995).mkv"));
        var result = (await _service.RescanAllAsync()).Single().Result;

        Assert.Equal(1, result.Moved);
        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.MarkedMissing);
        var now = _db.ListVideos().Single(v => v.ParsedTitle == "Heat");
        Assert.Equal(heat, now.VideoId);
        Assert.False(now.Missing);
        Assert.Equal(@"Filme\Neu\Heat (1995).mkv", now.RelPath);
        Assert.Equal(1234, _db.GetPlayback(heat)!.PositionSeconds);
        Assert.Equal(2.5, _db.GetSubtitleAdjustment(heat).DelaySeconds);
        Assert.Equal(2, _db.ListVideos().Count);
    }

    [Fact]
    public async Task A_moved_and_renamed_file_is_recognised_by_its_content()
    {
        var original = Movie(@"Filme\heat.mkv");
        await _service.AddFolderAsync(Path.Combine(_drive, "Filme"));
        var video = VideoOf("heat.mkv");
        _db.SavePlayback(video, 50, watched: true);

        File.Move(original, Path.Combine(_drive, @"Filme\Heat (1995) 1080p.mkv"));
        await _service.RescanAllAsync();

        var only = Assert.Single(_db.ListVideos());
        Assert.Equal(video, only.VideoId);
        Assert.True(_db.GetPlayback(video)!.Watched);
    }

    [Fact]
    public async Task A_copy_is_a_new_entry_while_the_original_is_still_there()
    {
        var original = Movie(@"Filme\Heat (1995).mkv");
        await _service.AddFolderAsync(Path.Combine(_drive, "Filme"));

        File.Copy(original, Path.Combine(_drive, @"Filme\Heat Kopie (1995).mkv"));
        var result = (await _service.RescanAllAsync()).Single().Result;

        Assert.Equal(0, result.Moved);
        Assert.Equal(1, result.Added);
        Assert.Equal(2, _db.ListVideos().Count);
    }

    [Fact]
    public async Task A_file_moved_from_one_source_to_another_is_found_in_either_scan_order()
    {
        var original = Movie(@"Eins\Heat (1995).mkv");
        Directory.CreateDirectory(Path.Combine(_drive, "Zwei"));
        await _service.AddFolderAsync(Path.Combine(_drive, "Eins"));
        await _service.AddFolderAsync(Path.Combine(_drive, "Zwei"));
        var video = VideoOf("Heat (1995).mkv");
        _db.SavePlayback(video, 77, watched: false);

        File.Move(original, Path.Combine(_drive, @"Zwei\Heat (1995).mkv"));
        var results = await _service.RescanAllAsync();

        Assert.Equal(1, results.Sum(r => r.Result.Moved));
        var only = Assert.Single(_db.ListVideos());
        Assert.Equal(video, only.VideoId);
        Assert.False(only.Missing);
        Assert.Equal(77, _db.GetPlayback(video)!.PositionSeconds);
    }

    [Fact]
    public async Task A_file_whose_old_drive_is_not_connected_is_not_taken_over()
    {
        var original = Movie(@"Filme\Heat (1995).mkv");
        await _service.AddFolderAsync(Path.Combine(_drive, "Filme"));

        var other = Path.Combine(_base, "other");
        Directory.CreateDirectory(other);
        File.Copy(original, Path.Combine(other, "Heat (1995).mkv"));
        _volumes.Mounted.Clear(); // das Laufwerk mit dem Original ist weg …
        _volumes.Mounted.Add(Vol("BBBB0002", other));
        await _service.AddFolderAsync(other); // … die Kopie liegt auf einem anderen

        Assert.Equal(2, _db.ListVideos().Count);
    }

    [Fact]
    public async Task Existing_files_get_their_fingerprint_on_the_next_scan()
    {
        Movie(@"Filme\Heat (1995).mkv");
        await _service.AddFolderAsync(Path.Combine(_drive, "Filme"));
        Assert.Empty(_db.GetFileIdsWithoutFingerprint());

        using (var connection = new SqliteConnection($"Data Source={Path.Combine(_base, "library.db")}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE files SET fingerprint = NULL";
            cmd.ExecuteNonQuery();
        }
        Assert.Single(_db.GetFileIdsWithoutFingerprint());

        var result = (await _service.RescanAllAsync()).Single().Result;

        Assert.Equal(1, result.Unchanged);
        Assert.Empty(_db.GetFileIdsWithoutFingerprint());
    }

    [Fact]
    public void Moving_onto_a_path_of_another_file_is_refused()
    {
        Movie(@"Filme\A (2000).mkv");
        Movie(@"Filme\B (2001).mkv");
        _service.AddFolderAsync(Path.Combine(_drive, "Filme")).GetAwaiter().GetResult();
        var files = _db.ListVideos();

        var ex = Assert.Throws<InvalidOperationException>(() => _db.MoveFile(files[0].FileId, files[1].VolumeId, files[1].RelPath));
        Assert.Contains("gehört schon einer anderen Datei", ex.Message);
    }
}
