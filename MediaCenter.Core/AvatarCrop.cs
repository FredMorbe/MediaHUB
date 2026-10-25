namespace MediaCenter.Core;

/// <summary>Ein quadratischer Ausschnitt eines Bildes in Bildpunkten.</summary>
public readonly record struct CropSquare(int X, int Y, int Size);

/// <summary>
/// Rechnung hinter dem Zuschneiden eines Profilbilds: Das Bild liegt mit Maßstab <c>scale</c> (Anzeigepunkte je Bildpunkt) und der linken oberen Ecke
/// bei (offsetX, offsetY) in einer Fläche; der Kreis darin (Anfang <c>circleX/circleY</c>, Durchmesser <c>circle</c>) ist der sichtbare Ausschnitt.
/// Das Bild muss den Kreis immer ganz bedecken.
/// </summary>
public static class AvatarCrop
{
    /// <summary>Größter Zoom, bezogen auf den kleinsten (Bild füllt den Kreis gerade).</summary>
    public const double MaxZoom = 5;

    /// <summary>Kleinster Maßstab: Die kürzere Bildseite füllt den Kreis.</summary>
    public static double MinScale(int imageWidth, int imageHeight, double circle)
    {
        if (imageWidth <= 0 || imageHeight <= 0) throw new ArgumentOutOfRangeException(nameof(imageWidth), "Das Bild hat keine Größe.");
        if (circle <= 0) throw new ArgumentOutOfRangeException(nameof(circle), circle, "Der Kreis braucht eine Größe.");
        return circle / Math.Min(imageWidth, imageHeight);
    }

    /// <summary>Hält eine Bildkante so, dass der Kreis auf dieser Achse im Bild bleibt (<paramref name="dimension"/> = Bildpunkte auf der Achse).</summary>
    public static double ClampOffset(double offset, double scale, int dimension, double circleStart, double circle) =>
        Math.Clamp(offset, circleStart + circle - dimension * scale, circleStart);

    /// <summary>Anfangslage einer Achse: das Bild mittig im Kreis.</summary>
    public static double CenteredOffset(double scale, int dimension, double circleStart, double circle) =>
        circleStart + circle / 2 - dimension * scale / 2;

    /// <summary>Neuer Versatz einer Achse, wenn der Maßstab von <paramref name="oldScale"/> auf <paramref name="newScale"/> wechselt: Der Bildpunkt in der Kreismitte bleibt dort.</summary>
    public static double ZoomedOffset(double offset, double oldScale, double newScale, double circleStart, double circle)
    {
        var center = circleStart + circle / 2;
        return center - (center - offset) * newScale / oldScale;
    }

    /// <summary>Der Teil des Bildes im Kreis, in Bildpunkten und ganz im Bild (Rundung kann ihn um einen Punkt verschieben).</summary>
    public static CropSquare SourceSquare(int imageWidth, int imageHeight, double scale, double offsetX, double offsetY, double circleX, double circleY, double circle)
    {
        var size = Math.Clamp((int)Math.Round(circle / scale), 1, Math.Min(imageWidth, imageHeight));
        var x = Math.Clamp((int)Math.Round((circleX - offsetX) / scale), 0, imageWidth - size);
        var y = Math.Clamp((int)Math.Round((circleY - offsetY) / scale), 0, imageHeight - size);
        return new CropSquare(x, y, size);
    }
}
