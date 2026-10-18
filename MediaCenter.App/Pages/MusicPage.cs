using System.Windows;
using System.Windows.Controls;
using MediaCenter.App.Controls;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App.Pages;

/// <summary>Musikbibliothek: Alben, Künstler und Titel, mit Suche. Mit Künstlernamen nur dessen Alben.</summary>
public sealed class MusicPage : UserControl
{
    private const int TracksPerPage = 200;

    private enum Tab
    {
        Albums,
        Artists,
        Tracks,
    }

    private readonly IShell _shell;
    private readonly LibraryModel _model;
    private readonly string? _artist;
    private readonly StackPanel _content = new();
    private readonly TextBlock _count = new() { FontSize = 14 };
    private readonly TextBox _search;
    private Tab _tab = Tab.Albums;
    private int _shownTracks = TracksPerPage;

    public MusicPage(IShell shell, string? artist = null)
    {
        _shell = shell;
        _model = shell.Services.Model;
        _artist = artist;
        _count.SetResourceReference(StyleProperty, "MutedText");

        var stack = new StackPanel();
        if (artist is not null)
        {
            var back = new Button { Content = "", HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Zurück (Esc)", Margin = new Thickness(0, 0, 0, 6) };
            back.SetResourceReference(StyleProperty, "IconButton");
            back.Click += (_, _) => _shell.Back();
            stack.Children.Add(back);
        }
        stack.Children.Add(PageParts.Header(artist is null ? "Bibliothek" : "Künstler", artist ?? "Musik", _count));

        var (field, box) = PageParts.HintBox("Suchen …", 260);
        _search = box;
        _search.TextChanged += (_, _) =>
        {
            _shownTracks = TracksPerPage;
            Fill();
        };

        // Werkzeugleiste: Ansicht links (Alben, Künstler, Titel), Suche rechts
        var bar = new Grid { Margin = new Thickness(0, 0, 0, 22) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (artist is null)
        {
            bar.Children.Add(PageParts.Segmented("musictab",
                new[] { (Tab.Albums, "Alben"), (Tab.Artists, "Künstler"), (Tab.Tracks, "Titel") }
                    .Select(t => (t.Item2, t.Item1 == _tab, (Action)(() =>
                    {
                        _tab = t.Item1;
                        _shownTracks = TracksPerPage;
                        Fill();
                    }))),
                "Ansicht"));
        }
        field.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(field, 1);
        bar.Children.Add(field);
        stack.Children.Add(bar);
        stack.Children.Add(_content);

        Content = PageParts.Scrolling(stack);
        Fill();
    }

    private IEnumerable<AlbumInfo> FilteredAlbums()
    {
        var text = _search.Text.Trim();
        return _model.Albums
            .Where(a => _artist is null || string.Equals(a.Artist, _artist, StringComparison.CurrentCultureIgnoreCase))
            .Where(a => text.Length == 0 || a.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase) || a.Artist.Contains(text, StringComparison.CurrentCultureIgnoreCase));
    }

    private void Fill()
    {
        _content.Children.Clear();
        if (_model.Tracks.Count == 0)
        {
            _count.Visibility = Visibility.Collapsed;
            _content.Children.Add(PageParts.EmptyState("\uE8D6", "Noch keine Musik",
                "Füge einen Ordner mit Musik hinzu. Titel werden an ihren Tags (Titel, Künstler, Album) erkannt; ohne Tags dient der Ordnername als Album.",
                PageParts.Button("Ordner hinzufügen", "PrimaryButton", async (_, _) => await LibraryActions.AddFolderAsync(_shell), new Thickness(0))));
            return;
        }

        switch (_tab)
        {
            case Tab.Albums: FillAlbums(); break;
            case Tab.Artists: FillArtists(); break;
            case Tab.Tracks: FillTracks(); break;
        }
    }

    private void FillAlbums()
    {
        var albums = FilteredAlbums().ToList();
        _count.Text = $"{albums.Count} {(albums.Count == 1 ? "Album" : "Alben")}";

        var grid = new WrapPanel();
        foreach (var album in albums)
        {
            var card = new CardView(_model.ToCard(album)) { Margin = new Thickness(0, 0, 22, 28) };
            card.Activated += _ => _shell.Open(() => new AlbumPage(_shell, album.Key));
            grid.Children.Add(card);
        }
        _content.Children.Add(grid);
    }

    private void FillArtists()
    {
        var text = _search.Text.Trim();
        var artists = _model.Artists.Where(a => text.Length == 0 || a.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)).ToList();
        _count.Text = $"{artists.Count} {(artists.Count == 1 ? "Künstler" : "Künstler")}";

        var grid = new WrapPanel();
        foreach (var artist in artists)
        {
            var cover = artist.Albums.Select(a => _model.AlbumArtPath(a.Key)).FirstOrDefault(p => p is not null);
            var card = new CardView(new CardItem
            {
                TitleId = 0,
                Title = artist.Name,
                Meta = $"{artist.Albums.Count} {(artist.Albums.Count == 1 ? "Album" : "Alben")}",
                Kind = TitleKind.Movie,
                Image = ImageCache.Load(cover, 360),
                Square = true,
            }) { Margin = new Thickness(0, 0, 22, 28) };
            card.Activated += _ => _shell.Open(() => new MusicPage(_shell, artist.Name));
            grid.Children.Add(card);
        }
        _content.Children.Add(grid);
    }

    private void FillTracks()
    {
        var text = _search.Text.Trim();
        var tracks = _model.Tracks
            .Where(t => text.Length == 0
                || t.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                || (t.Artist?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false)
                || (t.Album?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false))
            .OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _count.Text = $"{tracks.Count} {(tracks.Count == 1 ? "Titel" : "Titel")}";

        foreach (var (track, index) in tracks.Take(_shownTracks).Select((t, i) => (t, i)))
        {
            var online = _model.PathOf(track) is not null;
            var title = PageParts.Semi(track.Title);
            var meta = PageParts.Muted($"{(string.IsNullOrWhiteSpace(track.Artist) ? MusicIndex.ArtistOf(track) : track.Artist)} · {track.Album ?? MusicIndex.UnknownAlbum}" + (online ? "" : " · offline"), 13);
            meta.TextTrimming = TextTrimming.CharacterEllipsis;
            var time = PageParts.Muted(PageParts.FormatClock(track.DurationSeconds), 13);
            time.VerticalAlignment = VerticalAlignment.Center;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel { Children = { title, meta }, Margin = new Thickness(0, 0, 16, 0) };
            Grid.SetColumn(time, 1);
            grid.Children.Add(left);
            grid.Children.Add(time);
            if (!online) grid.Opacity = 0.55;

            var captured = index;
            _content.Children.Add(PageParts.ClickableRow(grid, async () => await AudioQueues.PlayTracksAsync(_shell, tracks, captured), null, track.Title));
        }

        if (tracks.Count > _shownTracks)
        {
            _content.Children.Add(PageParts.Button($"Weitere anzeigen ({tracks.Count - _shownTracks})", "GhostButton", (_, _) =>
            {
                _shownTracks += TracksPerPage;
                Fill();
            }, new Thickness(0, 14, 0, 0)));
        }
    }
}
