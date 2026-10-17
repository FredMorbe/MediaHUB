namespace MediaCenter.Core;

public enum AspectKind
{
    /// <summary>Seitenverhältnis der Datei.</summary>
    Original,

    /// <summary>Fest vorgegebenes Seitenverhältnis (mpv: video-aspect-override).</summary>
    Ratio,

    /// <summary>Fenster ausfüllen, der Überstand wird abgeschnitten (mpv: panscan).</summary>
    Fill,

    /// <summary>Fenster ausfüllen, das Bild wird verzerrt (mpv: keepaspect=no).</summary>
    Stretch,
}

/// <param name="Mpv">Wert für video-aspect-override; nur bei <see cref="AspectKind.Ratio"/>.</param>
public sealed record AspectOption(string Id, string Label, AspectKind Kind, string? Mpv = null)
{
    public static readonly AspectOption Original = new("original", "Original", AspectKind.Original);

    /// <summary>Reihenfolge wie im Menü; „Nächstes Seitenverhältnis“ (Taste A) läuft sie der Reihe nach durch.</summary>
    public static readonly IReadOnlyList<AspectOption> All = new[]
    {
        Original,
        new AspectOption("4:3", "4:3", AspectKind.Ratio, "4:3"),
        new AspectOption("5:4", "5:4", AspectKind.Ratio, "5:4"),
        new AspectOption("16:9", "16:9", AspectKind.Ratio, "16:9"),
        new AspectOption("16:10", "16:10", AspectKind.Ratio, "16:10"),
        new AspectOption("21:9", "21:9", AspectKind.Ratio, "21:9"),
        new AspectOption("2.35:1", "2,35:1", AspectKind.Ratio, "235:100"),
        new AspectOption("1.85:1", "1,85:1", AspectKind.Ratio, "185:100"),
        new AspectOption("fill", "Füllen (zuschneiden)", AspectKind.Fill),
        new AspectOption("stretch", "Strecken", AspectKind.Stretch),
    };

