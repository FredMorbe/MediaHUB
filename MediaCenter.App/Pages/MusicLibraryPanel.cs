using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App.Pages;

/// <summary>
/// Linkes Panel der Musik-Seite wie bei Spotify („Deine Bibliothek“): Alben oder Künstler als Liste mit kleinem Cover, dazu Suche.
/// Die Zeilen entstehen portionsweise beim Scrollen, damit auch große Sammlungen die Seite nicht verzögern.
/// </summary>
internal sealed class MusicLibraryPanel : Border
{
    private const int Chunk = 60;
    private const string LibraryGlyph = "\uE8F1";
    private const string AddGlyph = "\uE710";
    private const string NoteGlyph = "\uE8D6";

    private enum Mode
    {
        Albums,
        Artists,
        Playlists,
    }

    private readonly IShell _shell;
    private readonly LibraryModel _model;
    private readonly Action<string> _openAlbum;
    private readonly Action<string> _openArtist;
    private readonly Action _openTracks;
    private readonly Action<long> _openPlaylist;
    private readonly Action _newPlaylist;
    private readonly StackPanel _list = new();
    private readonly ScrollViewer _scroll;
    private readonly TextBox _search;
    private readonly Dictionary<string, TextBlock> _albumNames = new();
    private readonly Dictionary<string, Border> _selection = new();
    private List<Func<UIElement>> _rows = new();
    private int _shown;
    private Mode _mode = Mode.Albums;
    private string? _selectedKey;
    private string? _playingAlbum;
    private long? _playingTrack;

    public MusicLibraryPanel(IShell shell, Action<string> openAlbum, Action<string> openArtist, Action openTracks, Action<long> openPlaylist, Action newPlaylist)
    {
        _shell = shell;
        _model = shell.Services.Model;
        _openAlbum = openAlbum;
        _openArtist = openArtist;
        _openTracks = openTracks;
        _openPlaylist = openPlaylist;
        _newPlaylist = newPlaylist;

        Width = 280;
        CornerRadius = new CornerRadius(12);
        this.SetResourceReference(BackgroundProperty, "BgSoftBrush");

        var icon = new TextBlock { Text = LibraryGlyph, FontSize = 18, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        icon.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var title = PageParts.Semi("Deine Bibliothek", 14);
        title.VerticalAlignment = VerticalAlignment.Center;
        title.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        var add = new Button { Content = AddGlyph, Width = 30, Height = 30, FontSize = 13, ToolTip = "Ordner mit Musik hinzufügen" };
        add.SetResourceReference(StyleProperty, "IconButton");
        System.Windows.Automation.AutomationProperties.SetName(add, "Ordner mit Musik hinzufügen");
        add.Click += async (_, _) => await LibraryActions.AddFolderAsync(_shell);
        Grid.SetColumn(add, 2);
        var header = new Grid { Margin = new Thickness(16, 10, 10, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(title, 1);
        header.Children.Add(icon);
        header.Children.Add(title);
        header.Children.Add(add);

        var modes = PageParts.Segmented("musiclib",
            new[] { (Mode.Albums, "Alben"), (Mode.Artists, "Künstler"), (Mode.Playlists, "Playlists") }.Select(m => (m.Item2, m.Item1 == _mode, (Action)(() =>
            {
                _mode = m.Item1;
                Fill();
            }))),
            "Bibliothek");
        modes.Margin = new Thickness(12, 0, 12, 6);

        var (field, box) = PageParts.HintBox("In Bibliothek suchen", 0, 13);
        _search = box;
        box.Width = double.NaN;
        box.Height = 32;
        field.Margin = new Thickness(12, 0, 12, 6);
        box.TextChanged += (_, _) => Fill();

        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Content = _list };
        _list.Margin = new Thickness(6, 0, 6, 6);
        _scroll.ScrollChanged += (_, _) =>
        {
            if (_shown < _rows.Count && _scroll.VerticalOffset >= _scroll.ScrollableHeight - 240) AddChunk();
        };

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(modes, 1);
        Grid.SetRow(field, 2);
        Grid.SetRow(_scroll, 3);
        layout.Children.Add(header);
        layout.Children.Add(modes);
        layout.Children.Add(field);
        layout.Children.Add(_scroll);
        Child = layout;

        Fill();
        // Der laufende Titel färbt sein Album in der Liste ein.
        Loaded += (_, _) =>
        {
            _shell.Services.Audio.Changed += OnAudioChanged;
            _model.PlaylistsChanged += OnPlaylistsChanged;
            OnAudioChanged();
        };
        Unloaded += (_, _) =>
        {
            _shell.Services.Audio.Changed -= OnAudioChanged;
            _model.PlaylistsChanged -= OnPlaylistsChanged;
        };
    }

    /// <summary>Die Liste der Playlists hat sich geändert; ist sie sichtbar, baut sie sich neu auf, ohne dass die Liste nach oben springt.</summary>
    private void OnPlaylistsChanged()
    {
        if (_mode != Mode.Playlists) return;
        var offset = _scroll.VerticalOffset;
        Fill();
        _scroll.UpdateLayout();
        _scroll.ScrollToVerticalOffset(offset);
    }

    /// <summary>Hebt den Eintrag hervor, der im Mittelpanel offen ist ("album:…", "artist:…", "tracks"); null hebt nichts hervor.</summary>
    public void Select(string? key)
    {
        _selectedKey = key;
        foreach (var (rowKey, background) in _selection) background.Visibility = rowKey == key ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAudioChanged()
    {
        var audio = _shell.Services.Audio;
        var id = audio.Current is { Kind: QueueKind.Track, Id: { } trackId } ? trackId : (long?)null;
        if (id == _playingTrack) return;
        _playingTrack = id;
        _playingAlbum = id is null ? null : _model.Tracks.FirstOrDefault(t => t.TrackId == id) is { } track
            ? MusicIndex.AlbumKey(MusicIndex.ArtistOf(track), track.Album ?? MusicIndex.UnknownAlbum)
            : null;
        foreach (var (key, name) in _albumNames) name.SetResourceReference(TextBlock.ForegroundProperty, key == _playingAlbum ? "AccentBrush" : "InkBrush");
    }

    private void Fill()
    {
        _list.Children.Clear();
        _albumNames.Clear();
        _selection.Clear();
        _rows = new List<Func<UIElement>>();
        _shown = 0;

        if (_model.Tracks.Count == 0)
        {
            var note = PageParts.Muted("Noch keine Musik. Mit + einen Ordner hinzufügen.", 13);
            note.Margin = new Thickness(10, 8, 10, 0);
            _list.Children.Add(note);
            return;
        }

        var text = _search.Text.Trim();
        if (text.Length == 0)
        {
            _rows.Add(() => Row("tracks", null, false, $"{_model.Tracks.Count} Titel", "Alle Titel", _openTracks, null, accentTile: true));
        }

        if (_mode == Mode.Albums)
        {
            // Zuletzt gehörte Alben zuerst, danach der Rest in der Reihenfolge der Bibliothek.
            var recent = _shell.Services.Settings.RecentAlbums;
            var albums = _model.Albums
                .Where(a => text.Length == 0 || a.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase) || a.Artist.Contains(text, StringComparison.CurrentCultureIgnoreCase))
                .OrderBy(a => { var i = recent.IndexOf(a.Key); return i < 0 ? int.MaxValue : i; })
                .ToList();
            foreach (var album in albums)
            {
                var captured = album;
                _rows.Add(() => Row($"album:{captured.Key}", ImageCache.Load(_model.AlbumArtPath(captured.Key), 96), false, $"Album · {captured.Artist}", captured.Title, () => _openAlbum(captured.Key), captured.Key));
            }
        }
        else if (_mode == Mode.Playlists)
        {
            if (text.Length == 0) _rows.Add(() => Row("new-playlist", null, false, "Leere Playlist anlegen", "Neue Playlist", _newPlaylist, null, accentTile: true, symbol: AddGlyph));
            foreach (var playlist in _model.Playlists.Where(p => text.Length == 0 || p.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)))
            {
                var captured = playlist;
                var count = $"Playlist · {captured.Tracks.Count} Titel";
                _rows.Add(() => Row($"playlist:{captured.Id}", ImageCache.Load(_model.PlaylistCover(captured), 96), false, count, captured.Name, () => _openPlaylist(captured.Id), null));
            }
        }
        else
        {
            foreach (var artist in _model.Artists.Where(a => text.Length == 0 || a.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)))
            {
                var captured = artist;
                var cover = captured.Albums.Select(a => _model.AlbumArtPath(a.Key)).FirstOrDefault(p => p is not null);
                var count = $"Künstler · {captured.Albums.Count} {(captured.Albums.Count == 1 ? "Album" : "Alben")}";
                _rows.Add(() => Row($"artist:{captured.Name}", ImageCache.Load(cover, 96), true, count, captured.Name, () => _openArtist(captured.Name), null));
            }
        }

        if (text.Length > 0 && _rows.Count == 0)
        {
            var none = PageParts.Muted("Nichts gefunden.", 13);
            none.Margin = new Thickness(10, 8, 10, 0);
            _list.Children.Add(none);
            return;
        }
        _scroll.ScrollToTop();
        AddChunk();
    }

