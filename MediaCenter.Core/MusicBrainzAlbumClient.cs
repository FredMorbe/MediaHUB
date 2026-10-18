using System.Text.Json;

namespace MediaCenter.Core;

/// <summary>
/// Album-Cover aus MusicBrainz (Suche nach dem Album, musicbrainz.org/ws/2, ohne Schlüssel) und dem Cover Art Archive (das Bild, coverartarchive.org).
/// Wie bei iTunes muss ein Treffer Künstler und Albumtitel genau treffen. Gibt es zum Album keinen Eintrag oder kein Bild, ist das kein Fehler (null).
/// </summary>
/// <param name="contact">Kontakt für den User-Agent (MusicBrainz verlangt, dass ein Programm sich zu erkennen gibt), z. B. die Projektadresse.</param>
/// <param name="minGap">MusicBrainz erlaubt eine Anfrage pro Sekunde; der Abstand gilt für alle Anfragen dieser Quelle.</param>
public sealed class MusicBrainzAlbumClient(HttpClient http, string contact, TimeSpan? minGap = null) : IAlbumCoverSource
{
    private const long MaxJsonBytes = 4_000_000;

    /// <summary>So viele passende Treffer werden höchstens auf ein Bild geprüft (jeder kostet eine Anfrage).</summary>
    private const int MaxCandidates = 3;

    private static readonly TimeSpan DefaultGap = TimeSpan.FromSeconds(1.5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastCall = DateTime.MinValue;

    public string Provider => "musicbrainz";

    public async Task<OnlinePoster?> FindAsync(string artist, string album, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist)) throw new ArgumentException("Künstler fehlt.", nameof(artist));
        if (string.IsNullOrWhiteSpace(album)) throw new ArgumentException("Albumtitel fehlt.", nameof(album));
        if (string.IsNullOrWhiteSpace(contact)) throw new ArgumentException("Kontakt für den User-Agent fehlt.", nameof(contact));

        var wantedArtist = PosterNames.MatchKey(artist);
        var wantedAlbums = AlbumMatching.TitleKeys(album);
        if (wantedArtist.Length == 0 || wantedAlbums.Count == 0) return null;

        var query = $"releasegroup:\"{Escape(album)}\" AND artist:\"{Escape(artist)}\"";
        using var json = await SendAsync(HttpMethod.Get, $"https://musicbrainz.org/ws/2/release-group/?fmt=json&limit=10&query={Uri.EscapeDataString(query)}", ct);
        if (json is null || !json.RootElement.TryGetProperty("release-groups", out var groups) || groups.ValueKind != JsonValueKind.Array) return null;

        var checkedCandidates = 0;
        foreach (var group in groups.EnumerateArray())
        {
            if (String(group, "title") is not { } title || String(group, "id") is not { } id || !Guid.TryParse(id, out var guid)) continue;
            if (!AlbumMatching.TitleKeys(title).Overlaps(wantedAlbums) || !ArtistMatches(group, wantedArtist)) continue;
            if (++checkedCandidates > MaxCandidates) break;

            // Nicht jedes Album hat ein Bild: die Anfrage prüft das, bevor es als Treffer zählt (404 = kein Bild, nächster Kandidat).
            var cover = $"https://coverartarchive.org/release-group/{guid:D}/front-500";
            using var probe = await SendAsync(HttpMethod.Head, cover, ct);
            if (probe is not null) return new OnlinePoster(title, cover);
        }
        return null;
    }

    /// <summary>Der gesuchte Künstler muss einer der genannten Künstler sein (auch zusammengesetzt, „A &amp; B“).</summary>
    private static bool ArtistMatches(JsonElement group, string wantedArtist)
    {
        if (!group.TryGetProperty("artist-credit", out var credits) || credits.ValueKind != JsonValueKind.Array) return false;

        var joined = new System.Text.StringBuilder();
        foreach (var credit in credits.EnumerateArray())
        {
            var name = String(credit, "name");
            if (name is not null)
            {
                if (PosterNames.MatchKey(name) == wantedArtist) return true;
                joined.Append(name).Append(String(credit, "joinphrase") ?? "");
            }
            if (credit.TryGetProperty("artist", out var artist) && String(artist, "name") is { } official && PosterNames.MatchKey(official) == wantedArtist) return true;
        }
        return PosterNames.MatchKey(joined.ToString()) == wantedArtist;
    }

    /// <summary>Anführungszeichen und Backslash würden die Suchanfrage umbauen.</summary>
    private static string Escape(string text) => text.Trim().Replace("\\", " ").Replace("\"", " ");

    private static string? String(JsonElement item, string name) =>
        item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <returns>Die Antwort als JSON (GET) bzw. ein leeres Dokument (HEAD erfolgreich); null bei 404 (es gibt nichts).</returns>
    private async Task<JsonDocument?> SendAsync(HttpMethod method, string url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var wait = _lastCall + (minGap ?? DefaultGap) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);

            using var request = new HttpRequestMessage(method, url);
            // Ein User-Agent an der Anfrage ersetzt den des Clients; deshalb dessen Teile zuerst übernehmen, dann den Kontakt anhängen.
            foreach (var product in http.DefaultRequestHeaders.UserAgent) request.Headers.UserAgent.Add(product);
            request.Headers.UserAgent.ParseAdd($"({contact.Trim()})");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            _lastCall = DateTime.UtcNow;
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"{request.RequestUri!.Host} antwortete mit {(int)response.StatusCode} {response.ReasonPhrase}.");
            }
            return method == HttpMethod.Head ? JsonDocument.Parse("{}") : JsonDocument.Parse(await HttpFetch.ReadLimitedAsync(response, request.RequestUri!, MaxJsonBytes, ct));
        }
        finally
        {
            _gate.Release();
        }
    }
}
