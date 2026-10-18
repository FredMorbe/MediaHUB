using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MediaCenter.App.Controls;
using MediaCenter.App.Services;

namespace MediaCenter.App.Pages;

/// <summary>Was sich in welcher Version geändert hat (<see cref="Changelog"/>); erreichbar über die Versionsnummer im Kopf und in den Einstellungen. Erscheint als Fenster über der Seite.</summary>
public sealed class ChangelogPage : UserControl
{
    public ChangelogPage()
    {
        var stack = new StackPanel();

        // Vergessene Pflege fällt hier auf, statt dass die Anzeige stillschweigend eine andere Version nennt.
        if (Changelog.Entries[0].Version != AppInfo.Version)
        {
            var warning = PageParts.Muted($"Der Changelog kennt die laufende Version {AppInfo.Version} nicht (neuester Eintrag: {Changelog.Entries[0].Version}).", 13);
            warning.Margin = new Thickness(0, 0, 0, 14);
            stack.Children.Add(warning);
        }

        for (var i = 0; i < Changelog.Entries.Count; i++)
        {
            if (i > 0) stack.Children.Add(PageParts.Divider(new Thickness(0, 22, 0, 0)));
            stack.Children.Add(Entry(Changelog.Entries[i], first: i == 0));
        }
        Content = stack;
    }

    public static void Show(IShell shell) =>
        shell.OpenModal($"Version {AppInfo.Version}", "Changelog", new ChangelogPage(), ModalSize.Medium, dismissOnScrim: true);

    private static UIElement Entry(ChangelogEntry entry, bool first)
    {
        var version = new TextBlock { Text = entry.Version, FontSize = 30, VerticalAlignment = VerticalAlignment.Center };
        version.SetResourceReference(StyleProperty, "DisplayText");
        var date = PageParts.Muted(entry.Date, 13);
        date.VerticalAlignment = VerticalAlignment.Center;
        date.Margin = new Thickness(12, 2, 0, 0);

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, first ? 0 : 20, 0, 12), Children = { version, date } };
        if (entry.Version == AppInfo.Version)
        {
            var current = PageParts.Badge("aktuell", highlighted: true);
            current.VerticalAlignment = VerticalAlignment.Center;
            current.Margin = new Thickness(12, 2, 0, 0);
            header.Children.Add(current);
        }

        var panel = new StackPanel { Children = { header } };
        foreach (var change in entry.Changes)
        {
            // Zeile mit Aufzählungspunkt in der Akzentfarbe; der Text umbricht eingerückt.
            var dot = new TextBlock { Text = "•", FontSize = 14, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top };
            dot.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            var text = new TextBlock { Text = change, FontSize = 14, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
            var line = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(text, 1);
            line.Children.Add(dot);
            line.Children.Add(text);
            panel.Children.Add(line);
        }
        AutomationProperties.SetName(panel, $"Version {entry.Version}");
        return panel;
    }
}
