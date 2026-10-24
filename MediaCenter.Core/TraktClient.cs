using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MediaCenter.Core;

/// <summary>Zugangsdaten zu Trakt: <see cref="ClientId"/> und <see cref="ClientSecret"/> der eigenen Trakt-App (trakt.tv/oauth/applications) und die Anmeldung.</summary>
public sealed record TraktTokens(string AccessToken, string RefreshToken, DateTime ExpiresUtc);

/// <summary>Anmeldung am Trakt-Konto; gehört zu dem Profil, das sie eingerichtet hat (Gesehen-Stände sind je Profil).</summary>
public sealed record TraktAccount(string ClientId, string ClientSecret, long ProfileId, string? Username = null, TraktTokens? Tokens = null);

/// <summary>Der Code, den der Nutzer auf trakt.tv/activate eingibt.</summary>
public sealed record TraktDeviceCode(string DeviceCode, string UserCode, string VerificationUrl, TimeSpan ExpiresIn, TimeSpan Interval);

public sealed record TraktWatchedMovie(string Title, int? Year, DateTime? WatchedUtc);

public sealed record TraktWatchedEpisode(int Season, int Episode, DateTime? WatchedUtc);

public sealed record TraktWatchedShow(string Title, int? Year, IReadOnlyList<TraktWatchedEpisode> Episodes);

/// <summary>Ein gesehener Film der Bibliothek, der an Trakt gehen soll.</summary>
public sealed record TraktMovieWatch(string Title, int? Year, DateTime WatchedUtc);

/// <summary>Gesehene Folgen einer Serie der Bibliothek, die an Trakt gehen sollen.</summary>
public sealed record TraktShowWatch(string Title, int? Year, IReadOnlyList<TraktWatchedEpisode> Episodes);

/// <param name="Added">Neue Wiedergaben bei Trakt (Filme und Folgen).</param>
/// <param name="NotFound">Titel, die Trakt nicht zuordnen konnte (der Dienst meldet sie zurück).</param>
public sealed record TraktPushResult(int Added, IReadOnlyList<string> NotFound);

/// <summary>Trakt lehnt die Anmeldung ab (Code abgelaufen, verweigert, falsche App-Daten, Zugang entzogen): neu verbinden.</summary>
public sealed class TraktAuthException(string message) : Exception(message);

/// <summary>
/// Das Wenige von Trakt (api.trakt.tv, API Version 2), das der Abgleich braucht: Anmeldung mit Gerätecode (ohne Passwort in der App), gesehene Filme und
/// Folgen lesen, Gesehenes eintragen. Nur eine Quelle von Zuordnungen: Titel und Jahr (die Bibliothek kennt keine Kennungen).
/// </summary>
public sealed class TraktClient(HttpClient http, TraktAccount account, Action<TraktTokens>? tokensRefreshed = null)
{
    private const string Api = "https://api.trakt.tv";
    private const long MaxJsonBytes = 20_000_000;

    /// <summary>Trakt erlaubt je Sekunde einen schreibenden Abruf; der Abstand liegt darüber.</summary>
    private static readonly TimeSpan WriteGap = TimeSpan.FromMilliseconds(1100);

    private TraktTokens? _tokens = account.Tokens;
    private DateTime _lastWrite = DateTime.MinValue;

    public TraktTokens? Tokens => _tokens;

    // --- Anmeldung -----------------------------------------------------------------------------------------

