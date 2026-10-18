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

    /// <summary>
    /// Kopf einer Seite: Kleinzeile in der Akzentfarbe, großer Titel, darunter ein gedämpfter Text (z. B. Anzahl oder Hinweis).
    /// Den Text übernimmt die Seite als TextBlock, wenn sie ihn später ändert.
    /// </summary>
    public static StackPanel Header(string kicker, string title, TextBlock? subtitle = null, double size = 56)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, size < 56 ? 14 : 22) };
        if (kicker.Length > 0)
        {
            var label = new TextBlock { Text = kicker.ToUpperInvariant(), Margin = new Thickness(2, 0, 0, 0) };
            label.SetResourceReference(FrameworkElement.StyleProperty, "KickerText");
            stack.Children.Add(label);
        }
        stack.Children.Add(Heading(title, size));
        if (subtitle is not null)
        {
            subtitle.Margin = new Thickness(2, size < 56 ? 2 : 6, 0, 0);
            subtitle.MaxWidth = 820;
            subtitle.HorizontalAlignment = HorizontalAlignment.Left;
            subtitle.TextWrapping = TextWrapping.Wrap;
            stack.Children.Add(subtitle);
        }
        return stack;
    }

    public static StackPanel Header(string kicker, string title, string subtitle, double size = 56) => Header(kicker, title, Muted(subtitle, size < 56 ? 13 : 14), size);

    /// <summary>Überschrift eines Abschnitts innerhalb einer Seite.</summary>
    public static TextBlock SectionTitle(string text, Thickness? margin = null)
    {
        var block = new TextBlock { Text = text, FontSize = 28, Margin = margin ?? new Thickness(0, 30, 0, 10) };
        block.SetResourceReference(FrameworkElement.StyleProperty, "DisplayText");
        return block;
    }

    /// <summary>Leerer Zustand: Symbol, Titel, Erklärung und mögliche Schritte.</summary>
    public static UIElement EmptyState(string glyph, string title, string text, params UIElement[] actions)
    {
        var icon = new TextBlock { Text = glyph, FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        var disc = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(28), HorizontalAlignment = HorizontalAlignment.Left, Child = icon };
        disc.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");

        var heading = Semi(title, 18);
        heading.Margin = new Thickness(0, 16, 0, 0);
        heading.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        var body = Muted(text, 14);
        body.Margin = new Thickness(0, 6, 0, 0);
        body.MaxWidth = 520;
        body.HorizontalAlignment = HorizontalAlignment.Left;

        var stack = new StackPanel { Margin = new Thickness(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Children = { disc, heading, body } };
        if (actions.Length > 0)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 20, 0, 0) };
            foreach (var action in actions) row.Children.Add(action);
            stack.Children.Add(row);
        }
        return stack;
    }

    /// <summary>Kleinere Fassung eines Knopfes für enge Stellen (Listenzeilen, Werkzeugleisten).</summary>
    public static Button Compact(Button button)
    {
        button.Padding = new Thickness(16, 7, 16, 7);
        button.FontSize = 13;
        return button;
    }

    /// <summary>Auswahl aus wenigen Möglichkeiten als gemeinsame Pille; der gewählte Teil ist gefüllt. <paramref name="group"/> muss je Auswahl eindeutig sein.</summary>
    public static Border Segmented(string group, IEnumerable<(string Text, bool Selected, Action Apply)> items, string? automationPrefix = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (text, selected, apply) in items)
        {
            var segment = new RadioButton { Content = text, GroupName = group, IsChecked = selected };
            segment.SetResourceReference(FrameworkElement.StyleProperty, "SegmentButton");
            System.Windows.Automation.AutomationProperties.SetName(segment, $"{automationPrefix ?? group} {text}");
            segment.Checked += (_, _) => apply();
            row.Children.Add(segment);
        }
        var frame = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Left, Child = row };
        Pill(frame);
        frame.SetResourceReference(Border.BackgroundProperty, "LineBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "GlassEdgeBrush");
        return frame;
    }

    /// <summary>Rundet einen Rahmen zur Pille (halbe Höhe als Radius; ein fester großer Radius würde in WPF zur Ellipse).</summary>
    public static T Pill<T>(T border) where T : Border
    {
        border.SetBinding(Border.CornerRadiusProperty, new System.Windows.Data.Binding(nameof(FrameworkElement.ActualHeight)) { RelativeSource = System.Windows.Data.RelativeSource.Self, Converter = new Controls.PillRadiusConverter() });
        return border;
    }

    /// <summary>Feine Trennlinie.</summary>
    public static Border Divider(Thickness? margin = null)
    {
        var line = new Border { Height = 1, Margin = margin ?? new Thickness(0) };
        line.SetResourceReference(Border.BackgroundProperty, "LineBrush");
        return line;
    }

    /// <summary>
    /// Eine Einstellung oder ein Feld: links Name und Erklärung, rechts das Bedienelement. Mit <paramref name="stacked"/> steht das Element unter dem Text
    /// (für breite Elemente wie Farbtafeln oder lange Auswahllisten).
    /// </summary>
    public static UIElement SettingRow(string title, string? description, UIElement? control, bool stacked = false)
    {
        var name = Semi(title, 15);
        name.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name } };
        if (!string.IsNullOrEmpty(description))
        {
            var note = Muted(description, 13);
            note.Margin = new Thickness(0, 3, 0, 0);
            note.MaxWidth = stacked ? 760 : 520;
            note.HorizontalAlignment = HorizontalAlignment.Left;
            text.Children.Add(note);
        }

        var grid = new Grid { Margin = new Thickness(0, 16, 0, 16) };
        if (stacked || control is null)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(text);
            if (control is not null)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                if (control is FrameworkElement fe) fe.Margin = new Thickness(fe.Margin.Left, fe.Margin.Top + 12, fe.Margin.Right, fe.Margin.Bottom);
                Grid.SetRow(control, 1);
                grid.Children.Add(control);
            }
            return grid;
        }
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        text.Margin = new Thickness(0, 0, 28, 0);
        if (control is FrameworkElement element) element.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
        return grid;
    }

    /// <summary>Fläche, die mehrere Einstellungen mit feinen Linien dazwischen zusammenfasst.</summary>
    public static Border Group(params UIElement[] rows)
    {
        var stack = new StackPanel();
        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0) stack.Children.Add(Divider());
            stack.Children.Add(rows[i]);
        }
        var card = Card(stack, new Thickness(0, 0, 0, 16));
        card.Padding = new Thickness(26, 0, 26, 0);
        return card;
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
            CornerRadius = new CornerRadius(16),
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
    public static (Grid Field, TextBox Box) HintBox(string hint, double width, double fontSize = 14)
    {
        var box = new TextBox { Width = width, FontSize = fontSize };
        var label = new TextBlock { Text = hint, FontSize = fontSize, Margin = new Thickness(13, 0, 0, 0), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        label.SetResourceReference(FrameworkElement.StyleProperty, "MutedText");
        box.TextChanged += (_, _) => label.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        return (new Grid { Children = { box, label } }, box);
    }

    /// <summary>Zeile einer Liste: beim Überfahren hinterlegt, Klick oder Enter löst die Aktion aus (Klicks auf Knöpfe darin zählen nicht).</summary>
    public static Border ClickableRow(UIElement content, Action onClick, Thickness? margin = null, string? name = null)
    {
        var row = new RowControl(onClick)
        {
            CornerRadius = new CornerRadius(12),
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

    /// <summary>
    /// Kopfzeile einer Tabelle (Titellisten): kleine gedämpfte Beschriftung in den Spalten von <paramref name="widths"/>, die letzte Spalte
    /// rechtsbündig, darunter eine feine Linie. Fluchtet mit <see cref="TableRow"/>.
    /// </summary>
    public static UIElement TableHeader(IReadOnlyList<GridLength> widths, params string[] titles)
    {
        var grid = new Grid { Margin = new Thickness(8, 0, 8, 6) };
        for (var i = 0; i < widths.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = widths[i] });
            var label = Muted(i < titles.Length ? titles[i].ToUpperInvariant() : "", 11);
            label.TextWrapping = TextWrapping.NoWrap;
            label.Margin = new Thickness(0, 0, i < widths.Count - 1 ? 12 : 0, 0);
            if (i == widths.Count - 1) label.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(label, i);
            grid.Children.Add(label);
        }
        return new StackPanel { Margin = new Thickness(0, 0, 0, 4), Children = { grid, Divider() } };
    }

    /// <summary>
    /// Kompakte Tabellenzeile (Titel einer Liste): eine Zelle je Spalte, die letzte rechtsbündig; nicht erreichbare Titel gedimmt.
    /// Mit <paramref name="menu"/> öffnen Rechtsklick und Menütaste ein Kontextmenü (für Automation: „Aufklappen“).
    /// </summary>
    public static Border TableRow(IReadOnlyList<GridLength> widths, IReadOnlyList<FrameworkElement> cells, Action onClick, string name, bool dimmed = false,
        Func<IReadOnlyList<(string Text, Action Click)>>? menu = null)
    {
        var grid = new Grid();
        for (var i = 0; i < widths.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = widths[i] });
            var cell = cells[i];
            cell.VerticalAlignment = VerticalAlignment.Center;
            cell.Margin = new Thickness(cell.Margin.Left, cell.Margin.Top, i < widths.Count - 1 ? 12 : 0, cell.Margin.Bottom);
            if (i == widths.Count - 1) cell.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        if (dimmed) grid.Opacity = 0.55;
        var row = (RowControl)ClickableRow(grid, onClick, new Thickness(0), name);
        row.Padding = new Thickness(8, 5, 8, 5);
        row.CornerRadius = new CornerRadius(6);
        if (menu is not null)
        {
            row.Menu = menu;
            row.MouseRightButtonUp += (_, e) => e.Handled = row.ShowMenu();
            row.KeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Apps || (e.Key == System.Windows.Input.Key.F10 && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Shift))
                    e.Handled = row.ShowMenu();
            };
        }
        return row;
    }

    /// <summary>Einzeiliger gedämpfter Text für Tabellenzellen (wird bei Platzmangel mit „…“ gekürzt).</summary>
    public static TextBlock Cell(string text, double size = 13)
    {
        var block = Muted(text, size);
        block.TextWrapping = TextWrapping.NoWrap;
        block.TextTrimming = TextTrimming.CharacterEllipsis;
        return block;
    }

    /// <summary>Anklickbare Zeile; für Screenreader und Automation ein Knopf.</summary>
    private sealed class RowControl(Action onClick) : Border
    {
        /// <summary>Einträge des Kontextmenüs; null = kein Menü.</summary>
        public Func<IReadOnlyList<(string Text, Action Click)>>? Menu { get; set; }

        public void Activate() => onClick();

        /// <returns>true, wenn ein Menü geöffnet wurde.</returns>
        public bool ShowMenu()
        {
            if (Menu?.Invoke() is not { Count: > 0 } items) return false;
            Controls.ContextPopup.Show(this, items, System.Windows.Automation.AutomationProperties.GetName(this));
            return true;
        }

        protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        private sealed class Peer(RowControl owner) : System.Windows.Automation.Peers.FrameworkElementAutomationPeer(owner),
            System.Windows.Automation.Provider.IInvokeProvider, System.Windows.Automation.Provider.IExpandCollapseProvider
        {
            protected override string GetClassNameCore() => "ListRow";

            protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() =>
                System.Windows.Automation.Peers.AutomationControlType.Button;

            // „Aufklappen“ öffnet das Kontextmenü (für Bedienhilfen und Automation, die nicht rechtsklicken können).
            public override object? GetPattern(System.Windows.Automation.Peers.PatternInterface patternInterface) => patternInterface switch
            {
                System.Windows.Automation.Peers.PatternInterface.Invoke => this,
                System.Windows.Automation.Peers.PatternInterface.ExpandCollapse when owner.Menu is not null => this,
                _ => base.GetPattern(patternInterface),
            };

            public void Invoke() => owner.Dispatcher.BeginInvoke(owner.Activate);

            public System.Windows.Automation.ExpandCollapseState ExpandCollapseState => System.Windows.Automation.ExpandCollapseState.Collapsed;

            public void Expand() => owner.Dispatcher.BeginInvoke(() => owner.ShowMenu());

            public void Collapse()
            {
            }
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

    /// <summary>Höhe des Navigationsstreifens oben; Seiten lassen darunter Platz.</summary>
    public const double NavInset = 60;

    /// <summary>Seitenrahmen unterhalb der Navigation: Inhalt scrollt senkrecht.</summary>
    public static ScrollViewer Scrolling(Panel content)
    {
        content.Margin = new Thickness(48, NavInset + 28, 48, 60);
        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = content,
        };
    }
}
