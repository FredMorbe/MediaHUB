using System.Windows;
using System.Windows.Controls;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App.Pages;

/// <summary>
/// Ein Album im Mittelpanel der Musik-Seite: Cover, Angaben, runder Abspielknopf und die Titeltabelle; ein Klick startet die
/// Wiedergabe ab diesem Titel. Bildlauf und Zurück liefert die Musik-Seite.
/// </summary>
public sealed class AlbumPage : UserControl
{
    private const string PlayGlyph = "\uE768";
    private const string ShuffleGlyph = "\uE8B1";
    private const string RenameGlyph = "\uE70F";

    private readonly IShell _shell;
    private readonly LibraryModel _model;
    private readonly AlbumInfo _album;
    private readonly StackPanel _list = new();
    private readonly IReadOnlyList<GridLength> _columns;
    private readonly bool _showArtist;
    private long? _playingId;

    /// <param name="openArtist">Öffnet die Seite des Künstlers; der Name im Kopf ist dann ein Link.</param>
    public AlbumPage(IShell shell, string albumKey, Action<string>? openArtist = null)
    {
        _shell = shell;
        _model = shell.Services.Model;
        _album = _model.Albums.FirstOrDefault(a => a.Key == albumKey) ?? throw new InvalidOperationException($"Album '{albumKey}' existiert nicht (mehr).");

        // Bei Sammlungen mit wechselnden Künstlern bekommt die Tabelle eine Spalte für den Künstler.
        _showArtist = _album.Tracks.Select(t => t.Artist).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.CurrentCultureIgnoreCase).Count() > 1;
        _columns = _showArtist
            ? new[] { new GridLength(36), new GridLength(3, GridUnitType.Star), new GridLength(2, GridUnitType.Star), new GridLength(56) }
            : new[] { new GridLength(36), new GridLength(1, GridUnitType.Star), new GridLength(56) };

        var cover = PageParts.Thumb(ImageCache.Load(_model.AlbumArtPath(_album.Key), 360), 168);
        cover.CornerRadius = new CornerRadius(6);
        cover.VerticalAlignment = VerticalAlignment.Bottom;

        var kicker = new TextBlock { Text = "ALBUM", Margin = new Thickness(0, 0, 0, 4) };
        kicker.SetResourceReference(StyleProperty, "KickerText");
        var title = PageParts.Heading(_album.Title, 44);
        title.TextWrapping = TextWrapping.Wrap;
        title.LineHeight = 42;
        title.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;

        var artist = PageParts.Semi(_album.Artist, 15);
        artist.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        if (openArtist is not null && _album.Artist != MusicIndex.UnknownArtist)
        {
            artist.Cursor = System.Windows.Input.Cursors.Hand;
            artist.ToolTip = "Künstler öffnen";
            artist.MouseEnter += (_, _) => artist.TextDecorations = TextDecorations.Underline;
            artist.MouseLeave += (_, _) => artist.TextDecorations = null;
            artist.MouseLeftButtonUp += (_, _) => openArtist(_album.Artist);
        }
        var total = _album.Tracks.Sum(t => t.DurationSeconds ?? 0);
        var meta = PageParts.Muted(string.Join(" · ", new[]
        {
            _album.Year?.ToString(), $"{_album.Tracks.Count} {(_album.Tracks.Count == 1 ? "Titel" : "Titel")}", PageParts.FormatDuration(total),
        }.Where(s => !string.IsNullOrEmpty(s))), 13);
        meta.Margin = new Thickness(0, 2, 0, 0);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        var play = new Button { Content = PlayGlyph, Width = 44, Height = 44, FontSize = 18, ToolTip = "Abspielen" };
        play.SetResourceReference(StyleProperty, "AccentIconButton");
        System.Windows.Automation.AutomationProperties.SetName(play, "Album abspielen");
        play.Click += async (_, _) => await AudioQueues.PlayTracksAsync(_shell, _album.Tracks, 0);
        var shuffle = new Button { Content = ShuffleGlyph, Margin = new Thickness(8, 0, 0, 0), ToolTip = "Zufällig" };
        shuffle.SetResourceReference(StyleProperty, "IconButton");
        System.Windows.Automation.AutomationProperties.SetName(shuffle, "Album zufällig abspielen");
        shuffle.Click += async (_, _) => await AudioQueues.PlayTracksAsync(_shell, _album.Tracks, 0, shuffle: true);
        var toPlaylist = new Button { Content = "\uE710", Margin = new Thickness(8, 0, 0, 0), ToolTip = "Alle Titel zu einer Playlist hinzufügen" };
        toPlaylist.SetResourceReference(StyleProperty, "IconButton");
        System.Windows.Automation.AutomationProperties.SetName(toPlaylist, "Album zu Playlist hinzufügen");
        toPlaylist.Click += (_, _) => PlaylistActions.ShowAddMenu(_shell, toPlaylist, _album.Tracks);
        var rename = new Button { Content = RenameGlyph, Margin = new Thickness(8, 0, 0, 0), ToolTip = "Album bearbeiten (Name, Künstler)" };
        rename.SetResourceReference(StyleProperty, "IconButton");
        System.Windows.Automation.AutomationProperties.SetName(rename, "Album bearbeiten");
        rename.Click += (_, _) => MusicRenamePage.ShowAlbum(_shell, _album.Key);
        buttons.Children.Add(play);
        buttons.Children.Add(shuffle);
        buttons.Children.Add(toPlaylist);
        buttons.Children.Add(rename);

        var info = new StackPanel { Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom, Children = { kicker, title, artist, meta } };
        var header = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(info, 1);
        header.Children.Add(cover);
        header.Children.Add(info);

        var tableHead = PageParts.TableHeader(_columns, _showArtist ? new[] { "#", "Titel", "Künstler", "Dauer" } : new[] { "#", "Titel", "Dauer" });
        ((FrameworkElement)tableHead).Margin = new Thickness(0, 16, 0, 4);
        Content = new StackPanel { Children = { header, buttons, tableHead, _list } };

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
        int? lastDisc = null;

        foreach (var (track, index) in _album.Tracks.Select((t, i) => (t, i)))
        {
            if (multiDisc && (track.DiscNo ?? 1) != lastDisc)
            {
                lastDisc = track.DiscNo ?? 1;
                var disc = PageParts.Muted($"Disc {lastDisc}", 12);
                disc.Margin = new Thickness(8, 10, 0, 4);
                _list.Children.Add(disc);
            }

            var playing = track.TrackId == _playingId;
            var name = PageParts.Semi(track.Title, 14);
            name.SetResourceReference(TextBlock.ForegroundProperty, playing ? "AccentBrush" : "InkBrush");
            var cells = new List<FrameworkElement> { PageParts.Cell(track.TrackNo?.ToString() ?? ""), name };
            if (_showArtist) cells.Add(PageParts.Cell(track.Artist ?? ""));
            cells.Add(PageParts.Cell(PageParts.FormatClock(track.DurationSeconds)));

            var captured = index;
            Border? row = null;
            row = PageParts.TableRow(_columns, cells, async () => await AudioQueues.PlayTracksAsync(_shell, _album.Tracks, captured), track.Title, dimmed: _model.PathOf(track) is null,
                menu: () => new (string, Action)[]
                {
                    ("Zu Playlist hinzufügen …", () => PlaylistActions.ShowAddMenu(_shell, row!, new[] { track })),
                    ("Bearbeiten …", () => MusicRenamePage.ShowTrack(_shell, track.TrackId)),
                });
            _list.Children.Add(row);
        }
    }
}
