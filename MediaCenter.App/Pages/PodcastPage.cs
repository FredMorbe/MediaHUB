using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App.Pages;

/// <summary>Ein Podcast: Beschreibung und Folgen mit Hörfortschritt; ein Klick spielt die Folge ab dem gemerkten Punkt.</summary>
public sealed class PodcastPage : UserControl
{
    private const int EpisodesPerPage = 40;

    private static readonly CultureInfo German = new("de-DE");

    private readonly IShell _shell;
    private readonly LibraryModel _model;
    private readonly PodcastInfo _podcast;
    private readonly StackPanel _list = new();
    private int _shown = EpisodesPerPage;
    private long? _playingId;

    public PodcastPage(IShell shell, long podcastId)
    {
        _shell = shell;
        _model = shell.Services.Model;
        _podcast = _model.Podcasts.FirstOrDefault(p => p.Id == podcastId) ?? throw new InvalidOperationException($"Podcast {podcastId} existiert nicht (mehr).");

        var back = new Button { Content = "", HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Zurück (Esc)", Margin = new Thickness(0, 0, 0, 14) };
        back.SetResourceReference(StyleProperty, "IconButton");
        back.Click += (_, _) => _shell.Back();

        var cover = PageParts.Thumb(ImageCache.Load(_model.CachePath(_podcast.ImageFile), 440), 220);
        cover.VerticalAlignment = VerticalAlignment.Top;

        var kicker = new TextBlock { Text = "PODCAST", Margin = new Thickness(0, 0, 0, 6) };
        kicker.SetResourceReference(StyleProperty, "KickerText");
        var title = PageParts.Heading(_podcast.Title, 56);
        title.TextWrapping = TextWrapping.Wrap;
        var author = PageParts.Semi(_podcast.Author ?? "", 18);
        author.Margin = new Thickness(0, 4, 0, 0);
        var description = PageParts.Muted(_podcast.Description ?? "");
        description.MaxHeight = 78;
        description.TextTrimming = TextTrimming.CharacterEllipsis;
        description.Margin = new Thickness(0, 8, 0, 0);

        var buttons = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(PageParts.Button("Aktualisieren", "GhostButton", async (_, _) => await RefreshAsync()));
        buttons.Children.Add(PageParts.Button("Abbestellen", "GhostButton", (_, _) => Unsubscribe()));

        var info = new StackPanel { Margin = new Thickness(30, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom, MaxWidth = 760, Children = { kicker, title, author, description, buttons } };
        var header = new Grid { Margin = new Thickness(0, 0, 0, 24) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(info, 1);
        header.Children.Add(cover);
        header.Children.Add(info);

        Content = PageParts.Scrolling(new StackPanel { Children = { back, header, _list } });

        _playingId = Playing();
        BuildEpisodes();
        Loaded += (_, _) => _shell.Services.Audio.Changed += OnAudioChanged;
        Unloaded += (_, _) => _shell.Services.Audio.Changed -= OnAudioChanged;
    }

    private long? Playing() => _shell.Services.Audio.Current is { Kind: QueueKind.Podcast, Id: { } id } ? id : null;

    private void OnAudioChanged()
    {
        if (Playing() == _playingId) return;
        _playingId = Playing();
        BuildEpisodes();
    }

    private void BuildEpisodes()
    {
        _list.Children.Clear();
        var episodes = _model.Db.ListEpisodes(_podcast.Id);
        var progress = _model.Db.GetEpisodeProgress(_podcast.Id);
        if (episodes.Count == 0)
        {
            _list.Children.Add(PageParts.Muted("Keine Folgen im Feed. Mit „Aktualisieren“ neu laden."));
            return;
        }

        foreach (var episode in episodes.Take(_shown)) _list.Children.Add(EpisodeRow(episode, progress.GetValueOrDefault(episode.Id)));
        if (episodes.Count > _shown)
        {
            _list.Children.Add(PageParts.Button($"Ältere Folgen anzeigen ({episodes.Count - _shown})", "GhostButton", (_, _) =>
            {
                _shown += EpisodesPerPage;
                BuildEpisodes();
            }, new Thickness(0, 14, 0, 0)));
        }
    }

    private UIElement EpisodeRow(EpisodeInfo episode, EpisodeProgress? progress)
    {
        var playing = episode.Id == _playingId;
        var title = PageParts.Semi(episode.Title, 16);
        title.TextWrapping = TextWrapping.Wrap;
        title.SetResourceReference(TextBlock.ForegroundProperty, playing ? "AccentBrush" : "InkBrush");

        var details = new List<string>();
        if (DateTime.TryParse(episode.PublishedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var published))
        {
            details.Add(published.ToLocalTime().ToString("d. MMMM yyyy", German));
        }
        if (episode.DurationSeconds is > 0) details.Add(PageParts.FormatDuration(episode.DurationSeconds));
        if (progress is { Played: true }) details.Add("gehört");
        else if (progress is { PositionSeconds: > 0 } && episode.DurationSeconds is { } total) details.Add($"noch {PageParts.FormatDuration(Math.Max(total - progress.PositionSeconds, 60))}");
        var meta = PageParts.Muted(string.Join(" · ", details), 13);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, meta } };
        if (progress is { Played: false, PositionSeconds: > 0 } && episode.DurationSeconds is > 0)
        {
            var track = new Border { Height = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Width = 220 };
            track.SetResourceReference(Border.BackgroundProperty, "LineBrush");
            var fill = new Border { Height = 3, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = 220 * Math.Clamp(progress.PositionSeconds / episode.DurationSeconds.Value, 0, 1) };
            fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            track.Child = fill;
            text.Children.Add(track);
        }

        var playedToggle = new Button
        {
            Content = progress is { Played: true } ? "" : "",
            ToolTip = progress is { Played: true } ? "Als ungehört markieren" : "Als gehört markieren",
            VerticalAlignment = VerticalAlignment.Center,
        };
        playedToggle.SetResourceReference(StyleProperty, "IconButton");
        playedToggle.Click += (_, _) =>
        {
            _model.Db.SaveEpisodeProgress(episode.Id, 0, played: progress is not { Played: true });
            BuildEpisodes();
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(playedToggle, 1);
        grid.Children.Add(text);
        grid.Children.Add(playedToggle);
        return PageParts.ClickableRow(grid, async () => await PlayAsync(episode, progress), new Thickness(0, 0, 0, 4), episode.Title);
    }

    private async Task PlayAsync(EpisodeInfo episode, EpisodeProgress? progress)
    {
        var start = progress is { Played: false, PositionSeconds: >= 15 } ? progress.PositionSeconds : 0;
        var item = AudioQueues.ForEpisode(_podcast, episode, _model.CachePath(_podcast.ImageFile));
        await _shell.RunBusyAsync("Folge wird geöffnet …", () => _shell.Services.Audio.PlayAsync(new[] { item }, 0, start));
    }

    private async Task RefreshAsync()
    {
        IReadOnlyList<PodcastRefresh> results = Array.Empty<PodcastRefresh>();
        await _shell.RunBusyAsync("Feed wird aktualisiert …", async () => results = await _shell.Services.Podcasts.RefreshAllAsync());
        _model.Reload();
        var mine = results.FirstOrDefault(r => r.Podcast.Id == _podcast.Id);
        if (mine?.Error is { } error) _shell.ShowMessage($"Feed nicht erreichbar: {error}", isError: true);
        else _shell.ShowMessage(mine is null ? "Aktualisiert." : $"Aktualisiert: {mine.NewEpisodes} neue Folgen.");
        BuildEpisodes();
    }

    private void Unsubscribe()
    {
        var answer = MessageBox.Show(
            $"„{_podcast.Title}“ abbestellen? Folgen und Hörfortschritt dieses Podcasts werden aus der Bibliothek entfernt.",
            "Podcast abbestellen", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;

        _model.Db.DeletePodcast(_podcast.Id);
        _model.Reload();
        _shell.Back();
    }
}
