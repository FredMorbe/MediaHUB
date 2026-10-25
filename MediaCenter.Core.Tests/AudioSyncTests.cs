using Microsoft.Data.Sqlite;
using MediaCenter.Core;
using static MediaCenter.Core.Tests.TestSupport;

namespace MediaCenter.Core.Tests;

public sealed class AudioSyncTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-asy-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryDb _db;
    private readonly LibraryService _service;
    private readonly string _drive;

    public AudioSyncTests()
    {
        _drive = Path.Combine(_base, "drive");
        Touch(_drive, @"Filme\Solo (2020).mkv");
        Touch(_drive, @"Filme\Duo (2021).mkv");
        var volumes = new FakeVolumes();
        volumes.Mounted.Add(Vol("AAAA0001", _drive));
        _db = new LibraryDb(Path.Combine(_base, "library.db"));
        _service = new LibraryService(_db, volumes, new FakeProbe());
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_base, recursive: true);
    }

    private long VideoId(string title) => _db.ListVideos().Single(v => v.TitleName == title).VideoId;

    [Fact]
    public async Task Without_a_saved_row_the_audio_is_not_shifted()
    {
        await _service.AddFolderAsync(_drive);

        Assert.Equal(0, _db.GetAudioDelay(VideoId("Solo")));
    }

    [Fact]
    public async Task The_delay_is_saved_per_video_and_replaced_on_change()
    {
        await _service.AddFolderAsync(_drive);
        var solo = VideoId("Solo");
        var duo = VideoId("Duo");

        _db.SaveAudioDelay(solo, -0.35);
        _db.SaveAudioDelay(duo, 0.1);
        _db.SaveAudioDelay(solo, -0.4);

        Assert.Equal(-0.4, _db.GetAudioDelay(solo));
        Assert.Equal(0.1, _db.GetAudioDelay(duo));
    }

    [Fact]
    public async Task Saving_zero_removes_the_row()
    {
        await _service.AddFolderAsync(_drive);
        var solo = VideoId("Solo");
        _db.SaveAudioDelay(solo, 0.5);

        _db.SaveAudioDelay(solo, 0);

        Assert.Equal(0, _db.GetAudioDelay(solo));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(-30.5)]
    [InlineData(double.NaN)]
    public async Task Values_out_of_range_throw_instead_of_being_clamped(double seconds)
    {
        await _service.AddFolderAsync(_drive);

        Assert.Throws<ArgumentOutOfRangeException>(() => _db.SaveAudioDelay(VideoId("Solo"), seconds));
    }

    [Fact]
    public void The_keys_are_free_and_unique()
    {
        var map = PlayerKeys.Resolve(null);

        Assert.Equal(PlayerAction.AudioEarlier, map["G"]);
        Assert.Equal(PlayerAction.AudioLater, map["H"]);
    }
}
