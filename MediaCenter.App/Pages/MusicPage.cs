using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MediaCenter.App.Controls;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App.Pages;

/// <summary>
/// Musik nach dem Vorbild der Spotify-Desktop-App: links die Bibliothek, in der Mitte Start, Album, Künstler oder eine Gesamtliste, rechts
/// (solange ein Titel läuft) „Jetzt läuft“. Das Mittelpanel hat eine eigene Navigation mit Zurück; die Bibliothek bleibt dabei stehen.
/// </summary>
public sealed class MusicPage : UserControl
{
    private const int TracksPerPage = 300;
    private const int QuickTiles = 8;
    private const int RowItems = 12;
    private const double NowPlayingMinWidth = 1180;

    private enum Kind
    {
        Home,
        Albums,
        Artists,
        Tracks,
        Album,
        Artist,
        Playlist,
    }

    /// <param name="Key">Albumschlüssel, Künstlername bzw. Id der Playlist.</param>
    /// <param name="Rename">Eine eben angelegte Playlist öffnet gleich das Namensfeld; gilt nur für das erste Anzeigen.</param>
    private sealed record View(Kind Kind, string? Key = null, bool Rename = false);

    private readonly IShell _shell;
    private readonly LibraryModel _model;
    private readonly Stack<View> _back = new();
    private readonly ScrollViewer _scroll;
    private readonly Border _backHolder;
    private readonly MusicLibraryPanel _library;
    private readonly NowPlayingPanel _nowPlaying;
    private View _view = new(Kind.Home);

    /// <summary>Die Seite wurde von außen (Suche) direkt auf ein Album geöffnet: „Zurück“ führt aus der Musik heraus statt auf deren Start.</summary>
    private readonly bool _fromOutside;

    /// <param name="openAlbumKey">Öffnet gleich dieses Album; null zeigt den Start der Musik.</param>
    public MusicPage(IShell shell, string? openAlbumKey = null)
    {
        _shell = shell;
        _model = shell.Services.Model;
        _fromOutside = openAlbumKey is not null;
        if (openAlbumKey is not null) _view = new View(Kind.Album, openAlbumKey);

        _library = new MusicLibraryPanel(shell, key => Go(new View(Kind.Album, key)), name => Go(new View(Kind.Artist, name)), () => Go(new View(Kind.Tracks)),
            id => Go(new View(Kind.Playlist, id.ToString())), NewPlaylist)
        {
            Margin = new Thickness(0, 0, 6, 0),
        };
        _nowPlaying = new NowPlayingPanel(shell) { Margin = new Thickness(6, 0, 0, 0) };

        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };

        // Farbschein oben im Panel wie bei Spotify, hier in der Akzentfarbe; die Maske blendet ihn nach unten aus
        var tint = new System.Windows.Shapes.Rectangle
        {
            Height = 320,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            OpacityMask = new LinearGradientBrush(Colors.Black, Colors.Transparent, 90),
        };
        tint.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentSoftBrush");

        var backButton = new Button { Content = "\uE72B", Width = 30, Height = 30, FontSize = 13, ToolTip = "Zurück (Esc)" };
        backButton.SetResourceReference(StyleProperty, "IconButton");
        System.Windows.Automation.AutomationProperties.SetName(backButton, "Zurück");
        backButton.Click += (_, _) => GoBack();
        _backHolder = new Border
        {
            CornerRadius = new CornerRadius(15),
            Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(14, 10, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = backButton,
        };

        var center = new Border { CornerRadius = new CornerRadius(12), ClipToBounds = true };
        center.SetResourceReference(Border.BackgroundProperty, "BgSoftBrush");
        center.Child = new Grid
        {
            Children =
            {
                tint,
                _scroll,
                _backHolder,
            },
        };

        // Wie bei Spotify liegen die Panels mit kleinem Abstand auf dem dunklen Grund; oben bleibt die Navigationsleiste frei.
        var root = new Grid { Margin = new Thickness(6, PageParts.NavInset + 2, 6, 6) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 320 });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(center, 1);
        Grid.SetColumn(_nowPlaying, 2);
        root.Children.Add(_library);
        root.Children.Add(center);
        root.Children.Add(_nowPlaying);
        Content = root;

