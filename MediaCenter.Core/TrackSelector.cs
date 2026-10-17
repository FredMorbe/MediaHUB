using System.Text.RegularExpressions;

namespace MediaCenter.Core;

/// <summary>Eine Ton- oder Untertitelspur, wie mpv sie in <c>track-list</c> meldet.</summary>
/// <param name="File">Pfad einer externen Untertiteldatei.</param>
/// <param name="UserAdded">Vom Nutzer über „Untertitel-Datei laden“ hinzugefügt (liegt im <see cref="SubtitleStore"/>).</param>
public sealed partial record MediaTrack(
    int Id, string Type, string? Lang, string? Title, string? Codec, bool Default, bool Forced, bool External, bool Selected, int? Channels = null,
    string? File = null, bool UserAdded = false)
{
    public Language? Language => Languages.Find(Lang) ?? Languages.FindInText(Title);

    public bool IsForced => Forced || HasWord("forced", "erzwungen", "forciert");

    /// <summary>Untertitel für Hörgeschädigte (enthalten auch Geräusche).</summary>
    public bool IsHearingImpaired => HasWord("sdh", "cc", "hearing", "hörgeschädigte", "hoergeschaedigte");

    /// <summary>Nur Schilder und Lieder (Anime-Gruppen liefern sie als eigene Spur).</summary>
    public bool IsSignsOnly => HasWord("signs", "songs", "schilder", "lieder");

    /// <summary>Beschriftung für das Spurmenü, z. B. „Deutsch · AC3 5.1“ oder „Englisch · SRT · extern“.</summary>
    public string Label
    {
        get
        {
            var parts = new List<string>();
            var language = Language;
            if (language is not null) parts.Add(language.Name);
            else if (!string.IsNullOrWhiteSpace(Lang) && Lang != "und") parts.Add(Lang);

            // Bei automatisch gefundenen externen Dateien ist der Titel nur der Dateiname ("deu.forced.srt"); "extern" sagt dasselbe
            // kürzer. Selbst hinzugefügte behalten den Namen, damit man mehrere unterscheiden kann.
            if ((!External || UserAdded) && !string.IsNullOrWhiteSpace(Title) && !IsJustLanguage(Title, language)) parts.Add(Title.Trim());
            if (CodecName(Codec) is { } codec) parts.Add(Type == "audio" && ChannelName(Channels) is { } channels ? $"{codec} {channels}" : codec);
            if (IsForced) parts.Add("erzwungen");
            if (UserAdded) parts.Add("hinzugefügt");
            else if (External) parts.Add("extern");
            return parts.Count > 0 ? string.Join(" · ", parts) : $"Spur {Id}";
        }
    }

    private static bool IsJustLanguage(string title, Language? language) =>
        language is not null && (Languages.Find(title) == language);

    private bool HasWord(params string[] words)
    {
        if (string.IsNullOrWhiteSpace(Title)) return false;

        var tokens = Words().Split(Title.ToLowerInvariant());
        return words.Any(w => tokens.Contains(w));
    }

    private static string? CodecName(string? codec) => codec switch
    {
        null or "" => null,
        "subrip" => "SRT",
        "ass" or "ssa" => "ASS",
        "mov_text" => "TX3G",
        "webvtt" => "VTT",
        "hdmv_pgs_subtitle" => "PGS",
        "dvd_subtitle" => "VobSub",
        "dvb_subtitle" => "DVB",
        "eac3" => "E-AC3",
        "truehd" => "TrueHD",
        _ => codec.ToUpperInvariant(),
    };

    private static string? ChannelName(int? channels) => channels switch
    {
        null or 0 => null,
        1 => "Mono",
        2 => "Stereo",
        6 => "5.1",
        8 => "7.1",
        var n => $"{n} Kanäle",
    };

    [GeneratedRegex(@"[^\p{L}]+")]
    private static partial Regex Words();
}

/// <summary>Wählt die Untertitelspur nach den Einstellungen; die Tonspur wählt mpv über <c>alang</c>.</summary>
public static class TrackSelector
{
    /// <summary>
    /// Welche Untertitelspur laufen soll (<paramref name="subtitleId"/> null = keine). Liefert false, solange die Wahl
    /// nicht möglich ist: Es gibt Tonspuren, aber noch keine ist gewählt – dann ist unbekannt, welche Sprache läuft.
    /// </summary>
    public static bool TryPickSubtitle(IReadOnlyList<MediaTrack> tracks, PlaybackPreferences prefs, out int? subtitleId)
    {
        subtitleId = null;
        if (prefs.Subtitles == SubtitleMode.Off) return true;

        var audioTracks = tracks.Where(t => t.Type == "audio").ToList();
        var audio = audioTracks.FirstOrDefault(t => t.Selected);
        if (audioTracks.Count > 0 && audio is null) return false;

        var wanted = Languages.Get(prefs.SubtitleLanguage);
        var matching = tracks.Where(t => t.Type == "sub" && t.Language == wanted).ToList();
        var forced = matching.Where(t => t.IsForced).ToList();
        // Selbst hinzugefügte Untertitel gehen vor: wer eine Datei lädt, will sie sehen (z. B. weil die eingebauten nichts taugen).
        var full = matching.Where(t => !t.IsForced)
            .Select((t, index) => (Track: t, Index: index))
            .OrderBy(x => x.Track.UserAdded ? 0 : 1)
            .ThenBy(x => (x.Track.IsHearingImpaired ? 1 : 0) + (x.Track.IsSignsOnly ? 2 : 0))
            .ThenBy(x => x.Index)
            .Select(x => x.Track)
            .ToList();

        var audioInWantedLanguage = audio?.Language == wanted;
        if (prefs.Subtitles == SubtitleMode.Foreign && audioInWantedLanguage)
        {
            subtitleId = forced.FirstOrDefault()?.Id;
            return true;
        }

        subtitleId = (full.FirstOrDefault() ?? forced.FirstOrDefault())?.Id;
        return true;
    }
}
