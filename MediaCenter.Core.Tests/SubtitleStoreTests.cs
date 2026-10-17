using MediaCenter.Core;

namespace MediaCenter.Core.Tests;

public sealed class SubtitleStoreTests : IDisposable
{
    private static readonly Language German = Languages.Get("de");
    private static readonly Language English = Languages.Get("en");

    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-sub-" + Guid.NewGuid().ToString("N"));
    private readonly SubtitleStore _store;

    public SubtitleStoreTests()
    {
        Directory.CreateDirectory(Path.Combine(_base, "src"));
        _store = new SubtitleStore(Path.Combine(_base, "store"));
    }

    public void Dispose() => Directory.Delete(_base, recursive: true);

    private string Source(string name, string content = "1\n00:00:01,000 --> 00:00:02,000\nHallo\n")
    {
        var path = Path.Combine(_base, "src", name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Add_copies_the_file_into_the_video_folder_and_tags_the_language_in_the_name()
    {
        var stored = _store.Add(7, Source("Mein Untertitel.srt"), German);

        Assert.Equal(Path.Combine(_base, "store", "7", "Mein Untertitel.deu.srt"), stored);
        Assert.True(File.Exists(stored));
        Assert.Equal(File.ReadAllBytes(Source("Mein Untertitel.srt")), File.ReadAllBytes(stored));
        Assert.True(_store.Contains(stored));
        Assert.Equal(new[] { stored }, _store.List(7));
    }

    [Fact]
    public void Add_replaces_an_existing_language_marker_in_the_name()
    {
        Assert.EndsWith("Film.eng.srt", _store.Add(1, Source("Film.German.srt"), English));
        Assert.EndsWith("Film2.deu.ass", _store.Add(1, Source("Film2.de.ASS"), German)); // Endung klein
    }

    [Fact]
    public void Add_keeps_non_language_dots_and_spaces()
    {
        Assert.EndsWith("Dr. No.deu.srt", _store.Add(1, Source("Dr. No.srt"), German)); // " No" mit Leerzeichen ist keine Sprache
        Assert.EndsWith("Show.S01E02.deu.srt", _store.Add(1, Source("Show.S01E02.srt"), German));
    }

    [Fact]
    public void Adding_the_same_content_twice_returns_the_existing_copy()
    {
        var first = _store.Add(1, Source("a.srt"), German);
        var second = _store.Add(1, Source("a.srt"), German);

        Assert.Equal(first, second);
        Assert.Single(_store.List(1));
    }

    [Fact]
    public void Different_content_under_the_same_name_gets_a_numbered_name()
    {
        var first = _store.Add(1, Source("a.srt", "eins"), German);
        var second = _store.Add(1, Source("a.srt", "zwei"), German);

        Assert.NotEqual(first, second);
        Assert.EndsWith("a-2.deu.srt", second);
        Assert.Equal(2, _store.List(1).Count);
    }

    [Fact]
    public void Subtitles_of_other_videos_stay_separate()
    {
        _store.Add(1, Source("a.srt"), German);
        Assert.Empty(_store.List(2));
    }

    [Fact]
    public void Adding_a_file_that_is_already_in_the_store_returns_it_unchanged()
    {
        var stored = _store.Add(1, Source("a.srt"), German);
        Assert.Equal(stored, _store.Add(1, stored, German));
        Assert.Single(_store.List(1));
    }

    [Fact]
    public void Add_rejects_missing_wrong_type_empty_and_oversized_files_with_clear_errors()
    {
        Assert.Throws<FileNotFoundException>(() => _store.Add(1, Path.Combine(_base, "src", "nope.srt"), German));
        Assert.Contains("kein unterstütztes", Assert.Throws<InvalidDataException>(() => _store.Add(1, Source("virus.exe"), German)).Message);
        Assert.Contains("leer", Assert.Throws<InvalidDataException>(() => _store.Add(1, Source("leer.srt", ""), German)).Message);

        var big = Path.Combine(_base, "src", "gross.srt");
        using (var stream = File.Create(big)) stream.SetLength(SubtitleStore.MaxBytes + 1);
        Assert.Contains("zu groß", Assert.Throws<InvalidDataException>(() => _store.Add(1, big, German)).Message);

        Assert.Empty(_store.List(1)); // nichts halb Gespeichertes
    }

    [Fact]
    public void Remove_deletes_the_file_and_the_empty_video_folder()
    {
        var stored = _store.Add(5, Source("a.srt"), German);
        _store.Remove(stored);

        Assert.False(File.Exists(stored));
        Assert.False(Directory.Exists(_store.DirectoryOf(5)));
        Assert.Empty(_store.List(5));
    }

    [Fact]
    public void Remove_keeps_the_folder_while_other_subtitles_remain()
    {
        var one = _store.Add(5, Source("a.srt", "eins"), German);
        var two = _store.Add(5, Source("b.srt", "zwei"), German);
        _store.Remove(one);

        Assert.Equal(new[] { two }, _store.List(5));
    }

    [Fact]
    public void Remove_refuses_files_outside_the_store()
    {
        var outside = Source("wichtig.srt");
        Assert.Throws<ArgumentException>(() => _store.Remove(outside));
        Assert.Throws<ArgumentException>(() => _store.Remove(Path.Combine(_base, "store", "..", "src", "wichtig.srt")));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void Contains_does_not_match_a_sibling_folder_with_the_same_prefix()
    {
        Assert.False(_store.Contains(Path.Combine(_base, "store-evil", "1", "a.srt")));
        Assert.True(_store.Contains(Path.Combine(_base, "store", "1", "a.srt")));
    }

    [Fact]
    public void List_ignores_files_that_are_not_subtitles_and_missing_folders()
    {
        Directory.CreateDirectory(_store.DirectoryOf(3));
        File.WriteAllText(Path.Combine(_store.DirectoryOf(3), "notiz.txt"), "x");
        Assert.Empty(_store.List(3));
        Assert.Empty(_store.List(99));
    }

    [Theory]
    [InlineData("Film.German.srt", "de")]
    [InlineData("Film.deu.srt", "de")]
    [InlineData("Film.de.srt", "de")]
    [InlineData("Film English.srt", "en")]
    [InlineData("Film.fra.forced.srt", "fr")]
    public void Language_is_detected_from_the_file_name(string name, string id) =>
        Assert.Equal(id, SubtitleStore.DetectLanguage(name)?.Id);

    [Theory]
    [InlineData("Let it be.srt")]   // "it" ist kein Italienisch
    [InlineData("Dr. No.srt")]
    [InlineData("untertitel_final.srt")]
    [InlineData("Show.S01E02.srt")]
    public void Language_is_not_guessed_from_ordinary_words(string name) =>
        Assert.Null(SubtitleStore.DetectLanguage(name));

    [Theory]
    [InlineData(@"C:\x\5\Mein Sub.deu.srt", "Mein Sub")]
    [InlineData(@"C:\x\5\Show.S01E02.deu.ass", "Show.S01E02")]
    [InlineData(@"C:\x\5\Ohne Kürzel.srt", "Ohne Kürzel")]
    public void Display_name_drops_the_extension_and_the_language_marker(string path, string expected) =>
        Assert.Equal(expected, SubtitleStore.DisplayName(path));

    // --- Spurwahl und Beschriftung für selbst hinzugefügte Untertitel --------------------------------------

    private static MediaTrack Audio(int id, string lang, bool selected) =>
        new(id, "audio", lang, null, "ac3", false, false, false, selected, 2);

    private static MediaTrack Sub(int id, string lang, bool userAdded = false, string? title = null) =>
        new(id, "sub", lang, title, "subrip", false, false, External: userAdded, Selected: false, UserAdded: userAdded);

    [Fact]
    public void Selector_prefers_a_user_added_subtitle_over_an_embedded_one()
    {
        var tracks = new[] { Audio(1, "eng", true), Sub(1, "deu"), Sub(2, "deu", userAdded: true, title: "Film.deu.srt") };
        Assert.True(TrackSelector.TryPickSubtitle(tracks, new PlaybackPreferences("de", SubtitleMode.Foreign, "de"), out var id));
        Assert.Equal(2, id);
    }

    [Fact]
    public void Selector_does_not_use_a_user_added_subtitle_when_subtitles_are_off()
    {
        var tracks = new[] { Audio(1, "eng", true), Sub(1, "deu", userAdded: true) };
        Assert.True(TrackSelector.TryPickSubtitle(tracks, new PlaybackPreferences("de", SubtitleMode.Off, "de"), out var id));
        Assert.Null(id);
    }

    [Fact]
    public void Label_of_a_user_added_subtitle_shows_its_name_and_that_it_was_added()
    {
        Assert.Equal("Deutsch · Mein Sub.deu.srt · SRT · hinzugefügt", Sub(3, "deu", userAdded: true, title: "Mein Sub.deu.srt").Label);
        Assert.Equal("Deutsch · SRT · extern", new MediaTrack(4, "sub", "deu", "deu.srt", "subrip", false, false, true, false).Label);
    }

    [Fact]
    public void A_stored_file_name_is_recognized_as_its_language_by_the_track_when_mpv_gives_no_lang()
    {
        // mpv setzt bei "sub-files" kein lang; der Titel ist der Dateiname mit dem dreibuchstabigen Kürzel
        var track = new MediaTrack(1, "sub", null, "Mein Sub.deu.srt", "subrip", false, false, true, false, UserAdded: true);
        Assert.Equal("de", track.Language?.Id);
    }
}
