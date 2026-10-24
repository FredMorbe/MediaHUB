using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MediaCenter.App.Controls;

/// <summary>
/// Abgerundete Karte mit dem Titelbild einer Seite (Start, Detail): Bild, Verdunklung für den Text links und unten, helle Kante.
/// Liegt als unterste Ebene in einem Grid; Text und Knöpfe darüber binden die dunklen Farben aus <c>ThemeService.ArtTheme</c> ein.
/// </summary>
public sealed class HeroArt : Control
{
    public static readonly DependencyProperty ImageProperty = DependencyProperty.Register(
        nameof(Image), typeof(ImageSource), typeof(HeroArt));

    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(HeroArt), new PropertyMetadata(36.0));

    static HeroArt()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(HeroArt), new FrameworkPropertyMetadata(typeof(HeroArt)));
    }

    /// <summary>Das Querformat-Bild; ohne Bild bleibt die dunkle Grundfläche mit Farbschein.</summary>
    public ImageSource? Image
    {
        get => (ImageSource?)GetValue(ImageProperty);
        set => SetValue(ImageProperty, value);
    }

    public double Radius
    {
        get => (double)GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }
}
