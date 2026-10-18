using System.Windows;
using MediaCenter.App.Controls;
using MediaCenter.Core;

namespace MediaCenter.App.Services;

/// <summary>Playlists anlegen und füllen, mit Rückmeldung an den Nutzer.</summary>
public static class PlaylistActions
{
    public const string DefaultName = "Neue Playlist";

    /// <summary>„Neue Playlist“, bei Bedarf mit Zähler („Neue Playlist 2“), damit der Name frei ist.</summary>
    public static string NextName(LibraryModel model)
    {
        var taken = model.Playlists.Select(p => p.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!taken.Contains(DefaultName)) return DefaultName;
        for (var n = 2; ; n++)
        {
            var name = $"{DefaultName} {n}";
            if (!taken.Contains(name)) return name;
        }
    }

    /// <summary>Legt eine Playlist mit freiem Namen an, auf Wunsch gleich mit Titeln, und liest die Playlists neu.</summary>
    public static PlaylistInfo Create(IShell shell, IReadOnlyList<TrackRow> tracks)
    {
        var model = shell.Services.Model;
        var id = model.Db.CreatePlaylist(NextName(model));
        if (tracks.Count > 0) model.Db.AddTracksToPlaylist(id, tracks.Select(t => t.TrackId));
        model.ReloadPlaylists();
        return model.Playlists.First(p => p.Id == id);
    }

    /// <summary>Hängt Titel an die Playlist und sagt, was daraus wurde (auch, wenn alles schon drin war).</summary>
    public static void Add(IShell shell, PlaylistInfo playlist, IReadOnlyList<TrackRow> tracks)
    {
        var model = shell.Services.Model;
        var added = model.Db.AddTracksToPlaylist(playlist.Id, tracks.Select(t => t.TrackId));
        model.ReloadPlaylists();

        var skipped = tracks.Select(t => t.TrackId).Distinct().Count() - added;
        shell.ShowMessage(added switch
        {
            0 when tracks.Count == 1 => $"„{tracks[0].Title}“ ist schon in „{playlist.Name}“.",
            0 => $"Alle Titel sind schon in „{playlist.Name}“.",
            1 when tracks.Count == 1 => $"„{tracks[0].Title}“ zu „{playlist.Name}“ hinzugefügt.",
            _ => $"{added} Titel zu „{playlist.Name}“ hinzugefügt." + (skipped > 0 ? $" {skipped} waren schon drin." : ""),
        });
    }

    /// <summary>
    /// Zeigt die Auswahl „Neue Playlist anlegen“ und die vorhandenen Playlists (außer <paramref name="exceptPlaylistId"/>) und fügt die Titel dort ein.
    /// </summary>
    public static void ShowAddMenu(IShell shell, FrameworkElement owner, IReadOnlyList<TrackRow> tracks, long? exceptPlaylistId = null)
    {
        var items = new List<(string Text, Action Click)>
        {
            ("Neue Playlist anlegen", () =>
            {
                var playlist = Create(shell, tracks);
                shell.ShowMessage($"Playlist „{playlist.Name}“ mit {tracks.Count} Titel{(tracks.Count == 1 ? "" : "n")} angelegt.");
            }),
        };
        foreach (var playlist in shell.Services.Model.Playlists.Where(p => p.Id != exceptPlaylistId))
        {
            var target = playlist;
            items.Add(($"Zu „{target.Name}“", () => Add(shell, target, tracks)));
        }
        ContextPopup.Show(owner, items, "Zu Playlist hinzufügen");
    }
}
