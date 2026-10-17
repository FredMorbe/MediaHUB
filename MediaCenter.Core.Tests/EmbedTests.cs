using System.Diagnostics;
using System.Text;
using MediaCenter.Core;

namespace MediaCenter.Core.Tests;

/// <summary>Läuft gegen das echte ffmpeg/ffprobe: Untertitel in eine MKV einbetten.</summary>
public sealed class EmbedTests : IDisposable
{
    private static readonly Language German = Languages.Get("de");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mc-embed-" + Guid.NewGuid().ToString("N"));
    private readonly MkvSubtitleEmbedder _embedder = MkvSubtitleEmbedder.FromEnvironment();
    private readonly FfprobeReader _probe = FfprobeReader.FromEnvironment();

    public EmbedTests() => Directory.CreateDirectory(_dir);

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

    /// <summary>MKV mit Video, Ton, einem englischen Untertitel, Cover-Bild und Schrift als Anhang und zwei Kapiteln.</summary>
    private async Task<string> MakeVideo(string name = "film.mkv")
    {
        var cover = Path.Combine(_dir, "cover.jpg");
        await Run(Ffmpeg, "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=0x3366aa:s=200x300", "-frames:v", "1", cover);
        var srt = Path.Combine(_dir, "eng.srt");
        File.WriteAllText(srt, "1\n00:00:00,500 --> 00:00:01,500\nEnglish line\n");
        var meta = Path.Combine(_dir, "chapters.txt");
        File.WriteAllText(meta, ";FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=2000\ntitle=Eins\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=2000\nEND=4000\ntitle=Zwei\n");
        var font = Path.Combine(_dir, "Schrift.ttf");
        File.WriteAllBytes(font, Enumerable.Range(0, 2048).Select(i => (byte)(i * 7)).ToArray());
        var video = Path.Combine(_dir, name);
        await Run(Ffmpeg, "-v", "error", "-y",
            "-f", "lavfi", "-i", "testsrc=d=4:s=320x180:r=25", "-f", "lavfi", "-i", "sine=d=4", "-i", srt, "-i", meta,
            "-map", "0:v", "-map", "1:a", "-map", "2:0", "-map_metadata", "3", "-map_chapters", "3",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-c:s", "srt",
            "-metadata:s:s:0", "language=eng", "-metadata:s:a:0", "language=eng",
            "-attach", cover, "-metadata:s:t:0", "mimetype=image/jpeg", "-metadata:s:t:0", "filename=cover.jpg",
            "-attach", font, "-metadata:s:t:1", "mimetype=application/x-truetype-font", "-metadata:s:t:1", "filename=Schrift.ttf", video);
        return video;
    }

