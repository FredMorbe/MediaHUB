namespace MediaCenter.Core;

/// <summary>Lädt Bilder aus dem Netz in den Cover-Cache (verkleinert, als JPEG).</summary>
public sealed class RemoteImages(HttpClient http, IImageTool images, string cacheDir)
{
    private const long MaxImageBytes = 8_000_000;

    public string CacheDir => cacheDir;

    /// <returns>Der Dateiname im Cache.</returns>
    public async Task<string> DownloadAsync(string url, string fileName, int maxWidth, int maxHeight, CancellationToken ct = default)
    {
        var uri = HttpFetch.RequireWebUri(url);
        var bytes = await HttpFetch.GetBytesAsync(http, uri, MaxImageBytes, ct);

        Directory.CreateDirectory(cacheDir);
        var temp = Path.Combine(cacheDir, $"{Guid.NewGuid():N}.download");
        await File.WriteAllBytesAsync(temp, bytes, ct);
        try
        {
            // ffmpeg erkennt das Bildformat am Inhalt und liefert ein JPEG in der Zielgröße.
            await images.ScaleImageAsync(temp, Path.Combine(cacheDir, fileName), maxWidth, maxHeight, ct);
        }
        finally
        {
            File.Delete(temp);
        }
        return fileName;
    }
}

/// <param name="Warning">Das Abo ist angelegt, aber etwas Nebensächliches (Bild) ist fehlgeschlagen.</param>
public sealed record SubscribeResult(long PodcastId, int Episodes, string? Warning);

public sealed record PodcastRefresh(PodcastInfo Podcast, int NewEpisodes, string? Error);

/// <summary>Podcast-Abos anlegen und aktualisieren.</summary>
public sealed class PodcastService(LibraryDb db, PodcastFeedClient feeds, RemoteImages images)
{
    public async Task<SubscribeResult> SubscribeAsync(string feedUrl, CancellationToken ct = default)
    {
        var url = feedUrl.Trim();
        var feed = await feeds.FetchAsync(url, ct);
        var id = db.SavePodcast(url, feed);
        var warning = await TryUpdateImageAsync(id, feed.ImageUrl, ct);
        return new SubscribeResult(id, feed.Episodes.Count, warning);
    }

    /// <summary>Holt den Feed neu; ein Fehler bei einem Podcast betrifft die anderen nicht.</summary>
    public async Task<IReadOnlyList<PodcastRefresh>> RefreshAllAsync(CancellationToken ct = default)
    {
        var results = new List<PodcastRefresh>();
        foreach (var podcast in db.ListPodcasts())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var feed = await feeds.FetchAsync(podcast.FeedUrl, ct);
                var before = db.ListEpisodes(podcast.Id).Count;
                db.SavePodcast(podcast.FeedUrl, feed);
                var added = db.ListEpisodes(podcast.Id).Count - before;
                var warning = await TryUpdateImageAsync(podcast.Id, feed.ImageUrl, ct, podcast);
                results.Add(new PodcastRefresh(podcast, added, warning));
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or TaskCanceledException or ArgumentException && !ct.IsCancellationRequested)
            {
                results.Add(new PodcastRefresh(podcast, 0, ex.Message));
            }
        }
        return results;
    }

    private async Task<string?> TryUpdateImageAsync(long id, string? imageUrl, CancellationToken ct, PodcastInfo? previous = null)
    {
        if (imageUrl is null) return null;
        var fileName = $"podcast-{id}.jpg";
        var hasFile = previous?.ImageFile is { } existing && File.Exists(Path.Combine(images.CacheDir, existing));
        if (hasFile && previous!.ImageUrl == imageUrl) return null;

        try
        {
            db.SetPodcastImage(id, await images.DownloadAsync(imageUrl, fileName, 600, 600, ct));
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or InvalidOperationException or TaskCanceledException or ArgumentException && !ct.IsCancellationRequested)
        {
            return $"Bild konnte nicht geladen werden: {ex.Message}";
        }
    }
}

/// <summary>Sender speichern, samt Logo.</summary>
public sealed class RadioService(LibraryDb db, RemoteImages images)
{
    /// <returns>Die Id und eine Warnung, falls das Logo nicht geladen werden konnte.</returns>
    public async Task<(long Id, string? Warning)> SaveAsync(RadioStation station, CancellationToken ct = default)
    {
        var id = db.SaveStation(station);
        if (station.FaviconUrl is null) return (id, null);

        try
        {
            db.SetStationFavicon(id, await images.DownloadAsync(station.FaviconUrl, $"radio-{id}.jpg", 256, 256, ct));
            return (id, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or InvalidOperationException or TaskCanceledException or ArgumentException && !ct.IsCancellationRequested)
        {
            return (id, $"Logo konnte nicht geladen werden: {ex.Message}");
        }
    }
}
