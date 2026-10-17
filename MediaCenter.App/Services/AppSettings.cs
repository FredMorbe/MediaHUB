using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaCenter.Core;

namespace MediaCenter.App.Services;

/// <summary>Nutzereinstellungen der Oberfläche; liegen als JSON neben der Datenbank.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>"system", "light" oder "dark".</summary>
    public string Mode { get; set; } = "system";

    /// <summary>Id eines Farbstils aus <see cref="Theming.Palettes"/>.</summary>
    public string Palette { get; set; } = "signal";

    /// <summary>Lautstärke von Musik, Radio und Podcasts (0 bis 100).</summary>
    public double AudioVolume { get; set; } = 80;

    /// <summary>Bevorzugte Tonsprache (Schlüssel aus <see cref="Languages.All"/>); null nimmt die Spur, die die Datei vorgibt.</summary>
    public string? AudioLanguage { get; set; } = "de";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SubtitleMode Subtitles { get; set; } = SubtitleMode.Foreign;

    public string SubtitleLanguage { get; set; } = "de";

    /// <summary>Nach einer Folge die nächste der Serie automatisch starten (mit Countdown).</summary>
    public bool AutoPlayNext { get; set; } = true;

    [JsonIgnore]
    public PlaybackPreferences Playback => new PlaybackPreferences(AudioLanguage, Subtitles, SubtitleLanguage).Validated();

    /// <summary>Ordner für Datenbank, Cover-Cache und Einstellungen; <c>MEDIACENTER_DATA</c> überschreibt ihn (z. B. für Tests).</summary>
    public static string DataDirectory { get; } = Environment.GetEnvironmentVariable("MEDIACENTER_DATA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaCenter");

    public static string FilePath { get; } = Path.Combine(DataDirectory, "settings.json");

    /// <summary>Fehlt die Datei, gelten die Standardwerte (erster Start); eine kaputte Datei ist ein lauter Fehler.</summary>
    public static AppSettings Load()
    {
        if (!File.Exists(FilePath)) return new AppSettings();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath))
                ?? throw new InvalidDataException("Datei ist leer.");
            _ = settings.Playback; // prüft die Sprachschlüssel
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException)
        {
            throw new InvalidDataException($"Einstellungen sind fehlerhaft: {FilePath}. Datei löschen, um sie zurückzusetzen. ({ex.Message})", ex);
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, FilePath, overwrite: true);
    }
}
