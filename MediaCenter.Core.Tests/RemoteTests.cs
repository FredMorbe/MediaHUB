using MediaCenter.Core;

namespace MediaCenter.Core.Tests;

public class RemoteAccessTests
{
    private DateTime _now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    private RemoteAccess Access(RemoteAccessState? state = null) => new(state, () => _now);

    [Fact]
    public void A_new_pin_has_eight_digits_and_differs_between_instances()
    {
        var a = RemoteAccess.NewPin();

        Assert.Equal(RemoteAccess.PinLength, a.Length);
        Assert.All(a, c => Assert.True(char.IsAsciiDigit(c)));
        Assert.Contains(Enumerable.Range(0, 20).Select(_ => RemoteAccess.NewPin()), p => p != a);
    }

    [Fact]
    public void The_right_pin_gives_a_token_that_is_valid_and_a_wrong_pin_gives_nothing()
    {
        var access = Access();

        var wrong = access.TryLogin("192.168.1.5", "00000000" == access.Pin ? "11111111" : "00000000");
        var login = access.TryLogin("192.168.1.5", access.Pin);

        Assert.False(wrong.Ok);
        Assert.Null(wrong.Token);
        Assert.True(login.Ok);
        Assert.Equal(64, login.Token!.Length);
        Assert.True(access.IsValidToken(login.Token));
        Assert.Equal(1, access.DeviceCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void Unknown_or_malformed_tokens_are_not_valid(string? token) => Assert.False(Access().IsValidToken(token));

    [Fact]
    public void After_five_wrong_pins_the_device_is_locked_even_for_the_right_pin()
    {
        var access = Access();
        var wrong = access.Pin == "00000000" ? "11111111" : "00000000";
        for (var i = 0; i < RemoteAccess.MaxFailures; i++) access.TryLogin("telefon", wrong);

        var locked = access.TryLogin("telefon", access.Pin);

        Assert.False(locked.Ok);
        Assert.NotNull(locked.LockedFor);
        Assert.InRange(locked.LockedFor!.Value, TimeSpan.FromMinutes(9), RemoteAccess.Lockout);
        Assert.True(access.TryLogin("anderes-geraet", access.Pin).Ok); // die Sperre gilt nur dem einen Gerät
    }

    [Fact]
    public void The_lock_ends_after_ten_minutes()
    {
        var access = Access();
        var wrong = access.Pin == "00000000" ? "11111111" : "00000000";
        for (var i = 0; i < RemoteAccess.MaxFailures; i++) access.TryLogin("telefon", wrong);

        _now = _now.AddMinutes(11);

        Assert.True(access.TryLogin("telefon", access.Pin).Ok);
    }

    [Fact]
    public void Four_wrong_pins_do_not_lock_and_a_success_clears_the_count()
    {
        var access = Access();
        var wrong = access.Pin == "00000000" ? "11111111" : "00000000";
        for (var i = 0; i < 4; i++) access.TryLogin("telefon", wrong);
        Assert.True(access.TryLogin("telefon", access.Pin).Ok);

        for (var i = 0; i < 4; i++) access.TryLogin("telefon", wrong);

        Assert.True(access.TryLogin("telefon", access.Pin).Ok);
    }

    [Fact]
    public void Only_hashes_are_kept_and_the_state_restores_the_devices()
    {
        var access = Access();
        var token = access.TryLogin("a", access.Pin).Token!;

        var state = access.State;
        Assert.DoesNotContain(token, state.TokenHashes);
        var restored = Access(state);

        Assert.Equal(access.Pin, restored.Pin);
        Assert.True(restored.IsValidToken(token));
    }

    [Fact]
    public void Revoking_all_devices_changes_the_pin_and_invalidates_every_token()
    {
        var access = Access();
        var oldPin = access.Pin;
        var token = access.TryLogin("a", oldPin).Token!;
        var raised = 0;
        access.Changed += () => raised++;

        access.RevokeAll();

        Assert.False(access.IsValidToken(token));
        Assert.NotEqual(oldPin, access.Pin);
        Assert.Equal(0, access.DeviceCount);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void A_broken_saved_pin_is_replaced_by_a_new_one()
    {
        Assert.Equal(RemoteAccess.PinLength, Access(new RemoteAccessState("12", Array.Empty<string>())).Pin.Length);
        Assert.All(Access(new RemoteAccessState("abcdefgh", Array.Empty<string>())).Pin, c => Assert.True(char.IsAsciiDigit(c)));
    }

    [Fact]
    public void The_oldest_device_drops_out_beyond_25()
    {
        var access = Access();
        var first = access.TryLogin("0", access.Pin).Token!;
        for (var i = 1; i <= 25; i++) access.TryLogin(i.ToString(), access.Pin);

        Assert.Equal(25, access.DeviceCount);
        Assert.False(access.IsValidToken(first));
    }
}

public class WebStreamTests
{
    private static readonly PlaybackPreferences German = new("de", SubtitleMode.Foreign, "de");

    private static StreamInfo Video(string codec = "h264", string profile = "High", string? transfer = "bt709") =>
        new(0, "video", codec, profile, 1920, 1080, null, null, null, true, false, transfer);

    private static StreamInfo Audio(int index, string codec, string language, bool isDefault) =>
        new(index, "audio", codec, null, null, null, 2, language, null, isDefault, false, null);

    private static ProbeResult Probe(StreamInfo video, params StreamInfo[] audio) => new(100, "x", new[] { video }.Concat(audio).ToList());

    [Fact]
    public void An_mp4_with_h264_and_aac_goes_out_unchanged()
    {
        var plan = WebStreamPlanner.Decide(Probe(Video(), Audio(1, "aac", "ger", true)), @"D:\Filme\a.mp4", German);

        Assert.True(plan.Direct);
    }

    [Theory]
    [InlineData(@"D:\Filme\a.mkv")]          // der Browser kann kein Matroska
    public void Other_containers_are_converted_but_the_picture_is_copied_when_it_is_plain_h264(string path)
    {
        var plan = WebStreamPlanner.Decide(Probe(Video(), Audio(1, "aac", "ger", true)), path, German);

        Assert.False(plan.Direct);
        Assert.True(plan.CopyVideo);
        Assert.False(plan.ToneMap);
    }

    [Fact]
    public void Ac3_audio_in_an_mp4_is_converted()
    {
        var plan = WebStreamPlanner.Decide(Probe(Video(), Audio(1, "ac3", "ger", true)), @"D:\a.mp4", German);

        Assert.False(plan.Direct);
        Assert.True(plan.CopyVideo);
        Assert.Contains("AAC", plan.Description);
    }

    [Fact]
    public void Hevc_and_ten_bit_h264_are_re_encoded()
    {
        Assert.False(WebStreamPlanner.Decide(Probe(Video("hevc", "Main"), Audio(1, "aac", "ger", true)), @"D:\a.mp4", German).CopyVideo);
        Assert.False(WebStreamPlanner.Decide(Probe(Video("h264", "High 10"), Audio(1, "aac", "ger", true)), @"D:\a.mp4", German).CopyVideo);
    }

    [Fact]
    public void Hdr_is_tone_mapped_to_sdr()
    {
        var plan = WebStreamPlanner.Decide(Probe(Video("hevc", "Main 10", "smpte2084"), Audio(1, "eac3", "ger", true)), @"D:\a.mkv", German);

        Assert.True(plan.ToneMap);
        Assert.False(plan.CopyVideo);
        Assert.Contains("HDR", plan.Description);
    }

    [Fact]
    public void A_preferred_language_that_is_not_the_default_track_forces_conversion_with_that_track()
    {
        var probe = Probe(Video(), Audio(1, "aac", "eng", true), Audio(2, "aac", "ger", false));

        var plan = WebStreamPlanner.Decide(probe, @"D:\a.mp4", German);

        Assert.False(plan.Direct);
        Assert.Equal(2, plan.AudioStreamIndex);
    }

    [Fact]
    public void Without_a_language_preference_the_default_track_wins_and_mp4_stays_direct()
    {
        var probe = Probe(Video(), Audio(1, "aac", "eng", true), Audio(2, "aac", "ger", false));

        var plan = WebStreamPlanner.Decide(probe, @"D:\a.mp4", new PlaybackPreferences(null, SubtitleMode.Off, "de"));

        Assert.True(plan.Direct);
    }

    [Fact]
    public void A_file_without_a_picture_is_a_loud_error()
    {
        Assert.Throws<InvalidDataException>(() => WebStreamPlanner.Decide(new ProbeResult(10, "mp3", new[] { Audio(0, "mp3", "ger", true) }), @"D:\a.mp3", German));
    }

    [Fact]
    public void Ffmpeg_arguments_copy_the_picture_and_convert_the_sound_into_a_fragmented_mp4_on_stdout()
    {
        var plan = WebStreamPlanner.Decide(Probe(Video(), Audio(1, "ac3", "ger", true)), @"D:\a.mkv", German);

        var args = WebStreamPlanner.FfmpegArguments(plan, @"D:\a.mkv", 0);

        Assert.DoesNotContain("-ss", args);
        Assert.Equal("copy", args[args.ToList().IndexOf("-c:v") + 1]);
        Assert.Equal("aac", args[args.ToList().IndexOf("-c:a") + 1]);
        Assert.Contains("frag_keyframe+empty_moov+default_base_moof", args);
        Assert.Equal("pipe:1", args[^1]);
        Assert.Equal(new[] { "-map", "0:0", "-map", "0:1" }, args.SkipWhile(a => a != "-map").Take(4).ToArray());
    }

    [Fact]
    public void Ffmpeg_arguments_seek_before_the_input_and_re_encode_with_a_height_limit()
    {
        var plan = WebStreamPlanner.Decide(Probe(Video("hevc", "Main"), Audio(1, "aac", "ger", true)), @"D:\a.mkv", German);

        var args = WebStreamPlanner.FfmpegArguments(plan, @"D:\a.mkv", 123.4567).ToList();

        Assert.True(args.IndexOf("-ss") < args.IndexOf("-i"));
        Assert.Equal("123.457", args[args.IndexOf("-ss") + 1]);
        Assert.Equal("libx264", args[args.IndexOf("-c:v") + 1]);
        Assert.Contains("min(ih,1080)", args[args.IndexOf("-vf") + 1]);
    }

    [Fact]
    public void Hdr_conversion_puts_the_tone_mapping_before_the_scaling()
    {
        var plan = WebStreamPlanner.Decide(Probe(Video("hevc", "Main 10", "arib-std-b67"), Audio(1, "aac", "ger", true)), @"D:\a.mkv", German);

        var filter = WebStreamPlanner.FfmpegArguments(plan, @"D:\a.mkv", 0).ToList() is var a ? a[a.IndexOf("-vf") + 1] : "";

        Assert.StartsWith("zscale", filter);
        Assert.Contains("scale=-2", filter);
    }

    [Fact]
    public void A_direct_plan_has_no_ffmpeg_arguments()
    {
        var plan = WebStreamPlanner.Decide(Probe(Video(), Audio(1, "aac", "ger", true)), @"D:\a.mp4", German);

        Assert.Throws<InvalidOperationException>(() => WebStreamPlanner.FfmpegArguments(plan, @"D:\a.mp4", 0));
    }

    [Fact]
    public void A_file_without_audio_makes_a_silent_stream()
    {
        var plan = WebStreamPlanner.Decide(Probe(Video()), @"D:\a.mkv", German);

        var args = WebStreamPlanner.FfmpegArguments(plan, @"D:\a.mkv", 0);

        Assert.DoesNotContain("-c:a", args);
        Assert.Single(args.Where(a => a == "-map"));
    }
}
