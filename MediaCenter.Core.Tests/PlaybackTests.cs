using MediaCenter.Core;

namespace MediaCenter.Core.Tests;

public sealed class PlaybackTests
{
    private static MediaTrack Audio(int id, string? lang, bool selected, string? title = null, int? channels = 2) =>
        new(id, "audio", lang, title, "ac3", Default: false, Forced: false, External: false, selected, channels);

    private static MediaTrack Sub(int id, string? lang, string? title = null, bool forced = false, bool external = false) =>
        new(id, "sub", lang, title, "subrip", Default: false, forced, external, Selected: false);

    private static PlaybackPreferences Prefs(SubtitleMode mode, string? audio = "de", string sub = "de") => new(audio, mode, sub);

    private static int? Pick(IReadOnlyList<MediaTrack> tracks, PlaybackPreferences prefs)
    {
        Assert.True(TrackSelector.TryPickSubtitle(tracks, prefs, out var id));
        return id;
    }

    // --- Sprachen --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("de", "de")]
    [InlineData("deu", "de")]
    [InlineData("ger", "de")]
    [InlineData("DE-de", "de")]
    [InlineData("pt_BR", "pt")]
    [InlineData("German", "de")]
    [InlineData("Deutsch", "de")]
    [InlineData("eng", "en")]
    [InlineData("jpn", "ja")]
    public void Language_codes_and_names_are_recognized(string text, string id) =>
        Assert.Equal(id, Languages.Find(text)?.Id);

