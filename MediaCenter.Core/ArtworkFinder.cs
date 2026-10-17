namespace MediaCenter.Core;

public sealed record SidecarArt(string? Poster, string? Landscape)
{
    public static SidecarArt None { get; } = new(null, null);
}

/// <summary>Findet Cover-Dateien neben den Videos (Kodi-/Jellyfin-Konvention).</summary>
public static class SidecarFinder
{
    private static readonly string[] PosterNames = { "poster", "folder", "cover", "show" };
    private static readonly string[] LandscapeNames = { "fanart", "backdrop", "background", "landscape" };

    /// <summary>
    /// Zuerst Dateien mit dem Namen des Films (Name-poster.jpg, Name-fanart.jpg, Name.jpg), danach
    /// poster/folder/cover … im Ordner – aber nur, wenn dort genau ein Video liegt; sonst wäre unklar, zu welchem Film das Bild gehört.
    /// </summary>
    public static SidecarArt ForMovie(string videoPath)
    {
        var dir = Path.GetDirectoryName(videoPath);
        if (dir is null || !Directory.Exists(dir)) return SidecarArt.None;

        var files = ImagesIn(dir);
        var baseName = Path.GetFileNameWithoutExtension(videoPath);

        var poster = Find(files, $"{baseName}-poster", baseName);
        var landscape = Find(files, $"{baseName}-fanart", $"{baseName}-backdrop", $"{baseName}-landscape");

        var videosInFolder = Directory.EnumerateFiles(dir).Count(f => MediaExtensions.Video.Contains(Path.GetExtension(f)));
        if (videosInFolder == 1)
        {
            poster ??= Find(files, PosterNames);
            landscape ??= Find(files, LandscapeNames);
        }
        return new SidecarArt(poster, landscape);
    }

    /// <summary>
    /// Sucht im Serienordner (bei Staffelordnern: eine Ebene höher). Bilder dort gelten nur, wenn alle Videos
    /// unterhalb dieses Ordners zur Serie gehören – ein gemeinsamer Ordner mehrerer Serien liefert kein eindeutiges Cover.
    /// </summary>
    public static SidecarArt ForSeries(IReadOnlyCollection<string> videoPaths)
    {
        var first = videoPaths.FirstOrDefault();
        var dir = first is null ? null : Path.GetDirectoryName(first);
        if (dir is null) return SidecarArt.None;

        if (FileNameParser.IsSeasonFolder(Path.GetFileName(dir))) dir = Path.GetDirectoryName(dir);
        if (dir is null || !Directory.Exists(dir)) return SidecarArt.None;

        var rootPrefix = dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var owned = videoPaths.Count(p => p.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase));
        var onDisk = Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true })
            .Count(f => MediaExtensions.Video.Contains(Path.GetExtension(f)));
        if (owned != onDisk) return SidecarArt.None;

        var files = ImagesIn(dir);
        return new SidecarArt(Find(files, PosterNames), Find(files, LandscapeNames));
    }

    /// <summary>Dateiname ohne Endung (klein) → Pfad, nur Bilder direkt in diesem Ordner.</summary>
    private static Dictionary<string, string> ImagesIn(string dir)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (MediaExtensions.Image.Contains(Path.GetExtension(file))) result.TryAdd(Path.GetFileNameWithoutExtension(file), file);
        }
        return result;
    }

    private static string? Find(Dictionary<string, string> files, params string[] names)
    {
        foreach (var name in names)
        {
            if (files.TryGetValue(name, out var path)) return path;
        }
        return null;
    }
}

/// <summary>Findet das Cover eines Albums im Ordner seiner Titel.</summary>
public static class AlbumCoverFinder
{
    private static readonly string[] Names = { "cover", "folder", "front", "album" };

    public static string? Find(string directory)
    {
        if (!Directory.Exists(directory)) return null;

        var images = Directory.EnumerateFiles(directory)
            .Where(f => MediaExtensions.Image.Contains(Path.GetExtension(f)))
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => f, StringComparer.OrdinalIgnoreCase);
        return Names.Select(n => images.GetValueOrDefault(n)).FirstOrDefault(p => p is not null);
    }
}

/// <summary>Wählt aus den in einer Datei eingebetteten Bildern Poster und Querformat-Bild aus.</summary>
public static class EmbeddedArtwork
{
    /// <summary>Nach Seitenverhältnis statt Dateiname: Hochformat und Quadrat sind Poster, Querformat ist Landscape.</summary>
    public static (StreamInfo? Poster, StreamInfo? Landscape) Choose(ProbeResult probe)
    {
        StreamInfo? poster = null, landscape = null;
        foreach (var picture in probe.AttachedPictures)
        {
            if (picture.Width is not { } w || picture.Height is not { } h || w <= 0 || h <= 0) continue;
            if (h >= w) poster ??= picture;
            else landscape ??= picture;
        }
        return (poster, landscape);
    }
}
