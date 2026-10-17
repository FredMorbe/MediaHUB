namespace MediaCenter.Core;

public enum SubtitleMode
{
    /// <summary>Es wird nie automatisch ein Untertitel gewählt (von Hand im Player möglich).</summary>
    Off,

    /// <summary>Volle Untertitel nur, wenn der Ton nicht in der Untertitelsprache ist; sonst höchstens erzwungene.</summary>
    Foreign,

    /// <summary>Immer die vollen Untertitel der Sprache, auch bei passendem Ton.</summary>
    Always,
}

/// <param name="AudioLanguage">Bevorzugte Tonsprache; null nimmt die Spur, die die Datei vorgibt.</param>
/// <param name="SubtitleLanguage">Schlüssel einer Sprache aus <see cref="Languages.All"/>.</param>
public sealed record PlaybackPreferences(string? AudioLanguage, SubtitleMode Subtitles, string SubtitleLanguage)
{
    /// <summary>Liste für mpvs <c>alang</c>; leer, wenn die Datei entscheiden soll.</summary>
    public string AudioMpvList => AudioLanguage is null ? "" : Languages.Get(AudioLanguage).MpvList;

    /// <summary>Wirft bei einem unbekannten Sprachschlüssel.</summary>
    public PlaybackPreferences Validated()
    {
        if (AudioLanguage is not null) Languages.Get(AudioLanguage);
        Languages.Get(SubtitleLanguage);
        return this;
    }
}
