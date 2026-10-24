using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App.Pages;

/// <summary>Verlauf (zuletzt angeschaut, nach Tagen) und Statistik (gesehene Zeit, Monate, Genres) in zwei Reitern.</summary>
public sealed class HistoryPage : UserControl
{
    private const int HistoryLimit = 200;
    private static readonly CultureInfo German = new("de-DE");

    /// <summary>Der zuletzt gewählte Reiter bleibt beim Wiederöffnen erhalten.</summary>
    private static bool s_showStatistics;

    private readonly IShell _shell;
    private readonly ContentControl _body = new() { IsTabStop = false };

    public HistoryPage(IShell shell)
    {
        _shell = shell;

        var tabs = PageParts.Segmented("history-tab", new[]
        {
            ("Verlauf", !s_showStatistics, (Action)(() => Show(statistics: false))),
            ("Statistik", s_showStatistics, (Action)(() => Show(statistics: true))),
        }, "Reiter");
        tabs.Margin = new Thickness(0, 0, 0, 22);

        var stack = new StackPanel { MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Left };
        stack.Children.Add(PageParts.Header("Persönlich", "Verlauf"));
        stack.Children.Add(tabs);
        stack.Children.Add(_body);

        Content = PageParts.Scrolling(new StackPanel { Children = { stack } });
        Show(s_showStatistics);
    }

    private void Show(bool statistics)
    {
        s_showStatistics = statistics;
        _body.Content = statistics ? Statistics() : History();
    }

    // --- Verlauf -------------------------------------------------------------------------------------------

    private UIElement History()
    {
        var model = _shell.Services.Model;
        var videos = model.VideosByTitle.Values.SelectMany(v => v).ToList();
        var entries = WatchStatistics.Recent(videos, model.Titles, model.Db.GetAllPlayback(), HistoryLimit);
        if (entries.Count == 0)
        {
            return PageParts.EmptyState("", "Noch nichts angeschaut",
                "Hier erscheint, was du zuletzt gesehen oder angefangen hast, nach Tagen geordnet.");
        }

        var today = DateTime.Today;
        var panel = new StackPanel();
        panel.Children.Add(PageParts.Muted(
            "Pro Film und Folge steht der letzte Stand: wer etwas zweimal sieht, findet es einmal, mit der Zeit des letzten Mals.", 13));

        foreach (var day in entries.GroupBy(e => e.WhenUtc.ToLocalTime().Date))
        {
            var label = day.Key == today ? "Heute" : day.Key == today.AddDays(-1) ? "Gestern" : day.Key.ToString("dddd, d. MMMM" + (day.Key.Year == today.Year ? "" : " yyyy"), German);
            panel.Children.Add(PageParts.SectionTitle(label, new Thickness(0, 26, 0, 8)));
            var list = new StackPanel();
            foreach (var entry in day) list.Children.Add(HistoryRow(entry));
            panel.Children.Add(PageParts.Card(list));
        }
        return panel;
    }

