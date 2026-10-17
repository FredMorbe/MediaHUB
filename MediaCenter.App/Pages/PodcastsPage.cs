using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using MediaCenter.App.Controls;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App.Pages;

/// <summary>Podcast-Abos, Suche (iTunes-Verzeichnis) und Abo per Feed-Adresse.</summary>
public sealed class PodcastsPage : UserControl
{
    private readonly IShell _shell;
    private readonly LibraryModel _model;
    private readonly WrapPanel _subscriptions = new();
    private readonly StackPanel _results = new();
    private readonly TextBox _query;

    public PodcastsPage(IShell shell)
    {
        _shell = shell;
        _model = shell.Services.Model;

        var stack = new StackPanel();
        stack.Children.Add(PageParts.Heading("Podcasts"));
        stack.Children.Add(PageParts.Muted("Suche im Verzeichnis oder füge direkt die Feed-Adresse (RSS) eines Podcasts ein."));

        var (field, box) = PageParts.HintBox("Podcast suchen oder Feed-Adresse einfügen …", 420);
        _query = box;
        _query.KeyDown += async (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) await SearchOrSubscribeAsync();
        };
        field.Margin = new Thickness(0, 0, 10, 8);
        var go = PageParts.Button("Suchen", "PrimaryButton", async (_, _) => await SearchOrSubscribeAsync(), new Thickness(0, 0, 10, 8));
        go.Padding = new Thickness(22, 8, 22, 8);
        var refresh = PageParts.Button("Alle aktualisieren", "GhostButton", async (_, _) => await RefreshAllAsync(), new Thickness(0, 0, 0, 8));
        refresh.Padding = new Thickness(18, 8, 18, 8);
        stack.Children.Add(new WrapPanel { Margin = new Thickness(0, 18, 0, 6), Children = { field, go, refresh } });
        stack.Children.Add(_results);

        var heading = new TextBlock { Text = "Abonniert", FontSize = 34, Margin = new Thickness(0, 22, 0, 10) };
        heading.SetResourceReference(StyleProperty, "DisplayText");
        stack.Children.Add(heading);
        stack.Children.Add(_subscriptions);

        Content = PageParts.Scrolling(stack);
        BuildSubscriptions();
    }

    private void BuildSubscriptions()
    {
        _subscriptions.Children.Clear();
        if (_model.Podcasts.Count == 0)
        {
            _subscriptions.Children.Add(PageParts.Muted("Noch keine Abos."));
            return;
        }
        foreach (var podcast in _model.Podcasts)
        {
            var card = new CardView(new CardItem
            {
                TitleId = 0,
                Title = podcast.Title,
                Meta = podcast.Author ?? "",
                Kind = TitleKind.Movie,
                Image = ImageCache.Load(_model.CachePath(podcast.ImageFile), 360),
                Square = true,
            }) { Margin = new Thickness(0, 0, 22, 28) };
            card.Activated += _ => _shell.Open(() => new PodcastPage(_shell, podcast.Id));
            _subscriptions.Children.Add(card);
        }
    }

    private async Task SearchOrSubscribeAsync()
    {
        var text = _query.Text.Trim();
        if (text.Length == 0)
        {
            _shell.ShowMessage("Bitte einen Suchbegriff oder eine Feed-Adresse eingeben.", isError: true);
            return;
        }

        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            await SubscribeAsync(text);
            return;
        }

        IReadOnlyList<PodcastSearchResult> found = Array.Empty<PodcastSearchResult>();
        await _shell.RunBusyAsync("Podcasts werden gesucht …", async () => found = await _shell.Services.PodcastSearch.SearchAsync(text));

        _results.Children.Clear();
        var header = PageParts.Muted(found.Count == 0 ? "Keine Podcasts gefunden." : $"{found.Count} Treffer", 14);
        header.Margin = new Thickness(0, 4, 0, 8);
        _results.Children.Add(header);
        foreach (var result in found) _results.Children.Add(ResultRow(result));
    }

    private UIElement ResultRow(PodcastSearchResult result)
    {
        var subscribed = _model.Podcasts.Any(p => string.Equals(p.FeedUrl, result.FeedUrl, StringComparison.OrdinalIgnoreCase));
        var thumb = PageParts.Thumb(RemoteImage(result.ImageUrl), 64, "");

        var title = PageParts.Semi(result.Title, 16);
        var author = PageParts.Muted(result.Author ?? "", 13);
        author.TextTrimming = TextTrimming.CharacterEllipsis;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 14, 0), Children = { title, author } };

        FrameworkElement button;
        if (subscribed)
        {
            button = PageParts.Badge("abonniert", true);
        }
        else
        {
            var subscribe = PageParts.Button("Abonnieren", "GhostButton", async (_, _) => await SubscribeAsync(result.FeedUrl), new Thickness(0));
            subscribe.Padding = new Thickness(18, 7, 18, 7);
            button = subscribe;
        }
        button.VerticalAlignment = VerticalAlignment.Center;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(button, 2);
        grid.Children.Add(thumb);
        grid.Children.Add(text);
        grid.Children.Add(button);
        return PageParts.Card(grid, new Thickness(0, 0, 0, 8));
    }

    private static System.Windows.Media.ImageSource? RemoteImage(string? url)
    {
        if (url is null) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = HttpFetch.RequireWebUri(url);
            image.DecodePixelWidth = 128;
            image.EndInit();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private async Task SubscribeAsync(string feedUrl)
    {
        SubscribeResult? result = null;
        await _shell.RunBusyAsync("Podcast wird abonniert …", async () => result = await _shell.Services.Podcasts.SubscribeAsync(feedUrl));
        if (result is null) return;

        _model.Reload();
        BuildSubscriptions();
        _results.Children.Clear();
        _query.Text = "";
        var name = _model.Podcasts.FirstOrDefault(p => p.Id == result.PodcastId)?.Title ?? "Podcast";
        _shell.ShowMessage($"„{name}“ abonniert ({result.Episodes} Folgen)." + (result.Warning is null ? "" : " " + result.Warning), result.Warning is not null);
    }

    private async Task RefreshAllAsync()
    {
        IReadOnlyList<PodcastRefresh> results = Array.Empty<PodcastRefresh>();
        await _shell.RunBusyAsync("Podcasts werden aktualisiert …", async () => results = await _shell.Services.Podcasts.RefreshAllAsync());
        _model.Reload();
        BuildSubscriptions();

        var added = results.Sum(r => r.NewEpisodes);
        _shell.ShowMessage(results.Count == 0 ? "Keine Abos vorhanden." : $"Aktualisiert: {added} neue Folgen.");
        var failed = results.Where(r => r.Error is not null).ToList();
        if (failed.Count > 0)
        {
            _shell.ShowMessage($"{failed.Count} Feed(s) nicht erreichbar: " + string.Join("; ", failed.Take(3).Select(f => $"{f.Podcast.Title} ({f.Error})")), isError: true);
        }
    }
}