    /// <summary>Beginnt die Anmeldung: Trakt liefert einen Code, den der Nutzer auf der Trakt-Seite eingibt.</summary>
    public static async Task<TraktDeviceCode> StartDeviceAuthAsync(HttpClient http, string clientId, CancellationToken ct = default)
    {
        JsonDocument doc;
        try
        {
            doc = await SendAsync(http, HttpMethod.Post, $"{Api}/oauth/device/code", clientId, null, new { client_id = clientId }, ct);
        }
        catch (TraktAuthException)
        {
            // Ohne Anmeldung kann nur die Client-ID der Grund sein.
            throw new TraktAuthException("Trakt kennt diese Client-ID nicht. Client-ID und Client-Secret stehen auf trakt.tv/oauth/applications bei deiner App.");
        }
        using var _ = doc;
        var root = doc.RootElement;
        return new TraktDeviceCode(
            Str(root, "device_code") ?? throw new InvalidDataException("Trakt lieferte keinen Gerätecode."),
            Str(root, "user_code") ?? throw new InvalidDataException("Trakt lieferte keinen Anzeigecode."),
            Str(root, "verification_url") ?? "https://trakt.tv/activate",
            TimeSpan.FromSeconds(Num(root, "expires_in") ?? 600),
            TimeSpan.FromSeconds(Math.Max(1, Num(root, "interval") ?? 5)));
    }

    /// <summary>Wartet, bis der Nutzer den Code bestätigt hat, und gibt die Anmeldung zurück.</summary>
    /// <exception cref="TraktAuthException">Code abgelaufen, verweigert oder ungültig.</exception>
    public static async Task<TraktTokens> WaitForDeviceTokenAsync(HttpClient http, TraktAccount account, TraktDeviceCode code, DateTime? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null, CancellationToken ct = default)
    {
        delay ??= (span, token) => Task.Delay(span, token);
        var started = now ?? DateTime.UtcNow;
        var interval = code.Interval;
        var waited = TimeSpan.Zero;
        while (waited < code.ExpiresIn)
        {
            await delay(interval, ct);
            waited += interval;

            using var request = Request(HttpMethod.Post, $"{Api}/oauth/device/token", account.ClientId, null,
                new { code = code.DeviceCode, client_id = account.ClientId, client_secret = account.ClientSecret });
            using var response = await http.SendAsync(request, ct);
            switch ((int)response.StatusCode)
            {
                case 200:
                    using (var doc = JsonDocument.Parse(await HttpFetch.ReadLimitedAsync(response, request.RequestUri!, MaxJsonBytes, ct))) return ParseTokens(doc.RootElement, started + waited);
                case 400: // noch nicht bestätigt
                    break;
                case 429: // zu schnell gefragt
                    interval += TimeSpan.FromSeconds(1);
                    break;
                case 404: throw new TraktAuthException("Trakt kennt den Code nicht (mehr).");
                case 409: throw new TraktAuthException("Der Code wurde schon benutzt.");
                case 410: throw new TraktAuthException("Der Code ist abgelaufen.");
                case 418: throw new TraktAuthException("Die Anmeldung wurde auf trakt.tv abgelehnt.");
                case 401: throw new TraktAuthException("Trakt lehnt die Client-ID oder das Client-Secret ab. Beides steht auf trakt.tv/oauth/applications.");
                default: throw new HttpRequestException($"api.trakt.tv antwortete mit {(int)response.StatusCode} {response.ReasonPhrase}.");
            }
        }
        throw new TraktAuthException("Der Code ist abgelaufen, bevor er bestätigt wurde.");
    }

    private static TraktTokens ParseTokens(JsonElement root, DateTime fallbackNow)
    {
        var access = Str(root, "access_token") ?? throw new InvalidDataException("Trakt lieferte kein Zugriffstoken.");
        var refresh = Str(root, "refresh_token") ?? throw new InvalidDataException("Trakt lieferte kein Erneuerungstoken.");
        var created = Num(root, "created_at") is { } c ? DateTimeOffset.FromUnixTimeSeconds((long)c).UtcDateTime : fallbackNow;
        return new TraktTokens(access, refresh, created.AddSeconds(Num(root, "expires_in") ?? 7_776_000));
    }

