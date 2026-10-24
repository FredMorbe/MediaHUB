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

    /// <summary>Die App beim Start im Vollbild öffnen (ohne Fensterrahmen); F11 oder der Knopf in der Navigation beenden es.</summary>
    public bool StartFullscreen { get; set; }

    /// <summary>Schlüssel der zuletzt gespielten Alben, das neueste zuerst (für „Zuletzt gehört“ auf der Musik-Seite).</summary>
    public List<string> RecentAlbums { get; set; } = new();

    /// <summary>Lautstärke von Musik, Radio und Podcasts (0 bis 100).</summary>
    public double AudioVolume { get; set; } = 80;

    /// <summary>Zuletzt benutztes Profil (Id); die App öffnet beim nächsten Start dieses.</summary>
    public long LastProfileId { get; set; } = LibraryDb.DefaultProfileId;

    /// <summary>Beim Start „Wer schaut?“ zeigen, sobald es mehr als ein Profil gibt (hat das letzte Profil eine PIN, fragt die App immer).</summary>
    public bool AskProfileAtStart { get; set; } = true;

    /// <summary>Länge der Überblendung zwischen zwei Musiktiteln in Sekunden (<see cref="Crossfade.Options"/>); 0 = aus (lückenlos ohne Überblendung).</summary>
    public int AudioCrossfadeSeconds { get; set; }

    /// <summary>Bevorzugte Tonsprache (Schlüssel aus <see cref="Languages.All"/>); null nimmt die Spur, die die Datei vorgibt.</summary>
    public string? AudioLanguage { get; set; } = "de";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SubtitleMode Subtitles { get; set; } = SubtitleMode.Foreign;

    public string SubtitleLanguage { get; set; } = "de";

    /// <summary>Schrift, Größe, Farben und Hintergrund der Untertitel; gilt für alle Videos (Einstellungen → Wiedergabe oder Anpassen-Fenster im Player).</summary>
    public SubtitleStyle SubtitleStyle { get; set; } = SubtitleStyle.Default;

    /// <summary>Vom Nutzer geänderte Tasten des Players: Name der <see cref="PlayerAction"/> → Tastennamen (<see cref="System.Windows.Input.Key"/>); fehlende Aktionen behalten ihre Standardtasten. Beim Ändern wird das ganze Wörterbuch ersetzt.</summary>
    public Dictionary<string, string[]> KeyBindings { get; set; } = new();

    /// <summary>Heimnetz-Server: Weboberfläche und Fernbedienung fürs Handy (<see cref="RemoteServer"/>). Aus, bis der Nutzer ihn einschaltet.</summary>
    public bool HomeServer { get; set; }

    /// <summary>Anschluss (Port) des Heimnetz-Servers.</summary>
    public int HomeServerPort { get; set; } = 8484;

    /// <summary>true: im ganzen Heimnetz erreichbar; false: nur von diesem PC (zum Ausprobieren).</summary>
    public bool HomeServerLan { get; set; } = true;

    /// <summary>Sendepläne der Serien bei TVmaze abfragen (Kalender, Hinweis auf neue Folgen). Gesendet werden Serientitel und Jahr.</summary>
    public bool UpcomingEpisodes { get; set; } = true;

    /// <summary>Die Ordner der Quellen beobachten und die Bibliothek bei neuen, entfernten oder umbenannten Dateien von selbst aktualisieren (<see cref="LibraryWatch"/>).</summary>
    public bool WatchFolders { get; set; } = true;

    /// <summary>Ob HDR-Videos als HDR an den Bildschirm gehen (Standard: wenn Windows-HDR an ist) oder immer nach SDR umgerechnet werden. Gilt ab der nächsten Wiedergabe.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HdrOutput Hdr { get; set; } = HdrOutput.Auto;

    /// <summary>Nach einer Folge die nächste der Serie automatisch starten (mit Countdown).</summary>
    public bool AutoPlayNext { get; set; } = true;

    /// <summary>
    /// Der Fernseher spielt HEVC (auch HDR) selbst ab. Aus: HEVC-Videos wandelt ffmpeg beim Übertragen nach H.264 um (belastet den PC);
    /// an: sie gehen unverändert hin, ein Fernseher ohne HEVC zeigt dann nichts.
    /// </summary>
    public bool CastHevc { get; set; }

    /// <summary>
    /// Fehlende Poster im Internet suchen: Serien bei TVmaze, Filme bei OMDb (nur mit Schlüssel). Gesendet werden Titel und Jahr.
    /// </summary>
    public bool OnlineCovers { get; set; } = true;

    /// <summary>Songtexte im Internet suchen (LRCLIB), wenn weder Datei noch Tag einen haben. Gesendet werden Künstler, Titel, Album und Dauer.</summary>
    public bool OnlineLyrics { get; set; } = true;

    /// <summary>Beim Überfahren eines Films oder einer Serie einen kurzen stummen Ausschnitt zeigen (wird beim ersten Mal per ffmpeg erzeugt).</summary>
    public bool PreviewOnHover { get; set; } = true;

    /// <summary>Wann die letzte Sicherung geschrieben wurde (Einstellungen → Bibliothek → Sicherung); null, solange es keine gab.</summary>
    public DateTime? LastBackupUtc { get; set; }

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
            settings.SubtitleStyle.Validated();
            Crossfade.Validated(settings.AudioCrossfadeSeconds);
            Equalizer.Validated(settings.EqualizerGains);
            if (settings.HomeServerPort is < 1024 or > 65535)
                throw new ArgumentException($"HomeServerPort {settings.HomeServerPort} ist ungültig (1024 bis 65535).");
            settings.KeyBindings ??= new();
            PlayerKeys.Resolve(settings.KeyBindings);
            foreach (var key in settings.KeyBindings.Values.SelectMany(k => k))
            {
                if (!Enum.TryParse<System.Windows.Input.Key>(key, out var parsed) || !Enum.IsDefined(parsed))
                    throw new ArgumentException($"Die Taste „{key}“ in KeyBindings gibt es nicht.");
            }
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