    private string Subtitle(string name, string content, Encoding? encoding = null)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, (encoding ?? new UTF8Encoding(false)).GetBytes(content));
        return path;
    }

    private static string Cue(string text = "Straße über Käse", string start = "00:00:01,000") => $"1\n{start} --> 00:00:02,500\n{text}\n";

    /// <summary>Text der n-ten Untertitelspur, von ffmpeg als SRT ausgegeben.</summary>
    private static Task<string> SubtitleTextOf(string video, int subtitleIndex) =>
        Run(Ffmpeg, "-v", "error", "-i", video, "-map", $"0:s:{subtitleIndex}", "-f", "srt", "pipe:1");

    private static void Discard(string path) => File.Delete(path);

    [Fact]
    public async Task Embedding_adds_the_subtitle_and_keeps_every_other_stream_chapters_and_attachment()
    {
        var video = await MakeVideo();
        var before = await _probe.ProbeAsync(video);
        var sub = Subtitle("Mein Film.srt", Cue());

        var result = await _embedder.EmbedAsync(video, sub, German, "Mein Film", 0, Discard);

        Assert.Equal(video, result.VideoPath);
        Assert.Equal(1, result.SubtitleStreamIndex); // nach dem vorhandenen englischen
        Assert.Null(result.OriginalLeftBehind);

        var after = await _probe.ProbeAsync(video);
        Assert.Equal(before.Streams.Count + 1, after.Streams.Count);
        // Alle ursprünglichen Streams sind noch da (die Cover rücken zu den Anhängen, die Reihenfolge darf sich dabei ändern)
        var addedStream = after.Streams.Last(s => s.Type == "subtitle");
        Assert.Equal(
            before.Streams.Select(s => (s.Type, s.Codec)).Order(),
            after.Streams.Where(s => s != addedStream).Select(s => (s.Type, s.Codec)).Order());
        var added = addedStream;
        Assert.Equal(("subtitle", "deu", "Mein Film"), (added.Type, added.Language, added.Title));
        // Cover bleibt ein Cover (kein zweiter Videostream), die Schrift bleibt ein Anhang, beide mit Namen und Typ wie zuvor
        Assert.Equal(1, after.Streams.Count(s => s.Type == FfprobeReader.AttachedPictureType));
        Assert.Equal(1, after.Streams.Count(s => s.Type == "video"));
        Assert.Contains(after.Streams, s => s.Type == "attachment" && s.Codec == "ttf");
        var names = await Run(Environment.GetEnvironmentVariable("MEDIACENTER_FFPROBE") ?? "ffprobe", "-v", "error", "-show_entries", "stream_tags=filename,mimetype", "-of", "csv=p=0", video);
        Assert.Contains("cover.jpg", names);
        Assert.Contains("Schrift.ttf", names);
        Assert.Contains("image/jpeg", names);
        Assert.Equal(2, JsonChapterCount(await Run(Environment.GetEnvironmentVariable("MEDIACENTER_FFPROBE") ?? "ffprobe", "-v", "error", "-print_format", "json", "-show_chapters", video)));
        Assert.Contains("Straße über Käse", await SubtitleTextOf(video, 1));
        Assert.Contains("English line", await SubtitleTextOf(video, 0));

        AssertNoLeftovers(video);
    }

    private static int JsonChapterCount(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("chapters").GetArrayLength();

    private static void AssertNoLeftovers(string video)
    {
        Assert.False(File.Exists(video + MkvSubtitleEmbedder.TempSuffix));
        Assert.False(File.Exists(video + MkvSubtitleEmbedder.OldSuffix));
        Assert.Equal(new[] { Path.GetFileName(video) }, Directory.GetFiles(Path.GetDirectoryName(video)!, "film.mkv*").Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_windows_1252_subtitle_keeps_its_umlauts()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var video = await MakeVideo();
        var sub = Subtitle("alt.srt", Cue(), Encoding.GetEncoding(1252));
        Assert.False(File.ReadAllBytes(sub).AsSpan().IndexOf((byte)0xDF) < 0); // wirklich nicht UTF-8 kodiert

        await _embedder.EmbedAsync(video, sub, German, null, 0, Discard);

        Assert.Contains("Straße über Käse", await SubtitleTextOf(video, 1));
    }

    [Fact]
    public async Task The_delay_is_baked_into_the_timestamps_of_the_embedded_track()
    {
        var video = await MakeVideo();
        var sub = Subtitle("a.srt", Cue(start: "00:00:01,000"));

        await _embedder.EmbedAsync(video, sub, German, null, 1.5, Discard);

        Assert.Contains("00:00:02,500 --> 00:00:04,000", await SubtitleTextOf(video, 1));
    }

    [Fact]
    public async Task An_ass_file_stays_ass()
    {
        var video = await MakeVideo();
        var ass = Subtitle("stil.ass", "[Script Info]\nScriptType: v4.00+\n\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\nStyle: Default,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,2,2,10,10,10,1\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,Hallo\n");

        await _embedder.EmbedAsync(video, ass, German, null, 0, Discard);

        Assert.Equal("ass", (await _probe.ProbeAsync(video)).Streams.Last(s => s.Type == "subtitle").Codec);
    }

    [Fact]
    public async Task Non_mkv_files_are_refused_and_untouched()
    {
        var mp4 = Path.Combine(_dir, "film.mp4");
        await Run(Ffmpeg, "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=d=1:s=160x90:r=10", "-c:v", "libx264", "-pix_fmt", "yuv420p", mp4);
        var bytes = File.ReadAllBytes(mp4);

        await Assert.ThrowsAsync<NotSupportedException>(() => _embedder.EmbedAsync(mp4, Subtitle("a.srt", Cue()), German, null, 0, Discard));

        Assert.Equal(bytes, File.ReadAllBytes(mp4));
        Assert.False(MkvSubtitleEmbedder.CanEmbedInto(mp4));
        Assert.True(MkvSubtitleEmbedder.CanEmbedInto("X.MKV"));
    }

    [Fact]
    public async Task Bad_subtitle_formats_and_delays_are_refused_before_anything_is_written()
    {
        var video = await MakeVideo();
        var bytes = File.ReadAllBytes(video);

        await Assert.ThrowsAsync<InvalidDataException>(() => _embedder.EmbedAsync(video, Subtitle("a.txt", "x"), German, null, 0, Discard));
        await Assert.ThrowsAsync<FileNotFoundException>(() => _embedder.EmbedAsync(video, Path.Combine(_dir, "fehlt.srt"), German, null, 0, Discard));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _embedder.EmbedAsync(video, Subtitle("a.srt", Cue()), German, null, 9999, Discard));

        Assert.Equal(bytes, File.ReadAllBytes(video));
        AssertNoLeftovers(video);
    }

    [Fact]
    public async Task A_leftover_old_file_from_an_earlier_attempt_stops_the_embedding_and_is_not_overwritten()
    {
        var video = await MakeVideo();
        var old = video + MkvSubtitleEmbedder.OldSuffix;
        File.WriteAllText(old, "vielleicht das Original");

        await Assert.ThrowsAsync<IOException>(() => _embedder.EmbedAsync(video, Subtitle("a.srt", Cue()), German, null, 0, Discard));

        Assert.Equal("vielleicht das Original", File.ReadAllText(old));
        Assert.Single((await _probe.ProbeAsync(video)).Streams, s => s.Type == "subtitle"); // Video unverändert
    }

    [Fact]
    public async Task When_the_original_cannot_be_discarded_the_new_file_is_still_in_place_and_the_old_path_is_reported()
    {
        var video = await MakeVideo();

        var result = await _embedder.EmbedAsync(video, Subtitle("a.srt", Cue()), German, null, 0, _ => throw new IOException("gesperrt"));

        Assert.Equal(video + MkvSubtitleEmbedder.OldSuffix, result.OriginalLeftBehind);
        Assert.True(File.Exists(result.OriginalLeftBehind));
        Assert.Equal(2, (await _probe.ProbeAsync(video)).Streams.Count(s => s.Type == "subtitle"));
    }

    [Fact]
    public async Task A_locked_video_fails_immediately_with_a_clear_message_and_nothing_is_written()
    {
        var video = await MakeVideo();
        using var openElsewhere = new FileStream(video, FileMode.Open, FileAccess.Read, FileShare.Read);

        var error = await Assert.ThrowsAsync<IOException>(() => _embedder.EmbedAsync(video, Subtitle("a.srt", Cue()), German, null, 0, Discard));

        Assert.Contains("gesperrt oder schreibgeschützt", error.Message);
        Assert.False(File.Exists(video + MkvSubtitleEmbedder.TempSuffix));
        Assert.False(File.Exists(video + MkvSubtitleEmbedder.OldSuffix));
    }

    [Fact]
    public async Task Cancelling_stops_ffmpeg_and_leaves_the_original_untouched()
    {
        var video = await MakeVideo();
        var bytes = File.ReadAllBytes(video);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _embedder.EmbedAsync(video, Subtitle("a.srt", Cue()), German, null, 0, Discard, ct: cts.Token));

        Assert.Equal(bytes, File.ReadAllBytes(video));
        AssertNoLeftovers(video);
    }

    [Fact]
    public async Task Progress_is_reported_up_to_one()
    {
        var video = await MakeVideo();
        double last = -1;
        var progress = new Progress<double>(v => last = v);

        await _embedder.EmbedAsync(video, Subtitle("a.srt", Cue()), German, null, 0, Discard, progress);
        await Task.Delay(100); // Progress<T> meldet asynchron

        Assert.Equal(1, last);
    }

    [Fact]
    public void Subtitle_text_is_read_as_utf8_with_or_without_bom_and_falls_back_to_windows_1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Assert.Equal("Straße", SubtitleText.ReadAsUtf8(Subtitle("u.srt", "Straße")));
        Assert.Equal("Straße", SubtitleText.ReadAsUtf8(Subtitle("b.srt", "﻿Straße")));
        Assert.Equal("Straße", SubtitleText.ReadAsUtf8(Subtitle("w.srt", "Straße", Encoding.GetEncoding(1252))));
        Assert.Equal("nur ascii", SubtitleText.ReadAsUtf8(Subtitle("a.srt", "nur ascii")));
    }
}
