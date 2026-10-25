using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaCenter.Core;

namespace MediaCenter.App.Services;

/// <summary>Liest eine hochgeladene Bilddatei und macht aus dem gewählten Ausschnitt das Profilbild (höchstens 256 × 256 Pixel, PNG).</summary>
public static class AvatarImage
{
    public const int Size = 256;

    /// <summary>Größte Datei, die gelesen wird (Fotos sind selten größer); schützt davor, ein riesiges Bild zu entpacken.</summary>
    public const long MaxSourceBytes = 30L * 1024 * 1024;

    /// <summary>Längere Bildseiten werden beim Lesen auf diese Länge verkleinert (spart Speicher bei Fotos von 12 Megapixeln und mehr).</summary>
    private const int MaxSide = 2048;

    /// <summary>Lädt das Bild so, wie es aussehen soll: Die Drehung aus den Foto-Daten (EXIF) ist angewendet.</summary>
    /// <exception cref="InvalidDataException">Die Datei ist zu groß oder kein Bild, das Windows lesen kann.</exception>
    public static BitmapSource Load(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Die Bilddatei gibt es nicht.", path);
        if (info.Length > MaxSourceBytes) throw new InvalidDataException($"Die Bilddatei ist größer als {MaxSourceBytes / (1024 * 1024)} MB.");

        try
        {
            var uri = new Uri(path);
            var frame = BitmapFrame.Create(uri, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var orientation = ReadOrientation(frame);

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = uri;
            if (Math.Max(frame.PixelWidth, frame.PixelHeight) > MaxSide)
            {
                // nur eine Seite angeben, damit das Seitenverhältnis bleibt
                if (frame.PixelWidth >= frame.PixelHeight) image.DecodePixelWidth = MaxSide;
                else image.DecodePixelHeight = MaxSide;
            }
            image.EndInit();
            image.Freeze();

            BitmapSource result = orientation switch
            {
                3 => new TransformedBitmap(image, new RotateTransform(180)),
                6 => new TransformedBitmap(image, new RotateTransform(90)),
                8 => new TransformedBitmap(image, new RotateTransform(270)),
                _ => image,
            };
            result.Freeze();
            return result;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException)
        {
            throw new InvalidDataException($"„{Path.GetFileName(path)}“ ist kein Bild, das sich lesen lässt (PNG, JPG, BMP, GIF, TIFF).", ex);
        }
    }

    /// <summary>Orientierung aus den Foto-Daten (1 = normal, 3 = 180°, 6 = 90° rechts, 8 = 90° links); ohne Angabe oder bei Spiegelungen 1.</summary>
    private static int ReadOrientation(BitmapFrame frame)
    {
        try
        {
            return frame.Metadata is BitmapMetadata metadata && metadata.GetQuery("System.Photo.Orientation") is ushort value ? value : 1;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return 1; // Format ohne Metadaten (PNG, BMP …)
        }
    }

    /// <summary>Schneidet den quadratischen Ausschnitt heraus, verkleinert ihn auf höchstens <see cref="Size"/> Pixel und kodiert ihn als PNG.</summary>
    /// <exception cref="InvalidDataException">Das Ergebnis wäre größer als <see cref="LibraryDb.MaxAvatarBytes"/>.</exception>
    public static byte[] Encode(BitmapSource source, CropSquare crop)
    {
        BitmapSource result = new CroppedBitmap(source, new Int32Rect(crop.X, crop.Y, crop.Size, crop.Size));
        var scale = Math.Min(1.0, Size / (double)crop.Size);
        if (scale < 1) result = new TransformedBitmap(result, new ScaleTransform(scale, scale));

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(result));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        var png = stream.ToArray();
        if (png.Length > LibraryDb.MaxAvatarBytes) throw new InvalidDataException("Das Bild ist auch verkleinert noch zu groß.");
        return png;
    }
}
