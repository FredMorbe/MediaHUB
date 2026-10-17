using System.Diagnostics;
using System.Globalization;
using System.Text;
using MediaCenter.Core;

namespace MediaCenter.Core.Tests;

/// <summary>Läuft gegen das echte ffmpeg/ffprobe: MKV-Spuren entfernen, hinzufügen, ändern und verschieben.</summary>
public sealed class MkvEditorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mc-edit-" + Guid.NewGuid().ToString("N"));
    private readonly MkvEditor _editor = MkvEditor.FromEnvironment();
    private readonly FfprobeReader _probe = FfprobeReader.FromEnvironment();

    public MkvEditorTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Ffmpeg => Environment.GetEnvironmentVariable("MEDIACENTER_FFMPEG") ?? "ffmpeg";

    private static string Ffprobe => Environment.GetEnvironmentVariable("MEDIACENTER_FFPROBE") ?? "ffprobe";

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

    /// <summary>
    /// MKV: Video; Ton 0 = Englisch „Original“ (Standard), Ton 1 = Deutsch „Synchron“; Untertitel 0 = Englisch (Standard),
    /// Untertitel 1 = Deutsch, erzwungen; dazu Cover, Schrift und zwei Kapitel.
    /// </summary>
    private async Task<string> MakeVideo(string name = "film.mkv")
    {
        var cover = Path.Combine(_dir, "cover.jpg");
        await Run(Ffmpeg, "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=0x3366aa:s=200x300", "-frames:v", "1", cover);
        var eng = Path.Combine(_dir, "eng.srt");
        File.WriteAllText(eng, "1\n00:00:01,000 --> 00:00:02,000\nEnglish line\n");
        var deu = Path.Combine(_dir, "deu.srt");
        File.WriteAllText(deu, "1\n00:00:01,000 --> 00:00:02,000\nDeutsche Zeile\n");
        var meta = Path.Combine(_dir, "chapters.txt");
        File.WriteAllText(meta, ";FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=2000\ntitle=Eins\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=2000\nEND=4000\ntitle=Zwei\n");
        var font = Path.Combine(_dir, "Schrift.ttf");
        File.WriteAllBytes(font, Enumerable.Range(0, 2048).Select(i => (byte)(i * 7)).ToArray());
        var video = Path.Combine(_dir, name);
        await Run(Ffmpeg, "-v", "error", "-y",
            "-f", "lavfi", "-i", "testsrc=d=4:s=320x180:r=25", "-f", "lavfi", "-i", "sine=f=440:d=4", "-f", "lavfi", "-i", "sine=f=880:d=4",
            "-i", eng, "-i", deu, "-i", meta,
            "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:0", "-map", "4:0", "-map_metadata", "5", "-map_chapters", "5",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-c:s", "srt",
            "-metadata:s:a:0", "language=eng", "-metadata:s:a:0", "title=Original", "-metadata:s:a:1", "language=deu", "-metadata:s:a:1", "title=Synchron",
            "-metadata:s:s:0", "language=eng", "-metadata:s:s:1", "language=deu",
            "-disposition:a:0", "default", "-disposition:a:1", "0", "-disposition:s:0", "default", "-disposition:s:1", "forced",
            "-attach", cover, "-metadata:s:t:0", "mimetype=image/jpeg", "-metadata:s:t:0", "filename=cover.jpg",
            "-attach", font, "-metadata:s:t:1", "mimetype=application/x-truetype-font", "-metadata:s:t:1", "filename=Schrift.ttf", video);
        return video;
    }

    private async Task<string> MakeAudio(string name, double frequency = 660, string codec = "ac3")
    {
        var path = Path.Combine(_dir, name);
        await Run(Ffmpeg, "-v", "error", "-y", "-f", "lavfi", "-i", $"sine=f={frequency.ToString(CultureInfo.InvariantCulture)}:d=4", "-c:a", codec, path);
        return path;
    }

    private string Subtitle(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static void Discard(string path) => File.Delete(path);

    private static MkvEditPlan Plan(IReadOnlyList<MkvTrackEdit>? tracks = null, IReadOnlyList<MkvAddedTrack>? added = null, string? copyTo = null) =>
        new(tracks, added ?? Array.Empty<MkvAddedTrack>(), copyTo);

    /// <summary>Streams ohne Anhänge und Cover, in Dateireihenfolge.</summary>
    private async Task<IReadOnlyList<StreamInfo>> Tracks(string file) =>
        (await _probe.ProbeAsync(file)).Streams.Where(s => s.Type is "video" or "audio" or "subtitle").ToList();

    private static async Task<string> SubtitleTextOf(string video, int subtitleIndex) =>
        await Run(Ffmpeg, "-v", "error", "-i", video, "-map", $"0:s:{subtitleIndex}", "-f", "srt", "pipe:1");

    /// <summary>Beginn der Spur gegenüber dem Beginn des Videos, in Sekunden.</summary>
    private static async Task<double> StartRelativeToVideo(string file, string selector)
    {
        double Start(string s) => double.Parse(
            Run(Ffprobe, "-v", "error", "-select_streams", s, "-show_entries", "stream=start_time", "-of", "csv=p=0", file).GetAwaiter().GetResult().Trim().Split('\n')[0],
            CultureInfo.InvariantCulture);
        return await Task.Run(() => Start(selector) - Start("v:0"));
    }

    private async Task AssertSharedPartsKept(string file)
    {
        var probe = await _probe.ProbeAsync(file);
        Assert.Equal(1, probe.Streams.Count(s => s.Type == "video"));
        Assert.Equal(1, probe.Streams.Count(s => s.Type == FfprobeReader.AttachedPictureType));
        Assert.Contains(probe.Streams, s => s.Type == "attachment" && s.Codec == "ttf");
        var names = await Run(Ffprobe, "-v", "error", "-show_entries", "stream_tags=filename,mimetype", "-of", "csv=p=0", file);
        Assert.Contains("cover.jpg", names);
        Assert.Contains("Schrift.ttf", names);
        Assert.Contains("image/jpeg", names);
        var chapters = await Run(Ffprobe, "-v", "error", "-print_format", "json", "-show_chapters", file);
        Assert.Equal(2, System.Text.Json.JsonDocument.Parse(chapters).RootElement.GetProperty("chapters").GetArrayLength());
    }

    private static void AssertNoLeftovers(string video)
    {
        Assert.False(File.Exists(video + MkvEditor.TempSuffix));
        Assert.False(File.Exists(video + MkvEditor.OldSuffix));
    }

    // --- Entfernen -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Removing_an_audio_and_a_subtitle_track_keeps_everything_else()
    {
        var video = await MakeVideo();
        var before = await Tracks(video);   // 0 Video, 1 Ton eng, 2 Ton deu, 3 Untertitel eng, 4 Untertitel deu
        var tracks = before.Select(s => new MkvTrackEdit(s.Index, Remove: s.Index is 1 or 4)).ToList();

        var result = await _editor.EditAsync(video, Plan(tracks), Discard);

        Assert.Equal(video, result.VideoPath);
        Assert.Null(result.OriginalLeftBehind);
        var after = await Tracks(video);
        Assert.Equal(new[] { "video", "audio", "subtitle" }, after.Select(s => s.Type));
        var audio = after.Single(s => s.Type == "audio");
        Assert.Equal(("deu", "Synchron"), (audio.Language, audio.Title));
        var subtitle = after.Single(s => s.Type == "subtitle");
        Assert.Equal("eng", subtitle.Language);
        Assert.Contains("English line", await SubtitleTextOf(video, 0));
        await AssertSharedPartsKept(video);
        AssertNoLeftovers(video);
    }

    [Fact]
    public async Task The_video_track_cannot_be_removed_and_nothing_is_written()
    {
        var video = await MakeVideo();
        var bytes = File.ReadAllBytes(video);
        var tracks = (await Tracks(video)).Select(s => new MkvTrackEdit(s.Index, Remove: s.Type == "video")).ToList();

        await Assert.ThrowsAsync<ArgumentException>(() => _editor.EditAsync(video, Plan(tracks), Discard));

        Assert.Equal(bytes, File.ReadAllBytes(video));
        AssertNoLeftovers(video);
    }

    // --- Eigenschaften und Reihenfolge ---------------------------------------------------------------------

    [Fact]
    public async Task Language_title_and_flags_can_be_changed_and_a_title_cleared()
    {
        var video = await MakeVideo();
        var tracks = (await Tracks(video)).Select(s => s.Index switch
        {
            1 => new MkvTrackEdit(1, Language: "jpn", Title: "Japanisch", IsDefault: false),
            2 => new MkvTrackEdit(2, Title: "", IsDefault: true),
            4 => new MkvTrackEdit(4, IsForced: false),
            _ => new MkvTrackEdit(s.Index),
        }).ToList();

        await _editor.EditAsync(video, Plan(tracks), Discard);

        var after = await Tracks(video);
        var first = after[1];
        Assert.Equal(("jpn", "Japanisch", false), (first.Language, first.Title, first.IsDefault));
        var second = after[2];
        Assert.Equal(("deu", null, true), (second.Language, second.Title, second.IsDefault));
        Assert.False(after[4].IsForced);
        Assert.True(after[3].IsDefault);
        await AssertSharedPartsKept(video);
    }

    [Fact]
    public async Task Tracks_can_be_reordered()
    {
        var video = await MakeVideo();
        var order = new[] { 0, 2, 1, 4, 3 };   // Ton und Untertitel vertauscht
        var tracks = order.Select(i => new MkvTrackEdit(i)).ToList();

        await _editor.EditAsync(video, Plan(tracks), Discard);

        var after = await Tracks(video);
        Assert.Equal(new[] { "deu", "eng" }, after.Where(s => s.Type == "audio").Select(s => s.Language));
        Assert.Equal(new[] { "deu", "eng" }, after.Where(s => s.Type == "subtitle").Select(s => s.Language));
        Assert.Contains("Deutsche Zeile", await SubtitleTextOf(video, 0));
        Assert.Contains("English line", await SubtitleTextOf(video, 1));
        await AssertSharedPartsKept(video);
    }

    // --- Hinzufügen ----------------------------------------------------------------------------------------

    [Fact]
    public async Task An_audio_file_and_a_subtitle_file_are_added_with_language_title_and_flags()
    {
        var video = await MakeVideo();
        var audio = await MakeAudio("Kommentar.ac3");
        var sub = Subtitle("fr.srt", "1\n00:00:01,000 --> 00:00:02,000\nLigne française\n");
        var added = new[]
        {
            new MkvAddedTrack(audio, MkvAddKind.Audio, "fra", "Kommentar"),
            new MkvAddedTrack(sub, MkvAddKind.Subtitle, "fra", "Französisch", IsForced: true),
        };

        await _editor.EditAsync(video, Plan(added: added), Discard);

        var after = await Tracks(video);
        Assert.Equal(new[] { "video", "audio", "audio", "audio", "subtitle", "subtitle", "subtitle" }, after.Select(s => s.Type));
        var newAudio = after.Where(s => s.Type == "audio").Last();
        Assert.Equal(("ac3", "fra", "Kommentar", false), (newAudio.Codec, newAudio.Language, newAudio.Title, newAudio.IsDefault));
        var newSub = after.Where(s => s.Type == "subtitle").Last();
        Assert.Equal(("fra", "Französisch", true), (newSub.Language, newSub.Title, newSub.IsForced));
        Assert.Contains("Ligne française", await SubtitleTextOf(video, 2));
        // die vorhandenen Spuren sind unverändert
        Assert.Equal(("eng", "Original", true), (after[1].Language, after[1].Title, after[1].IsDefault));
        await AssertSharedPartsKept(video);
        AssertNoLeftovers(video);
    }

    [Fact]
    public async Task An_added_track_can_be_placed_first_or_in_the_middle()
    {
        var video = await MakeVideo();
        var first = await MakeAudio("erste.ac3", 300);
        var middle = await MakeAudio("mitte.ac3", 500);
        var added = new[]
        {
            new MkvAddedTrack(middle, MkvAddKind.Audio, "fra", "Mitte", Position: 2),
            new MkvAddedTrack(first, MkvAddKind.Audio, "ita", "Erste", IsDefault: true, Position: 0),
            new MkvAddedTrack(Subtitle("p.srt", "1\n00:00:01,000 --> 00:00:02,000\nVorn\n"), MkvAddKind.Subtitle, "spa", "Vorn", Position: 0),
        };
        var tracks = (await Tracks(video)).Select(s => new MkvTrackEdit(s.Index, IsDefault: s.Type == "audio" ? false : null)).ToList();

        await _editor.EditAsync(video, Plan(tracks, added), Discard);

        var after = await Tracks(video);
        Assert.Equal(new[] { "Erste", "Original", "Mitte", "Synchron" }, after.Where(s => s.Type == "audio").Select(s => s.Title));
        Assert.Equal(new[] { "ita", "eng", "fra", "deu" }, after.Where(s => s.Type == "audio").Select(s => s.Language));
        Assert.Equal(new[] { true, false, false, false }, after.Where(s => s.Type == "audio").Select(s => s.IsDefault));
        Assert.Equal("spa", after.First(s => s.Type == "subtitle").Language);
        Assert.Contains("Vorn", await SubtitleTextOf(video, 0));
        await AssertSharedPartsKept(video);
    }

    [Fact]
    public async Task Tracks_can_be_taken_from_another_container_by_stream_index()
    {
        var video = await MakeVideo();
        var other = Path.Combine(_dir, "andere.mkv");
        var otherSub = Subtitle("it.srt", "1\n00:00:01,000 --> 00:00:02,000\nRiga italiana\n");
        await Run(Ffmpeg, "-v", "error", "-y",
            "-f", "lavfi", "-i", "testsrc=d=4:s=160x90:r=25", "-f", "lavfi", "-i", "sine=f=300:d=4", "-f", "lavfi", "-i", "sine=f=500:d=4", "-i", otherSub,
            "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:0", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-c:s", "srt", other);
        var added = new[]
        {
            new MkvAddedTrack(other, MkvAddKind.Audio, "ita", "Zweite Tonspur", SourceStreamIndex: 2),
            new MkvAddedTrack(other, MkvAddKind.Subtitle, "ita", "Aus anderer Datei"),
        };

        await _editor.EditAsync(video, Plan(added: added), Discard);

        var after = await Tracks(video);
        Assert.Equal(("audio", "ita", "Zweite Tonspur"), (after[3].Type, after[3].Language, after[3].Title));
        Assert.Equal(("subtitle", "ita", "Aus anderer Datei"), (after[6].Type, after[6].Language, after[6].Title));
        Assert.Contains("Riga italiana", await SubtitleTextOf(video, 2));
    }

    [Fact]
    public async Task A_stream_of_the_wrong_kind_or_a_missing_stream_is_refused_before_anything_is_written()
    {
        var video = await MakeVideo();
        var bytes = File.ReadAllBytes(video);
        var audio = await MakeAudio("a.ac3");

        await Assert.ThrowsAsync<InvalidDataException>(() => _editor.EditAsync(video, Plan(added: new[] { new MkvAddedTrack(audio, MkvAddKind.Subtitle, "deu") }), Discard));
        await Assert.ThrowsAsync<InvalidDataException>(() => _editor.EditAsync(video, Plan(added: new[] { new MkvAddedTrack(audio, MkvAddKind.Audio, "deu", SourceStreamIndex: 7) }), Discard));
        await Assert.ThrowsAsync<InvalidDataException>(() => _editor.EditAsync(video, Plan(added: new[] { new MkvAddedTrack(Subtitle("x.txt", "kein Untertitel"), MkvAddKind.Subtitle, "deu") }), Discard));
        await Assert.ThrowsAsync<FileNotFoundException>(() => _editor.EditAsync(video, Plan(added: new[] { new MkvAddedTrack(Path.Combine(_dir, "fehlt.ac3"), MkvAddKind.Audio, "deu") }), Discard));

        Assert.Equal(bytes, File.ReadAllBytes(video));
        AssertNoLeftovers(video);
    }

    // --- Sync ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1.0)]
    [InlineData(-0.5)]
    public async Task An_existing_audio_track_is_shifted_permanently(double delay)
    {
        var video = await MakeVideo();
        var tracks = (await Tracks(video)).Select(s => new MkvTrackEdit(s.Index, DelaySeconds: s.Index == 2 ? delay : 0)).ToList();
        var untouchedBefore = await StartRelativeToVideo(video, "a:0");

        await _editor.EditAsync(video, Plan(tracks), Discard);

        Assert.Equal(untouchedBefore, await StartRelativeToVideo(video, "a:0"), 0.05);
        Assert.Equal(delay, await StartRelativeToVideo(video, "a:1"), 0.1);
        await AssertSharedPartsKept(video);
    }

    [Fact]
    public async Task An_existing_subtitle_track_is_shifted_and_the_other_one_is_not()
    {
        var video = await MakeVideo();
        var tracks = (await Tracks(video)).Select(s => new MkvTrackEdit(s.Index, DelaySeconds: s.Index == 4 ? 1.5 : 0)).ToList();

        await _editor.EditAsync(video, Plan(tracks), Discard);

        Assert.Contains("00:00:01,000 --> 00:00:02,000", await SubtitleTextOf(video, 0));
        Assert.Contains("00:00:02,500 --> 00:00:03,500", await SubtitleTextOf(video, 1));
    }

    [Fact]
    public async Task A_subtitle_can_be_shifted_earlier_and_an_added_audio_can_be_shifted()
    {
        var video = await MakeVideo();
        var audio = await MakeAudio("spaet.ac3");
        var tracks = (await Tracks(video)).Select(s => new MkvTrackEdit(s.Index, DelaySeconds: s.Index == 3 ? -0.5 : 0)).ToList();

        await _editor.EditAsync(video, Plan(tracks, new[] { new MkvAddedTrack(audio, MkvAddKind.Audio, "deu", DelaySeconds: 0.8) }), Discard);

        Assert.Contains("00:00:00,500 --> 00:00:01,500", await SubtitleTextOf(video, 0));
        Assert.Equal(0.8, await StartRelativeToVideo(video, "a:2"), 0.1);
    }

    [Fact]
    public async Task The_video_and_attachments_cannot_be_shifted_or_renamed()
    {
        var video = await MakeVideo();
        var bytes = File.ReadAllBytes(video);

        var shifted = (await Tracks(video)).Select(s => new MkvTrackEdit(s.Index, DelaySeconds: s.Type == "video" ? 1 : 0)).ToList();
        await Assert.ThrowsAsync<ArgumentException>(() => _editor.EditAsync(video, Plan(shifted), Discard));
        var tooFar = (await Tracks(video)).Select(s => new MkvTrackEdit(s.Index, DelaySeconds: s.Index == 1 ? 9999 : 0)).ToList();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _editor.EditAsync(video, Plan(tooFar), Discard));

        Assert.Equal(bytes, File.ReadAllBytes(video));
    }

    // --- Kopie ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_copy_leaves_the_original_untouched_and_carries_the_changes()
    {
        var video = await MakeVideo();
        var bytes = File.ReadAllBytes(video);
        var copy = Path.Combine(_dir, "film (bearbeitet).mkv");
        var tracks = (await Tracks(video)).Select(s => new MkvTrackEdit(s.Index, Remove: s.Index == 2)).ToList();

        var result = await _editor.EditAsync(video, Plan(tracks, copyTo: copy), _ => Assert.Fail("das Original darf nicht angefasst werden"));

        Assert.Equal(copy, result.VideoPath);
        Assert.Equal(bytes, File.ReadAllBytes(video));
        Assert.Equal(1, (await Tracks(copy)).Count(s => s.Type == "audio"));
        Assert.Equal(2, (await Tracks(video)).Count(s => s.Type == "audio"));
        await AssertSharedPartsKept(copy);
        AssertNoLeftovers(video);
        AssertNoLeftovers(copy);
    }

    [Fact]
    public async Task A_copy_never_overwrites_an_existing_file_or_the_original()
    {
        var video = await MakeVideo();
        var existing = Path.Combine(_dir, "da.mkv");
        File.WriteAllText(existing, "bleibt");
        var tracks = (await Tracks(video)).Select(s => new MkvTrackEdit(s.Index, Remove: s.Index == 2)).ToList();

        await Assert.ThrowsAsync<IOException>(() => _editor.EditAsync(video, Plan(tracks, copyTo: existing), Discard));
        await Assert.ThrowsAsync<ArgumentException>(() => _editor.EditAsync(video, Plan(tracks, copyTo: video), Discard));
        await Assert.ThrowsAsync<ArgumentException>(() => _editor.EditAsync(video, Plan(tracks, copyTo: Path.Combine(_dir, "kopie.mp4")), Discard));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => _editor.EditAsync(video, Plan(tracks, copyTo: Path.Combine(_dir, "fehlt", "kopie.mkv")), Discard));

        Assert.Equal("bleibt", File.ReadAllText(existing));
        Assert.Equal(2, (await Tracks(video)).Count(s => s.Type == "audio"));
    }

    // --- Prüfungen und Fehler ------------------------------------------------------------------------------

    [Fact]
    public async Task An_incomplete_duplicate_or_foreign_track_list_and_a_plan_without_changes_are_refused()
    {
        var video = await MakeVideo();
        var bytes = File.ReadAllBytes(video);
        var all = (await Tracks(video)).Select(s => new MkvTrackEdit(s.Index)).ToList();

        await Assert.ThrowsAsync<ArgumentException>(() => _editor.EditAsync(video, Plan(all.Take(4).ToList()), Discard));                       // eine fehlt
        await Assert.ThrowsAsync<ArgumentException>(() => _editor.EditAsync(video, Plan(all.Append(all[1]).ToList()), Discard));                // doppelt
        await Assert.ThrowsAsync<ArgumentException>(() => _editor.EditAsync(video, Plan(all.Select(t => t with { StreamIndex = t.StreamIndex + 10 }).ToList()), Discard)); // fremd
        await Assert.ThrowsAsync<ArgumentException>(() => _editor.EditAsync(video, Plan(all), Discard));                                        // nichts zu tun
        await Assert.ThrowsAsync<ArgumentException>(() => _editor.EditAsync(video, Plan(all.Select(t => t.StreamIndex == 1 ? t with { Language = "de" } : t).ToList()), Discard)); // Kürzel zu kurz

        Assert.Equal(bytes, File.ReadAllBytes(video));
        AssertNoLeftovers(video);
    }

    [Fact]
    public async Task Non_mkv_files_are_refused_and_untouched()
    {
        var mp4 = Path.Combine(_dir, "film.mp4");
        await Run(Ffmpeg, "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=d=1:s=160x90:r=10", "-c:v", "libx264", "-pix_fmt", "yuv420p", mp4);
        var bytes = File.ReadAllBytes(mp4);

        await Assert.ThrowsAsync<NotSupportedException>(() => _editor.EditAsync(mp4, Plan(added: new[] { new MkvAddedTrack(Subtitle("a.srt", "x"), MkvAddKind.Subtitle, "deu") }), Discard));

        Assert.Equal(bytes, File.ReadAllBytes(mp4));
        Assert.False(MkvEditor.CanEdit(mp4));
        Assert.True(MkvEditor.CanEdit("X.MKV"));
    }

    [Fact]
    public async Task A_locked_video_fails_immediately_when_replacing_but_can_still_be_copied()
    {
        var video = await MakeVideo();
        var tracks = (await Tracks(video)).Select(s => new MkvTrackEdit(s.Index, Remove: s.Index == 2)).ToList();
        using var openElsewhere = new FileStream(video, FileMode.Open, FileAccess.Read, FileShare.Read);

        var error = await Assert.ThrowsAsync<IOException>(() => _editor.EditAsync(video, Plan(tracks), Discard));
        Assert.Contains("gesperrt oder schreibgeschützt", error.Message);
        AssertNoLeftovers(video);

        var copy = Path.Combine(_dir, "kopie.mkv");
        await _editor.EditAsync(video, Plan(tracks, copyTo: copy), Discard);
        Assert.Equal(1, (await Tracks(copy)).Count(s => s.Type == "audio"));
    }

    [Fact]
    public async Task A_leftover_old_file_stops_replacing_and_is_not_overwritten()
    {
        var video = await MakeVideo();
        var old = video + MkvEditor.OldSuffix;
        File.WriteAllText(old, "vielleicht das Original");
        var tracks = (await Tracks(video)).Select(s => new MkvTrackEdit(s.Index, Remove: s.Index == 2)).ToList();

        await Assert.ThrowsAsync<IOException>(() => _editor.EditAsync(video, Plan(tracks), Discard));

        Assert.Equal("vielleicht das Original", File.ReadAllText(old));
        Assert.Equal(2, (await Tracks(video)).Count(s => s.Type == "audio"));
    }

    [Fact]
    public async Task When_the_original_cannot_be_discarded_the_new_file_is_in_place_and_the_old_path_is_reported()
    {
        var video = await MakeVideo();
        var tracks = (await Tracks(video)).Select(s => new MkvTrackEdit(s.Index, Remove: s.Index == 2)).ToList();

        var result = await _editor.EditAsync(video, Plan(tracks), _ => throw new IOException("gesperrt"));

        Assert.Equal(video + MkvEditor.OldSuffix, result.OriginalLeftBehind);
        Assert.Equal(2, (await Tracks(result.OriginalLeftBehind!)).Count(s => s.Type == "audio"));
        Assert.Equal(1, (await Tracks(video)).Count(s => s.Type == "audio"));
    }

    [Fact]
    public async Task Cancelling_leaves_the_original_untouched_and_no_work_files_behind()
    {
        var video = await MakeVideo();
        var bytes = File.ReadAllBytes(video);
        var tracks = (await Tracks(video)).Select(s => new MkvTrackEdit(s.Index, Remove: s.Index == 2)).ToList();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _editor.EditAsync(video, Plan(tracks), Discard, ct: cts.Token));

        Assert.Equal(bytes, File.ReadAllBytes(video));
        AssertNoLeftovers(video);
    }

    [Fact]
    public async Task Cancelling_while_ffmpeg_is_writing_stops_it_and_removes_the_work_file()
    {
        // Große Anhänge machen das Umschreiben lang genug, um mittendrin abzubrechen (je Anhang höchstens 256 MB, sonst ist die MKV ungültig).
        var big = Path.Combine(_dir, "gross.bin");
        var random = new Random(1);
        await using (var stream = File.Create(big))
        {
            var block = new byte[1 << 20];
            for (var i = 0; i < 100; i++)
            {
                random.NextBytes(block);
                await stream.WriteAsync(block);
            }
        }
        var video = Path.Combine(_dir, "schwer.mkv");
        var attach = new List<string> { "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=d=4:s=160x90:r=10", "-f", "lavfi", "-i", "sine=d=4", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac" };
        for (var i = 0; i < 4; i++) attach.AddRange(new[] { "-attach", big, $"-metadata:s:t:{i}", "mimetype=application/octet-stream", $"-metadata:s:t:{i}", $"filename=gross{i}.bin" });
        attach.Add(video);
        await Run(Ffmpeg, attach.ToArray());
        var bytes = File.ReadAllBytes(video).Length;
        File.Delete(big);
        var before = await Tracks(video);
        var tracks = before.Select(s => new MkvTrackEdit(s.Index, Remove: s.Type == "audio")).ToList();
        using var cts = new CancellationTokenSource();

        var edit = Task.Run(() => _editor.EditAsync(video, Plan(tracks), Discard, ct: cts.Token));
        var temp = video + MkvEditor.TempSuffix;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!(File.Exists(temp) && new FileInfo(temp).Length > 0))
        {
            Assert.True(DateTime.UtcNow < deadline && !edit.IsCompleted, "ffmpeg hat nicht zu schreiben begonnen");
            await Task.Delay(2);
        }
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => edit);
        Assert.Equal(bytes, File.ReadAllBytes(video).Length);
        AssertNoLeftovers(video);
        Assert.Equal(2, before.Count);
        Assert.Equal(2, (await Tracks(video)).Count);
    }

    [Fact]
    public async Task Progress_is_reported_up_to_one()
    {
        var video = await MakeVideo();
        var tracks = (await Tracks(video)).Select(s => new MkvTrackEdit(s.Index, Remove: s.Index == 2)).ToList();
        double last = -1;

        await _editor.EditAsync(video, Plan(tracks), Discard, new Progress<double>(v => last = v));
        await Task.Delay(100); // Progress<T> meldet asynchron

        Assert.Equal(1, last);
    }
}
