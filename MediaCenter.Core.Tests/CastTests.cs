using System.Diagnostics;
using System.Net;
using MediaCenter.Core;

namespace MediaCenter.Core.Tests;

public class CastCompatibilityTests
{
    private static StreamInfo Video(string codec, string? profile = "High", string? transfer = "bt709") =>
        new(0, "video", codec, profile, 1920, 1080, null, null, null, true, false, transfer);

    private static StreamInfo Audio(int index, string codec, bool isDefault = false, int channels = 2) =>
        new(index, "audio", codec, null, null, null, channels, "deu", null, isDefault, false, null);

    private static ProbeResult Probe(StreamInfo video, params StreamInfo[] audio) =>
        new(100, "matroska", new[] { video }.Concat(audio).ToList());

    [Fact]
    public void H264_with_aac_in_mp4_goes_directly()
    {
        var plan = CastCompatibility.Decide(Probe(Video("h264"), Audio(1, "aac")), @"D:\x\film.mp4", new CastCapabilities());

        Assert.Equal(CastMode.Direct, plan.Mode);
    }

    [Fact]
    public void H264_with_aac_in_mkv_is_remuxed_without_transcoding()
    {
        var plan = CastCompatibility.Decide(Probe(Video("h264"), Audio(1, "aac")), @"D:\x\film.mkv", new CastCapabilities());

        Assert.Equal(CastMode.Stream, plan.Mode);
        Assert.True(plan.CopyVideo);
        Assert.True(plan.CopyAudio);
    }

    [Fact]
    public void Dts_audio_is_converted_but_the_picture_is_copied()
    {
        var plan = CastCompatibility.Decide(Probe(Video("h264"), Audio(1, "dts", isDefault: true, channels: 6)), @"D:\x\film.mkv", new CastCapabilities());

        Assert.True(plan.CopyVideo);
        Assert.False(plan.CopyAudio);
        Assert.Equal(1, plan.AudioStreamIndex);
    }

    [Fact]
    public void Hevc_is_transcoded_unless_the_tv_can_play_it()
    {
        var probe = Probe(Video("hevc", "Main 10", "smpte2084"), Audio(1, "eac3", isDefault: true));

        var without = CastCompatibility.Decide(probe, @"D:\x\film.mkv", new CastCapabilities(Hevc: false));
        var with = CastCompatibility.Decide(probe, @"D:\x\film.mkv", new CastCapabilities(Hevc: true));

        Assert.False(without.CopyVideo);
        Assert.True(without.ToneMap); // HDR-Quelle, die nach H.264 geht, wird nach SDR umgerechnet
        Assert.True(with.CopyVideo);
        Assert.False(with.ToneMap);
        Assert.Equal(CastMode.Stream, with.Mode);
    }

    [Fact]
    public void H264_with_ten_bit_is_transcoded()
    {
        var plan = CastCompatibility.Decide(Probe(Video("h264", "High 10"), Audio(1, "aac")), @"D:\x\anime.mp4", new CastCapabilities());

        Assert.False(plan.CopyVideo);
        Assert.Equal(CastMode.Stream, plan.Mode);
    }

    [Fact]
    public void A_chosen_second_audio_track_needs_ffmpeg_even_in_mp4()
    {
        var probe = Probe(Video("h264"), Audio(1, "aac", isDefault: true), Audio(2, "aac"));

        var plan = CastCompatibility.Decide(probe, @"D:\x\film.mp4", new CastCapabilities(), audioStreamIndex: 2);

        Assert.Equal(CastMode.Stream, plan.Mode);
        Assert.Equal(2, plan.AudioStreamIndex);
    }

    [Fact]
    public void The_default_audio_track_wins_over_the_first()
    {
        var probe = Probe(Video("h264"), Audio(1, "aac"), Audio(2, "aac", isDefault: true));

        var plan = CastCompatibility.Decide(probe, @"D:\x\film.mkv", new CastCapabilities());

        Assert.Equal(2, plan.AudioStreamIndex);
    }