        SizeChanged += (_, e) => _nowPlaying.Suppressed = e.NewSize.Width < NowPlayingMinWidth;
        Loaded += (_, _) => _model.MusicChanged += OnMusicChanged;
        Unloaded += (_, _) => _model.MusicChanged -= OnMusicChanged;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || _back.Count == 0 || Keyboard.FocusedElement is TextBox) return;
            GoBack();
            e.Handled = true;
        };
        Render(animate: false);
    }

    private void Go(View next)
    {
        if (next == _view) return;
        _back.Push(_view);
        _view = next;
        Render(animate: true);
    }

    private void GoBack()
    {
        if (_back.Count == 0)
        {
            if (_fromOutside) _shell.Back();
            return;
        }
        _view = _back.Pop();
        Render(animate: true);
    }

    /// <summary>Legt eine leere Playlist an und öffnet sie mit dem Namensfeld.</summary>
    private void NewPlaylist()
    {
        var playlist = PlaylistActions.Create(_shell, Array.Empty<TrackRow>());
        Go(new View(Kind.Playlist, playlist.Id.ToString(), Rename: true));
    }

    /// <summary>Die Playlist ist gelöscht: Einträge für sie fallen aus dem Zurück-Verlauf, und die Ansicht wechselt zur vorherigen.</summary>
    private void OnPlaylistDeleted(long id)
    {
        bool IsDeleted(View view) => view.Kind == Kind.Playlist && view.Key == id.ToString();
        var kept = _back.Reverse().Where(v => !IsDeleted(v)).ToList(); // der Stapel liefert die neueste zuerst
        _back.Clear();
        foreach (var view in kept) _back.Push(view);
        _view = _back.Count > 0 ? _back.Pop() : new View(Kind.Home);
        Render(animate: true);
    }

    /// <summary>
    /// Ein Song oder Album wurde umbenannt: die Ansicht baut sich an derselben Stelle neu auf. Ein umbenanntes Album hat einen neuen Schlüssel,
    /// den die offene Ansicht und der Zurück-Verlauf übernehmen.
    /// </summary>
    private void OnMusicChanged(string? oldAlbumKey, string? newAlbumKey)
    {
        View Remap(View view) => oldAlbumKey is not null && view.Kind == Kind.Album && view.Key == oldAlbumKey ? view with { Key = newAlbumKey } : view;

        var kept = _back.Reverse().Select(Remap).ToList(); // der Stapel liefert die neueste zuerst
        _back.Clear();
        foreach (var view in kept) _back.Push(view);
        _view = Remap(_view);

        var offset = _scroll.VerticalOffset;
        Render(animate: false);
        _scroll.UpdateLayout();
        _scroll.ScrollToVerticalOffset(offset);
    }

    /// <summary>Menü einer Albumkarte.</summary>
    private IReadOnlyList<(string Text, Action Click)> AlbumCardMenu(CardItem item) =>
        item.Tag is AlbumInfo album
            ? new (string, Action)[]
            {
                ("Als Nächstes spielen", async () => await AudioQueues.EnqueueTracksAsync(_shell, album.Tracks, next: true)),
                ("Zur Warteschlange hinzufügen", async () => await AudioQueues.EnqueueTracksAsync(_shell, album.Tracks, next: false)),
                ("Bearbeiten …", () => MusicRenamePage.ShowAlbum(_shell, album.Key)),
                ("Tags in den Dateien ändern …", () => TagEditorPage.ShowAlbum(_shell, album.Key)),
            }
            : Array.Empty<(string, Action)>();

    private void Render(bool animate)
    {
        var startRenaming = _view.Rename;
        _view = _view with { Rename = false }; // beim Zurückkehren soll das Feld nicht wieder aufgehen
        var content = _view.Kind switch
        {
            Kind.Home => BuildHome(),
            Kind.Albums => BuildAlbums(),
            Kind.Artists => BuildArtists(),
            Kind.Tracks => BuildTracks(),
            Kind.Album => new AlbumPage(_shell, _view.Key!, name => Go(new View(Kind.Artist, name))),
            Kind.Artist => BuildArtist(_view.Key!),
            Kind.Playlist => new PlaylistPage(_shell, long.Parse(_view.Key!), OnPlaylistDeleted, startRenaming),
            _ => throw new InvalidOperationException($"Unbekannte Ansicht {_view.Kind}."),
        };
        content.Margin = new Thickness(24, 50, 24, 24);
        _scroll.Content = content;
        _scroll.ScrollToTop();
        if (animate) content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));

        _backHolder.Visibility = _back.Count > 0 || _fromOutside ? Visibility.Visible : Visibility.Collapsed;
        _library.Select(_view.Kind switch
        {
            Kind.Album => $"album:{_view.Key}",
            Kind.Artist => $"artist:{_view.Key}",
            Kind.Playlist => $"playlist:{_view.Key}",
            Kind.Tracks => "tracks",
            _ => null,
        });
    }

    // --- Start ---------------------------------------------------------------------------------------------

    private FrameworkElement BuildHome()
    {
        var stack = new StackPanel();
        if (_model.Tracks.Count == 0)
        {
            stack.Children.Add(PageParts.Header("Bibliothek", "Musik", (TextBlock?)null, 36));
            stack.Children.Add(PageParts.EmptyState("\uE8D6", "Noch keine Musik",
                "Füge einen Ordner mit Musik hinzu. Titel werden an ihren Tags (Titel, Künstler, Album) erkannt; ohne Tags dient der Ordnername als Album.",
                PageParts.Button("Ordner hinzufügen", "PrimaryButton", async (_, _) => await LibraryActions.AddFolderAsync(_shell), new Thickness(0))));
            return stack;
        }

        var hour = DateTime.Now.Hour;
        var greeting = PageParts.Heading(hour < 11 ? "Guten Morgen" : hour < 18 ? "Guten Tag" : "Guten Abend", 34);
        greeting.Margin = new Thickness(0, 0, 0, 12);
        stack.Children.Add(greeting);

        var recent = RecentAlbums().ToList();
        var newest = _model.Albums.OrderByDescending(a => a.Tracks.Max(t => t.TrackId)).ToList();

        // Schnellzugriff: zuletzt gehörte Alben, mit den neuesten aufgefüllt
        var quick = recent.Concat(newest.Where(a => recent.All(r => r.Key != a.Key))).Take(QuickTiles).ToList();
        var grid = new UniformGrid { Columns = 2 };
        foreach (var album in quick) grid.Children.Add(QuickTile(album));
        stack.Children.Add(grid);
        stack.SizeChanged += (_, e) => grid.Columns = e.NewSize.Width >= 1000 ? 4 : 2;

        if (recent.Count > 0) AddRow(stack, "Zuletzt gehört", recent.Take(RowItems), null);
        AddRow(stack, "Neu hinzugefügt", newest.Take(RowItems), () => Go(new View(Kind.Albums)));
        if (_model.Artists.Count > 0)
        {
            var artists = _model.Artists.Take(RowItems).Select(ArtistCard).ToList();
            stack.Children.Add(new CardRow("Künstler", artists, card => Go(new View(Kind.Artist, card.Title)), null, () => Go(new View(Kind.Artists)), compact: true)
            {
                Margin = new Thickness(0, 12, 0, 0),
            });
        }
        return stack;
    }

    private IEnumerable<AlbumInfo> RecentAlbums() =>
        _shell.Services.Settings.RecentAlbums.Select(key => _model.Albums.FirstOrDefault(a => a.Key == key)).OfType<AlbumInfo>();

    private void AddRow(StackPanel stack, string heading, IEnumerable<AlbumInfo> albums, Action? showAll)
    {
        var cards = albums.Select(a => _model.ToCard(a)).ToList();
        if (cards.Count == 0) return;
        stack.Children.Add(new CardRow(heading, cards, card => Go(new View(Kind.Album, ((AlbumInfo)card.Tag!).Key)), AlbumCardMenu, showAll, compact: true) { Margin = new Thickness(0, 12, 0, 0) });
    }

    /// <summary>Kachel des Schnellzugriffs: Cover links, Titel rechts, beim Überfahren erscheint ein Abspielknopf.</summary>
    private UIElement QuickTile(AlbumInfo album)
    {
        var thumb = PageParts.Thumb(ImageCache.Load(_model.AlbumArtPath(album.Key), 120), 52, "\uE8D6");
        thumb.CornerRadius = new CornerRadius(6, 0, 0, 6);
        thumb.VerticalAlignment = VerticalAlignment.Stretch;
        thumb.Height = double.NaN;

        var name = PageParts.Semi(album.Title, 13);
        name.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        name.TextWrapping = TextWrapping.Wrap;
        name.MaxHeight = 36;
        name.VerticalAlignment = VerticalAlignment.Center;
        name.Margin = new Thickness(12, 0, 6, 0);

        var play = new Button { Content = "\uE768", Width = 34, Height = 34, FontSize = 14, Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed, ToolTip = "Abspielen" };
        play.SetResourceReference(StyleProperty, "AccentIconButton");
        play.Width = 34;
        play.Height = 34;
        System.Windows.Automation.AutomationProperties.SetName(play, $"{album.Title} abspielen");
        play.Click += async (_, _) => await AudioQueues.PlayTracksAsync(_shell, album.Tracks, 0);

        var layout = new Grid { Height = 52 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(name, 1);
        Grid.SetColumn(play, 2);
        layout.Children.Add(thumb);
        layout.Children.Add(name);
        layout.Children.Add(play);

        var row = PageParts.ClickableRow(layout, () => Go(new View(Kind.Album, album.Key)), new Thickness(0), album.Title);
        row.Padding = new Thickness(0);
        row.CornerRadius = new CornerRadius(6);
        row.MouseEnter += (_, _) => play.Visibility = Visibility.Visible;
        row.MouseLeave += (_, _) => play.Visibility = Visibility.Collapsed;

        var rest = new Border { CornerRadius = new CornerRadius(6) };
        rest.SetResourceReference(Border.BackgroundProperty, "LineBrush");
        return new Grid { Margin = new Thickness(0, 0, 10, 10), Children = { rest, row } };
    }

    private CardItem ArtistCard(ArtistInfo artist)
    {
        var cover = artist.Albums.Select(a => _model.AlbumArtPath(a.Key)).FirstOrDefault(p => p is not null);
        return new CardItem
        {
            TitleId = 0,
            Title = artist.Name,
            Meta = $"{artist.Albums.Count} {(artist.Albums.Count == 1 ? "Album" : "Alben")}",
            Kind = TitleKind.Movie,
            Image = ImageCache.Load(cover, 360),
            Square = true,
            SquareSize = 140,
            Tag = artist,
        };
    }

    // --- Gesamtlisten, Künstler ----------------------------------------------------------------------------

    private FrameworkElement BuildAlbums()
    {
        var stack = new StackPanel();
        stack.Children.Add(PageParts.Header("Bibliothek", "Alben", $"{_model.Albums.Count} {(_model.Albums.Count == 1 ? "Album" : "Alben")}", 36));
        stack.Children.Add(AlbumGrid(_model.Albums));
        return stack;
    }

    private FrameworkElement BuildArtists()
    {
        var stack = new StackPanel();
        stack.Children.Add(PageParts.Header("Bibliothek", "Künstler", $"{_model.Artists.Count} Künstler", 36));
        var grid = new WrapPanel();
        foreach (var artist in _model.Artists)
        {
            var card = new CardView(ArtistCard(artist)) { Margin = new Thickness(0, 0, 14, 14) };
            var name = artist.Name;
            card.Activated += _ => Go(new View(Kind.Artist, name));
            grid.Children.Add(card);
        }
        stack.Children.Add(grid);
        return stack;
    }

    private FrameworkElement BuildArtist(string name)
    {
        var albums = _model.Albums.Where(a => string.Equals(a.Artist, name, StringComparison.CurrentCultureIgnoreCase)).ToList();
        var stack = new StackPanel();
        stack.Children.Add(PageParts.Header("Künstler", name, $"{albums.Count} {(albums.Count == 1 ? "Album" : "Alben")}", 36));
        stack.Children.Add(AlbumGrid(albums));
        return stack;
    }

    private WrapPanel AlbumGrid(IEnumerable<AlbumInfo> albums)
    {
        var grid = new WrapPanel();
        foreach (var album in albums)
        {
            var card = new CardView(_model.ToCard(album)) { Margin = new Thickness(0, 0, 14, 14), Menu = AlbumCardMenu };
            var key = album.Key;
            card.Activated += _ => Go(new View(Kind.Album, key));
            grid.Children.Add(card);
        }
        return grid;
    }

    private FrameworkElement BuildTracks()
    {
        var count = new TextBlock { FontSize = 13 };
        count.SetResourceReference(StyleProperty, "MutedText");
        var stack = new StackPanel();
        stack.Children.Add(PageParts.Header("", "Titel", count, 30));

        var (field, box) = PageParts.HintBox("Titel suchen …", 280, 13);
        box.Height = 32;
        field.HorizontalAlignment = HorizontalAlignment.Left;
        field.Margin = new Thickness(0, 0, 0, 8);
        stack.Children.Add(field);

        var columns = new[] { new GridLength(40), new GridLength(3, GridUnitType.Star), new GridLength(2, GridUnitType.Star), new GridLength(2, GridUnitType.Star), new GridLength(56) };
        stack.Children.Add(PageParts.TableHeader(columns, "#", "Titel", "Künstler", "Album", "Dauer"));
        var list = new StackPanel();
        stack.Children.Add(list);
        var shown = TracksPerPage;

        void Fill()
        {
            list.Children.Clear();
            var text = box.Text.Trim();
            var tracks = _model.Tracks
                .Where(t => text.Length == 0
                    || t.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                    || (t.Artist?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false)
                    || (t.Album?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false))
                .OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            count.Text = $"{tracks.Count} Titel";

            foreach (var (track, index) in tracks.Take(shown).Select((t, i) => (t, i)))
            {
                var title = PageParts.Semi(track.Title, 14);
                title.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
                var cells = new List<FrameworkElement>
                {
                    PageParts.Cell((index + 1).ToString()),
                    title,
                    PageParts.Cell(string.IsNullOrWhiteSpace(track.Artist) ? MusicIndex.ArtistOf(track) : track.Artist),
                    PageParts.Cell(track.Album ?? MusicIndex.UnknownAlbum),
                    PageParts.Cell(PageParts.FormatClock(track.DurationSeconds)),
                };
                var captured = index;
                Border? row = null;
                row = PageParts.TableRow(columns, cells, async () => await AudioQueues.PlayTracksAsync(_shell, tracks, captured), track.Title, dimmed: _model.PathOf(track) is null,
                    menu: () => new (string, Action)[]
                    {
                        ("Als Nächstes spielen", async () => await AudioQueues.EnqueueTracksAsync(_shell, new[] { track }, next: true)),
                        ("Zur Warteschlange hinzufügen", async () => await AudioQueues.EnqueueTracksAsync(_shell, new[] { track }, next: false)),
                        ("Zu Playlist hinzufügen …", () => PlaylistActions.ShowAddMenu(_shell, row!, new[] { track })),
                        ("Bearbeiten …", () => MusicRenamePage.ShowTrack(_shell, track.TrackId)),
                        ("Tags in der Datei ändern …", () => TagEditorPage.ShowTrack(_shell, track.TrackId)),
                    });
                list.Children.Add(row);
            }

            if (tracks.Count > shown)
            {
                list.Children.Add(PageParts.Button($"Weitere anzeigen ({tracks.Count - shown})", "GhostButton", (_, _) =>
                {
                    shown += TracksPerPage;
                    Fill();
                }, new Thickness(0, 10, 0, 0)));
            }
        }

        box.TextChanged += (_, _) =>
        {
            shown = TracksPerPage;
            Fill();
        };
        Fill();
        return stack;
    }
}
