using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MediaCenter.App.Services;

namespace MediaCenter.App.Controls;

/// <summary>Überschrift und waagerecht scrollende Reihe von Karten, mit Pfeilen an den Rändern.</summary>
public sealed class CardRow : Grid
{
    public CardRow(string title, IEnumerable<CardItem> items, Action<CardItem> onActivated)
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock { Text = title, FontSize = 30, Margin = new Thickness(0, 0, 0, 4) };
        header.SetResourceReference(StyleProperty, "DisplayText");
        Children.Add(header);

        var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 14) };
        foreach (var item in items)
        {
            var card = new CardView(item) { Margin = new Thickness(0, 0, 18, 0) };
            card.Activated += onActivated;
            strip.Children.Add(card);
        }

        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = strip,
            // Platz für die vergrößerte Karte beim Hover, sonst wird sie am Rand abgeschnitten.
            Margin = new Thickness(-12, -10, -12, -10),
            Padding = new Thickness(12, 10, 12, 10),
        };
        scroller.PreviewMouseWheel += (_, e) =>
        {
            // Das Mausrad soll die Seite scrollen, nicht die Reihe.
            e.Handled = true;
            var forwarded = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = scroller };
            ((UIElement)scroller.Parent).RaiseEvent(forwarded);
        };

        var holder = new Grid();
        holder.Children.Add(scroller);
        var left = Arrow("", HorizontalAlignment.Left, () => scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - scroller.ViewportWidth * 0.8));
        var right = Arrow("", HorizontalAlignment.Right, () => scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset + scroller.ViewportWidth * 0.8));
        holder.Children.Add(left);
        holder.Children.Add(right);
        scroller.ScrollChanged += (_, _) =>
        {
            left.Visibility = scroller.HorizontalOffset > 0 ? Visibility.Visible : Visibility.Collapsed;
            right.Visibility = scroller.HorizontalOffset < scroller.ScrollableWidth ? Visibility.Visible : Visibility.Collapsed;
        };
        SetRow(holder, 1);
        Children.Add(holder);
    }

    private static Button Arrow(string glyph, HorizontalAlignment side, Action scroll)
    {
        var button = new Button
        {
            Content = glyph,
            HorizontalAlignment = side,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(side == HorizontalAlignment.Left ? 4 : 0, 0, side == HorizontalAlignment.Right ? 4 : 0, 34),
            Visibility = Visibility.Collapsed,
            Width = 44,
            Height = 44,
        };
        button.SetResourceReference(StyleProperty, "IconButton");
        button.SetResourceReference(BackgroundProperty, "GlassTintBrush");
        button.Click += (_, _) => scroll();
        // Der Symbolknopf hat keinen eigenen Hintergrund; der runde Rand kommt über einen Rahmen.
        button.Template = ArrowTemplate();
        return button;
    }

    private static ControlTemplate ArrowTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(999));
        border.SetResourceReference(Border.BackgroundProperty, "BgSoftBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "GlassEdgeBrush");
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        template.VisualTree = border;
        return template;
    }
}
