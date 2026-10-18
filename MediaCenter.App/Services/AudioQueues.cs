using MediaCenter.Core;

namespace MediaCenter.App.Services;

/// <summary>Baut Warteschlangen für den <see cref="AudioController"/> aus Bibliotheksdaten.</summary>
public static class AudioQueues
{
    /// <summary>Nur erreichbare Titel kommen in die Warteschlange; fehlende (Laufwerk abgesteckt) werden übersprungen.</summary>
    public static List<QueueItem> ForTracks(LibraryModel model, IEnumerable<TrackRow> tracks)
    {
        var items = new List<QueueItem>();
        foreach (var track in tracks)
        {
            if (model.PathOf(track) is not { } path) continue;
            var artist = string.IsNullOrWhiteSpace(track.Artist) ? MusicIndex.ArtistOf(track) : track.Artist!;
            var albumKey = MusicIndex.AlbumKey(MusicIndex.ArtistOf(track), track.Album ?? MusicIndex.UnknownAlbum);
            items.Add(new QueueItem(QueueKind.Track, track.Title, $"{artist} · {track.Album ?? MusicIndex.UnknownAlbum}", path,
                model.AlbumArtPath(albumKey), track.TrackId, track.DurationSeconds));
        }
        return items;
    }

    public static QueueItem ForStation(RadioStation station, string? coverPath)
    {
        var details = string.Join(" · ", new[] { station.Country, station.Codec, station.Bitrate is > 0 ? $"{station.Bitrate} kbit/s" : null }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        return new QueueItem(QueueKind.Radio, station.Name, details, station.Url, coverPath);
    }

    public static QueueItem ForEpisode(PodcastInfo podcast, EpisodeInfo episode, string? coverPath) =>
        new(QueueKind.Podcast, episode.Title, podcast.Title, episode.AudioUrl, coverPath, episode.Id, episode.DurationSeconds);

    /// <summary>Spielt Titel ab dem gewählten; auf Wunsch gemischt.</summary>
    public static async Task PlayTracksAsync(IShell shell, IReadOnlyList<TrackRow> tracks, int startIndex = 0, bool shuffle = false)
    {
        var model = shell.Services.Model;
        var items = ForTracks(model, tracks);
        if (items.Count == 0)
        {
            shell.ShowMessage("Keiner dieser Titel ist gerade erreichbar (Laufwerk abgesteckt oder Datei fehlt).", isError: true);
            return;
        }

        // Der Startindex bezieht sich auf die ungefilterte Liste; nach dem Herausfiltern unerreichbarer Titel neu bestimmen.
        var start = 0;
        if (!shuffle && startIndex > 0 && startIndex < tracks.Count)
        {
            var wanted = model.PathOf(tracks[startIndex]);
            start = Math.Max(0, items.FindIndex(i => i.Source == wanted));
        }
        if (shuffle) items = items.OrderBy(_ => Random.Shared.Next()).ToList();

        await shell.RunBusyAsync("Wiedergabe startet …", async () =>
        {
            await shell.Services.Audio.PlayAsync(items, start);
            RememberAlbum(shell.Services.Settings, tracks[shuffle ? 0 : Math.Clamp(startIndex, 0, tracks.Count - 1)]);
        });
    }

    private const int MaxRecentAlbums = 24;

    /// <summary>Merkt das Album des gespielten Titels als zuletzt gehört (ohne Dopplung, die Liste bleibt kurz).</summary>
    private static void RememberAlbum(AppSettings settings, TrackRow track)
    {
        var key = MusicIndex.AlbumKey(MusicIndex.ArtistOf(track), track.Album ?? MusicIndex.UnknownAlbum);
        settings.RecentAlbums.RemoveAll(k => k == key);
        settings.RecentAlbums.Insert(0, key);
        if (settings.RecentAlbums.Count > MaxRecentAlbums) settings.RecentAlbums.RemoveRange(MaxRecentAlbums, settings.RecentAlbums.Count - MaxRecentAlbums);
        settings.Save();
    }
}
