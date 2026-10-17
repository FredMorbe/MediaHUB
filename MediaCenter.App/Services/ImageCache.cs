using System.IO;
using System.Windows.Media.Imaging;

namespace MediaCenter.App.Services;

/// <summary>Lädt Cover aus dem Cache der Bibliothek; der Schlüssel enthält die Änderungszeit, damit ersetzte Bilder neu geladen werden.</summary>
public static class ImageCache
{
    private static readonly Dictionary<string, BitmapImage> Cache = new();

    /// <returns>Das Bild oder null, wenn die Datei fehlt (z. B. Cache von Hand geleert).</returns>
    public static BitmapImage? Load(string? path, int decodeWidth)
    {
        if (path is null || !File.Exists(path)) return null;

        var key = $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}|{decodeWidth}";
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad; // Datei nicht gesperrt halten
        image.DecodePixelWidth = decodeWidth;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return Cache[key] = image;
    }
}
