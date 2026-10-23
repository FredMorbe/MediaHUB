using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using MediaCenter.App.Controls;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App.Pages;

/// <summary>
/// „Bearbeiten“ für einen Song oder ein Album: Name und Künstler. Ändert nur, was die Bibliothek anzeigt: die Dateien und ihre Tags bleiben,
/// die eigenen Angaben überstehen einen neuen Scan. Beim Album sind es auch die Angaben, nach denen die Online-Suche das Cover sucht.
/// Erscheint als Fenster über der Seite.
/// </summary>
public sealed class MusicRenamePage : UserControl, IModalContent
{
    private readonly IShell _shell;
    private readonly long? _trackId;
    private readonly string? _albumKey;
    private readonly string _currentName;
    private readonly string _currentArtist;
    private bool _busy;

    public string? BlockClose => _busy ? "Es wird gerade gespeichert. Bitte einen Moment warten." : null;

    /// <summary>Öffnet das Fenster zum Bearbeiten eines Songs.</summary>
    public static void ShowTrack(IShell shell, long trackId)
    {
        var track = shell.Services.Model.Tracks.FirstOrDefault(t => t.TrackId == trackId) ?? throw new InvalidOperationException($"Titel {trackId} existiert nicht (mehr).");
        shell.OpenModal("Song bearbeiten", track.Title, new MusicRenamePage(shell, track, null), ModalSize.Medium);
    }

    /// <summary>Öffnet das Fenster zum Bearbeiten eines Albums.</summary>
    public static void ShowAlbum(IShell shell, string albumKey)
    {
        var album = shell.Services.Model.Albums.FirstOrDefault(a => a.Key == albumKey) ?? throw new InvalidOperationException($"Album '{albumKey}' existiert nicht (mehr).");
        shell.OpenModal("Album bearbeiten", album.Title, new MusicRenamePage(shell, null, album), ModalSize.Medium);
    }

    private MusicRenamePage(IShell shell, TrackRow? track, AlbumInfo? album)
    {
        _shell = shell;
        _trackId = track?.TrackId;
        _albumKey = album?.Key;

        var isTrack = track is not null;
        _currentName = isTrack ? track!.Title : album!.Title;
        _currentArtist = isTrack ? track!.Artist ?? "" : album!.Artist == MusicIndex.UnknownArtist ? "" : album.Artist;

        // Angaben aus den Tags, solange eigene gelten.
        string? tagName, tagArtist;
        if (isTrack)
        {
            tagName = track!.TagTitle;
            tagArtist = track.TagArtist;
        }
        else
        {
            tagName = album!.Tracks.Select(t => t.TagAlbum).FirstOrDefault(a => a is not null);
            tagArtist = album.Tracks.Select(t => t.TagAlbumArtist).FirstOrDefault(a => a is not null);
        }

        var hint = PageParts.Muted(isTrack
            ? "Ändert nur, was die Bibliothek anzeigt: die Datei und ihre Tags bleiben. Die Angaben gelten auch nach einem neuen Scan. " +
              "Der Künstler des Songs ändert nichts an seinem Album; leer lassen heißt: der Künstler aus den Tags."
            : "Ändert nur, was die Bibliothek anzeigt: die Dateien und ihre Tags bleiben. Die Angaben gelten für alle Titel des Albums, auch für später hinzukommende, " +
              "und auch nach einem neuen Scan. Ein geänderter Künstler gilt für das Album und für alle seine Titel; leer lassen heißt: der Künstler aus den Tags. " +
              "Heißt das Album danach wie ein anderes desselben Künstlers, werden beide zu einem. Nach Name und Künstler sucht auch die Online-Suche nach Covern.");

        var nameLabel = isTrack ? "Name des Songs" : "Name des Albums";
        var artistLabel = isTrack ? "Künstler des Songs" : "Künstler des Albums";
        var (nameField, nameBox) = PageParts.HintBox(nameLabel, double.NaN);
        nameBox.Text = _currentName;
        AutomationProperties.SetName(nameBox, nameLabel);
        var (artistField, artistBox) = PageParts.HintBox(artistLabel, double.NaN);
        artistBox.Text = _currentArtist;
        AutomationProperties.SetName(artistBox, artistLabel);

        async Task Save() => await SaveAsync(nameBox.Text, artistBox.Text);
        foreach (var box in new[] { nameBox, artistBox })
        {
            box.KeyDown += async (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                await Save();
            };
        }
        var save = PageParts.Button("Speichern", "PrimaryButton", async (_, _) => await Save(), new Thickness(0, 18, 0, 0));
        AutomationProperties.SetName(save, "Speichern");
        save.HorizontalAlignment = HorizontalAlignment.Left;

        var stack = new StackPanel { Children = { hint, Label("Name"), nameField, Label("Künstler"), artistField, save } };
        if (tagName is not null || tagArtist is not null)
        {
            var original = string.Join(" · ", new[] { tagName, tagArtist is { Length: > 0 } ? tagArtist : null }.Where(s => s is not null));
            var reset = PageParts.Button("Angaben aus den Tags wiederherstellen", "GhostButton", (_, _) => Reset(), new Thickness(0, 22, 0, 0));
            AutomationProperties.SetName(reset, "Angaben aus den Tags wiederherstellen");
            var note = PageParts.Muted($"Aus den Tags: {original}", 13);
            note.Margin = new Thickness(0, 10, 0, 0);
            reset.Margin = new Thickness(0, 6, 0, 0);
            reset.HorizontalAlignment = HorizontalAlignment.Left;
            stack.Children.Add(note);
            stack.Children.Add(reset);
        }
        Content = stack;
    }