    [Theory]
    [InlineData("und")]
    [InlineData("xx")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_language_codes_are_null(string? text) => Assert.Null(Languages.Find(text));

    [Fact]
    public void Unknown_language_id_in_settings_is_a_loud_error() =>
        Assert.Throws<ArgumentException>(() => Languages.Get("xx"));

    [Fact]
    public void Language_in_free_text_needs_three_letters()
    {
        Assert.Equal("de", Languages.FindInText("German.srt")?.Id);
        Assert.Equal("de", Languages.FindInText("deu forced")?.Id);
        Assert.Null(Languages.FindInText("No Subtitles")); // "no" ist Norwegisch, aber auch ein englisches Wort
        Assert.Null(Languages.FindInText(null));
    }

    [Fact]
    public void Mpv_list_contains_id_and_all_codes() =>
        Assert.Equal("de,deu,ger", Languages.Get("de").MpvList);

    [Fact]
    public void Preferences_without_audio_language_leave_the_choice_to_the_file() =>
        Assert.Equal("", Prefs(SubtitleMode.Off, audio: null).AudioMpvList);

    // --- Untertitelwahl --------------------------------------------------------------------------------------

    [Fact]
    public void Off_never_picks_a_subtitle()
    {
        var tracks = new[] { Audio(1, "eng", true), Sub(1, "deu") };
        Assert.Null(Pick(tracks, Prefs(SubtitleMode.Off)));
    }

    [Fact]
    public void Foreign_picks_full_subtitles_when_the_audio_is_in_another_language()
    {
        var tracks = new[] { Audio(1, "eng", true), Audio(2, "deu", false), Sub(1, "eng"), Sub(2, "deu"), Sub(3, "deu", forced: true) };
        Assert.Equal(2, Pick(tracks, Prefs(SubtitleMode.Foreign)));
    }

    [Fact]
    public void Foreign_picks_only_the_forced_track_when_the_audio_matches()
    {
        var tracks = new[] { Audio(1, "eng", false), Audio(2, "deu", true), Sub(1, "eng"), Sub(2, "deu"), Sub(3, "deu", forced: true) };
        Assert.Equal(3, Pick(tracks, Prefs(SubtitleMode.Foreign)));
    }

    [Fact]
    public void Foreign_picks_nothing_when_the_audio_matches_and_there_is_no_forced_track()
    {
        var tracks = new[] { Audio(1, "deu", true), Sub(1, "deu"), Sub(2, "eng") };
        Assert.Null(Pick(tracks, Prefs(SubtitleMode.Foreign)));
    }

    [Fact]
    public void Always_picks_the_full_track_even_when_the_audio_matches_and_a_forced_track_exists()
    {
        var tracks = new[] { Audio(1, "deu", true), Sub(1, "deu", forced: true), Sub(2, "deu") };
        Assert.Equal(2, Pick(tracks, Prefs(SubtitleMode.Always)));
    }

    [Fact]
    public void Always_falls_back_to_a_forced_track_when_no_full_one_exists()
    {
        var tracks = new[] { Audio(1, "eng", true), Sub(1, "deu", forced: true) };
        Assert.Equal(1, Pick(tracks, Prefs(SubtitleMode.Always)));
    }

    [Fact]
    public void Missing_subtitle_language_picks_nothing_rather_than_another_language()
    {
        var tracks = new[] { Audio(1, "eng", true), Sub(1, "fra"), Sub(2, "eng") };
        Assert.Null(Pick(tracks, Prefs(SubtitleMode.Foreign)));
    }

    [Fact]
    public void Hearing_impaired_and_signs_tracks_come_last()
    {
        var tracks = new[]
        {
            Audio(1, "eng", true),
            Sub(1, "deu", "Deutsch SDH"),
            Sub(2, "deu", "Signs & Songs"),
            Sub(3, "deu", "Vollständig"),
        };
        Assert.Equal(3, Pick(tracks, Prefs(SubtitleMode.Foreign)));
    }

    [Fact]
    public void Hearing_impaired_track_is_still_used_when_it_is_the_only_one()
    {
        var tracks = new[] { Audio(1, "eng", true), Sub(1, "deu", "SDH") };
        Assert.Equal(1, Pick(tracks, Prefs(SubtitleMode.Foreign)));
    }

    [Fact]
    public void External_file_without_language_tag_is_recognized_by_its_name()
    {
        var tracks = new[] { Audio(1, "eng", true), Sub(1, null, "German.srt", external: true) };
        Assert.Equal(1, Pick(tracks, Prefs(SubtitleMode.Foreign)));
    }

    [Fact]
    public void External_file_named_forced_counts_as_forced_even_if_mpv_did_not_flag_it()
    {
        var tracks = new[] { Audio(1, "deu", true), Sub(1, "deu", "deu.forced.srt", external: true) };
        Assert.Equal(1, Pick(tracks, Prefs(SubtitleMode.Foreign)));
    }

    [Fact]
    public void Decision_waits_while_audio_tracks_exist_but_none_is_selected_yet()
    {
        var tracks = new[] { Audio(1, "deu", false), Sub(1, "deu") };
        Assert.False(TrackSelector.TryPickSubtitle(tracks, Prefs(SubtitleMode.Foreign), out _));
    }

    [Fact]
    public void Unknown_audio_language_counts_as_foreign()
    {
        var tracks = new[] { Audio(1, null, true), Sub(1, "deu") };
        Assert.Equal(1, Pick(tracks, Prefs(SubtitleMode.Foreign)));
    }

    [Fact]
    public void Language_tag_with_region_matches()
    {
        var tracks = new[] { Audio(1, "eng", true), Sub(1, "de-DE") };
        Assert.Equal(1, Pick(tracks, Prefs(SubtitleMode.Foreign)));
    }

    // --- Spurbeschriftung ------------------------------------------------------------------------------------

    [Fact]
    public void Labels_show_language_codec_channels_and_flags()
    {
        Assert.Equal("Deutsch · AC3 5.1", Audio(1, "ger", true, channels: 6).Label);
        Assert.Equal("Englisch · Director's Commentary · AC3 Stereo", Audio(2, "eng", false, "Director's Commentary").Label);
        Assert.Equal("Deutsch · SRT · erzwungen", Sub(1, "deu", forced: true).Label);
        Assert.Equal("Deutsch · SRT · extern", Sub(2, null, "German.srt", external: true).Label);
        Assert.Equal("Spur 4", new MediaTrack(4, "sub", null, null, null, false, false, false, false).Label);
    }

    [Fact]
    public void Title_that_only_repeats_the_language_is_left_out_of_the_label() =>
        Assert.Equal("Deutsch · AC3 Stereo", Audio(1, "deu", true, "Deutsch").Label);

    // --- Nächste Folge ---------------------------------------------------------------------------------------

    private static VideoRow Video(long id, long titleId, TitleKind kind, int? season, int? episode) =>
        new(id, id, 1, $"v{id}.mkv", Missing: false, titleId, kind, "T", null, season, episode, false, "t", VideoKind.Episode, 60, null, null, null, false);

    [Fact]
    public void Next_episode_follows_season_and_episode_order_regardless_of_list_order()
    {
        var videos = new[]
        {
            Video(1, 7, TitleKind.Series, 2, 1),
            Video(2, 7, TitleKind.Series, 1, 2),
            Video(3, 7, TitleKind.Series, 1, 1),
        };
        Assert.Equal(2, EpisodeOrder.Next(videos, videos[2])!.VideoId); // S01E01 -> S01E02
        Assert.Equal(1, EpisodeOrder.Next(videos, videos[1])!.VideoId); // letzte der Staffel 1 -> S02E01
        Assert.Null(EpisodeOrder.Next(videos, videos[0]));              // letzte Folge
    }

    [Fact]
    public void Next_episode_skips_a_second_version_of_the_same_episode()
    {
        var videos = new[]
        {
            Video(1, 7, TitleKind.Series, 1, 1),
            Video(2, 7, TitleKind.Series, 1, 1),
            Video(3, 7, TitleKind.Series, 1, 2),
        };
        Assert.Equal(3, EpisodeOrder.Next(videos, videos[0])!.VideoId);
        Assert.Equal(3, EpisodeOrder.Next(videos, videos[1])!.VideoId);
    }

    [Fact]
    public void Next_episode_ignores_other_titles_movies_and_videos_without_numbers()
    {
        var current = Video(1, 7, TitleKind.Series, 1, 1);
        var videos = new[]
        {
            current,
            Video(2, 8, TitleKind.Series, 1, 2),
            Video(3, 7, TitleKind.Series, null, null),
        };
        Assert.Null(EpisodeOrder.Next(videos, current));
        Assert.Null(EpisodeOrder.Next(videos, Video(9, 7, TitleKind.Movie, 1, 1)));
        Assert.Null(EpisodeOrder.Next(videos, videos[2]));
    }

    [Fact]
    public void Next_episode_works_for_series_without_season_numbers()
    {
        var videos = new[] { Video(1, 7, TitleKind.Series, null, 1), Video(2, 7, TitleKind.Series, null, 2) };
        Assert.Equal(2, EpisodeOrder.Next(videos, videos[0])!.VideoId);
    }
}
