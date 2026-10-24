using MediaCenter.Core;

namespace MediaCenter.Core.Tests;

public sealed class FolderWatcherTests : IDisposable
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mc-" + Guid.NewGuid().ToString("N"));
    private readonly FolderWatcher _watcher = new(Settle);
    private readonly List<IReadOnlyList<long>> _batches = new();

    public FolderWatcherTests()
    {
        Directory.CreateDirectory(_root);
        _watcher.Changed += ids =>
        {
            lock (_batches) _batches.Add(ids);
        };
    }

    public void Dispose()
    {
        _watcher.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private int Batches
    {
        get
        {
            lock (_batches) return _batches.Count;
        }
    }

    private async Task<bool> WaitForAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    [Theory]
    [InlineData(@"C:\Filme\Film.mkv", true)]
    [InlineData(@"C:\Filme\Lied.FLAC", true)]
    [InlineData(@"C:\Filme\Staffel 1", true)]
    [InlineData(@"C:\Filme\Dr. Who", true)]
    [InlineData(@"C:\Filme\Film (2010)", true)]
    [InlineData(@"C:\Filme\Film.mkv.part", false)]
    [InlineData(@"C:\Filme\Film.tmp", false)]
    [InlineData(@"C:\Filme\Film.srt", false)]
    [InlineData(@"C:\Filme\poster.jpg", false)]
    public void Relevance_follows_media_extensions_and_folder_looking_names(string path, bool relevant) =>
        Assert.Equal(relevant, FolderWatcher.IsRelevant(path));

    [Fact]
    public async Task New_media_file_is_reported_once_after_the_settle_time_with_the_source_id()
    {
        Assert.Empty(_watcher.Watch(new[] { new WatchTarget(7, _root) }));

        // ein „Kopiervorgang“: viele Schreibzugriffe kurz hintereinander
        var path = Path.Combine(_root, "Film.mkv");
        await using (var stream = File.Create(path))
        {
            for (var i = 0; i < 5; i++)
            {
                await stream.WriteAsync(new byte[1024]);
                await stream.FlushAsync();
                await Task.Delay(40);
            }
        }

        Assert.True(await WaitForAsync(() => Batches > 0));
        await Task.Delay(Settle * 2);
        Assert.Equal(1, Batches);
        Assert.Equal(new long[] { 7 }, _batches[0]);
    }

    [Fact]
    public async Task Files_in_subfolders_deletions_and_renames_are_reported()
    {
        var sub = Path.Combine(_root, "Serie", "Staffel 1");
        Directory.CreateDirectory(sub);
        var file = Path.Combine(sub, "S01E01.mkv");
        File.WriteAllText(file, "x");
        _watcher.Watch(new[] { new WatchTarget(1, _root) });

        File.Move(file, Path.Combine(sub, "S01E02.mkv"));
        Assert.True(await WaitForAsync(() => Batches >= 1));

        var before = Batches;
        File.Delete(Path.Combine(sub, "S01E02.mkv"));
        Assert.True(await WaitForAsync(() => Batches > before));
    }

    [Fact]
    public async Task Deleting_a_folder_is_reported_even_though_only_the_folder_is_named()
    {
        var folder = Path.Combine(_root, "Dr. Who");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "S01E01.mkv"), "x");
        _watcher.Watch(new[] { new WatchTarget(3, _root) });

        Directory.Delete(folder, recursive: true);

        Assert.True(await WaitForAsync(() => Batches > 0));
        Assert.Contains(3L, _batches.SelectMany(b => b));
    }

    [Fact]
    public async Task Irrelevant_files_are_ignored()
    {
        _watcher.Watch(new[] { new WatchTarget(1, _root) });

        File.WriteAllText(Path.Combine(_root, "Film.srt"), "x");
        File.WriteAllText(Path.Combine(_root, "Film.mkv.part"), "x");
        File.WriteAllText(Path.Combine(_root, "folder.jpg"), "x");

        await Task.Delay(Settle * 4);
        Assert.Equal(0, Batches);
    }

    [Fact]
    public async Task Changes_in_two_sources_come_in_one_batch_with_both_ids()
    {
        var other = Path.Combine(Path.GetTempPath(), "mc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);
        try
        {
            _watcher.Watch(new[] { new WatchTarget(1, _root), new WatchTarget(2, other) });

            File.WriteAllText(Path.Combine(_root, "a.mkv"), "x");
            File.WriteAllText(Path.Combine(other, "b.mkv"), "x");

            Assert.True(await WaitForAsync(() => _batches.SelectMany(b => b).Distinct().Count() == 2));
        }
        finally
        {
            _watcher.Stop();
            Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public void A_folder_that_does_not_exist_is_returned_as_failure_and_the_others_are_watched()
    {
        var failures = _watcher.Watch(new[] { new WatchTarget(1, Path.Combine(_root, "gibt-es-nicht")), new WatchTarget(2, _root) });

        var failure = Assert.Single(failures);
        Assert.Equal(1, failure.Target.SourceId);
        Assert.NotEmpty(failure.Error);
        Assert.Equal(1, _watcher.Count);
    }

    [Fact]
    public async Task Stop_discards_pending_changes_and_watch_replaces_the_targets()
    {
        _watcher.Watch(new[] { new WatchTarget(1, _root) });
        File.WriteAllText(Path.Combine(_root, "a.mkv"), "x");
        _watcher.Stop();

        await Task.Delay(Settle * 3);
        Assert.Equal(0, Batches);
        Assert.Equal(0, _watcher.Count);

        _watcher.Watch(new[] { new WatchTarget(5, _root) });
        File.WriteAllText(Path.Combine(_root, "b.mkv"), "x");
        Assert.True(await WaitForAsync(() => Batches > 0));
        Assert.Equal(new long[] { 5 }, _batches[0]);
    }
}