    private UIElement HistoryRow(HistoryEntry entry)
    {
        var model = _shell.Services.Model;
        var cover = model.BestCover(entry.TitleId);
        var poster = new Border { Width = 46, Height = 66, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        if (cover is { } c) poster.Background = new ImageBrush(ImageCache.Load(c.Path, 160)) { Stretch = Stretch.UniformToFill };
        else poster.SetResourceReference(Border.BackgroundProperty, "LineBrush");

        var episode = entry.Season is { } s && entry.Episode is { } e ? $"  ·  S{s:00}E{e:00}" : "";
        var name = PageParts.Semi(entry.TitleName + episode, 16);
        name.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        var state = entry.Watched
            ? "Gesehen"
            : entry.DurationSeconds is > 0
                ? $"Angefangen: {PageParts.FormatClock(entry.PositionSeconds)} von {PageParts.FormatDuration(entry.DurationSeconds)}"
                : $"Angefangen: {PageParts.FormatClock(entry.PositionSeconds)}";
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name, PageParts.Muted(state, 13) } };
        if (!entry.Watched && entry.DurationSeconds is > 0)
        {
            var bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = Math.Clamp(entry.PositionSeconds / entry.DurationSeconds.Value, 0, 1), Height = 4, Width = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0), BorderThickness = new Thickness(0) };
            bar.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
            bar.SetResourceReference(Control.BackgroundProperty, "LineBrush");
            text.Children.Add(bar);
        }

        var time = PageParts.Muted(entry.WhenUtc.ToLocalTime().ToString("HH:mm", German), 13);
        time.VerticalAlignment = VerticalAlignment.Center;
        var grid = new DockPanel();
        DockPanel.SetDock(time, Dock.Right);
        grid.Children.Add(time);
        grid.Children.Add(poster);
        grid.Children.Add(text);

        return PageParts.ClickableRow(grid, () => _shell.OpenTitle(entry.TitleId), new Thickness(0, 2, 0, 2), $"Verlauf {entry.TitleName}{episode}");
    }

    // --- Statistik -----------------------------------------------------------------------------------------

    private UIElement Statistics()
    {
        var model = _shell.Services.Model;
        var videos = model.VideosByTitle.Values.SelectMany(v => v).ToList();
        var stats = WatchStatistics.Build(videos, model.Titles, model.Genres, model.Db.GetAllPlayback(), model.Db.GetFileSizes(), DateTime.UtcNow);

        var panel = new StackPanel();
        var tiles = new WrapPanel();
        tiles.Children.Add(Tile("Gesehene Zeit", Hours(stats.WatchedHours), $"{stats.WatchedMovies} Filme und {stats.WatchedEpisodes} Folgen gesehen"));
        tiles.Children.Add(Tile("Letzte 30 Tage", Hours(stats.Last30DaysHours), "gesehene und angefangene Zeit"));
        tiles.Children.Add(Tile("Bibliothek", $"{stats.Movies + stats.Series}", $"{stats.Movies} Filme · {stats.Series} Serien · {stats.Videos} Dateien"));
        tiles.Children.Add(Tile("Laufzeit gesamt", Hours(stats.LibraryHours), $"{stats.LibraryBytes / (double)(1L << 30):0.0} GB auf den Laufwerken"));
        panel.Children.Add(tiles);

        panel.Children.Add(PageParts.SectionTitle("Pro Monat", new Thickness(0, 22, 0, 4)));
        panel.Children.Add(PageParts.Muted("Gesehene Stunden, nach dem Monat des letzten Eintrags zu dem Film oder der Folge.", 13));
        panel.Children.Add(PageParts.Card(MonthChart(stats.Months), new Thickness(0, 12, 0, 14)));

        var lists = new Grid();
        lists.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lists.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        lists.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var genres = PageParts.Card(Ranking("Genres", stats.Genres), new Thickness(0));
        var top = PageParts.Card(Ranking("Meistgesehen", stats.TopTitles), new Thickness(0));
        Grid.SetColumn(top, 2);
        lists.Children.Add(genres);
        lists.Children.Add(top);
        panel.Children.Add(lists);

        panel.Children.Add(PageParts.Muted(
            "Es gibt kein Protokoll jeder Wiedergabe: gezählt wird der gemerkte Stand je Video, ein gesehenes mit seiner ganzen Länge, ein angefangenes mit der Stelle, bei der es steht. „Als ungesehen markieren“ nimmt es wieder heraus.", 12)
            .WithMargin(new Thickness(2, 18, 0, 0)));
        return panel;
    }

    private static string Hours(double hours) =>
        hours >= 100 ? $"{hours:0} h"
        : hours >= 1 ? $"{hours:0.0} h".Replace('.', ',')
        : hours * 60 >= 1 ? $"{hours * 60:0} min"
        : hours > 0 ? "< 1 min" : "0 min";

    private static UIElement Tile(string title, string value, string note)
    {
        var big = new TextBlock { Text = value, FontSize = 44 };
        big.SetResourceReference(FrameworkElement.StyleProperty, "DisplayText");
        var label = new TextBlock { Text = title.ToUpperInvariant() };
        label.SetResourceReference(FrameworkElement.StyleProperty, "KickerText");
        var card = PageParts.Card(new StackPanel { Children = { label, big, PageParts.Muted(note, 12) } }, new Thickness(0, 0, 14, 14));
        card.Width = 252;
        return card;
    }

    /// <summary>Zwölf Säulen, die höchste füllt die Höhe; darunter der Monat, darüber die Stunden.</summary>
    private static UIElement MonthChart(IReadOnlyList<MonthBar> months)
    {
        const double MaxBar = 120;
        var max = Math.Max(months.Max(m => m.Hours), 0.01);
        var grid = new UniformGrid { Rows = 1, Columns = months.Count };
        foreach (var month in months)
        {
            var bar = new Border { Height = Math.Max(month.Hours <= 0 ? 3 : 6, month.Hours / max * MaxBar), CornerRadius = new CornerRadius(6), Width = 26, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center };
            bar.SetResourceReference(Border.BackgroundProperty, month.Hours > 0 ? "AccentBrush" : "LineBrush");
            var value = PageParts.Muted(month.Hours > 0 ? Hours(month.Hours) : "", 11);
            value.TextAlignment = TextAlignment.Center;
            value.HorizontalAlignment = HorizontalAlignment.Center;
            var name = PageParts.Muted(new DateTime(month.Year, month.Month, 1).ToString("MMM", German), 12);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.Margin = new Thickness(0, 6, 0, 0);
            var column = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Children = { value, new Grid { Height = MaxBar, Children = { bar } }, name } };
            column.ToolTip = $"{new DateTime(month.Year, month.Month, 1).ToString("MMMM yyyy", German)}: {month.Videos} {(month.Videos == 1 ? "Titel" : "Titel")}, {Hours(month.Hours)}";
            grid.Children.Add(column);
        }
        return grid;
    }

    private static UIElement Ranking(string title, IReadOnlyList<HoursShare> items)
    {
        var panel = new StackPanel { Children = { PageParts.SectionTitle(title, new Thickness(0, 0, 0, 10)) } };
        if (items.Count == 0)
        {
            panel.Children.Add(PageParts.Muted("Noch nichts gesehen.", 13));
            return panel;
        }
        var max = items.Max(i => i.Hours);
        foreach (var item in items)
        {
            var name = PageParts.Semi(item.Name, 14);
            name.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
            var hours = PageParts.Muted(Hours(item.Hours), 13);
            hours.HorizontalAlignment = HorizontalAlignment.Right;
            var line = new Grid { Children = { name, hours } };
            var bar = new ProgressBar { Minimum = 0, Maximum = max, Value = item.Hours, Height = 4, Margin = new Thickness(0, 4, 0, 0), BorderThickness = new Thickness(0) };
            bar.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
            bar.SetResourceReference(Control.BackgroundProperty, "LineBrush");
            panel.Children.Add(new StackPanel { Margin = new Thickness(0, 0, 0, 12), Children = { line, bar } });
        }
        return panel;
    }
}

internal static class HistoryPageExtensions
{
    public static T WithMargin<T>(this T element, Thickness margin) where T : FrameworkElement
    {
        element.Margin = margin;
        return element;
    }
}
