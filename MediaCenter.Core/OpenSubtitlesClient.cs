using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MediaCenter.Core;

/// <summary>Was OpenSubtitles zu einem Video wissen soll. Titel und Jahr oder Staffel und Folge machen die Suche eng; der Hash findet die passende Fassung der Datei.</summary>
/// <param name="Language">Zweibuchstabiger Schlüssel (<see cref="Languages"/>).</param>
/// <param name="MovieHash">Prüfsumme der Videodatei (<see cref="OpenSubtitlesClient.MovieHash"/>); null, wenn die Datei nicht lesbar ist.</param>
public sealed record SubtitleQuery(string Title, int? Year, int? Season, int? Episode, string Language, string? MovieHash);

/// <summary>Ein Untertitel aus der Suche.</summary>
/// <param name="FileId">Nummer für den Download.</param>
/// <param name="HashMatch">Gehört nachweislich zu genau dieser Videodatei (gleicher Hash): Zeitpunkte stimmen sicher.</param>
public sealed record SubtitleHit(
    long FileId, string Release, string Language, int Downloads, bool HashMatch, bool HearingImpaired, bool Machine, string? Title, int? Year, int? Season, int? Episode);

/// <summary>Ein heruntergeladener Untertitel.</summary>
/// <param name="Remaining">Downloads, die das Konto heute noch hat; null, wenn der Server es nicht sagt.</param>
public sealed record DownloadedSubtitle(string FileName, byte[] Content, int? Remaining);

/// <summary>Anfrage an OpenSubtitles gescheitert; die Meldung ist für den Nutzer gedacht und enthält nie Schlüssel oder Passwort.</summary>
public sealed class OpenSubtitlesException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Sucht und lädt Untertitel von OpenSubtitles (REST-API v1, api.opensubtitles.com). Die Suche braucht einen API-Schlüssel, der Download zusätzlich ein Konto
/// (Anmeldung mit Benutzername und Passwort liefert ein Token, das nur im Speicher bleibt). Gesendet werden Titel, Jahr, Staffel, Folge, Sprache und die
/// Prüfsumme der Datei, nie Pfade.
/// </summary>
public sealed class OpenSubtitlesClient(HttpClient http, string apiKey, string userAgent)
{
    private const string DefaultHost = "api.opensubtitles.com";
    private const long MaxJsonBytes = 2_000_000;
    private const int MaxHits = 30;

    private string _host = DefaultHost;
    private string? _token;

    public bool LoggedIn => _token is not null;

