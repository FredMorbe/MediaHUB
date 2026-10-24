using System.IO;

namespace MediaCenter.App.Services;

/// <summary>
/// Fehlerprotokoll im Datenordner (<c>error.log</c>): unbehandelte Ausnahmen der Oberfläche, nicht abgefangene Fehler aus Hintergrundaufgaben
/// und Startfehler. Ohne Protokoll gingen Fehler aus Aufgaben, auf die niemand wartet, lautlos verloren.
/// </summary>
public static class ErrorLog
{
    private const long MaxBytes = 1_000_000;

    public static string FilePath { get; } = Path.Combine(AppSettings.DataDirectory, "error.log");

    public static void Write(string source, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDirectory);
            // Eine zu große Datei wird zur Vorgängerdatei; es bleibt höchstens eine alte.
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes) File.Move(FilePath, FilePath + ".old", overwrite: true);
            File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}] Version {AppInfo.Version}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Das Protokoll selbst darf den eigentlichen Fehler nicht überdecken: der steht weiter in der Meldung bzw. wird weitergereicht.
        }
    }
}
