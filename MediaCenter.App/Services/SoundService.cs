using MediaCenter.Core;

namespace MediaCenter.App.Services;

/// <summary>
/// Der Equalizer, den Musik und Video teilen. Wer mpv steuert (<see cref="AudioController"/>, der Videoplayer), hört auf <see cref="Changed"/>
/// und setzt <see cref="Filter"/> neu; beim Start einer mpv-Instanz gilt der gespeicherte Stand.
/// </summary>
public sealed class SoundService(AppSettings settings)
{
    /// <summary>Die Verstärkungen haben sich geändert; kommt auf dem UI-Thread.</summary>
    public event Action? Changed;

    public IReadOnlyList<int> Gains => settings.EqualizerGains;

    /// <summary>Id der Voreinstellung mit diesen Werten, sonst „custom“.</summary>
    public string Preset => Equalizer.MatchingPreset(settings.EqualizerGains);

    /// <summary>mpv-Audiofilter; null = kein Filter.</summary>
    public string? Filter => Equalizer.ToMpvFilter(settings.EqualizerGains, night: NightMode.Filter(settings.VideoNightStrength));

    /// <summary>Stärke des Nachtmodus für Video in Prozent, 0 = aus.</summary>
    public int Night => settings.VideoNightStrength;

    /// <param name="save">false beim Ziehen eines Reglers: der Ton ändert sich sofort, gespeichert wird später per <see cref="Save"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">Außerhalb von 0 bis 100.</exception>
    public void SetNight(int strength, bool save = true)
    {
        settings.VideoNightStrength = NightMode.Validated(strength);
        if (save) settings.Save();
        Changed?.Invoke();
    }

    /// <summary>Gleiche Lautstärke für Musik, Radio und Podcasts an?</summary>
    public bool Normalize => settings.AudioNormalize;

    /// <summary>Filter des Audioplayers (Musik, Radio, Podcasts): Equalizer plus, wenn eingeschaltet, die Lautstärke-Angleichung. Video nutzt <see cref="Filter"/>.</summary>
    public string? MusicFilter => Equalizer.ToMpvFilter(settings.EqualizerGains, settings.AudioNormalize);

    public void SetNormalize(bool on)
    {
        settings.AudioNormalize = on;
        settings.Save();
        Changed?.Invoke();
    }

    /// <param name="save">false beim Ziehen eines Reglers: der Klang ändert sich sofort, gespeichert wird später per <see cref="Save"/>.</param>
    /// <exception cref="ArgumentException">Falsche Anzahl Bänder oder Werte außerhalb von ±12 dB.</exception>
    public void SetGains(IReadOnlyList<int> gains, bool save = true)
    {
        settings.EqualizerGains = Equalizer.Validated(gains).ToList();
        if (save) settings.Save();
        Changed?.Invoke();
    }

    public void Save() => settings.Save();

    public void SetPreset(string id) => SetGains(Equalizer.PresetById(id).Gains);
}