    private static TextBlock Label(string text)
    {
        var label = PageParts.Muted(text, 12);
        label.Margin = new Thickness(0, 14, 0, 4);
        return label;
    }

    private LibraryModel Model => _shell.Services.Model;

    private TrackRow FindTrack() =>
        Model.Tracks.FirstOrDefault(t => t.TrackId == _trackId) ?? throw new InvalidOperationException($"Titel {_trackId} existiert nicht (mehr).");

    private AlbumInfo FindAlbum() =>
        Model.Albums.FirstOrDefault(a => a.Key == _albumKey) ?? throw new InvalidOperationException($"Album '{_albumKey}' existiert nicht (mehr).");

    private async Task SaveAsync(string nameText, string artistText)
    {
        if (_busy) return;
        var name = nameText.Trim();
        var artist = artistText.Trim();
        if (name.Length == 0)
        {
            _shell.ShowMessage("Der Name ist leer.", isError: true);
            return;
        }
        if (name == _currentName && artist == _currentArtist)
        {
            _shell.ShowMessage("Name und Künstler sind schon so.");
            return;
        }

        _busy = true;
        try
        {
            if (_trackId is not null)
            {
                Model.Db.RenameTrack(FindTrack(), name, artist);
                Model.ReloadMusic();
                _shell.CloseModal();
                _shell.ShowMessage($"Song geändert: {name}.");
                return;
            }

            await RenameAlbumAsync(FindAlbum(), name, artist);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RenameAlbumAsync(AlbumInfo album, string name, string artist)
    {
        // Nur ein geänderter Künstler wird an alle Titel weitergegeben; blieb er stehen, behalten die Titel ihre eigenen (Sampler).
        var newKey = Model.Db.RenameAlbum(album.Tracks, name, artist == _currentArtist ? null : artist);
        FinishAlbum(album, newKey);
        _shell.CloseModal();
        _shell.ShowMessage($"Album geändert: {name}.");

        // Die Online-Suche merkt sich einen Wechsel von Name oder Künstler: ein Album ohne Cover wird neu gesucht.
        if (Model.AlbumArtPath(newKey) is not null) return;
        var failures = new List<ArtworkFailure>();
        await _shell.RunBusyAsync("Cover wird gesucht …", async () => failures.AddRange((await Model.Artwork.RefreshAlbumsAsync(Model.Volumes)).Failures));
        Model.ReloadMusic();
        if (failures.Count > 0)
        {
            _shell.ShowMessage($"Für {failures.Count} Alben konnte kein Cover erzeugt werden: " +
                string.Join("; ", failures.Take(3).Select(f => $"{f.Subject} ({f.Error})")), isError: true);
        }
    }

    private void Reset()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (_trackId is not null)
            {
                Model.Db.ResetTrack(_trackId.Value);
                Model.ReloadMusic();
                _shell.CloseModal();
                _shell.ShowMessage("Der Song hat wieder die Angaben aus den Tags.");
                return;
            }

            var album = FindAlbum();
            FinishAlbum(album, Model.Db.ResetAlbum(album.Tracks));
            _shell.CloseModal();
            _shell.ShowMessage("Das Album hat wieder die Angaben aus den Tags.");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Das Album hat einen neuen Schlüssel: „Zuletzt gehört“ und die Musik-Seite ziehen mit.</summary>
    private void FinishAlbum(AlbumInfo album, string newKey)
    {
        var recent = _shell.Services.Settings.RecentAlbums;
        if (album.Key != newKey && recent.Contains(album.Key))
        {
            var index = recent.IndexOf(album.Key);
            recent.RemoveAll(k => k == newKey || k == album.Key);
            recent.Insert(Math.Min(index, recent.Count), newKey);
            _shell.Services.Settings.Save();
        }
        Model.ReloadMusic(album.Key, newKey);
    }
}
