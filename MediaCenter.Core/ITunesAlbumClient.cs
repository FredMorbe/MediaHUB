using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaCenter.Core;

/// <summary>Findet Album-Cover im Internet. Gibt null zurück, wenn es keinen sicheren Treffer gibt (nie ein geratenes Bild).</summary>
public interface IAlbumCoverSource
{
    /// <summary>Kennung des Anbieters; unter ihr merkt sich die Datenbank ergebnislose Suchen.</summary>
    string Provider { get; }

    Task<OnlinePoster?> FindAsync(string artist, string album, CancellationToken ct = default);
}

/// <summary>
/// Album-Cover aus der iTunes Search API (itunes.apple.com/search, ohne Schlüssel). Ein Treffer muss Künstler und Albumtitel genau
/// treffen (Groß-/Kleinschreibung, Satzzeichen, Akzente egal; Zusätze wie „(Remastered)“ oder „- EP“ beim Anbieter zählen nicht).
/// </summary>
public sealed class ITunesAlbumClient(HttpClient http, TimeSpan? minGap = null) : IAlbumCoverSource
{
    private const long MaxJsonBytes = 2_000_000;

    /// <summary>Apple nennt etwa 20 Abrufe pro Minute; mit Abstand bleiben wir darunter.</summary>
    private static readonly TimeSpan DefaultGap = TimeSpan.FromSeconds(3.2);

    private static readonly Regex Suffix = new(@"\s*([\(\[][^\)\]]*[\)\]]|-\s*(ep|single)\s*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastCall = DateTime.MinValue;

    public string Provider => "itunes";

    public async Task<OnlinePoster?> FindAsync(string artist, string album, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist)) throw new ArgumentException("Künstler fehlt.", nameof(artist));
        if (string.IsNullOrWhiteSpace(album)) throw new ArgumentException("Albumtitel fehlt.", nameof(album));

        var wantedArtist = PosterNames.MatchKey(artist);
        var wantedAlbums = Keys(album);
        if (wantedArtist.Length == 0 || wantedAlbums.Count == 0) return null;

        using var json = await GetJsonAsync(
            $"https://itunes.apple.com/search?media=music&entity=album&limit=10&term={Uri.EscapeDataString($"{artist.Trim()} {album.Trim()}")}", ct);
        if (!json.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return null;

        foreach (var item in results.EnumerateArray())
        {
            var name = String(item, "collectionName");
            var by = String(item, "artistName");
            if (name is null || by is null || PosterNames.MatchKey(by) != wantedArtist) continue;
            if (!Keys(name).Overlaps(wantedAlbums)) continue;
            if (String(item, "artworkUrl100") is not { } small || !Uri.TryCreate(small, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) continue;

            // Die API liefert 100 px; dieselbe Adresse mit größerer Kantenlänge gibt das große Bild.
            return new OnlinePoster(name, small.Replace("100x100bb", "600x600bb", StringComparison.Ordinal));
        }
        return null;
    }

    /// <summary>Vergleichsschlüssel eines Albumtitels: mit und ohne angehängten Zusatz in Klammern oder „- EP“.</summary>
    private static HashSet<string> Keys(string title)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var full = PosterNames.MatchKey(title);
        if (full.Length > 0) keys.Add(full);
        var stripped = PosterNames.MatchKey(Suffix.Replace(title.Trim(), ""));
        if (stripped.Length > 0) keys.Add(stripped);
        return keys;
    }

    private static string? String(JsonElement item, string name) =>
        item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var wait = _lastCall + (minGap ?? DefaultGap) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);

            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            _lastCall = DateTime.UtcNow;
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"itunes.apple.com antwortete mit {(int)response.StatusCode} {response.ReasonPhrase}.");
            }
            return JsonDocument.Parse(await HttpFetch.ReadLimitedAsync(response, new Uri(url), MaxJsonBytes, ct));
        }
        finally
        {
            _gate.Release();
        }
    }
}