    [Fact]
    public void Unknown_audio_stream_is_an_error_not_a_silent_fallback()
    {
        var probe = Probe(Video("h264"), Audio(1, "aac"));

        Assert.Throws<ArgumentException>(() => CastCompatibility.Decide(probe, @"D:\x\film.mkv", new CastCapabilities(), audioStreamIndex: 7));
    }

    [Fact]
    public void A_file_without_video_is_an_error()
    {
        var probe = new ProbeResult(10, "mp3", new[] { Audio(0, "mp3") });

        Assert.Throws<InvalidOperationException>(() => CastCompatibility.Decide(probe, @"D:\x\lied.mp3", new CastCapabilities()));
    }

    [Fact]
    public void A_video_without_sound_is_playable()
    {
        var plan = CastCompatibility.Decide(Probe(Video("h264")), @"D:\x\stumm.mp4", new CastCapabilities());

        Assert.Equal(CastMode.Direct, plan.Mode);
        Assert.Null(plan.AudioStreamIndex);
    }
}

public class CastFfmpegTests
{
    private static CastPlan Plan(bool copyVideo, bool copyAudio, bool toneMap = false, int? audio = 1, string codec = "h264") =>
        new(CastMode.Stream, copyVideo, copyAudio, toneMap, audio, codec, "test");

    [Fact]
    public void Copy_plan_copies_both_streams_and_starts_at_the_requested_second()
    {
        var args = CastFfmpeg.StreamArguments(@"D:\x\film.mkv", Plan(true, true), 90.5, CastFfmpeg.SoftwareEncoder);

        Assert.Equal("90.500", args[args.ToList().IndexOf("-ss") + 1]);
        Assert.Contains("copy", args.Skip(args.ToList().IndexOf("-c:v")).Take(2));
        Assert.Contains("copy", args.Skip(args.ToList().IndexOf("-c:a")).Take(2));
        Assert.DoesNotContain("-hwaccel", args);
        Assert.Equal("pipe:1", args[^1]);
    }

    [Fact]
    public void Start_at_zero_has_no_seek()
    {
        Assert.DoesNotContain("-ss", CastFfmpeg.StreamArguments(@"D:\x\film.mkv", Plan(true, true), 0, CastFfmpeg.SoftwareEncoder));
    }

    [Fact]
    public void Hevc_copy_is_tagged_hvc1()
    {
        var args = CastFfmpeg.StreamArguments(@"D:\x\film.mkv", Plan(true, true, codec: "hevc"), 0, CastFfmpeg.SoftwareEncoder).ToList();

        Assert.Equal("hvc1", args[args.IndexOf("-tag:v") + 1]);
    }

    [Fact]
    public void Transcoding_with_hdr_tonemaps_and_limits_the_height()
    {
        var args = CastFfmpeg.StreamArguments(@"D:\x\film.mkv", Plan(false, false, toneMap: true), 0, CastFfmpeg.SoftwareEncoder).ToList();

        var filter = args[args.IndexOf("-vf") + 1];
        Assert.Contains("tonemap", filter);
        Assert.Contains("min(1080,ih)", filter);
        Assert.Equal(CastFfmpeg.SoftwareEncoder, args[args.IndexOf("-c:v") + 1]);
        Assert.Equal("aac", args[args.IndexOf("-c:a") + 1]);
    }

    [Fact]
    public void Nvidia_encoder_is_used_when_asked_for()
    {
        var args = CastFfmpeg.StreamArguments(@"D:\x\film.mkv", Plan(false, true), 0, CastFfmpeg.NvidiaEncoder).ToList();

        Assert.Equal(CastFfmpeg.NvidiaEncoder, args[args.IndexOf("-c:v") + 1]);
    }

