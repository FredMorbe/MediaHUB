using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaCenter.Core;

namespace MediaCenter.App.Services;

/// <summary>Macht aus einer hochgeladenen Bilddatei das Profilbild: mittig quadratisch zugeschnitten, höchstens 256 × 256 Pixel, als PNG.</summary>
public static class AvatarImage
{
    public const int Size = 256;

    /// <summary>Größte Datei, die gelesen wird (Fotos sind selten größer); schützt davor, ein riesiges Bild zu entpacken.</summary>
    public const long MaxSourceBytes = 30L * 1024 * 1024;

    /// <exception cref="InvalidDataException">Die Datei ist zu groß oder kein Bild, das Windows lesen kann.</exception>
    public static byte[] FromFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Die Bilddatei gibt es nicht.", path);
        if (info.Length > MaxSourceBytes) throw new InvalidDataException($"Die Bilddatei ist größer als {MaxSourceBytes / (1024 * 1024)} MB.");

        BitmapImage source;
        try
        {
            source = new BitmapImage();
            source.BeginInit();
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.UriSource = new Uri(path);
            source.EndInit();
            source.Freeze();
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException)
        {
            throw new InvalidDataException($"„{Path.GetFileName(path)}“ ist kein Bild, das sich lesen lässt (PNG, JPG, BMP, GIF, TIFF).", ex);
        }

        var side = Math.Min(source.PixelWidth, source.PixelHeight);
        var square = new CroppedBitmap(source, new Int32Rect((source.PixelWidth - side) / 2, (source.PixelHeight - side) / 2, side, side));
        var scale = Math.Min(1.0, Size / (double)side);
        BitmapSource result = scale < 1 ? new TransformedBitmap(square, new ScaleTransform(scale, scale)) : square;

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(result));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        var png = stream.ToArray();
        if (png.Length > LibraryDb.MaxAvatarBytes) throw new InvalidDataException("Das Bild ist auch verkleinert noch zu groß.");
        return png;
    }
}
