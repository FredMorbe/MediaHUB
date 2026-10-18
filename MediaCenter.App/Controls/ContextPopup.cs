using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MediaCenter.App.Pages;

namespace MediaCenter.App.Controls;

/// <summary>Kleines Kontextmenü im Stil der App: eine Liste anklickbarer Zeilen in einem Popup.</summary>
internal static class ContextPopup
{
    /// <summary>Öffnet das Menü an der Maus (bei Tastatur oder Automation unter dem Element). Ein Klick schließt es und führt die Aktion aus.</summary>
    public static void Show(FrameworkElement owner, IEnumerable<(string Text, Action Click)> items, string? automationPrefix = null)
    {
        var popup = new Popup
        {
            PlacementTarget = owner,
            Placement = owner.IsMouseOver ? PlacementMode.MousePoint : PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            // Der Rahmen hat 14 Einheiten Rand für den Schatten; der Versatz hält die sichtbare Kante an der Maus.
            HorizontalOffset = -14,
            VerticalOffset = -14,
        };

        var list = new StackPanel();
        foreach (var (text, click) in items)
        {
            var label = new TextBlock { Text = text, FontSize = 13.5, Margin = new Thickness(6, 1, 12, 1) };
            label.SetResourceReference(TextBlock.FontFamilyProperty, "BodySemiFont");
            label.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
            var row = PageParts.ClickableRow(label, () =>
            {
                popup.IsOpen = false;
                click();
            }, new Thickness(0, 0, 0, 2), automationPrefix is null ? text : $"{automationPrefix} {text}");
            list.Children.Add(row);
        }

        var scroll = new ScrollViewer { Content = list, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var frame = new Border
        {
            MinWidth = 200, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(6), Margin = new Thickness(14), Child = scroll,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 8, Direction = 270, Opacity = 0.38, Color = System.Windows.Media.Colors.Black },
        };
        frame.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "GlassEdgeBrush");
        popup.Child = frame;
        popup.Closed += (_, _) => popup.Child = null;
        popup.IsOpen = true;
    }
}
