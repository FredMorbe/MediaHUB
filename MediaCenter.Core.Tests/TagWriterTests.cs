using System.Diagnostics;
using System.Text;
using MediaCenter.Core;

namespace MediaCenter.Core.Tests;

public sealed class TagChangesTests
{
    [Fact]
    public void Only_the_given_fields_are_listed_and_whitespace_is_trimmed()
    {
        var fields = new TagChanges(Title: "  Neu  ", Genre: "", Year: "2021").Fields;

        Assert.Equal(new[] { ("title", "Neu"), ("date", "2021"), ("genre", "") }, fields);
    }

    [Fact]
    public void Nothing_to_change_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new TagChanges().Validated());
    }

    [Theory]
    [InlineData("3")]
    [InlineData("03/12")]
    [InlineData("")]
    public void Valid_track_numbers_pass(string value) => new TagChanges(TrackNo: value).Validated();

    [Theory]
    [InlineData("drei")]
    [InlineData("3/")]
    [InlineData("12345")]
    public void Invalid_track_numbers_are_refused(string value)
    {
        Assert.Throws<ArgumentException>(() => new TagChanges(TrackNo: value).Validated());
        Assert.Throws<ArgumentException>(() => new TagChanges(DiscNo: value).Validated());
    }

    [Theory]
    [InlineData("2021")]
    [InlineData("2021-05")]
    [InlineData("2021-05-14")]
    [InlineData("")]
    public void Valid_years_pass(string value) => new TagChanges(Year: value).Validated();

    [Theory]
    [InlineData("21")]
    [InlineData("2021-5-1")]
    [InlineData("zweitausend")]
    public void Invalid_years_are_refused(string value) =>
        Assert.Throws<ArgumentException>(() => new TagChanges(Year: value).Validated());

    [Fact]
    public void Too_long_text_and_control_characters_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new TagChanges(Title: new string('x', 301)).Validated());
        Assert.Throws<ArgumentException>(() => new TagChanges(Title: "a\nb").Validated());
    }

    [Fact]
    public void Ogg_family_files_also_get_stream_level_tags_other_files_only_global_ones()
    {
        var mp3 = TagWriter.BuildArguments("a.mp3", "b.mp3", new TagChanges(Title: "X"));
        var opus = TagWriter.BuildArguments("a.opus", "b.opus", new TagChanges(Title: "X"));

        Assert.DoesNotContain("-metadata:s:a:0", mp3);
        Assert.Contains("-metadata:s:a:0", opus);
        Assert.Equal("b.mp3", mp3[^1]);
        Assert.Contains("copy", mp3);
    }
}

