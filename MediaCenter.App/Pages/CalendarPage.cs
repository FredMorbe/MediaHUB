using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App.Pages;

/// <summary>Kalender der Serien aus der Bibliothek: was in den nächsten Wochen läuft und welche neuen Folgen noch nicht in der Bibliothek liegen (Sendepläne von TVmaze).</summary>
public sealed class CalendarPage : UserControl
{
    private static readonly CultureInfo German = new("de-DE");

    private readonly IShell _shell;
    private readonly StackPanel _body = new();
    private readonly TextBlock _summary = PageParts.Muted("", 14);

    public CalendarPage(IShell shell)
    {
        _shell = shell;
        var back = PageParts.Compact(PageParts.Button("Zurück", "GhostButton", (_, _) => shell.Back(), new Thickness(0, 0, 0, 18)));
        AutomationProperties.SetName(back, "Zurück");

        var refresh = PageParts.Compact(PageParts.Button("Aktualisieren", "GhostButton", async (_, _) =>
        {
            await shell.Services.RefreshScheduleAsync(shell, force: true);
            Fill();
        }, new Thickness(0, 14, 0, 0)));
        AutomationProperties.SetName(refresh, "Sendepläne aktualisieren");

        var stack = new StackPanel { Margin = new Thickness(48, PageParts.NavInset + 28, 48, 60) };
        stack.Children.Add(back);
        stack.Children.Add(PageParts.Header("Serien", "Kalender", _summary));
        stack.Children.Add(refresh);
        stack.Children.Add(_body);
        // Feste Höchstbreite, die in schmalen Fenstern nachgibt: die Karten füllen die Spalte, Termin und Titel stoßen nicht aneinander.
        var column = new Grid();
        column.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 1100 });
        column.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        column.Children.Add(stack);
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = column,
        };
        Fill();
    }

    private void Fill()
    {
        _body.Children.Clear();
        var services = _shell.Services;
        if (!services.Settings.UpcomingEpisodes)
        {
            _summary.Text = "Ausgeschaltet.";
            _body.Children.Add(PageParts.EmptyState("", "Kalender ist aus",
                "Unter Einstellungen → Bibliothek → „Kommende Folgen“ lässt er sich einschalten. Dann fragt die App TVmaze nach den Sendeplänen deiner Serien (gesendet werden Titel und Jahr)."));
            return;
        }

        var now = DateTime.UtcNow;
        var offset = TimeZoneInfo.Local.GetUtcOffset(now);
        var videos = services.Model.VideosByTitle.Values.SelectMany(v => v).ToList();
        var view = services.Schedule.Calendar(videos, now, offset);
        _summary.Text = view.Upcoming.Count + view.Missing.Count == 0
            ? "Nichts Neues in Sicht."
            : $"{view.Upcoming.Count} kommende Folge{(view.Upcoming.Count == 1 ? "" : "n")}" + (view.Missing.Count > 0 ? $" · {view.Missing.Count} neue Folge{(view.Missing.Count == 1 ? "" : "n")} fehl{(view.Missing.Count == 1 ? "t" : "en")} in der Bibliothek" : "");

        if (view.Upcoming.Count + view.Missing.Count == 0)
        {
            var checkedCount = services.Db.ListScheduleChecks().Count;
            _body.Children.Add(PageParts.EmptyState("", "Keine Termine",
                checkedCount == 0
                    ? "Die Sendepläne werden gerade geholt oder die Bibliothek hat noch keine Serien. Mit „Aktualisieren“ lässt sich das anstoßen."
                    : "Für die Serien in deiner Bibliothek steht in den nächsten Wochen keine Folge an, und es gibt keine neuen, die dir fehlen."));
            return;
        }

        if (view.Missing.Count > 0)
        {
            _body.Children.Add(PageParts.SectionTitle("Neu erschienen, noch nicht in der Bibliothek", new Thickness(0, 26, 0, 8)));
            _body.Children.Add(PageParts.Card(Rows(view.Missing, offset, showDay: true)));
        }

        var today = (now + offset).Date;
        foreach (var day in view.Upcoming.GroupBy(r => r.HasTime ? (r.AirUtc + offset).Date : r.AirUtc.Date))
        {
            _body.Children.Add(PageParts.SectionTitle(DayLabel(day.Key, today), new Thickness(0, 26, 0, 8)));
            _body.Children.Add(PageParts.Card(Rows(day.ToList(), offset, showDay: false)));
        }
    }

    private static string DayLabel(DateTime day, DateTime today)
    {
        var diff = (day - today).Days;
        var name = day.ToString("dddd, d. MMMM" + (day.Year == today.Year ? "" : " yyyy"), German);
        return diff == 0 ? $"Heute · {name}" : diff == 1 ? $"Morgen · {name}" : name;
    }

    private UIElement Rows(IReadOnlyList<ScheduleRow> rows, TimeSpan offset, bool showDay)
    {
        var list = new StackPanel();
        foreach (var row in rows) list.Children.Add(Row(row, offset, showDay));
        return list;
    }

    private UIElement Row(ScheduleRow row, TimeSpan offset, bool showDay)
    {
        var model = _shell.Services.Model;
        var poster = new Border { Width = 46, Height = 66, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        if (model.BestCover(row.TitleId) is { } c) poster.Background = new ImageBrush(ImageCache.Load(c.Path, 160)) { Stretch = Stretch.UniformToFill };
        else poster.SetResourceReference(Border.BackgroundProperty, "LineBrush");

        var number = $"S{row.Season:00}E{row.Episode:00}";
        var name = PageParts.Semi($"{row.TitleName}  ·  {number}", 16);
        name.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name } };
        if (!string.IsNullOrWhiteSpace(row.EpisodeName)) text.Children.Add(PageParts.Muted(row.EpisodeName, 13));

        var local = row.HasTime ? row.AirUtc + offset : row.AirUtc;
        var when = showDay
            ? local.ToString(row.HasTime ? "ddd, d. MMM · HH:mm" : "ddd, d. MMM", German)
            : row.HasTime ? local.ToString("HH:mm", German) : "Uhrzeit offen";
        var time = PageParts.Muted(when, 13);
        time.VerticalAlignment = VerticalAlignment.Center;
        var grid = new DockPanel();
        DockPanel.SetDock(time, Dock.Right);
        grid.Children.Add(time);
        grid.Children.Add(poster);
        grid.Children.Add(text);

        return PageParts.ClickableRow(grid, () => _shell.OpenTitle(row.TitleId), new Thickness(0, 2, 0, 2), $"Kalender {row.TitleName} {number}");
    }
}
