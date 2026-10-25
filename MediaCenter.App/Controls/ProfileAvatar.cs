using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MediaCenter.Core;

namespace MediaCenter.App.Controls;

/// <summary>Runder Avatar des Profils: das hochgeladene Bild, sonst ein Farbkreis mit dem Anfangsbuchstaben.</summary>
public static class ProfileAvatar
{
    /// <summary>Holt die Bilddaten (PNG) eines Profils; das Hauptfenster setzt das beim Start auf die Datenbank.</summary>
    public static Func<long, byte[]?>? Loader { get; set; }

    private static readonly Dictionary<long, byte[]> Cache = new();

    /// <summary>Nach einer Änderung des Bildes: beim nächsten Zeichnen neu aus der Datenbank laden.</summary>
    public static void Forget(long profileId) => Cache.Remove(profileId);

    public static Border Create(ProfileRow profile, double size)
    {
        if (!profile.HasAvatar) return Create(profile.Name, profile.Color, size);

        if (!Cache.TryGetValue(profile.Id, out var png))
        {
            var loader = Loader ?? throw new InvalidOperationException("ProfileAvatar.Loader ist nicht gesetzt.");
            png = loader(profile.Id) ?? throw new InvalidOperationException($"Profil {profile.Id} hat ein Bild laut Liste, aber keine Bilddaten.");
            Cache[profile.Id] = png;
        }
        return Create(png, size);
    }

    /// <summary>Avatar aus Bilddaten (PNG), z. B. für die Vorschau vor dem Speichern.</summary>
    public static Border Create(byte[] png, double size)
    {
        var image = new System.Windows.Media.Imaging.BitmapImage();
        image.BeginInit();
        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        image.StreamSource = new System.IO.MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill },
        };
    }

    public static Border Create(string name, string colorHex, double size)
    {
        var color = (Color)ColorConverter.ConvertFromString(colorHex);
        // Dunkle Schrift auf hellen Farben, helle auf dunklen (Wahrnehmungshelligkeit).
        var luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255;
        var trimmed = name.Trim();
        var initial = trimmed.Length == 0 ? "" : new StringInfo(trimmed).SubstringByTextElements(0, 1).ToUpperInvariant(); // ohne Namen: nur der Farbkreis

        var letter = new TextBlock
        {
            Text = initial,
            FontSize = size * 0.46,
            Foreground = luminance > 0.55 ? new SolidColorBrush(Color.FromRgb(0x0B, 0x12, 0x20)) : Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        letter.SetResourceReference(TextBlock.FontFamilyProperty, "BodySemiFont");

        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = new SolidColorBrush(color),
            Child = letter,
        };
    }
}
