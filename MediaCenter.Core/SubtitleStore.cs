using System.Globalization;

namespace MediaCenter.Core;

/// <summary>
/// Vom Nutzer hinzugefügte Untertitel-Dateien. Sie werden pro Video in den Datenordner der App kopiert
/// (<c>subtitles\{videoId}\Name.deu.srt</c>), damit die Mediendateien unberührt bleiben (auch auf schreibgeschützten Laufwerken)
/// und die Untertitel beim nächsten Abspielen wieder da sind.
/// </summary>
public sealed class SubtitleStore(string root)
{
    /// <summary>Dateiendungen, die mpv als Untertitel lädt und die ohne Zusatzdatei auskommen (.idx braucht eine .sub dazu).</summary>
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".vtt", ".sub", ".smi",
    };

    /// <summary>Größer ist keine Untertiteldatei mehr (übliche SRT sind einige hundert KB).</summary>
    public const long MaxBytes = 10 * 1024 * 1024;

    private const int MaxBaseNameLength = 80;

    private readonly string _root = Path.GetFullPath(root);

    public string DirectoryOf(long videoId) => Path.Combine(_root, videoId.ToString(CultureInfo.InvariantCulture));

    /// <summary>Liegt die Datei im Ordner dieses Speichers? Pfade, die keine sind (Web-Adressen), liegen nicht darin.</summary>
    public bool Contains(string path)
    {
        try
        {
            return Path.GetFullPath(path).StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Alle gespeicherten Untertitel dieses Videos; leer, wenn es keine gibt.</summary>
    public IReadOnlyList<string> List(long videoId)
    {
        var directory = DirectoryOf(videoId);
        if (!Directory.Exists(directory)) return Array.Empty<string>();

        return Directory.EnumerateFiles(directory)
            .Where(f => Extensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Kopiert die Datei in den Speicher dieses Videos und gibt den Zielpfad zurück. Dieselbe Datei noch einmal zu
    /// speichern liefert die vorhandene Kopie. Fehler (fehlende Datei, falsche Endung, zu groß) werfen mit deutscher Meldung.
    /// </summary>
    /// <param name="language">Wird als Kürzel in den Dateinamen geschrieben ("Film.deu.srt"), damit mpv und die Spurwahl sie erkennen.</param>
    public string Add(long videoId, string source, Language language)
    {
        if (!File.Exists(source)) throw new FileNotFoundException($"Untertiteldatei nicht gefunden: {source}", source);

        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (!Extensions.Contains(extension))
        {
            throw new InvalidDataException($"„{extension}“ ist kein unterstütztes Untertitelformat (erlaubt: {string.Join(", ", Extensions.Order())}).");
        }

        var length = new FileInfo(source).Length;
        if (length == 0) throw new InvalidDataException("Die Untertiteldatei ist leer.");
        if (length > MaxBytes) throw new InvalidDataException($"Die Datei ist mit {length / 1024 / 1024} MB zu groß für eine Untertiteldatei (Grenze {MaxBytes / 1024 / 1024} MB).");

        if (Contains(source)) return Path.GetFullPath(source); // schon gespeichert

        var directory = DirectoryOf(videoId);
        Directory.CreateDirectory(directory);

        var baseName = CleanBaseName(Path.GetFileNameWithoutExtension(source));
        var code = language.Codes.FirstOrDefault() ?? language.Id;
        var bytes = File.ReadAllBytes(source);
        for (var attempt = 1; ; attempt++)
        {
            var suffix = attempt == 1 ? "" : $"-{attempt}";
            var target = Path.Combine(directory, $"{baseName}{suffix}.{code}{extension}");
            if (!File.Exists(target))
            {
                File.WriteAllBytes(target, bytes);
                return target;
            }
            if (File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) return target; // gleicher Inhalt: nichts doppelt ablegen
        }
    }

    /// <summary>Löscht eine gespeicherte Datei (und den Ordner des Videos, wenn er leer wird). Fremde Pfade werden abgelehnt.</summary>
    public void Remove(string storedPath)
    {
        if (!Contains(storedPath)) throw new ArgumentException($"Die Datei gehört nicht zu den gespeicherten Untertiteln: {storedPath}", nameof(storedPath));

        File.Delete(storedPath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(storedPath))!;
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }

    /// <summary>Name einer gespeicherten Datei ohne Endung und ohne das angehängte Sprachkürzel („Mein Sub.deu.srt“ → „Mein Sub“).</summary>
    public static string DisplayName(string storedPath)
    {
        var name = Path.GetFileNameWithoutExtension(storedPath);
        return TrailingLanguage(name, out var dot) is not null ? name[..dot] : name;
    }

    /// <summary>Sprache aus dem Dateinamen: „Film.German.srt“, „Film.deu.srt“, „Film.de.srt“; null, wenn keine zu erkennen ist.</summary>
    public static Language? DetectLanguage(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        // Ein durch Punkt abgetrenntes Kürzel am Ende darf zweibuchstabig sein ("Film.de.srt"); im übrigen Namen zählen nur
        // Sprachnamen und dreibuchstabige Kürzel ("Let it be.srt" ist nicht Italienisch).
        return TrailingLanguage(name, out _) ?? Languages.FindInText(name);
    }

    /// <summary>Das Sprachkürzel nach dem letzten Punkt („Film.de“), ohne Leerzeichen darin („Dr. No“ ist kein Norwegisch).</summary>
    private static Language? TrailingLanguage(string name, out int dot)
    {
        dot = name.LastIndexOf('.');
        if (dot <= 0) return null;

        var segment = name[(dot + 1)..];
        return segment.Contains(' ') ? null : Languages.Find(segment);
    }

    /// <summary>Name ohne Sprachkürzel am Ende („Film.German“ → „Film“) und ohne Zeichen, die Dateisysteme nicht mögen.</summary>
    private static string CleanBaseName(string name)
    {
        if (TrailingLanguage(name, out var dot) is not null) name = name[..dot];

        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        if (name.Length > MaxBaseNameLength) name = name[..MaxBaseNameLength].TrimEnd();
        return name.Length == 0 ? "Untertitel" : name;
    }
}