    /// <summary>Meldet sich an; ohne Anmeldung geht nur die Suche. Der Server nennt dabei den Rechner für die weiteren Anfragen.</summary>
    public async Task LoginAsync(string username, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)) throw new OpenSubtitlesException("Benutzername oder Passwort für OpenSubtitles fehlt.");

        var body = JsonSerializer.Serialize(new { username, password });
        using var request = Create(HttpMethod.Post, DefaultHost, "login");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var json = await SendAsync(request, "Anmeldung", ct);

        var root = json.RootElement;
        if (!root.TryGetProperty("token", out var token) || token.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(token.GetString()))
            throw new OpenSubtitlesException("OpenSubtitles hat bei der Anmeldung kein Token geliefert.");
        _token = token.GetString();

        // Den Rechner übernimmt nur, wer zu opensubtitles.com gehört: das Token soll nirgendwo anders hin.
        if (root.TryGetProperty("base_url", out var host) && host.ValueKind == JsonValueKind.String && host.GetString() is { } name
            && (name == "opensubtitles.com" || name.EndsWith(".opensubtitles.com", StringComparison.OrdinalIgnoreCase))
            && Uri.CheckHostName(name) == UriHostNameType.Dns)
        {
            _host = name;
        }
    }

    public async Task<IReadOnlyList<SubtitleHit>> SearchAsync(SubtitleQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Title)) throw new ArgumentException("Titel fehlt.", nameof(query));
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["languages"] = query.Language.ToLowerInvariant(),
            ["query"] = query.Title.Trim().ToLowerInvariant(),
        };
        if (query.Season is { } season && query.Episode is { } episode)
        {
            parameters["season_number"] = season.ToString(CultureInfo.InvariantCulture);
            parameters["episode_number"] = episode.ToString(CultureInfo.InvariantCulture);
            parameters["type"] = "episode";
        }
        else
        {
            parameters["type"] = "movie";
            if (query.Year is { } year) parameters["year"] = year.ToString(CultureInfo.InvariantCulture);
        }
        if (!string.IsNullOrEmpty(query.MovieHash)) parameters["moviehash"] = query.MovieHash;

        var path = "subtitles?" + string.Join('&', parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        using var request = Create(HttpMethod.Get, _host, path);
        using var json = await SendAsync(request, "Suche", ct);
        return ParseSearch(json);
    }

    /// <summary>Lädt den Untertitel als SRT. Braucht die Anmeldung.</summary>
    public async Task<DownloadedSubtitle> DownloadAsync(long fileId, CancellationToken ct = default)
    {
        if (_token is null) throw new OpenSubtitlesException("Zum Herunterladen ist die Anmeldung bei OpenSubtitles nötig.");

        using var request = Create(HttpMethod.Post, _host, "download");
        request.Content = new StringContent(JsonSerializer.Serialize(new { file_id = fileId, sub_format = "srt" }), Encoding.UTF8, "application/json");
        using var json = await SendAsync(request, "Download", ct);

        var root = json.RootElement;
        if (!root.TryGetProperty("link", out var link) || link.ValueKind != JsonValueKind.String)
            throw new OpenSubtitlesException("OpenSubtitles hat keinen Download-Link geliefert" + (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String ? $": {message.GetString()}" : "."));
        var name = root.TryGetProperty("file_name", out var file) && file.ValueKind == JsonValueKind.String ? file.GetString()! : $"{fileId}.srt";
        int? remaining = root.TryGetProperty("remaining", out var left) && left.ValueKind == JsonValueKind.Number ? left.GetInt32() : null;

        try
        {
            // Die Datei liegt bei einem Auslieferungsdienst; der Link ist von außen, darum nur http(s) und mit Größenlimit.
            var content = await HttpFetch.GetBytesAsync(http, HttpFetch.RequireWebUri(link.GetString()!), SubtitleStore.MaxBytes, ct);
            return new DownloadedSubtitle(name, content, remaining);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or ArgumentException)
        {
            throw new OpenSubtitlesException($"Der Untertitel ließ sich nicht laden: {ex.Message}", ex);
        }
    }

    private HttpRequestMessage Create(HttpMethod method, string host, string path)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new OpenSubtitlesException("Kein API-Schlüssel für OpenSubtitles hinterlegt.");
        var request = new HttpRequestMessage(method, $"https://{host}/api/v1/{path}");
        request.Headers.Add("Api-Key", apiKey);
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.ParseAdd(userAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (_token is not null && path != "login") request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return request;
    }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, string what, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new OpenSubtitlesException($"OpenSubtitles ist nicht erreichbar ({what}): {ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode) throw new OpenSubtitlesException(Explain(response.StatusCode, what, await ReadMessageAsync(response, request.RequestUri!, ct)));
            try
            {
                var bytes = await HttpFetch.ReadLimitedAsync(response, request.RequestUri!, MaxJsonBytes, ct);
                return JsonDocument.Parse(bytes);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                throw new OpenSubtitlesException($"Die Antwort von OpenSubtitles ({what}) ist unlesbar: {ex.Message}", ex);
            }
        }
    }

    private static async Task<string?> ReadMessageAsync(HttpResponseMessage response, Uri uri, CancellationToken ct)
    {
        try
        {
            var bytes = await HttpFetch.ReadLimitedAsync(response, uri, 100_000, ct);
            using var json = JsonDocument.Parse(bytes);
            return json.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String ? message.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return null;
        }
    }

    private static string Explain(HttpStatusCode status, string what, string? serverMessage)
    {
        var text = status switch
        {
            HttpStatusCode.Unauthorized => "Benutzername, Passwort oder API-Schlüssel wurden abgelehnt.",
            HttpStatusCode.Forbidden => "Der API-Schlüssel wurde abgelehnt.",
            HttpStatusCode.NotAcceptable => "Das Tageslimit für Downloads ist erreicht.",
            HttpStatusCode.TooManyRequests => "Zu viele Anfragen; bitte kurz warten.",
            _ => $"Antwort {(int)status} {status}.",
        };
        return $"OpenSubtitles ({what}): {text}" + (string.IsNullOrWhiteSpace(serverMessage) ? "" : $" ({serverMessage})");
    }

    /// <summary>Liest die Trefferliste. Treffer ohne Datei-Nummer fallen weg; mit Hash-Treffer zuerst, dann nach Beliebtheit.</summary>
    internal static IReadOnlyList<SubtitleHit> ParseSearch(JsonDocument json)
    {
        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return Array.Empty<SubtitleHit>();

        var hits = new List<SubtitleHit>();
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("attributes", out var a) || a.ValueKind != JsonValueKind.Object) continue;
            if (!a.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array || files.GetArrayLength() == 0) continue;
            if (!files[0].TryGetProperty("file_id", out var id) || id.ValueKind != JsonValueKind.Number) continue;

            string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            bool Flag(string name) => a.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;
            int? Number(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) ? n : null;

            var details = a.TryGetProperty("feature_details", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
            var hasDetails = details.ValueKind == JsonValueKind.Object;
            hits.Add(new SubtitleHit(
                id.GetInt64(),
                Text(a, "release") is { Length: > 0 } release ? release : Text(files[0], "file_name") ?? "",
                Text(a, "language") ?? "",
                Number(a, "download_count") ?? 0,
                Flag("moviehash_match"), Flag("hearing_impaired"), Flag("machine_translated") || Flag("ai_translated"),
                hasDetails ? Text(details, "title") ?? Text(details, "movie_name") : null,
                hasDetails ? Number(details, "year") : null,
                hasDetails ? Number(details, "season_number") : null,
                hasDetails ? Number(details, "episode_number") : null));
        }
        return hits.OrderByDescending(h => h.HashMatch).ThenBy(h => h.Machine).ThenByDescending(h => h.Downloads).Take(MaxHits).ToList();
    }

    /// <summary>
    /// Prüfsumme nach dem Verfahren von OpenSubtitles: Dateigröße plus die 64-Bit-Summen (Little Endian) der ersten und der letzten 64 KiB, als 16 Hexziffern.
    /// Gleiche Datei, gleicher Hash: der Treffer passt dann zeitlich sicher.
    /// </summary>
    /// <exception cref="InvalidDataException">Die Datei ist kleiner als 128 KiB.</exception>
    public static string MovieHash(string path)
    {
        const int Block = 64 * 1024;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length < 2 * Block) throw new InvalidDataException($"Die Datei ist zu klein für eine Prüfsumme: {Path.GetFileName(path)}");

        ulong hash = (ulong)stream.Length;
        var buffer = new byte[Block];
        foreach (var offset in new[] { 0L, stream.Length - Block })
        {
            stream.Position = offset;
            stream.ReadExactly(buffer);
            for (var i = 0; i < Block; i += 8) hash += BitConverter.ToUInt64(buffer, i);
        }
        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }
}
