using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MediaCenter.Core;

namespace MediaCenter.App.Controls;

/// <summary>Runder Farbkreis mit dem Anfangsbuchstaben des Profils.</summary>
public static class ProfileAvatar
{
    public static Border Create(ProfileRow profile, double size) => Create(profile.Name, profile.Color, size);

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
