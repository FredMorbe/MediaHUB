using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App.Pages;

/// <summary>
/// Eine Playlist im Mittelpanel der Musik-Seite: Cover, Name (zum Umbenennen anklicken), Abspielen, Zufall, Löschen und die Titeltabelle.
/// Ein Rechtsklick auf einen Titel verschiebt oder entfernt ihn. Bildlauf und Zurück liefert die Musik-Seite.
/// </summary>
public sealed class PlaylistPage : UserControl
{
    private const string PlayGlyph = "\uE768";
    private const string ShuffleGlyph = "\uE8B1";
    private const string RenameGlyph = "\uE70F";
    private const string DeleteGlyph = "\uE74D";
    private const string NoteGlyph = "\uE8D6";

    private static readonly GridLength[] Columns =
    {
        new(40), new(3, GridUnitType.Star), new(2, GridUnitType.Star), new(2, GridUnitType.Star), new(56),
    };

    private readonly IShell _shell;
    private readonly LibraryModel _model;
    private readonly long _id;
    private readonly Action<long> _onDeleted;
    private readonly StackPanel _list = new();
    private readonly Border _coverHost = new() { VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _name;
    private readonly TextBox _nameBox;
    private readonly TextBlock _meta;
    private readonly Grid _nameHost = new();
    private bool _renaming;
    private long? _playingId;

    /// <param name="onDeleted">Wird nach dem Löschen aufgerufen; die Musik-Seite verlässt dann die Ansicht.</param>
    /// <param name="startRenaming">Öffnet gleich das Namensfeld (für eine eben angelegte Playlist).</param>
    public PlaylistPage(IShell shell, long playlistId, Action<long> onDeleted, bool startRenaming = false)
    {
        _shell = shell;
        _model = shell.Services.Model;
        _id = playlistId;
        _onDeleted = onDeleted;
        _ = Current; // wirft, wenn es die Playlist nicht (mehr) gibt

        var kicker = new TextBlock { Text = "PLAYLIST", Margin = new Thickness(0, 0, 0, 4) };
        kicker.SetResourceReference(StyleProperty, "KickerText");

        _name = PageParts.Heading("", 44);
        _name.TextWrapping = TextWrapping.Wrap;
        _name.LineHeight = 42;
        _name.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        _name.Cursor = Cursors.IBeam;
        _name.ToolTip = "Zum Umbenennen anklicken";
        _name.MouseLeftButtonUp += (_, _) => StartRename();

        _nameBox = new TextBox { FontSize = 28, MaxLength = LibraryDb.MaxPlaylistNameLength, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 4) };
        System.Windows.Automation.AutomationProperties.SetName(_nameBox, "Name der Playlist");
        _nameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitRename();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelRename();
                e.Handled = true;
            }
        };
        _nameBox.LostKeyboardFocus += (_, _) => CommitRename();
        _nameHost.Children.Add(_name);
        _nameHost.Children.Add(_nameBox);

        _meta = PageParts.Muted("", 13);
        _meta.Margin = new Thickness(0, 4, 0, 0);

        var info = new StackPanel { Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom, Children = { kicker, _nameHost, _meta } };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(info, 1);
        header.Children.Add(_coverHost);
        header.Children.Add(info);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        var play = new Button { Content = PlayGlyph, Width = 44, Height = 44, FontSize = 18, ToolTip = "Abspielen" };
        play.SetResourceReference(StyleProperty, "AccentIconButton");
        System.Windows.Automation.AutomationProperties.SetName(play, "Playlist abspielen");
        play.Click += async (_, _) => await AudioQueues.PlayTracksAsync(_shell, Current.Tracks, 0);
        buttons.Children.Add(play);
        buttons.Children.Add(IconButton(ShuffleGlyph, "Zufällig", "Playlist zufällig abspielen", async () => await AudioQueues.PlayTracksAsync(_shell, Current.Tracks, 0, shuffle: true)));
        buttons.Children.Add(IconButton(RenameGlyph, "Umbenennen", "Playlist umbenennen", StartRename));
        buttons.Children.Add(IconButton(DeleteGlyph, "Playlist löschen", "Playlist löschen", Delete));

        var tableHead = PageParts.TableHeader(Columns, "#", "Titel", "Künstler", "Album", "Dauer");
        ((FrameworkElement)tableHead).Margin = new Thickness(0, 16, 0, 4);
        Content = new StackPanel { Children = { header, buttons, tableHead, _list } };

        _playingId = _shell.Services.Audio.Current is { Kind: QueueKind.Track, Id: { } playing } ? playing : null;
        Refresh();
        // Die Markierung des laufenden Titels folgt der Wiedergabe.
        Loaded += (_, _) => _shell.Services.Audio.Changed += OnAudioChanged;
        Unloaded += (_, _) => _shell.Services.Audio.Changed -= OnAudioChanged;
        if (startRenaming) Loaded += (_, _) => StartRename();
    }

    private PlaylistInfo Current =>
        _model.Playlists.FirstOrDefault(p => p.Id == _id) ?? throw new InvalidOperationException($"Playlist {_id} existiert nicht (mehr).");

    private Button IconButton(string glyph, string tip, string automationName, Action click)
    {
        var button = new Button { Content = glyph, Margin = new Thickness(8, 0, 0, 0), ToolTip = tip };
        button.SetResourceReference(StyleProperty, "IconButton");
        System.Windows.Automation.AutomationProperties.SetName(button, automationName);
        button.Click += (_, _) => click();
        return button;
    }

    private void OnAudioChanged()
    {
        var now = _shell.Services.Audio.Current is { Kind: QueueKind.Track, Id: { } id } ? id : (long?)null;
        if (now == _playingId) return;
        _playingId = now;
        BuildTracks();
    }

    /// <summary>Zeigt Name, Angaben, Cover und Titel der Playlist aus dem aktuellen Stand der Bibliothek.</summary>
    private void Refresh()
    {
        var playlist = Current;
        _name.Text = playlist.Name;
        var total = playlist.Tracks.Sum(t => t.DurationSeconds ?? 0);
        _meta.Text = string.Join(" · ", new[] { $"{playlist.Tracks.Count} Titel", PageParts.FormatDuration(total) }.Where(s => s.Length > 0));

        var cover = PageParts.Thumb(ImageCache.Load(_model.PlaylistCover(playlist), 360), 168, NoteGlyph);
        cover.CornerRadius = new CornerRadius(6);
        _coverHost.Child = cover;
        BuildTracks();
    }

    private void BuildTracks()
    {
        _list.Children.Clear();
        var tracks = Current.Tracks;
        if (tracks.Count == 0)
        {
            var hint = PageParts.Muted("Diese Playlist ist leer. Titel kommen per Rechtsklick auf einen Titel („Zu Playlist hinzufügen“) oder über das Plus-Symbol eines Albums hinein.", 13);
            hint.Margin = new Thickness(8, 14, 0, 0);
            hint.MaxWidth = 560;
            hint.HorizontalAlignment = HorizontalAlignment.Left;
            _list.Children.Add(hint);
            return;
        }

        foreach (var (track, index) in tracks.Select((t, i) => (t, i)))
        {
            var playing = track.TrackId == _playingId;
            var title = PageParts.Semi(track.Title, 14);
            title.SetResourceReference(TextBlock.ForegroundProperty, playing ? "AccentBrush" : "InkBrush");
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
            row = PageParts.TableRow(Columns, cells, async () => await AudioQueues.PlayTracksAsync(_shell, Current.Tracks, captured), track.Title,
                dimmed: _model.PathOf(track) is null, menu: () => TrackMenu(track, captured, tracks.Count, row!));
            _list.Children.Add(row);
        }
    }

    private IReadOnlyList<(string Text, Action Click)> TrackMenu(TrackRow track, int index, int count, FrameworkElement owner)
    {
        var menu = new List<(string Text, Action Click)>
        {
            ("Abspielen", async () => await AudioQueues.PlayTracksAsync(_shell, Current.Tracks, index)),
        };
        if (index > 0) menu.Add(("An den Anfang", () => Move(track, 0)));
        if (index > 0) menu.Add(("Nach oben", () => Move(track, index - 1)));
        if (index < count - 1) menu.Add(("Nach unten", () => Move(track, index + 1)));
        menu.Add(("Zu anderer Playlist hinzufügen …", () => PlaylistActions.ShowAddMenu(_shell, owner, new[] { track }, _id)));
        menu.Add(("Song bearbeiten …", () => MusicRenamePage.ShowTrack(_shell, track.TrackId)));
        menu.Add(("Aus Playlist entfernen", () =>
        {
            _model.Db.RemoveTrackFromPlaylist(_id, track.TrackId);
            _model.ReloadPlaylists();
            Refresh();
        }));
        return menu;
    }

    private void Move(TrackRow track, int newIndex)
    {
        _model.Db.MoveTrackInPlaylist(_id, track.TrackId, newIndex);
        _model.ReloadPlaylists();
        BuildTracks();
    }

    // --- Umbenennen und Löschen ------------------------------------------------------------------------------

    private void StartRename()
    {
        if (_renaming) return;
        _renaming = true;
        _nameBox.Text = Current.Name;
        _name.Visibility = Visibility.Collapsed;
        _nameBox.Visibility = Visibility.Visible;
        _nameBox.Focus();
        _nameBox.SelectAll();
    }

    private void EndRename()
    {
        _renaming = false;
        _nameBox.Visibility = Visibility.Collapsed;
        _name.Visibility = Visibility.Visible;
    }

    private void CancelRename()
    {
        if (!_renaming) return;
        EndRename();
        Keyboard.ClearFocus();
    }

    private void CommitRename()
    {
        if (!_renaming) return;
        var name = _nameBox.Text.Trim();
        EndRename();
        if (name == Current.Name) return;
        try
        {
            _model.Db.RenamePlaylist(_id, name);
        }
        catch (ArgumentException ex)
        {
            // Name leer, zu lang oder vergeben: dem Nutzer sagen, der alte Name bleibt.
            _shell.ShowMessage(ex.Message, isError: true);
            return;
        }
        _model.ReloadPlaylists();
        Refresh();
    }

    private void Delete()
    {
        var playlist = Current;
        var count = playlist.Tracks.Count;
        var question = $"Die Playlist „{playlist.Name}“" + (count > 0 ? $" mit {count} Titel{(count == 1 ? "" : "n")}" : "") + " löschen?\n\n" +
                       "• Die Titel bleiben in der Bibliothek und auf der Festplatte.\n" +
                       "• Nur die Playlist selbst geht verloren.";
        if (!_shell.Confirm("Playlist löschen?", question, "Löschen", danger: true)) return;
        _model.Db.DeletePlaylist(_id);
        _model.ReloadPlaylists();
        _shell.ShowMessage($"Playlist „{playlist.Name}“ gelöscht.");
        _onDeleted(_id);
    }
}
