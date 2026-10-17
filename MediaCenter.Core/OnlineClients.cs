using System.Globalization;
using System.Text.Json;

namespace MediaCenter.Core;

/// <summary>Holt und liest Podcast-Feeds.</summary>
public sealed class PodcastFeedClient(HttpClient http)
{
    private const long MaxFeedBytes = 20_000_000;

    public async Task<PodcastFeed> FetchAsync(string feedUrl, CancellationToken ct = default)
    {
        var uri = HttpFetch.RequireWebUri(feedUrl);
        var xml = await HttpFetch.GetStringAsync(http, uri, MaxFeedBytes, ct);
        // Große Feeds (mehrere MB) werden nicht auf dem UI-Thread geparst.
        return await Task.Run(() => PodcastFeedParser.Parse(xml), ct);
    }
}

/// <summary>Podcast-Suche über die kostenlose iTunes Search API (ohne Schlüssel).</summary>
public sealed class PodcastSearchClient(HttpClient http)
{
    public async Task<IReadOnlyList<PodcastSearchResult>> SearchAsync(string query, int limit = 25, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Suchbegriff fehlt.", nameof(query));

        var uri = new Uri($"https://itunes.apple.com/search?media=podcast&limit={limit}&term={Uri.EscapeDataString(query.Trim())}");
        using var doc = JsonDocument.Parse(await HttpFetch.GetStringAsync(http, uri, 2_000_000, ct));
        var results = new List<PodcastSearchResult>();
        if (!doc.RootElement.TryGetProperty("results", out var array)) return results;

        foreach (var item in array.EnumerateArray())
        {
            var feed = Str(item, "feedUrl");
            var title = Str(item, "collectionName") ?? Str(item, "trackName");
            if (feed is null || title is null || !Uri.TryCreate(feed, UriKind.Absolute, out var feedUri) || feedUri.Scheme is not ("http" or "https")) continue;
            results.Add(new PodcastSearchResult(title, Str(item, "artistName"), feed, Str(item, "artworkUrl600") ?? Str(item, "artworkUrl100")));
        }
        return results;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Sendersuche im offenen Verzeichnis radio-browser.info.</summary>
public sealed class RadioBrowserClient(HttpClient http)
{
    /// <summary>Gleichwertige Server; der erste erreichbare antwortet.</summary>
    private static readonly string[] Servers =
    {
        "https://all.api.radio-browser.info", "https://de1.api.radio-browser.info", "https://de2.api.radio-browser.info",
    };

    public async Task<IReadOnlyList<RadioStation>> SearchAsync(string? name, string? tag = null, int limit = 40, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(tag)) throw new ArgumentException("Name oder Genre fehlt.");

        var query = $"hidebroken=true&order=votes&reverse=true&limit={limit}";
        if (!string.IsNullOrWhiteSpace(name)) query += $"&name={Uri.EscapeDataString(name.Trim())}";
        if (!string.IsNullOrWhiteSpace(tag)) query += $"&tag={Uri.EscapeDataString(tag.Trim().ToLowerInvariant())}"; // Tags im Verzeichnis sind kleingeschrieben und werden exakt verglichen

        var errors = new List<string>();
        foreach (var server in Servers)
        {
            try
            {
                var json = await HttpFetch.GetStringAsync(http, new Uri($"{server}/json/stations/search?{query}"), 5_000_000, ct);
                return Parse(json);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException && !ct.IsCancellationRequested)
            {
                errors.Add($"{new Uri(server).Host}: {ex.Message}");
            }
        }
        throw new HttpRequestException("Kein radio-browser-Server war erreichbar. " + string.Join(" | ", errors));
    }

    public static IReadOnlyList<RadioStation> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var stations = new List<RadioStation>();
        foreach (var s in doc.RootElement.EnumerateArray())
        {
            var url = Str(s, "url_resolved") ?? Str(s, "url");
            var name = Str(s, "name");
            if (url is null || name is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;

            var favicon = Str(s, "favicon");
            if (favicon is not null && !(Uri.TryCreate(favicon, UriKind.Absolute, out var iconUri) && iconUri.Scheme is "http" or "https")) favicon = null;

            stations.Add(new RadioStation(
                Str(s, "stationuuid"), name.Trim(), url, favicon, Str(s, "country"), Str(s, "tags"), Str(s, "codec"),
                s.TryGetProperty("bitrate", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : null,
                s.TryGetProperty("votes", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0));
        }
        return stations;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;
}
