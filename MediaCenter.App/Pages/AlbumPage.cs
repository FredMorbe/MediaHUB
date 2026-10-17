using System.Windows;
using System.Windows.Controls;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App.Pages;

/// <summary>Ein Album: Cover, Angaben und die Titelliste; ein Klick startet die Wiedergabe ab diesem Titel.</summary>
public sealed class AlbumPage : UserControl
{
    private readonly IShell _shell;
    private readonly LibraryModel _model;
    private readonly AlbumInfo _album;
    private readonly StackPanel _list = new();
    private long? _playingId;

    public AlbumPage(IShell shell, string albumKey)
    {
        _shell = shell;
        _model = shell.Services.Model;
        _album = _model.Albums.FirstOrDefault(a => a.Key == albumKey) ?? throw new InvalidOperationException($"Album '{albumKey}' existiert nicht (mehr).");

        var back = new Button { Content = "", HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Zurück (Esc)", Margin = new Thickness(0, 0, 0, 14) };
        back.SetResourceReference(StyleProperty, "IconButton");
        back.Click += (_, _) => _shell.Back();

        var cover = PageParts.Thumb(ImageCache.Load(_model.AlbumArtPath(_album.Key), 520), 240);
        cover.VerticalAlignment = VerticalAlignment.Top;

        var kicker = new TextBlock { Text = "ALBUM", Margin = new Thickness(0, 0, 0, 6) };
        kicker.SetResourceReference(StyleProperty, "KickerText");
        var title = PageParts.Heading(_album.Title, 64);
        title.TextWrapping = TextWrapping.Wrap;
        var artist = PageParts.Semi(_album.Artist, 20);
        artist.Margin = new Thickness(0, 4, 0, 0);
        var total = _album.Tracks.Sum(t => t.DurationSeconds ?? 0);
        var meta = PageParts.Muted(string.Join(" · ", new[]
        {
            _album.Year?.ToString(), $"{_album.Tracks.Count} {(_album.Tracks.Count == 1 ? "Titel" : "Titel")}", PageParts.FormatDuration(total),
        }.Where(s => !string.IsNullOrEmpty(s))));
        meta.Margin = new Thickness(0, 6, 0, 0);

        var buttons = new WrapPanel { Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(PageParts.Button("Abspielen", "PrimaryButton", async (_, _) => await AudioQueues.PlayTracksAsync(_shell, _album.Tracks, 0)));
        buttons.Children.Add(PageParts.Button("Zufällig", "GhostButton", async (_, _) => await AudioQueues.PlayTracksAsync(_shell, _album.Tracks, 0, shuffle: true)));

        var info = new StackPanel { Margin = new Thickness(32, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom, Children = { kicker, title, artist, meta, buttons } };
        var header = new Grid { Margin = new Thickness(0, 0, 0, 26) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(info, 1);
        header.Children.Add(cover);
        header.Children.Add(info);

        var stack = new StackPanel { Children = { back, header, _list } };
        Content = PageParts.Scrolling(stack);

        _playingId = _shell.Services.Audio.Current is { Kind: QueueKind.Track, Id: { } playing } ? playing : null;
        BuildTracks();
        // Die Markierung des laufenden Titels folgt der Wiedergabe.
        Loaded += (_, _) => _shell.Services.Audio.Changed += OnAudioChanged;
        Unloaded += (_, _) => _shell.Services.Audio.Changed -= OnAudioChanged;
    }

    private void OnAudioChanged()
    {
        var now = _shell.Services.Audio.Current is { Kind: QueueKind.Track, Id: { } id } ? id : (long?)null;
        if (now == _playingId) return;
        _playingId = now;
        BuildTracks();
    }

    private void BuildTracks()
    {
        _list.Children.Clear();
        var multiDisc = _album.Tracks.Any(t => (t.DiscNo ?? 1) > 1);
        var variousArtists = _album.Tracks.Select(t => t.Artist).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.CurrentCultureIgnoreCase).Count() > 1;
        int? lastDisc = null;

        foreach (var (track, index) in _album.Tracks.Select((t, i) => (t, i)))
        {
            if (multiDisc && (track.DiscNo ?? 1) != lastDisc)
            {
                lastDisc = track.DiscNo ?? 1;
                var disc = PageParts.Muted($"Disc {lastDisc}", 13);
                disc.Margin = new Thickness(10, 14, 0, 6);
                _list.Children.Add(disc);
            }

            var online = _model.PathOf(track) is not null;
            var playing = track.TrackId == _playingId;
            var number = PageParts.Muted(track.TrackNo?.ToString() ?? "", 14);
            number.Width = 34;
            number.VerticalAlignment = VerticalAlignment.Center;
            var name = PageParts.Semi(track.Title, 16);
            name.SetResourceReference(TextBlock.ForegroundProperty, playing ? "AccentBrush" : "InkBrush");
            var sub = variousArtists && !string.IsNullOrWhiteSpace(track.Artist) ? PageParts.Muted(track.Artist!, 13) : null;
            var time = PageParts.Muted(PageParts.FormatClock(track.DurationSeconds), 13);
            time.VerticalAlignment = VerticalAlignment.Center;

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name } };
            if (sub is not null) left.Children.Add(sub);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(left, 1);
            Grid.SetColumn(time, 2);
            grid.Children.Add(number);
            grid.Children.Add(left);
            grid.Children.Add(time);
            if (!online) grid.Opacity = 0.55;

            var captured = index;
            _list.Children.Add(PageParts.ClickableRow(grid, async () => await AudioQueues.PlayTracksAsync(_shell, _album.Tracks, captured), null, track.Title));
        }
    }
}
