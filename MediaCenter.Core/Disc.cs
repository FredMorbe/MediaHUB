using System.Security.Cryptography;
using System.Text;

namespace MediaCenter.Core;

/// <summary>Art einer Video-Disc. Steht (als Behälter „disc:dvd“ / „disc:bluray“) in <c>files.container</c>.</summary>
public enum DiscKind
{
    None = 0,
    Dvd = 1,
    BluRay = 2,
}

/// <summary>
/// Erkennt DVD- und Blu-ray-Ordner sowie Disc-Abbilder (ISO) und liest, was für den Scan nötig ist. Unterstützt werden unverschlüsselte Discs und Abbilder
/// (Eigenkopien); verschlüsselte Discs öffnen die Bibliotheken von mpv und ffmpeg nur, wenn sie selbst entschlüsseln können – die App umgeht keinen Kopierschutz.
/// </summary>
public static class DiscDetector
{
    public const string DvdContainer = "disc:dvd";

    public const string BluRayContainer = "disc:bluray";

    public const string ImageExtension = ".iso";

    /// <summary>Dateien, an denen sich eine Disc ändert (Abbild, DVD- und Blu-ray-Struktur): der Ordner-Wächter beachtet sie auch.</summary>
    public static readonly HashSet<string> WatchedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".iso", ".vob", ".ifo", ".bup", ".bdmv", ".mpls", ".clpi" };

    public static bool IsImage(string path) => Path.GetExtension(path).Equals(ImageExtension, StringComparison.OrdinalIgnoreCase);

    public static string? ContainerOf(DiscKind kind) => kind switch
    {
        DiscKind.Dvd => DvdContainer,
        DiscKind.BluRay => BluRayContainer,
        _ => null,
    };

    public static DiscKind FromContainer(string? container) => container switch
    {
        DvdContainer => DiscKind.Dvd,
        BluRayContainer => DiscKind.BluRay,
        _ => DiscKind.None,
    };

    public static string Label(DiscKind kind) => kind switch
    {
        DiscKind.Dvd => "DVD",
        DiscKind.BluRay => "Blu-ray",
        _ => "",
    };

    /// <summary>
    /// Ob der Ordner das Wurzelverzeichnis einer Disc ist: DVD mit „VIDEO_TS/VIDEO_TS.IFO“, Blu-ray mit „BDMV/index.bdmv“. Ein Ordner, der selbst
    /// VIDEO_TS heißt und die IFO-Datei enthält, ist eine DVD (libdvdread nimmt auch ihn). Groß- und Kleinschreibung ist egal.
    /// </summary>
    public static DiscKind OfFolder(string directory)
    {
        if (!Directory.Exists(directory)) return DiscKind.None;

        if (HasFile(Combine(directory, "VIDEO_TS"), "VIDEO_TS.IFO")) return DiscKind.Dvd;
        if (HasFile(Combine(directory, "BDMV"), "index.bdmv")) return DiscKind.BluRay;
        if (Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase)
            && HasFile(directory, "VIDEO_TS.IFO")) return DiscKind.Dvd;
        return DiscKind.None;
    }

    /// <summary>Der Unterordner mit diesem Namen (ohne Rücksicht auf die Schreibweise), sonst null.</summary>
    private static string? Combine(string directory, string name)
    {
        try
        {
            return Directory.EnumerateDirectories(directory).FirstOrDefault(d => Path.GetFileName(d).Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool HasFile(string? directory, string name)
    {
        if (directory is null) return false;
        try
        {
            return Directory.EnumerateFiles(directory).Any(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Gesamtgröße und neueste Änderungszeit aller Dateien eines Disc-Ordners: ändert sich eine, gilt die Disc als geändert.</summary>
    public static (long Size, DateTime MtimeUtc) Signature(string directory)
    {
        long size = 0;
        var newest = DateTime.MinValue;
        foreach (var file in Directory.EnumerateFiles(directory, "*", new EnumerationOptions { RecurseSubdirectories = true }))
        {
            var info = new FileInfo(file);
            size += info.Length;
            if (info.LastWriteTimeUtc > newest) newest = info.LastWriteTimeUtc;
        }
        return (size, newest == DateTime.MinValue ? Directory.GetLastWriteTimeUtc(directory) : newest);
    }

    /// <summary>
    /// Fingerabdruck eines Disc-Ordners aus Namen und Größen seiner Dateien (nicht aus dem Inhalt: eine Disc hat Gigabytes). Er überlebt Verschieben und
    /// Umbenennen des Ordners; so findet der Scan eine verschobene Disc wieder und ihre Nutzerdaten wandern mit.
    /// </summary>
    public static string Fingerprint(string directory)
    {
        var root = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var entries = Directory.EnumerateFiles(directory, "*", new EnumerationOptions { RecurseSubdirectories = true })
            .Select(f => (Name: Path.GetRelativePath(root, f).Replace('\\', '/').ToUpperInvariant(), Size: new FileInfo(f).Length))
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToList();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes("disc"));
        foreach (var (name, size) in entries)
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"{name}:{size};"));
        }
        return Convert.ToHexString(hash.GetHashAndReset(), 0, 16).ToLowerInvariant();
    }

    /// <summary>
    /// Alles, was der Scan in einem Ordner anzeigen soll: Disc-Ordner als eine Einheit (ihr Inneres bleibt unberührt), Disc-Abbilder, Video- und Audiodateien.
    /// </summary>
    public static IEnumerable<ScanEntry> Enumerate(string directory)
    {
        if (OfFolder(directory) is var kind and not DiscKind.None)
        {
            yield return new ScanEntry(directory, kind, false);
            yield break;
        }

        var options = new EnumerationOptions { IgnoreInaccessible = true };
        foreach (var file in Directory.EnumerateFiles(directory, "*", options))
        {
            var extension = Path.GetExtension(file);
            if (IsImage(file)) yield return new ScanEntry(file, DiscKind.None, true);
            else if (MediaExtensions.Video.Contains(extension) || MediaExtensions.Audio.Contains(extension)) yield return new ScanEntry(file, DiscKind.None, false);
        }
        foreach (var sub in Directory.EnumerateDirectories(directory, "*", options))
        {
            foreach (var entry in Enumerate(sub)) yield return entry;
        }
    }
}

/// <summary>Ein Eintrag des Scans: eine Datei, ein Disc-Ordner (<paramref name="Disc"/>) oder ein Disc-Abbild (<paramref name="IsImage"/>; die Art findet erst die Probe heraus).</summary>
public sealed record ScanEntry(string Path, DiscKind Disc, bool IsImage)
{
    public bool IsDisc => Disc != DiscKind.None || IsImage;
}

/// <summary>Liest eine Disc (Ordner oder Abbild) über ffprobe.</summary>
public interface IDiscProbe
{
    /// <summary>
    /// Liest die Hauptspur der Disc (den längsten Titel). <paramref name="hint"/> ist die Art, wenn der Aufrufer sie schon kennt (Ordner); bei
    /// <see cref="DiscKind.None"/> (Abbild) wird DVD, dann Blu-ray versucht. Das Ergebnis trägt <see cref="DiscDetector.DvdContainer"/> bzw.
    /// <see cref="DiscDetector.BluRayContainer"/> als Behälter.
    /// </summary>
    /// <exception cref="InvalidDataException">Weder DVD noch Blu-ray lesbar (z. B. ein ISO mit Daten oder eine verschlüsselte Disc).</exception>
    Task<(DiscKind Kind, ProbeResult Result)> ProbeDiscAsync(string path, DiscKind hint, CancellationToken ct = default);
}

/// <summary>Wie mpv und ffmpeg eine Disc öffnen.</summary>
public static class DiscInput
{
    /// <summary>mpv: die Eigenschaft, die den Ort der Disc setzt, und die Adresse, die den Hauptfilm (den längsten Titel) abspielt.</summary>
    public static (string DeviceProperty, string Url) ForMpv(DiscKind kind) => kind switch
    {
        DiscKind.Dvd => ("dvd-device", "dvd://longest"),
        DiscKind.BluRay => ("bluray-device", "bd://longest"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Keine Disc."),
    };

    /// <summary>
    /// ffmpeg/ffprobe: die Eingabe als eine Zeichenkette, die <c>FfmpegTool</c> versteht („dvdvideo:Pfad“ → „-f dvdvideo -i Pfad“; „bluray:Pfad“ ist ein
    /// ffmpeg-Protokoll und geht unverändert hinein). Pfade mit Rückstrichen schreibt ffmpeg bei „bluray:“ mit Schrägstrichen.
    /// </summary>
    public static string ForFfmpeg(DiscKind kind, string path) => kind switch
    {
        DiscKind.Dvd => DvdPrefix + path,
        DiscKind.BluRay => "bluray:" + path.Replace('\\', '/'),
        _ => path,
    };

    public const string DvdPrefix = "dvdvideo:";

    /// <summary>Teilt eine Eingabe aus <see cref="ForFfmpeg"/> in Format-Argumente (vor „-i“) und die Eingabe selbst.</summary>
    public static (string[] FormatArgs, string Input) SplitFfmpeg(string input) =>
        input.StartsWith(DvdPrefix, StringComparison.Ordinal) ? (new[] { "-f", "dvdvideo" }, input[DvdPrefix.Length..]) : (Array.Empty<string>(), input);
}
