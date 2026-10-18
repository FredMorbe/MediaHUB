using System.Windows;
using System.Windows.Controls;
using MediaCenter.App.Pages;
using MediaCenter.App.Services;

namespace MediaCenter.App.Controls;

/// <summary>
/// Rechtes Panel der Musik-Seite wie bei Spotify: großes Cover, Titel und Interpret des laufenden Titels und die nächsten der Warteschlange.
/// Es erscheint nur, solange ein Musiktitel läuft (nicht bei Radio und Podcasts) und wenn <see cref="Suppressed"/> nicht gesetzt ist.
/// </summary>
public sealed class NowPlayingPanel : Border
{
    private const int UpcomingShown = 40;
    private const double CoverSize = 268;

    private readonly IShell _shell;
    private readonly AudioController _audio;
    private readonly StackPanel _content = new();
    private IReadOnlyList<QueueItem>? _shownQueue;
    private int _shownIndex = -2;
    private bool _suppressed;

    public NowPlayingPanel(IShell shell)
    {
        _shell = shell;
        _audio = shell.Services.Audio;
        Width = 304;
        CornerRadius = new CornerRadius(12);
        Visibility = Visibility.Collapsed;
        this.SetResourceReference(BackgroundProperty, "BgSoftBrush");
        Child = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = _content,
        };
        _content.Margin = new Thickness(18, 14, 18, 16);

        Loaded += (_, _) =>
        {
            _audio.Changed += Refresh;
            Refresh();
        };
        Unloaded += (_, _) => _audio.Changed -= Refresh;
    }

    /// <summary>Blendet das Panel aus, wenn das Fenster zu schmal ist.</summary>
    public bool Suppressed
    {
        get => _suppressed;
        set
        {
            if (_suppressed == value) return;
            _suppressed = value;
            Refresh();
        }
    }

    private void Refresh()
    {
        if (_suppressed || _audio is not { IsActive: true, Current: { Kind: QueueKind.Track } current })
        {
            Visibility = Visibility.Collapsed;
            _shownQueue = null;
            return;
        }
        Visibility = Visibility.Visible;

        // Änderungen von Lautstärke, Pause und Zeit berühren die Anzeige nicht; neu aufbauen nur bei neuem Titel oder neuer Warteschlange.
        if (ReferenceEquals(_shownQueue, _audio.Queue) && _shownIndex == _audio.Index) return;
        _shownQueue = _audio.Queue;
        _shownIndex = _audio.Index;
        Build(current);
    }

    private void Build(QueueItem current)
    {
        _content.Children.Clear();

        var header = PageParts.Semi("Jetzt läuft", 14);
        header.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        header.Margin = new Thickness(0, 0, 0, 10);
        _content.Children.Add(header);

        var cover = PageParts.Thumb(ImageCache.Load(current.CoverPath, 600), CoverSize, "\uE8D6");
        cover.CornerRadius = new CornerRadius(8);
        cover.HorizontalAlignment = HorizontalAlignment.Left;
        _content.Children.Add(cover);

        var title = PageParts.Semi(current.Title, 20);
        title.TextWrapping = TextWrapping.Wrap;
        title.TextTrimming = TextTrimming.None;
        title.Margin = new Thickness(0, 14, 0, 0);
        title.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        _content.Children.Add(title);

        var sub = PageParts.Muted(current.Subtitle, 13);
        sub.Margin = new Thickness(0, 2, 0, 0);
        _content.Children.Add(sub);

        var queue = _audio.Queue;
        var upcoming = Enumerable.Range(_audio.Index + 1, Math.Max(0, queue.Count - _audio.Index - 1)).Take(UpcomingShown).ToList();

        var next = PageParts.Semi("Als Nächstes", 13);
        next.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        next.Margin = new Thickness(0, 18, 0, 4);
        _content.Children.Add(next);

        if (upcoming.Count == 0)
        {
            _content.Children.Add(PageParts.Muted("Das ist der letzte Titel der Warteschlange.", 13));
            return;
        }

        foreach (var index in upcoming)
        {
            var item = queue[index];
            var thumb = PageParts.Thumb(ImageCache.Load(item.CoverPath, 80), 34, "\uE8D6");
            thumb.CornerRadius = new CornerRadius(4);
            var name = PageParts.Semi(item.Title, 13);
            name.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
            var artist = PageParts.Muted(item.Subtitle, 11);
            artist.TextWrapping = TextWrapping.NoWrap;
            artist.TextTrimming = TextTrimming.CharacterEllipsis;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { name, artist } };
            Grid.SetColumn(text, 1);
            grid.Children.Add(thumb);
            grid.Children.Add(text);

            var captured = index;
            var row = PageParts.ClickableRow(grid, async () => await _shell.RunBusyAsync("Wiedergabe startet …", () => _audio.PlayAsync(_audio.Queue, captured)), new Thickness(-8, 0, -8, 0), item.Title);
            row.Padding = new Thickness(8, 4, 8, 4);
            row.CornerRadius = new CornerRadius(8);
            _content.Children.Add(row);
        }
    }
}