    [Fact]
    public void No_audio_stream_means_no_audio_mapping()
    {
        var args = CastFfmpeg.StreamArguments(@"D:\x\stumm.mkv", Plan(true, true, audio: null), 0, CastFfmpeg.SoftwareEncoder);

        Assert.DoesNotContain("-c:a", args);
        Assert.Equal(1, args.Count(a => a == "-map")); // nur das Bild wird gemappt
    }

    [Fact]
    public void Direct_plan_cannot_be_streamed()
    {
        var direct = new CastPlan(CastMode.Direct, true, true, false, 1, "h264", "test");

        Assert.Throws<ArgumentException>(() => CastFfmpeg.StreamArguments(@"D:\x\film.mp4", direct, 0, CastFfmpeg.SoftwareEncoder));
    }

    [Fact]
    public void Negative_start_is_an_error()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CastFfmpeg.StreamArguments(@"D:\x\film.mkv", Plan(true, true), -1, CastFfmpeg.SoftwareEncoder));
    }
}

public class WebVttTests
{
    private static readonly SrtCue[] Cues =
    {
        new(1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), "Hallo <i>Welt</i>"),
        new(2, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(12.5), "Zweite Zeile\nund dritte"),
    };

    [Fact]
    public void Writes_header_and_dotted_timestamps()
    {
        var vtt = WebVtt.Format(Cues);

        Assert.StartsWith("WEBVTT\n\n", vtt);
        Assert.Contains("00:00:01.000 --> 00:00:03.000\nHallo <i>Welt</i>", vtt);
        Assert.Contains("00:00:10.000 --> 00:00:12.500\nZweite Zeile\nund dritte", vtt);
    }

    [Fact]
    public void Shift_moves_cues_and_drops_those_already_over()
    {
        var vtt = WebVtt.Format(Cues, shiftSeconds: 5);

        Assert.DoesNotContain("Hallo", vtt);
        Assert.Contains("00:00:05.000 --> 00:00:07.500\nZweite Zeile", vtt);
    }

    [Fact]
    public void Cue_running_across_the_shift_point_starts_at_zero()
    {
        var vtt = WebVtt.Format(Cues, shiftSeconds: 2);

        Assert.Contains("00:00:00.000 --> 00:00:01.000\nHallo", vtt);
    }

    [Fact]
    public void Markup_and_ass_tags_are_made_harmless()
    {
        var vtt = WebVtt.Format(new[] { new SrtCue(1, TimeSpan.Zero, TimeSpan.FromSeconds(1), @"{\an8}A & B <script> --> C") });

        Assert.DoesNotContain("{", vtt);
        Assert.Contains("A &amp; B &lt;script> --&gt; C", vtt);
    }
}

