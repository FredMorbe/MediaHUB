using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MediaCenter.App.Pages;

/// <summary>Kleine Bausteine, die mehrere Seiten teilen (Überschrift, Karte, Seitenrahmen mit Bildlauf).</summary>
internal static class PageParts
{
    public static TextBlock Heading(string text, double size = 64)
    {
        var block = new TextBlock { Text = text, FontSize = size };
        block.SetResourceReference(FrameworkElement.StyleProperty, "DisplayText");
        return block;
    }

    public static TextBlock Muted(string text, double size = 14)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(FrameworkElement.StyleProperty, "MutedText");
        return block;
    }

    public static Button Button(string text, string style, RoutedEventHandler click, Thickness? margin = null)
    {
        var button = new Button { Content = text, Margin = margin ?? new Thickness(0, 0, 10, 0), HorizontalAlignment = HorizontalAlignment.Left };
        button.SetResourceReference(FrameworkElement.StyleProperty, style);
        button.Click += click;
        return button;
    }

    /// <summary>Abgesetzte Fläche für eine Gruppe von Inhalten.</summary>
    public static Border Card(UIElement child, Thickness? margin = null)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(20),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(22, 18, 22, 18),
            Margin = margin ?? new Thickness(0, 0, 0, 14),
            Child = child,
        };
        card.SetResourceReference(Border.BackgroundProperty, "CardBgBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "GlassEdgeBrush");
        return card;
    }

    /// <summary>Kleine Pille mit Text, z. B. für den Zustand eines Laufwerks.</summary>
    public static Border Badge(string text, bool highlighted)
    {
        var label = new TextBlock { Text = text, FontSize = 12 };
        label.SetResourceReference(TextBlock.FontFamilyProperty, "BodySemiFont");
        label.SetResourceReference(TextBlock.ForegroundProperty, highlighted ? "AccentBrush" : "MutedBrush");
        var badge = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(11, 3, 11, 3), BorderThickness = new Thickness(1.5), Child = label };
        badge.SetResourceReference(Border.BackgroundProperty, highlighted ? "AccentSoftBrush" : "LineBrush");
        badge.SetResourceReference(Border.BorderBrushProperty, highlighted ? "AccentLineBrush" : "GlassEdgeBrush");
        return badge;
    }

    /// <summary>Eingabefeld mit Hinweistext, der verschwindet, sobald etwas drinsteht.</summary>
    public static (Grid Field, TextBox Box) HintBox(string hint, double width)
    {
        var box = new TextBox { Width = width };
        var label = new TextBlock { Text = hint, FontSize = 14, Margin = new Thickness(14, 0, 0, 0), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(FrameworkElement.StyleProperty, "MutedText");
        box.TextChanged += (_, _) => label.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        return (new Grid { Children = { box, label } }, box);
    }

    /// <summary>Zeile einer Liste: beim Überfahren hinterlegt, Klick oder Enter löst die Aktion aus (Klicks auf Knöpfe darin zählen nicht).</summary>
    public static Border ClickableRow(UIElement content, Action onClick, Thickness? margin = null, string? name = null)
    {
        var row = new RowControl(onClick)
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = margin ?? new Thickness(0, 0, 0, 4),
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = content,
            Focusable = true,
            FocusVisualStyle = null,
        };
        if (name is not null) System.Windows.Automation.AutomationProperties.SetName(row, name);
        row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "LineBrush");
        row.MouseLeave += (_, _) => row.Background = row.IsKeyboardFocused ? (Brush)row.FindResource("LineBrush") : Brushes.Transparent;
        row.GotKeyboardFocus += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "LineBrush");
        row.LostKeyboardFocus += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => row.Activate();
        row.KeyDown += (_, e) =>
        {
            if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space)
            {
                row.Activate();
                e.Handled = true;
            }
        };
        return row;
    }

    /// <summary>Anklickbare Zeile; für Screenreader und Automation ein Knopf.</summary>
    private sealed class RowControl(Action onClick) : Border
    {
        public void Activate() => onClick();

        protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        private sealed class Peer(RowControl owner) : System.Windows.Automation.Peers.FrameworkElementAutomationPeer(owner),
            System.Windows.Automation.Provider.IInvokeProvider
        {
            protected override string GetClassNameCore() => "ListRow";

            protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() =>
                System.Windows.Automation.Peers.AutomationControlType.Button;

            public override object? GetPattern(System.Windows.Automation.Peers.PatternInterface patternInterface) =>
                patternInterface == System.Windows.Automation.Peers.PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

            public void Invoke() => owner.Dispatcher.BeginInvoke(owner.Activate);
        }
    }

    /// <summary>Abgerundetes Quadrat für Cover und Logos: Bild oder Notensymbol.</summary>
    public static Border Thumb(ImageSource? image, double size, string glyph = "")
    {
        var thumb = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(Math.Max(8, size / 6)), VerticalAlignment = VerticalAlignment.Center };
        if (image is not null)
        {
            thumb.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            return thumb;
        }
        thumb.SetResourceReference(Border.BackgroundProperty, "CardBgBrush");
        var icon = new TextBlock { Text = glyph, FontSize = size / 3, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        icon.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        thumb.Child = icon;
        return thumb;
    }

    public static TextBlock Semi(string text, double size = 16)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis };
        block.SetResourceReference(TextBlock.FontFamilyProperty, "BodySemiFont");
        return block;
    }

    public static string FormatDuration(double? seconds)
    {
        if (seconds is not { } s || s <= 0) return "";
        var span = TimeSpan.FromSeconds(s);
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes:00} min" : span.TotalMinutes >= 1 ? $"{Math.Round(span.TotalMinutes):0} min" : $"{span.Seconds} s";
    }

    public static string FormatClock(double? seconds)
    {
        if (seconds is not { } s || s <= 0) return "";
        var span = TimeSpan.FromSeconds(s);
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
    }

    /// <summary>Seitenrahmen unterhalb der Navigation: Inhalt scrollt senkrecht.</summary>
    public static ScrollViewer Scrolling(Panel content)
    {
        content.Margin = new Thickness(48, 92, 48, 60);
        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = content,
        };
    }
}
