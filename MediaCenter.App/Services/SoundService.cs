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
    public string? Filter => Equalizer.ToMpvFilter(settings.EqualizerGains);

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