public sealed class CastTracksTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mc-casttracks-" + Guid.NewGuid().ToString("N"));

    public CastTracksTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static StreamInfo Audio(int index, string language, bool isDefault = false) =>
        new(index, "audio", "aac", null, null, null, 2, language, null, isDefault, false, null);

    private static StreamInfo Sub(int index, string language, string codec = "subrip", bool forced = false, string? title = null) =>
        new(index, "subtitle", codec, null, null, null, null, language, title, false, forced, null);

    private static readonly StreamInfo VideoStream = new(0, "video", "h264", "High", 1920, 1080, null, null, null, true, false, "bt709");

    private static ProbeResult Probe(params StreamInfo[] more) => new(100, "matroska", new[] { VideoStream }.Concat(more).ToList());

    private static readonly PlaybackPreferences German = new("de", SubtitleMode.Foreign, "de");

    [Fact]
    public void Preferred_audio_is_the_first_track_in_the_language()
    {
        var probe = Probe(Audio(1, "eng", isDefault: true), Audio(2, "ger"));

        Assert.Equal(2, CastTracks.PreferredAudioStream(probe, German));
    }

    [Fact]
    public void No_matching_audio_means_the_file_default()
    {
        var probe = Probe(Audio(1, "eng", isDefault: true), Audio(2, "jpn"));

        Assert.Null(CastTracks.PreferredAudioStream(probe, German));
        Assert.Null(CastTracks.PreferredAudioStream(probe, German with { AudioLanguage = null }));
    }

    [Fact]
    public void Audio_tracks_are_numbered_and_the_chosen_one_is_selected()
    {
        var probe = Probe(Audio(1, "eng", isDefault: true), Audio(2, "ger"));

        var set = CastTracks.Build(probe, audioStreamIndex: 2, Array.Empty<string>(), Array.Empty<string>(), German);

        var audio = set.Tracks.Where(t => t.Type == "audio").ToList();
        Assert.Equal(new[] { 1, 2 }, audio.Select(t => t.Id));
        Assert.Equal(2, audio.Single(t => t.Selected).FfIndex);
    }

    [Fact]
    public void Image_subtitles_are_left_out_and_text_subtitles_get_numbers()
    {
        var probe = Probe(Audio(1, "eng", isDefault: true), Sub(2, "eng"), Sub(3, "ger", "hdmv_pgs_subtitle"), Sub(4, "ger"));

        var set = CastTracks.Build(probe, null, Array.Empty<string>(), Array.Empty<string>(), German);

        Assert.Equal(new[] { 1, 2 }, set.Subtitles.Select(s => s.Id));
        Assert.Equal(new int?[] { 2, 4 }, set.Subtitles.Select(s => s.StreamIndex));
    }

    [Fact]
    public void Subtitles_follow_the_preferences_foreign_audio_shows_german_subtitles()
    {
        var probe = Probe(Audio(1, "eng", isDefault: true), Sub(2, "eng"), Sub(3, "ger"));

        var set = CastTracks.Build(probe, null, Array.Empty<string>(), Array.Empty<string>(), German);

        Assert.Equal(2, set.ActiveSubtitleId); // zweite Textspur = deutsch
        Assert.Equal(set.ActiveSubtitleId, set.Tracks.Single(t => t.Type == "sub" && t.Selected).Id);
    }

    [Fact]
    public void German_audio_with_foreign_mode_shows_no_subtitles()
    {
        var probe = Probe(Audio(1, "ger", isDefault: true), Sub(2, "ger"));

        var set = CastTracks.Build(probe, null, Array.Empty<string>(), Array.Empty<string>(), German);

        Assert.Null(set.ActiveSubtitleId);
        Assert.DoesNotContain(set.Tracks, t => t.Type == "sub" && t.Selected);
    }

    [Fact]
    public void Stored_subtitles_are_listed_after_embedded_ones_and_win_the_automatic_choice()
    {
        var probe = Probe(Audio(1, "eng", isDefault: true), Sub(2, "ger"));
        var stored = Path.Combine(_dir, "Mein Sub.deu.srt");
        File.WriteAllText(stored, "x");

        var set = CastTracks.Build(probe, null, new[] { stored }, Array.Empty<string>(), German);

        Assert.Equal(2, set.Subtitles.Count);
        Assert.Equal(stored, set.Subtitles[1].File);
        Assert.Equal(2, set.ActiveSubtitleId);
        Assert.True(set.Tracks.Single(t => t.Id == 2 && t.Type == "sub").UserAdded);
    }

    [Fact]
    public void Sidecars_are_found_next_to_the_video_and_in_a_subs_folder()
    {
        var video = Path.Combine(_dir, "Film.2020.mkv");
        File.WriteAllText(video, "x");
        File.WriteAllText(Path.Combine(_dir, "Film.2020.deu.srt"), "x");
        File.WriteAllText(Path.Combine(_dir, "Film.2020.sub"), "x");     // Bildformat: nicht aufnehmen
        File.WriteAllText(Path.Combine(_dir, "Anderer Film.srt"), "x");  // gehört nicht dazu
        Directory.CreateDirectory(Path.Combine(_dir, "Subs"));
        File.WriteAllText(Path.Combine(_dir, "Subs", "Film.2020.eng.ass"), "x");

        var found = CastTracks.FindSidecars(video).Select(Path.GetFileName).ToList();

        Assert.Equal(new[] { "Film.2020.deu.srt", "Film.2020.eng.ass" }, found);
    }

    [Fact]
    public void Missing_folder_gives_no_sidecars()
    {
        Assert.Empty(CastTracks.FindSidecars(Path.Combine(_dir, "gibt-es-nicht", "Film.mkv")));
    }
}

