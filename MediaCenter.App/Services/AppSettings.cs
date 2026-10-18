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

    public const string GpuAuto = "auto";

    public const string GpuOn = "on";

    public const string GpuOff = "off";

    /// <summary>
    /// Oberfläche über die Grafikkarte zeichnen („on“), per Software („off“) oder automatisch („auto“, Standard): Auf manchen Rechnern (belegt mit
    /// Intel Arc plus NVIDIA) bleibt jedes WPF-Fenster im Hardware-Modus weiß. Automatisch startet mit der Grafikkarte, prüft einmal das Fensterbild
    /// (<see cref="Services.RenderSelfTest"/>) und merkt sich das Ergebnis in <see cref="GpuAutoResult"/>. Gilt ab dem nächsten Start;
    /// das Video zeichnet mpv unabhängig davon auf der GPU.
    /// </summary>
    public string GpuMode { get; set; } = GpuAuto;

    /// <summary>Ergebnis des Tests im Modus „auto“: „ok“ (Fenster zeigt etwas), „failed“ (Fenster blieb leer, Software gilt) oder null (noch nicht geprüft).</summary>
    public string? GpuAutoResult { get; set; }

    /// <summary>Ob die Oberfläche beim nächsten Start per Software gezeichnet wird.</summary>
    [JsonIgnore]
    public bool SoftwareRendering => GpuMode == GpuOff || (GpuMode == GpuAuto && GpuAutoResult == "failed");

    /// <summary>Verstärkung in dB je Equalizer-Band (<see cref="Equalizer.Bands"/>); lauter Nullen = kein Eingriff in den Klang.</summary>
    public List<int> EqualizerGains { get; set; } = new int[Equalizer.Bands.Count].ToList();

    /// <summary>Schlüssel der zuletzt gespielten Alben, das neueste zuerst (für „Zuletzt gehört“ auf der Musik-Seite).</summary>
    public List<string> RecentAlbums { get; set; } = new();

    /// <summary>Lautstärke von Musik, Radio und Podcasts (0 bis 100).</summary>
    public double AudioVolume { get; set; } = 80;

    /// <summary>Bevorzugte Tonsprache (Schlüssel aus <see cref="Languages.All"/>); null nimmt die Spur, die die Datei vorgibt.</summary>
    public string? AudioLanguage { get; set; } = "de";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SubtitleMode Subtitles { get; set; } = SubtitleMode.Foreign;

    public string SubtitleLanguage { get; set; } = "de";

    /// <summary>Nach einer Folge die nächste der Serie automatisch starten (mit Countdown).</summary>
    public bool AutoPlayNext { get; set; } = true;

    /// <summary>
    /// Fehlende Poster im Internet suchen: Serien bei TVmaze, Filme bei OMDb (nur mit Schlüssel). Gesendet werden Titel und Jahr.
    /// </summary>
    public bool OnlineCovers { get; set; } = true;

    /// <summary>Songtexte im Internet suchen (LRCLIB), wenn weder Datei noch Tag einen haben. Gesendet werden Künstler, Titel, Album und Dauer.</summary>
    public bool OnlineLyrics { get; set; } = true;

    /// <summary>Beim Überfahren eines Films oder einer Serie einen kurzen stummen Ausschnitt zeigen (wird beim ersten Mal per ffmpeg erzeugt).</summary>
    public bool PreviewOnHover { get; set; } = true;

    /// <summary>Adresse des lokalen Ollama, das Untertitel übersetzt.</summary>
    public string OllamaUrl { get; set; } = "http://localhost:11434";

    /// <summary>Name des Ollama-Modells für Übersetzungen („qwen3:14b“); null, solange keins gewählt ist.</summary>
    public string? TranslationModel { get; set; }

    /// <summary>Eigenes Modell für Chinesisch; null nimmt <see cref="TranslationModel"/>.</summary>
    public string? TranslationModelChinese { get; set; }

    /// <summary>Das Modell für diese Zielsprache; null, wenn noch keins gewählt wurde.</summary>
    public string? ModelFor(TranslationTarget target) =>
        target.Id == TranslationTarget.TraditionalChinese.Id ? TranslationModelChinese ?? TranslationModel : TranslationModel;

    [JsonIgnore]
    public Uri OllamaUri => Uri.TryCreate(OllamaUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
        ? uri
        : throw new ArgumentException($"Die Ollama-Adresse „{OllamaUrl}“ ist keine http-Adresse (z. B. http://localhost:11434).");

    [JsonIgnore]
    public PlaybackPreferences Playback => new PlaybackPreferences(AudioLanguage, Subtitles, SubtitleLanguage).Validated();

    /// <summary>Ordner für Datenbank, Cover-Cache und Einstellungen; <c>MEDIACENTER_DATA</c> überschreibt ihn (z. B. für Tests).</summary>
    public static string DataDirectory { get; } = Environment.GetEnvironmentVariable("MEDIACENTER_DATA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaCenter");

    public static string FilePath { get; } = Path.Combine(DataDirectory, "settings.json");

    /// <summary>Zwischenstände laufender Untertitel-Übersetzungen (zum Fortsetzen nach einem Abbruch).</summary>
    public static string TranslationDirectory { get; } = Path.Combine(DataDirectory, "translation");

    /// <summary>Fehlt die Datei, gelten die Standardwerte (erster Start); eine kaputte Datei ist ein lauter Fehler.</summary>
    public static AppSettings Load()
    {
        if (!File.Exists(FilePath)) return new AppSettings();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath))
                ?? throw new InvalidDataException("Datei ist leer.");
            _ = settings.Playback; // prüft die Sprachschlüssel
            _ = settings.OllamaUri;
            Equalizer.Validated(settings.EqualizerGains);
            if (settings.GpuMode is not (GpuAuto or GpuOn or GpuOff))
                throw new ArgumentException($"GpuMode „{settings.GpuMode}“ ist unbekannt (auto, on oder off).");
            if (settings.GpuAutoResult is not (null or "ok" or "failed"))
                throw new ArgumentException($"GpuAutoResult „{settings.GpuAutoResult}“ ist unbekannt (ok oder failed).");
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
