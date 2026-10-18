using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MediaCenter.App.Services;

namespace MediaCenter.App.Pages;

/// <summary>Was sich in welcher Version geändert hat (<see cref="Changelog"/>); erreichbar über die Versionsnummer im Kopf und in den Einstellungen.</summary>
public sealed class ChangelogPage : UserControl
{
    public ChangelogPage(IShell shell)
    {
        var back = new Button { Content = "\uE72B", HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Zurück (Esc)", Margin = new Thickness(0, 0, 0, 14) };
        back.SetResourceReference(StyleProperty, "IconButton");
        AutomationProperties.SetName(back, "Zurück");
        back.Click += (_, _) => shell.Back();

        var kicker = new TextBlock { Text = $"VERSION {AppInfo.Version}", Margin = new Thickness(0, 0, 0, 6) };
        kicker.SetResourceReference(StyleProperty, "KickerText");

        var stack = new StackPanel { MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
        stack.Children.Add(back);
        stack.Children.Add(kicker);
        stack.Children.Add(PageParts.Heading("Changelog", 52));

        // Vergessene Pflege fällt hier auf, statt dass die Anzeige stillschweigend eine andere Version nennt.
        if (Changelog.Entries[0].Version != AppInfo.Version)
        {
            var warning = PageParts.Muted($"Der Changelog kennt die laufende Version {AppInfo.Version} nicht (neuester Eintrag: {Changelog.Entries[0].Version}).", 13);
            warning.Margin = new Thickness(0, 10, 0, 0);
            stack.Children.Add(warning);
        }

        foreach (var entry in Changelog.Entries) stack.Children.Add(EntryCard(entry));
        Content = PageParts.Scrolling(stack);
    }

    private static UIElement EntryCard(ChangelogEntry entry)
    {
        var version = new TextBlock { Text = entry.Version, FontSize = 32, VerticalAlignment = VerticalAlignment.Center };
        version.SetResourceReference(StyleProperty, "DisplayText");
        var date = PageParts.Muted(entry.Date, 13);
        date.VerticalAlignment = VerticalAlignment.Center;
        date.Margin = new Thickness(14, 0, 0, 0);

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10), Children = { version, date } };
        if (entry.Version == AppInfo.Version)
        {
            var current = PageParts.Badge("aktuell", highlighted: true);
            current.VerticalAlignment = VerticalAlignment.Center;
            current.Margin = new Thickness(14, 0, 0, 0);
            header.Children.Add(current);
        }

        var panel = new StackPanel { Children = { header } };
        foreach (var change in entry.Changes)
        {
            var line = new TextBlock { Text = "•  " + change, FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
            panel.Children.Add(line);
        }
        AutomationProperties.SetName(panel, $"Version {entry.Version}");
        return PageParts.Card(panel, new Thickness(0, 22, 0, 0));
    }
}