    /// <summary>Das vorgegebene Seitenverhältnis als Zahl (Breite / Höhe); null bei allen anderen Arten.</summary>
    public double? Ratio
    {
        get
        {
            if (Kind != AspectKind.Ratio) return null;
            var parts = Mpv!.Split(':');
            return double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture) / double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public AspectOption Next() => All[(IndexOf(this) + 1) % All.Count];

    private static int IndexOf(AspectOption option)
    {
        for (var i = 0; i < All.Count; i++)
        {
            if (All[i] == option) return i;
        }
        throw new ArgumentException($"Unbekanntes Seitenverhältnis: {option.Id}", nameof(option));
    }
}

/// <summary>Formate, aus denen sich ergibt, wie viel vom Bild beim Zoomen überstehen kann.</summary>
/// <param name="VideoAspect">Seitenverhältnis der Datei (Breite / Höhe, ohne Drehung und ohne eigene Vorgabe).</param>
/// <param name="WindowAspect">Seitenverhältnis des Videofensters.</param>
public sealed record PictureFit(double VideoAspect, double WindowAspect);

/// <summary>
/// Bildeinstellungen der laufenden Wiedergabe: Seitenverhältnis, Tempo, Zoom samt Ausschnitt und Drehung.
/// Gelten nur für das gerade gespielte Video; die Standardwerte ändern nichts.
/// </summary>
/// <param name="Speed">1 = normal (mpv: speed).</param>
/// <param name="ZoomPercent">100 = Bild passt ins Fenster; darüber wird vergrößert (mpv: video-zoom).</param>
/// <param name="PanX">Lage des Ausschnitts: −100 = linker Rand des Bildes, 0 = Mitte, 100 = rechter Rand.</param>
/// <param name="PanY">Lage des Ausschnitts: −100 = oberer Rand des Bildes, 0 = Mitte, 100 = unterer Rand.</param>
/// <param name="Rotation">Vielfaches von 90, im Uhrzeigersinn (mpv: video-rotate).</param>
/// <param name="Fit">Formate von Video und Fenster; ohne sie (Datei noch nicht geladen) gilt: das Bild füllt das Fenster.</param>
public sealed record PictureSettings(
    AspectOption Aspect,
    double Speed = 1,
    int ZoomPercent = 100,
    int PanX = 0,
    int PanY = 0,
    int Rotation = 0,
    PictureFit? Fit = null)
{
    public const int MinZoomPercent = 50;
    public const int MaxZoomPercent = 400;
    public const int ZoomStep = 10;
    public const int PanStep = 10;

    /// <summary>Stufen für Tempo-Tasten und Auswahl; 0,25 bis 8 deckt Zeitlupe bis Schnellvorlauf ab.</summary>
    public static readonly IReadOnlyList<double> SpeedSteps = new[] { 0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4, 8 };

    public static readonly PictureSettings Default = new(AspectOption.Original);

    public bool IsDefault => this with { Fit = null } == Default;

    /// <summary>Wirft bei Werten außerhalb des sinnvollen Bereichs (nie still klemmen).</summary>
    public PictureSettings Validated()
    {
        if (!double.IsFinite(Speed) || Speed < SpeedSteps[0] || Speed > SpeedSteps[^1])
            throw new ArgumentOutOfRangeException(nameof(Speed), Speed, $"Tempo zwischen {SpeedSteps[0]} und {SpeedSteps[^1]}.");
        if (ZoomPercent < MinZoomPercent || ZoomPercent > MaxZoomPercent)
            throw new ArgumentOutOfRangeException(nameof(ZoomPercent), ZoomPercent, $"Zoom zwischen {MinZoomPercent} und {MaxZoomPercent} %.");
        if (PanX is < -100 or > 100)
            throw new ArgumentOutOfRangeException(nameof(PanX), PanX, "Ausschnitt waagerecht zwischen −100 und 100.");
        if (PanY is < -100 or > 100)
            throw new ArgumentOutOfRangeException(nameof(PanY), PanY, "Ausschnitt senkrecht zwischen −100 und 100.");
        if (Rotation is not (0 or 90 or 180 or 270))
            throw new ArgumentOutOfRangeException(nameof(Rotation), Rotation, "Drehung nur 0, 90, 180 oder 270 Grad.");
        return this;
    }

    /// <summary>Der nächstgrößere (<paramref name="direction"/> &gt; 0) oder nächstkleinere Tempo-Schritt; null am Ende der Skala.</summary>
    public static double? StepSpeed(double current, int direction)
    {
        if (direction > 0) return SpeedSteps.Cast<double?>().FirstOrDefault(s => s > current + 1e-9);
        return SpeedSteps.Cast<double?>().LastOrDefault(s => s < current - 1e-9);
    }

    /// <summary>Drehung nach Addition von <paramref name="degrees"/> (Vielfaches von 90), immer 0, 90, 180 oder 270.</summary>
    public static int Rotated(int rotation, int degrees)
    {
        if (degrees % 90 != 0) throw new ArgumentOutOfRangeException(nameof(degrees), degrees, "Drehung nur in 90-Grad-Schritten.");
        return ((rotation + degrees) % 360 + 360) % 360;
    }

    // --- Werte für mpv ----------------------------------------------------------------------------------------

    /// <summary>mpv zählt den Zoom in Zweierpotenzen: 0 = 100 %, 1 = 200 %.</summary>
    public double MpvZoom => Math.Log2(ZoomPercent / 100.0);

    /// <summary>
    /// Größe des Bildes bei 100 % Zoom im Verhältnis zum Fenster (1 = füllt die Breite bzw. Höhe): ein breiter Film im
    /// 16:9-Fenster füllt die Breite, aber nicht die Höhe.
    /// </summary>
    private (double Width, double Height) Fitted()
    {
        if (Fit is not { } fit || Aspect.Kind == AspectKind.Stretch) return (1, 1);

        var aspect = Aspect.Ratio ?? fit.VideoAspect;
        if (Rotation is 90 or 270) aspect = 1 / aspect;
        var relative = aspect / fit.WindowAspect; // >1: Bild breiter als das Fenster
        var fill = Aspect.Kind == AspectKind.Fill;
        if (relative >= 1) return fill ? (relative, 1) : (1, 1 / relative);
        return fill ? (1, 1 / relative) : (relative, 1);
    }

    private static double Overhang(double zoomFactor, double fitted) => Math.Max(0, (1 - 1 / (zoomFactor * fitted)) / 2);

    /// <summary>
    /// Wie weit sich das Bild waagerecht verschieben lässt, als Anteil der Bildbreite: bis der Bildrand am Fensterrand liegt
    /// (bei 200 % und füllender Breite ±0,25). 0, wenn das Bild nicht über das Fenster hinausreicht.
    /// </summary>
    public double PanRangeX => Overhang(ZoomPercent / 100.0, Fitted().Width);

    public double PanRangeY => Overhang(ZoomPercent / 100.0, Fitted().Height);

    /// <summary>mpv verschiebt das Bild, nicht den Ausschnitt: Ausschnitt rechts heißt Bild nach links.</summary>
    public double MpvPanX => PanX == 0 ? 0 : -PanX / 100.0 * PanRangeX;

    public double MpvPanY => PanY == 0 ? 0 : -PanY / 100.0 * PanRangeY;

    public string MpvAspectOverride => Aspect.Kind == AspectKind.Ratio ? Aspect.Mpv! : "no";

    public double MpvPanscan => Aspect.Kind == AspectKind.Fill ? 1 : 0;

    public bool MpvKeepAspect => Aspect.Kind != AspectKind.Stretch;
}