/// <summary>Läuft gegen das echte ffmpeg/ffprobe: Tags schreiben, ohne den Ton neu zu kodieren.</summary>
public sealed class TagWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mc-tags-" + Guid.NewGuid().ToString("N"));
    private readonly TagWriter _writer = TagWriter.FromEnvironment();
    private readonly FfprobeReader _probe = FfprobeReader.FromEnvironment();

    public TagWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static async Task<string> Run(string tool, params string[] args)
    {
        var psi = new ProcessStartInfo(tool)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, $"{tool} schlug fehl: {await stderr}");
        return await stdout;
    }

    private static string Ffmpeg => Environment.GetEnvironmentVariable("MEDIACENTER_FFMPEG") ?? "ffmpeg";

    private static string Ffprobe => Environment.GetEnvironmentVariable("MEDIACENTER_FFPROBE") ?? "ffprobe";

    private async Task<string> MakeTrack(string name, params string[] codecArgs)
    {
        var path = Path.Combine(_dir, name);
        var args = new List<string>
        {
            "-v", "error", "-y", "-f", "lavfi", "-i", "sine=f=440:d=2",
            "-metadata", "title=Alter Titel", "-metadata", "artist=Alter Künstler", "-metadata", "album=Altes Album",
            "-metadata", "genre=Rock", "-metadata", "date=1999", "-metadata", "track=2",
        };
        args.AddRange(codecArgs);
        args.Add(path);
        await Run(Ffmpeg, args.ToArray());
        return path;
    }

    private async Task<IReadOnlyDictionary<string, string>> TagsOf(string path) =>
        (await _probe.ProbeAsync(path)).Tags ?? new Dictionary<string, string>();

    private static string Tag(IReadOnlyDictionary<string, string> tags, string key) =>
        tags.TryGetValue(key, out var value) ? value : "";

    [Theory]
    [InlineData("a.mp3", "libmp3lame")]
    [InlineData("a.flac", "flac")]
    [InlineData("a.m4a", "aac")]
    [InlineData("a.ogg", "libvorbis")]
    [InlineData("a.opus", "libopus")]
    public async Task Changed_tags_are_written_and_the_others_stay(string name, string codec)
    {
        var path = await MakeTrack(name, "-c:a", codec);

        await _writer.WriteAsync(path, new TagChanges(Title: "Über Äpfel – Zürich", Artist: "Neue Band", AlbumArtist: "Neue Band", TrackNo: "7/12", Year: "2021"));

        var tags = await TagsOf(path);
        Assert.Equal("Über Äpfel – Zürich", Tag(tags, "title"));
        Assert.Equal("Neue Band", Tag(tags, "artist"));
        Assert.Equal("Neue Band", Tag(tags, "album_artist"));
        Assert.StartsWith("7", Tag(tags, "track"));
        Assert.StartsWith("2021", Tag(tags, "date"));
        Assert.Equal("Altes Album", Tag(tags, "album")); // nicht angefasst
        Assert.Equal("Rock", Tag(tags, "genre"));
    }

    [Theory]
    [InlineData("b.mp3", "libmp3lame")]
    [InlineData("b.flac", "flac")]
    [InlineData("b.m4a", "aac")]
    [InlineData("b.opus", "libopus")]
    public async Task An_empty_value_removes_the_tag(string name, string codec)
    {
        var path = await MakeTrack(name, "-c:a", codec);

        await _writer.WriteAsync(path, new TagChanges(Genre: "", Album: ""));

        var tags = await TagsOf(path);
        Assert.False(tags.ContainsKey("genre"), "genre sollte fehlen");
        Assert.False(tags.ContainsKey("album"), "album sollte fehlen");
        Assert.Equal("Alter Titel", Tag(tags, "title"));
    }

    [Fact]
    public async Task The_audio_is_copied_not_re_encoded()
    {
        var path = await MakeTrack("c.flac", "-c:a", "flac");
        var before = await Run(Ffmpeg, "-v", "error", "-i", path, "-map", "0:a", "-f", "md5", "-");

        await _writer.WriteAsync(path, new TagChanges(Title: "Anders"));

        var after = await Run(Ffmpeg, "-v", "error", "-i", path, "-map", "0:a", "-f", "md5", "-");
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task An_embedded_cover_survives_in_mp3_and_m4a()
    {
        var cover = Path.Combine(_dir, "cover.jpg");
        await Run(Ffmpeg, "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=0x3366aa:s=200x200", "-frames:v", "1", cover);
        foreach (var (name, codec) in new[] { ("d.mp3", "libmp3lame"), ("d.m4a", "aac") })
        {
            var path = Path.Combine(_dir, name);
            await Run(Ffmpeg, "-v", "error", "-y", "-f", "lavfi", "-i", "sine=f=440:d=2", "-i", cover, "-map", "0:a", "-map", "1:v",
                "-c:a", codec, "-c:v", "mjpeg", "-disposition:v", "attached_pic", "-metadata", "title=Mit Cover", path);

            await _writer.WriteAsync(path, new TagChanges(Title: "Immer noch mit Cover"));

            var streams = await Run(Ffprobe, "-v", "error", "-show_entries", "stream=codec_type", "-of", "csv=p=0", path);
            Assert.Contains("video", streams);
            Assert.Equal("Immer noch mit Cover", Tag(await TagsOf(path), "title"));
        }
    }

    [Fact]
    public async Task A_failure_leaves_the_file_untouched_and_no_temp_file_behind()
    {
        var path = Path.Combine(_dir, "kaputt.mp3");
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3, 4, 5 });
        var before = await File.ReadAllBytesAsync(path);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _writer.WriteAsync(path, new TagChanges(Title: "x")));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task A_locked_file_fails_loudly_and_stays_as_it_was()
    {
        var path = await MakeTrack("e.mp3", "-c:a", "libmp3lame");
        var before = await File.ReadAllBytesAsync(path);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAsync<IOException>(() => _writer.WriteAsync(path, new TagChanges(Title: "x")));
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(_dir, "e*"));
    }

    [Fact]
    public async Task A_missing_file_and_invalid_changes_are_refused_before_ffmpeg_runs()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => _writer.WriteAsync(Path.Combine(_dir, "gibt-es-nicht.mp3"), new TagChanges(Title: "x")));
        var path = await MakeTrack("f.mp3", "-c:a", "libmp3lame");
        await Assert.ThrowsAsync<ArgumentException>(() => _writer.WriteAsync(path, new TagChanges(Year: "bald")));
        await Assert.ThrowsAsync<ArgumentException>(() => _writer.WriteAsync(path, new TagChanges()));
    }
}
