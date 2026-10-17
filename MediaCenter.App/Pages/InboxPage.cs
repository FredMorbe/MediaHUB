using System.IO;
using System.Windows;
using System.Windows.Controls;
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
        stack.Children.Add(PageParts.Heading("Inbox"));
        stack.Children.Add(PageParts.Muted(_model.Inbox.Count == 0
            ? "Alles zugeordnet. Neue Dateien, deren Name keine sichere Zuordnung ergibt, erscheinen hier."
            : "Bei diesen Dateien reicht der Name nicht für eine sichere Zuordnung (kein Jahr, keine Folgennummer). Ordne sie zu, dann erscheinen sie in der Bibliothek."));

        stack.Children.Add(new Border { Height = 18 });
        foreach (var video in _model.Inbox) stack.Children.Add(Row(video));

        Content = PageParts.Scrolling(stack);
    }

    private UIElement Row(VideoRow video)
    {
        var name = new TextBlock { Text = Path.GetFileName(video.RelPath), FontSize = 16, TextTrimming = TextTrimming.CharacterEllipsis };
        name.SetResourceReference(TextBlock.FontFamilyProperty, "BodySemiFont");
        var guess = PageParts.Muted($"Erkannt als: {video.ParsedTitle}" + (video.Missing ? " · Datei fehlt" : ""), 13);
        guess.TextTrimming = TextTrimming.CharacterEllipsis;
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name, guess } };

        var movieButton = PageParts.Button("Als Film", "PrimaryButton", async (_, _) => await AssignMovieAsync(video));
        movieButton.Padding = new Thickness(18, 8, 18, 8);

        var series = new TextBox { Text = video.ParsedTitle, Width = 190, ToolTip = "Name der Serie", Margin = new Thickness(0, 0, 8, 0) };
        var season = new TextBox { Width = 52, ToolTip = "Staffel", Text = video.Season?.ToString() ?? "", Margin = new Thickness(0, 0, 8, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
        var episode = new TextBox { Width = 52, ToolTip = "Folge", Text = video.Episode?.ToString() ?? "", Margin = new Thickness(0, 0, 8, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
        var episodeButton = PageParts.Button("Als Folge", "GhostButton", async (_, _) => await AssignEpisodeAsync(video, series.Text, season.Text, episode.Text), new Thickness(0));
        episodeButton.Padding = new Thickness(18, 8, 18, 8);

        var labelS = PageParts.Muted("S", 13);
        labelS.Margin = new Thickness(0, 0, 4, 0);
        labelS.VerticalAlignment = VerticalAlignment.Center;
        var labelE = PageParts.Muted("E", 13);
        labelE.Margin = new Thickness(0, 0, 4, 0);
        labelE.VerticalAlignment = VerticalAlignment.Center;
        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { movieButton, new Border { Width = 26 }, series, labelS, season, labelE, episode, episodeButton },
        };
        movieButton.Margin = new Thickness(0);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        left.Margin = new Thickness(0, 0, 20, 0);
        Grid.SetColumn(controls, 1);
        grid.Children.Add(left);
        grid.Children.Add(controls);
        return PageParts.Card(grid, new Thickness(0, 0, 0, 10));
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
