using System.Globalization;
using System.Text.RegularExpressions;

namespace MediaCenter.Core;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum SubtitleBackground
{
    /// <summary>Schrift mit Rand und weichem Schatten, ohne Fläche dahinter (mpv-Standard).</summary>
    Outline,

    /// <summary>Halbtransparenter Kasten hinter jeder Zeile.</summary>
    Box,
}

/// <summary>
/// Aussehen der Untertitel für alle Videos (Schrift, Größe, Farben, Rand, Hintergrund). Unabhängig davon stellt der Nutzer pro Video
/// Zeitversatz, Maßstab und Höhe ein (<see cref="SubtitleAdjustment"/>). Die Standardwerte entsprechen denen von mpv.
/// </summary>
/// <param name="Font">Schriftname; null nimmt die Standardschrift von mpv.</param>
/// <param name="FontSize">Schriftgröße in mpv-Einheiten (bei 720 Zeilen Höhe; mpv-Standard 38).</param>
/// <param name="TextColor">Schriftfarbe als #RRGGBB.</param>
/// <param name="OutlineColor">Randfarbe als #RRGGBB.</param>
/// <param name="OutlineSize">Randstärke (mpv-Standard 1,65; 0 = kein Rand).</param>
/// <param name="ShadowOffset">Abstand des Schattens (0 = kein Schatten).</param>
/// <param name="BackgroundOpacity">Deckkraft von Schatten bzw. Kasten in Prozent.</param>
/// <param name="ForceStyle">Auch Untertitel mit eigenem Stil (ASS/SSA, häufig bei Anime) in diesem Aussehen zeigen.</param>
public sealed record SubtitleStyle(
    string? Font = null,
    double FontSize = SubtitleStyle.DefaultFontSize,
    string TextColor = "#FFFFFF",
    string OutlineColor = "#000000",
    double OutlineSize = 1.65,
    double ShadowOffset = 0,
    SubtitleBackground Background = SubtitleBackground.Outline,
    int BackgroundOpacity = 70,
    bool Bold = false,
    bool ForceStyle = false)
{
    public const double DefaultFontSize = 38;
    public const double MinFontSize = 20;
    public const double MaxFontSize = 90;
    public const double MaxOutlineSize = 6;
    public const double MaxShadowOffset = 6;

    private static readonly Regex ColorPattern = new("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

    public static readonly SubtitleStyle Default = new();

    /// <summary>Fertige Aussehen zum Antippen, in der Reihenfolge der Anzeige.</summary>
    public static IReadOnlyList<(string Name, SubtitleStyle Style)> Presets { get; } = new (string, SubtitleStyle)[]
    {
        ("Standard", Default),
        ("Groß und fett", new SubtitleStyle(FontSize: 52, Bold: true, OutlineSize: 2.4)),
        ("Gelb", new SubtitleStyle(TextColor: "#FFE14D", OutlineSize: 2, Bold: true)),
        ("Kasten", new SubtitleStyle(Background: SubtitleBackground.Box, BackgroundOpacity: 65, OutlineSize: 0.6)),
        ("Dezent", new SubtitleStyle(FontSize: 32, TextColor: "#E6E6E6", OutlineSize: 1.2, ShadowOffset: 1)),
    };

    public bool IsDefault => this == Default;

    /// <exception cref="ArgumentException">Ein Wert liegt außerhalb des sinnvollen Bereichs (nie still klemmen).</exception>
    public SubtitleStyle Validated()
    {
        if (Font is not null && (string.IsNullOrWhiteSpace(Font) || Font.Length > 80 || Font.Any(char.IsControl)))
            throw new ArgumentException("Der Schriftname ist leer, zu lang oder enthält Steuerzeichen.", nameof(Font));
        if (!double.IsFinite(FontSize) || FontSize is < MinFontSize or > MaxFontSize)
            throw new ArgumentException($"Schriftgröße zwischen {MinFontSize:0} und {MaxFontSize:0}.", nameof(FontSize));
        if (!ColorPattern.IsMatch(TextColor))
            throw new ArgumentException($"Schriftfarbe „{TextColor}“ ist keine Farbe der Form #RRGGBB.", nameof(TextColor));
        if (!ColorPattern.IsMatch(OutlineColor))
            throw new ArgumentException($"Randfarbe „{OutlineColor}“ ist keine Farbe der Form #RRGGBB.", nameof(OutlineColor));
        if (!double.IsFinite(OutlineSize) || OutlineSize is < 0 or > MaxOutlineSize)
            throw new ArgumentException($"Randstärke zwischen 0 und {MaxOutlineSize:0}.", nameof(OutlineSize));
        if (!double.IsFinite(ShadowOffset) || ShadowOffset is < 0 or > MaxShadowOffset)
            throw new ArgumentException($"Schatten zwischen 0 und {MaxShadowOffset:0}.", nameof(ShadowOffset));
        if (!Enum.IsDefined(Background))
            throw new ArgumentException($"Hintergrund „{Background}“ ist unbekannt.", nameof(Background));
        if (BackgroundOpacity is < 0 or > 100)
            throw new ArgumentException("Deckkraft des Hintergrunds zwischen 0 und 100 Prozent.", nameof(BackgroundOpacity));
        return this;
    }

    /// <summary>
    /// Die mpv-Eigenschaften, die dieses Aussehen setzen (alle, damit ein früherer Stil sicher überschrieben wird).
    /// Namen und Werte nach der mpv-Anleitung: Farben als #AARRGGBB, Flags als yes/no.
    /// </summary>
    public IReadOnlyList<(string Name, string Value)> ToMpvProperties()
    {
        Validated();
        var alpha = (int)Math.Round(BackgroundOpacity * 255 / 100.0);
        var back = "#" + alpha.ToString("X2", CultureInfo.InvariantCulture) + "000000";
        var box = Background == SubtitleBackground.Box;

        return new (string, string)[]
        {
            ("sub-font", Font ?? "sans-serif"),
            ("sub-font-size", Number(FontSize)),
            ("sub-color", TextColor),
            ("sub-outline-color", OutlineColor),
            ("sub-outline-size", Number(OutlineSize)),
            ("sub-shadow-offset", Number(ShadowOffset)),
            ("sub-back-color", back),
            ("sub-border-style", box ? "background-box" : "outline-and-shadow"),
            ("sub-bold", Bold ? "yes" : "no"),
            ("sub-ass-override", ForceStyle ? "force" : "scale"),
        };
    }

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
