using System.IO;
using System.Windows;
using System.Windows.Controls;
using MediaCenter.App.Controls;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App.Pages;

/// <summary>Dateien ohne sichere Zuordnung: als Film oder als Folge einer Serie übernehmen.</summary>
public sealed class InboxPage : UserControl
{
    private readonly IShell _shell;
    private readonly LibraryModel _model;

    public InboxPage(IShell shell)
    {
        _shell = shell;
        _model = shell.Services.Model;

        var stack = new StackPanel();
        stack.Children.Add(PageParts.Header("Zuordnen", "Inbox", _model.Inbox.Count == 0
            ? "Neue Dateien, deren Name keine sichere Zuordnung ergibt, erscheinen hier."
            : "Bei diesen Dateien reicht der Name nicht für eine sichere Zuordnung (kein Jahr, keine Folgennummer). Ordne sie zu, dann erscheinen sie in der Bibliothek. " +
              "Rechtsklick auf eine Datei → „Aus Mediacenter entfernen“ nimmt sie aus der Bibliothek; auf der Festplatte bleibt sie."));

        if (_model.Inbox.Count == 0)
        {
            stack.Children.Add(PageParts.EmptyState("\uE73E", "Alles zugeordnet", "Es liegt nichts in der Inbox. Dateien mit unklarem Namen landen beim nächsten Scan wieder hier."));
        }
        foreach (var video in _model.Inbox) stack.Children.Add(Row(video));

        Content = PageParts.Scrolling(stack);
    }

    private UIElement Row(VideoRow video)
    {
        var name = new TextBlock { Text = Path.GetFileName(video.RelPath), FontSize = 16, TextTrimming = TextTrimming.CharacterEllipsis };
        name.SetResourceReference(TextBlock.FontFamilyProperty, "BodySemiFont");
        var guess = PageParts.Muted($"Erkannt als: {video.ParsedTitle}" + (video.Missing ? " · Datei fehlt" : ""), 13);
        guess.TextTrimming = TextTrimming.CharacterEllipsis;
        guess.Margin = new Thickness(0, 2, 0, 0);

        var movieButton = PageParts.Compact(PageParts.Button("Als Film", "PrimaryButton", async (_, _) => await AssignMovieAsync(video), new Thickness(0, 0, 18, 0)));

        // Eingabefelder mit Beschriftung im Feld und genug Breite für Namen und Zahlen
        var (seriesField, series) = PageParts.HintBox("Serie", 250);
        series.Text = video.ParsedTitle;
        series.ToolTip = "Name der Serie";
        System.Windows.Automation.AutomationProperties.SetName(series, "Serie");
        var (seasonField, season) = PageParts.HintBox("Staffel", 100);
        season.Text = video.Season?.ToString() ?? "";
        season.ToolTip = "Staffel";
        System.Windows.Automation.AutomationProperties.SetName(season, "Staffel");
        var (episodeField, episode) = PageParts.HintBox("Folge", 100);
        episode.Text = video.Episode?.ToString() ?? "";
        episode.ToolTip = "Folge";
        System.Windows.Automation.AutomationProperties.SetName(episode, "Folge");
        foreach (var field in new FrameworkElement[] { seriesField, seasonField, episodeField }) field.Margin = new Thickness(0, 0, 8, 0);

        var episodeButton = PageParts.Compact(PageParts.Button("Als Folge", "GhostButton", async (_, _) => await AssignEpisodeAsync(video, series.Text, season.Text, episode.Text), new Thickness(4, 0, 0, 0)));

        var or = PageParts.Muted("oder als Folge einer Serie:", 13);
        or.Margin = new Thickness(0, 0, 14, 0);

        var assign = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        foreach (var element in new FrameworkElement[] { movieButton, or, seriesField, seasonField, episodeField, episodeButton })
        {
            element.VerticalAlignment = VerticalAlignment.Center;
            assign.Children.Add(element);
        }

        var card = PageParts.Card(new StackPanel { Children = { name, guess, assign } }, new Thickness(0, 0, 0, 10));
        card.MouseRightButtonUp += (_, e) =>
        {
            ContextPopup.Show(card, new (string, Action)[]
            {
                ("Aus Mediacenter entfernen …", () =>
                {
                    if (LibraryActions.RemoveVideo(_shell, video)) _shell.ReloadView();
                }),
            }, name.Text);
            e.Handled = true;
        };
        return card;
    }

    private async Task AssignMovieAsync(VideoRow video)
    {
        var existing = _model.Titles.FirstOrDefault(t =>
            t.Kind == TitleKind.Movie && string.Equals(t.Name, video.ParsedTitle, StringComparison.OrdinalIgnoreCase) && t.Year == video.Year);
        var titleId = existing?.Id ?? _model.Db.CreateTitle(TitleKind.Movie, video.ParsedTitle, video.Year);
        _model.Db.AssignVideo(video.VideoId, titleId, null, null);
        await _shell.RefreshArtworkAsync();
    }

    private async Task AssignEpisodeAsync(VideoRow video, string seriesName, string seasonText, string episodeText)
    {
        var name = seriesName.Trim();
        if (name.Length == 0)
        {
            _shell.ShowMessage("Bitte einen Serienname eingeben.", isError: true);
            return;
        }
        if (!int.TryParse(seasonText, out var season) || !int.TryParse(episodeText, out var episode) || season < 0 || episode < 0)
        {
            _shell.ShowMessage("Staffel und Folge müssen Zahlen sein, z. B. 1 und 4.", isError: true);
            return;
        }

        var existing = _model.Titles.FirstOrDefault(t => t.Kind == TitleKind.Series && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        var titleId = existing?.Id ?? _model.Db.CreateTitle(TitleKind.Series, name);
        _model.Db.AssignVideo(video.VideoId, titleId, season, episode);
        await _shell.RefreshArtworkAsync();
    }
}
