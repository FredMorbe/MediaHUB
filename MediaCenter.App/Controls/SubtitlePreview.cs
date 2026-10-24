using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MediaCenter.Core;

namespace MediaCenter.App.Controls;

/// <summary>
/// Bildausschnitt mit Beispieluntertitel in einem <see cref="SubtitleStyle"/>: links heller, rechts dunkler Hintergrund, damit man die Lesbarkeit
/// auf beiden sieht. Größen folgen mpv: Schrift und Rand beziehen sich auf eine Bildhöhe von 720.
/// </summary>
public sealed class SubtitlePreview : FrameworkElement
{
    private const double ReferenceHeight = 720;

    private static readonly string[] Lines = { "So sieht dein Untertitel aus.", "Zwei Zeilen, helle und dunkle Szene." };

    private SubtitleStyle _style = SubtitleStyle.Default;

    public SubtitlePreview()
    {
        Width = 480;
        Height = 270;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    public SubtitleStyle Look
    {
        get => _style;
        set
        {
            _style = value;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRoundedRectangle(
            new LinearGradientBrush(Color.FromRgb(0x2E, 0x3B, 0x4E), Color.FromRgb(0x10, 0x14, 0x1C), 90), null, bounds, 10, 10);
        dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0xE9, 0xE4, 0xD6), Color.FromRgb(0xB9, 0xB2, 0xA0), 90), null,
            new Rect(0, 0, ActualWidth * 0.5, ActualHeight));

        var scale = ActualHeight / ReferenceHeight;
        var size = _style.FontSize * scale;
        var typeface = new Typeface(new FontFamily(_style.Font ?? "Arial"), FontStyles.Normal,
            _style.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        var text = Tint(_style.TextColor, 255);
        var outline = Tint(_style.OutlineColor, 255);
        var back = Tint("#000000", (byte)Math.Round(_style.BackgroundOpacity * 255 / 100.0));
        var box = _style.Background == SubtitleBackground.Box;

        var lines = Lines.Select(l => new FormattedText(l, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size,
            new SolidColorBrush(text), VisualTreeHelper.GetDpi(this).PixelsPerDip)).ToList();
        var bottom = ActualHeight - 34 * scale;
        var y = bottom - lines.Sum(l => l.Height);

        foreach (var line in lines)
        {
            var origin = new Point((ActualWidth - line.Width) / 2, y);
            if (box)
            {
                var pad = size * 0.25;
                dc.DrawRectangle(new SolidColorBrush(back), null, new Rect(origin.X - pad, origin.Y, line.Width + 2 * pad, line.Height));
            }

            var geometry = line.BuildGeometry(origin);
            if (!box && _style.ShadowOffset > 0)
            {
                var offset = _style.ShadowOffset * scale;
                dc.PushTransform(new TranslateTransform(offset, offset));
                dc.DrawGeometry(new SolidColorBrush(back), null, geometry);
                dc.Pop();
            }
            if (_style.OutlineSize > 0)
            {
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(outline), 2 * _style.OutlineSize * scale) { LineJoin = PenLineJoin.Round }, geometry);
            }
            dc.DrawGeometry(new SolidColorBrush(text), null, geometry);
            y += line.Height;
        }
    }

    private static Color Tint(string hex, byte alpha)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        c.A = alpha;
        return c;
    }
}
