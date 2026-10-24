namespace MediaCenter.Core;

/// <summary>Wo ein Werkzeug liegt und woher der Pfad stammt.</summary>
/// <param name="Path">Pfad zur Datei; wurde nichts gefunden, der Name bzw. Standardpfad, mit dem der Aufruf dann laut scheitert.</param>
/// <param name="Found">Die Datei existiert.</param>
/// <param name="Source">Kurz, wo gesucht wurde bzw. wo es lag (für Anzeige und Fehlertexte).</param>
public sealed record ToolLocation(string Path, bool Found, string Source)
{
    /// <summary>Eine Zeile für die Einstellungen: Pfad und Herkunft, bei einem Fehlschlag auch, wo gesucht wurde.</summary>
    public string Describe() => Found ? $"{Path}  ({Source})" : $"{Path}  (nicht gefunden; {Source})";
}

/// <summary>
/// Findet mpv, ffmpeg und ffprobe: erst die Umgebungsvariable (<c>MEDIACENTER_MPV</c>, <c>MEDIACENTER_FFMPEG</c>, <c>MEDIACENTER_FFPROBE</c>),
/// dann neben der EXE, dann im Unterordner <c>tools</c> neben der EXE, dann im PATH, zuletzt ein Standardpfad. So läuft die App auch auf einem
/// Rechner, auf dem die Werkzeuge nur im Programmordner liegen.
/// </summary>
public static class ToolLocator
{
    public const string ToolsFolder = "tools";

    public const string DefaultMpvPath = @"C:\Program Files\MPV Player\mpv.exe";

    public static ToolLocation Mpv() => Locate("MEDIACENTER_MPV", "mpv", DefaultMpvPath);

    public static ToolLocation Ffmpeg() => Locate("MEDIACENTER_FFMPEG", "ffmpeg", null);

    public static ToolLocation Ffprobe() => Locate("MEDIACENTER_FFPROBE", "ffprobe", null);

    private static ToolLocation Locate(string variable, string name, string? defaultPath) =>
        Resolve(Environment.GetEnvironmentVariable(variable), variable, name, AppContext.BaseDirectory, Environment.GetEnvironmentVariable("PATH"), defaultPath);

    /// <param name="configured">Wert der Umgebungsvariable; hat Vorrang und wird nicht auf Existenz geprüft (ein falscher Pfad soll laut scheitern).</param>
    /// <param name="variable">Name der Variable, nur für <see cref="ToolLocation.Source"/>.</param>
    /// <param name="name">Dateiname ohne .exe.</param>
    /// <param name="defaultPath">Letzter Versuch, wenn nichts gefunden wurde; null: nur der Name (der Aufruf scheitert dann laut).</param>
    public static ToolLocation Resolve(string? configured, string variable, string name, string baseDirectory, string? pathVariable, string? defaultPath)
    {
        if (!string.IsNullOrEmpty(configured)) return new ToolLocation(configured, File.Exists(configured), $"Umgebungsvariable {variable}");

        var file = name + ".exe";
        foreach (var (directory, source) in new[] { (baseDirectory, "neben der Anwendung"), (Path.Combine(baseDirectory, ToolsFolder), $"Ordner {ToolsFolder} neben der Anwendung") })
        {
            var candidate = Path.Combine(directory, file);
            if (File.Exists(candidate)) return new ToolLocation(candidate, true, source);
        }

        foreach (var directory in (pathVariable ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), file);
            if (File.Exists(candidate)) return new ToolLocation(candidate, true, "PATH");
        }

        if (defaultPath is not null) return new ToolLocation(defaultPath, File.Exists(defaultPath), "Standardpfad");
        return new ToolLocation(name, false, "gesucht neben der Anwendung, im Ordner tools und im PATH");
    }
}