    /// <summary>Erneuert die Anmeldung, wenn sie bald abläuft (oder <paramref name="force"/>).</summary>
    public async Task EnsureFreshAsync(DateTime nowUtc, bool force = false, CancellationToken ct = default)
    {
        if (_tokens is null) throw new TraktAuthException("Nicht mit Trakt verbunden.");
        if (!force && _tokens.ExpiresUtc - nowUtc > TimeSpan.FromDays(7)) return;

        using var request = Request(HttpMethod.Post, $"{Api}/oauth/token", account.ClientId, null, new
        {
            refresh_token = _tokens.RefreshToken,
            client_id = account.ClientId,
            client_secret = account.ClientSecret,
            redirect_uri = "urn:ietf:wg:oauth:2.0:oob",
            grant_type = "refresh_token",
        });
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
            throw new TraktAuthException("Die Anmeldung bei Trakt ist abgelaufen oder wurde entzogen. Bitte neu verbinden.");
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"api.trakt.tv antwortete mit {(int)response.StatusCode} {response.ReasonPhrase}.");
        using var doc = JsonDocument.Parse(await HttpFetch.ReadLimitedAsync(response, request.RequestUri!, MaxJsonBytes, ct));
        _tokens = ParseTokens(doc.RootElement, nowUtc);
        tokensRefreshed?.Invoke(_tokens);
    }

    /// <summary>Der Benutzername des verbundenen Kontos (zur Anzeige).</summary>
    public async Task<string?> GetUsernameAsync(CancellationToken ct = default)
    {
        using var doc = await AuthorizedAsync(HttpMethod.Get, $"{Api}/users/settings", null, ct);
        return doc.RootElement.TryGetProperty("user", out var user) ? Str(user, "username") : null;
    }

    // --- Gesehenes lesen -----------------------------------------------------------------------------------

    public async Task<IReadOnlyList<TraktWatchedMovie>> GetWatchedMoviesAsync(CancellationToken ct = default)
    {
        using var doc = await AuthorizedAsync(HttpMethod.Get, $"{Api}/sync/watched/movies", null, ct);
        var list = new List<TraktWatchedMovie>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("movie", out var movie) || Str(movie, "title") is not { } title) continue;
            list.Add(new TraktWatchedMovie(title, Year(movie), Time(item, "last_watched_at")));
        }
        return list;
    }

    public async Task<IReadOnlyList<TraktWatchedShow>> GetWatchedShowsAsync(CancellationToken ct = default)
    {
        using var doc = await AuthorizedAsync(HttpMethod.Get, $"{Api}/sync/watched/shows", null, ct);
        var list = new List<TraktWatchedShow>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("show", out var show) || Str(show, "title") is not { } title) continue;

            var episodes = new List<TraktWatchedEpisode>();
            if (item.TryGetProperty("seasons", out var seasons) && seasons.ValueKind == JsonValueKind.Array)
            {
                foreach (var season in seasons.EnumerateArray())
                {
                    if (Num(season, "number") is not { } seasonNumber || !season.TryGetProperty("episodes", out var eps) || eps.ValueKind != JsonValueKind.Array) continue;
                    foreach (var episode in eps.EnumerateArray())
                    {
                        if (Num(episode, "number") is { } number) episodes.Add(new TraktWatchedEpisode((int)seasonNumber, (int)number, Time(episode, "last_watched_at")));
                    }
                }
            }
            list.Add(new TraktWatchedShow(title, Year(show), episodes));
        }
        return list;
    }

    // --- Gesehenes eintragen -------------------------------------------------------------------------------

    /// <summary>Trägt gesehene Filme und Folgen bei Trakt ein (Zuordnung durch Trakt über Titel und Jahr).</summary>
    public async Task<TraktPushResult> AddHistoryAsync(IReadOnlyList<TraktMovieWatch> movies, IReadOnlyList<TraktShowWatch> shows, CancellationToken ct = default)
    {
        if (movies.Count == 0 && shows.Count == 0) return new TraktPushResult(0, Array.Empty<string>());

        var body = new Dictionary<string, object>
        {
            ["movies"] = movies.Select(m => Obj(("title", m.Title), ("year", m.Year), ("watched_at", Iso(m.WatchedUtc)))).ToList(),
            ["shows"] = shows.Select(s => Obj(("title", s.Title), ("year", s.Year), ("seasons",
                s.Episodes.GroupBy(e => e.Season).OrderBy(g => g.Key).Select(g => Obj(("number", g.Key), ("episodes",
                    g.OrderBy(e => e.Episode).Select(e => Obj(("number", e.Episode), ("watched_at", e.WatchedUtc is { } w ? Iso(w) : null))).ToList()))).ToList()))).ToList(),
        };
        var wait = _lastWrite + WriteGap - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        using var doc = await AuthorizedAsync(HttpMethod.Post, $"{Api}/sync/history", body, ct);
        _lastWrite = DateTime.UtcNow;

        var added = 0;
        if (doc.RootElement.TryGetProperty("added", out var a) && a.ValueKind == JsonValueKind.Object)
        {
            added = (int)((Num(a, "movies") ?? 0) + (Num(a, "episodes") ?? 0));
        }
        var notFound = new List<string>();
        if (doc.RootElement.TryGetProperty("not_found", out var nf) && nf.ValueKind == JsonValueKind.Object)
        {
            foreach (var group in new[] { "movies", "shows", "seasons", "episodes" })
            {
                if (!nf.TryGetProperty(group, out var items) || items.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in items.EnumerateArray()) notFound.Add(Str(item, "title") ?? group);
            }
        }
        return new TraktPushResult(added, notFound.Distinct().ToList());
    }

    // --- HTTP ------------------------------------------------------------------------------------------------

    private async Task<JsonDocument> AuthorizedAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        await EnsureFreshAsync(DateTime.UtcNow, force: false, ct);
        try
        {
            return await SendAsync(http, method, url, account.ClientId, _tokens!.AccessToken, body, ct);
        }
        catch (TraktAuthException)
        {
            // Der Zugriff wurde vor dem Ablauf entzogen oder ungültig: einmal erneuern und wiederholen.
            await EnsureFreshAsync(DateTime.UtcNow, force: true, ct);
            return await SendAsync(http, method, url, account.ClientId, _tokens!.AccessToken, body, ct);
        }
    }

    private static async Task<JsonDocument> SendAsync(HttpClient http, HttpMethod method, string url, string clientId, string? accessToken, object? body, CancellationToken ct)
    {
        using var request = Request(method, url, clientId, accessToken, body);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new TraktAuthException("Trakt lehnt die Anmeldung ab.");
        if (response.StatusCode == HttpStatusCode.Forbidden) throw new TraktAuthException("Trakt lehnt die Client-ID ab oder der Zugriff wurde entzogen.");
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"api.trakt.tv antwortete mit {(int)response.StatusCode} {response.ReasonPhrase}.");
        return JsonDocument.Parse(await HttpFetch.ReadLimitedAsync(response, request.RequestUri!, MaxJsonBytes, ct));
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string clientId, string? accessToken, object? body)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("trakt-api-version", "2");
        request.Headers.Add("trakt-api-key", clientId);
        if (accessToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }

    private static Dictionary<string, object?> Obj(params (string Key, object? Value)[] pairs)
    {
        var map = new Dictionary<string, object?>();
        foreach (var (key, value) in pairs)
        {
            if (value is not null) map[key] = value; // fehlende Angaben (kein Jahr) bleiben weg, statt null zu schicken
        }
        return map;
    }

    private static string Iso(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.000'Z'", CultureInfo.InvariantCulture);

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static int? Year(JsonElement e) => Num(e, "year") is { } y ? (int)y : null;

    private static DateTime? Time(JsonElement e, string name) =>
        Str(e, name) is { } text && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;
}
