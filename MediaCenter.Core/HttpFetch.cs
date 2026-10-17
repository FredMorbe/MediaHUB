namespace MediaCenter.Core;

/// <summary>Abrufe aus dem Internet mit Größenlimit. Antworten kommen von fremden Servern und gelten als unvertrauenswürdig.</summary>
public static class HttpFetch
{
    /// <summary>Nur http und https; alles andere (file:, ftp:, Skript-Schemata) wird abgelehnt.</summary>
    public static Uri RequireWebUri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException($"Keine gültige Web-Adresse (nur http und https): '{url}'.", nameof(url));
        }
        return uri;
    }

    public static async Task<byte[]> GetBytesAsync(HttpClient http, Uri uri, long maxBytes, CancellationToken ct)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{uri.Host} antwortete mit {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        return await ReadLimitedAsync(response, uri, maxBytes, ct);
    }

    /// <summary>Liest den Inhalt einer bereits geprüften Antwort, bricht aber bei mehr als <paramref name="maxBytes"/> ab.</summary>
    public static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, Uri uri, long maxBytes, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is { } length && length > maxBytes)
        {
            throw new InvalidDataException($"Antwort von {uri.Host} ist mit {length / 1_000_000} MB zu groß (Limit {maxBytes / 1_000_000} MB).");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new InvalidDataException($"Antwort von {uri.Host} überschreitet das Limit von {maxBytes / 1_000_000} MB.");
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    public static async Task<string> GetStringAsync(HttpClient http, Uri uri, long maxBytes, CancellationToken ct) =>
        System.Text.Encoding.UTF8.GetString(await GetBytesAsync(http, uri, maxBytes, ct));
}