public class CastNetworkTests
{
    [Fact]
    public void Same_subnet_is_decided_by_the_mask()
    {
        var mask = IPAddress.Parse("255.255.255.0");

        Assert.True(CastMediaServer.SameSubnet(IPAddress.Parse("192.168.1.20"), IPAddress.Parse("192.168.1.99"), mask));
        Assert.False(CastMediaServer.SameSubnet(IPAddress.Parse("192.168.1.20"), IPAddress.Parse("192.168.2.99"), mask));
    }
}

/// <summary>Läuft gegen das echte ffmpeg (PATH oder MEDIACENTER_FFMPEG) und einen echten Webserver auf einem freien Port.</summary>
public sealed class CastServerTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mc-cast-" + Guid.NewGuid().ToString("N"));
    private static readonly string Ffmpeg = Environment.GetEnvironmentVariable("MEDIACENTER_FFMPEG") ?? "ffmpeg";
    private static readonly string Ffprobe = Environment.GetEnvironmentVariable("MEDIACENTER_FFPROBE") ?? "ffprobe";
    private CastMediaServer _server = null!;
    private readonly List<string> _failures = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _server = new CastMediaServer(Ffmpeg, new SubtitleExtractor(Ffmpeg));
        _server.StreamFailed += message => _failures.Add(message);
        await _server.StartAsync(IPAddress.Loopback);
    }

    /// <summary>Holt Text; bei einem Fehlerstatus steht die Meldung des Servers in der Ausnahme.</summary>
    private async Task<string> GetText(string url)
    {
        using var response = await _http.GetAsync(url);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {string.Join(" | ", _failures)}");
        return await response.Content.ReadAsStringAsync();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    private static async Task Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", "-y" }.Where(_ => exe == Ffmpeg).Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        _ = p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, $"{exe} schlug fehl: {stderr}");
    }

    /// <summary>MKV mit HEVC-Bild und AC3-Ton: der Fernseher kann beides nicht, also muss ffmpeg umwandeln.</summary>
    private async Task<string> MakeMkv(double seconds = 6)
    {
        var path = Path.Combine(_dir, "film.mkv");
        await Run(Ffmpeg, "-f", "lavfi", "-i", $"testsrc=d={seconds}:s=1280x720:r=24", "-f", "lavfi", "-i", $"sine=d={seconds}",
            "-c:v", "libx265", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "ac3", path);
        return path;
    }

    private async Task<ProbeResult> ProbeOf(string path) => await new FfprobeReader(Ffprobe).ProbeAsync(path);

    [Fact]
    public async Task Streams_a_transcoded_mp4_that_has_h264_and_aac_and_starts_at_the_requested_second()
    {
        var path = await MakeMkv();
        var probe = await ProbeOf(path);
        var plan = CastCompatibility.Decide(probe, path, new CastCapabilities());
        Assert.Equal(CastMode.Stream, plan.Mode);
        var token = _server.Register(new CastItem(path, probe, plan, Array.Empty<CastSubtitle>()));

        var bytes = await _http.GetByteArrayAsync(CastMediaServer.MediaUrl(IPAddress.Loopback, _server.Port, token, start: 2));

        var output = Path.Combine(_dir, "out.mp4");
        await File.WriteAllBytesAsync(output, bytes);
        var result = await ProbeOf(output);
        Assert.Equal("h264", result.Video!.Codec);
        Assert.Equal("aac", result.Streams.Single(s => s.Type == "audio").Codec);
        Assert.InRange(result.DurationSeconds!.Value, 3.0, 4.5); // 6 s Quelle ab Sekunde 2
        Assert.Equal(1, _server.RequestCount(token));
    }

    [Fact]
    public async Task Direct_files_support_range_requests()
    {
        var path = Path.Combine(_dir, "direkt.mp4");
        await Run(Ffmpeg, "-f", "lavfi", "-i", "testsrc=d=3:s=640x360:r=24", "-f", "lavfi", "-i", "sine=d=3", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", path);
        var probe = await ProbeOf(path);
        var plan = CastCompatibility.Decide(probe, path, new CastCapabilities());
        Assert.Equal(CastMode.Direct, plan.Mode);
        var token = _server.Register(new CastItem(path, probe, plan, Array.Empty<CastSubtitle>()));

        using var request = new HttpRequestMessage(HttpMethod.Get, CastMediaServer.MediaUrl(IPAddress.Loopback, _server.Port, token, 0));
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(100, 199);
        using var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(100, (await response.Content.ReadAsByteArrayAsync()).Length);
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task Serves_external_subtitles_as_webvtt_shifted_by_the_start()
    {
        var path = await MakeMkv(3);
        var srt = Path.Combine(_dir, "film.deu.srt");
        await File.WriteAllTextAsync(srt, "1\n00:00:01,000 --> 00:00:02,000\nEins\n\n2\n00:00:08,000 --> 00:00:09,000\nZwei\n");
        var probe = await ProbeOf(path);
        var item = new CastItem(path, probe, CastCompatibility.Decide(probe, path, new CastCapabilities()), new[] { new CastSubtitle(1, "de", "Deutsch", srt, null) });
        var token = _server.Register(item);

        var vtt = await GetText(CastMediaServer.SubtitleUrl(IPAddress.Loopback, _server.Port, token, 1, start: 5));

        Assert.StartsWith("WEBVTT", vtt);
        Assert.DoesNotContain("Eins", vtt);
        Assert.Contains("00:00:03.000 --> 00:00:04.000\nZwei", vtt);
    }

    [Fact]
    public async Task Extracts_embedded_text_subtitles()
    {
        var srt = Path.Combine(_dir, "eingebettet.srt");
        await File.WriteAllTextAsync(srt, "1\n00:00:00,500 --> 00:00:02,000\nImVideo\n");
        var path = Path.Combine(_dir, "mit-untertitel.mkv");
        await Run(Ffmpeg, "-f", "lavfi", "-i", "testsrc=d=3:s=320x180:r=24", "-i", srt, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:s", "srt", path);
        var probe = await ProbeOf(path);
        var subtitleStream = probe.Streams.Single(s => s.Type == "subtitle");
        var item = new CastItem(path, probe, CastCompatibility.Decide(probe, path, new CastCapabilities()), new[] { new CastSubtitle(1, "de", "Deutsch", null, subtitleStream.Index) });
        var token = _server.Register(item);

        var vtt = await GetText(CastMediaServer.SubtitleUrl(IPAddress.Loopback, _server.Port, token, 1, start: 0));

        Assert.Contains("00:00:00.500 --> 00:00:02.000\nImVideo", vtt);
    }

    [Fact]
    public async Task Unknown_token_is_404_and_unregistered_items_are_gone()
    {
        var path = await MakeMkv(2);
        var probe = await ProbeOf(path);
        var token = _server.Register(new CastItem(path, probe, CastCompatibility.Decide(probe, path, new CastCapabilities()), Array.Empty<CastSubtitle>()));
        _server.Unregister(token);

        using var response = await _http.GetAsync(CastMediaServer.MediaUrl(IPAddress.Loopback, _server.Port, token, 0));
        using var unknown = await _http.GetAsync(CastMediaServer.MediaUrl(IPAddress.Loopback, _server.Port, "nicht-angemeldet", 0));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }
}