    private void AddChunk()
    {
        var end = Math.Min(_shown + Chunk, _rows.Count);
        for (; _shown < end; _shown++) _list.Children.Add(_rows[_shown]());
    }

    private UIElement Row(string selectKey, ImageSource? image, bool round, string sub, string title, Action open, string? albumKey, bool accentTile = false, string symbol = NoteGlyph)
    {
        var thumb = PageParts.Thumb(image, 40, symbol);
        thumb.CornerRadius = new CornerRadius(round ? 20 : 4);
        if (accentTile)
        {
            thumb.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
            if (thumb.Child is TextBlock glyph) glyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        }
        var name = PageParts.Semi(title, 13);
        name.SetResourceReference(TextBlock.ForegroundProperty, albumKey is not null && albumKey == _playingAlbum ? "AccentBrush" : "InkBrush");
        var meta = PageParts.Muted(sub, 11);
        meta.TextWrapping = TextWrapping.NoWrap;
        meta.TextTrimming = TextTrimming.CharacterEllipsis;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { name, meta } };
        Grid.SetColumn(text, 1);
        grid.Children.Add(thumb);
        grid.Children.Add(text);

        var selected = new Border { CornerRadius = new CornerRadius(8), Visibility = selectKey == _selectedKey ? Visibility.Visible : Visibility.Collapsed };
        selected.SetResourceReference(Border.BackgroundProperty, "CardBgBrush");
        _selection[selectKey] = selected;
        if (albumKey is not null) _albumNames[albumKey] = name;

        var row = PageParts.ClickableRow(grid, open, new Thickness(0), title);
        row.Padding = new Thickness(8, 4, 8, 4);
        row.CornerRadius = new CornerRadius(8);
        return new Grid { Children = { selected, row } };
    }
}
