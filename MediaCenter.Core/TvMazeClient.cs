using System.Globalization;
using System.Text.Json;

namespace MediaCenter.Core;

/// <summary>
/// Serienposter von TVmaze (api.tvmaze.com, ohne Schlüssel). Ein Treffer muss genau so heißen wie die Serie in der Bibliothek
/// (Groß-/Kleinschreibung, Satzzeichen und Akzente egal); bei bekanntem Jahr muss auch das Startjahr stimmen. Deutsche Titel
/// finden sich über die alternativen Namen der Serie (Die Simpsons → The Simpsons).
/// </summary>
public sealed class TvMazeClient(HttpClient http, TimeSpan? minGap = null) : IPosterSource
{
    private const long MaxJsonBytes = 2_000_000;

    /// <summary>TVmaze erlaubt 20 Abrufe in 10 Sekunden; mit Abstand bleiben wir sicher darunter.</summary>
    private static readonly TimeSpan DefaultGap = TimeSpan.FromMilliseconds(550);

    /// <summary>Wie viele Suchtreffer ohne gleichen Namen auf alternative Namen geprüft werden (je einer ist ein weiterer Abruf).</summary>
    private const int MaxAkaChecks = 3;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastCall = DateTime.MinValue;

    public string Provider => "tvmaze";

    public async Task<OnlinePoster?> FindAsync(string title, int? year, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Titel fehlt.", nameof(title));

        var wanted = PosterNames.MatchKey(title);
        if (wanted.Length == 0) return null;

        using var search = await GetJsonAsync($"https://api.tvmaze.com/search/shows?q={Uri.EscapeDataString(title.Trim())}", ct);
        var candidates = new List<Show>();
        foreach (var item in search.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("show", out var show) && Show.TryParse(show, out var parsed)) candidates.Add(parsed);
        }

        // Gleichnamige Serien (Das Boot 1985 und 2018) trennt das Jahr; ohne Jahr gewinnt der bessere Suchtreffer.
        var matching = candidates.Where(c => c.ImageUrl is not null && YearFits(c.Year, year)).ToList();
        if (matching.FirstOrDefault(c => PosterNames.MatchKey(c.Name) == wanted) is { } exact) return exact.ToPoster();

        foreach (var candidate in matching.Take(MaxAkaChecks))
        {
            ct.ThrowIfCancellationRequested();
            using var akas = await GetJsonAsync($"https://api.tvmaze.com/shows/{candidate.Id}/akas", ct);
            if (akas.RootElement.EnumerateArray().Any(a => a.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && PosterNames.MatchKey(n.GetString()!) == wanted))
            {
                return candidate.ToPoster();
            }
        }
        return null;
    }

    private static bool YearFits(int? showYear, int? wantedYear) => wantedYear is null || showYear == wantedYear;

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var wait = _lastCall + (minGap ?? DefaultGap) - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);

                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                _lastCall = DateTime.UtcNow;
                if ((int)response.StatusCode == 429 && attempt == 0)
                {
                    // Zu viele Abrufe: einmal kurz warten (Retry-After, höchstens 10 s), dann genau einen weiteren Versuch.
                    var pause = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                    await Task.Delay(pause > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : pause, ct);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"api.tvmaze.com antwortete mit {(int)response.StatusCode} {response.ReasonPhrase}.");
                }

                var bytes = await HttpFetch.ReadLimitedAsync(response, new Uri(url), MaxJsonBytes, ct);
                return JsonDocument.Parse(bytes);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record Show(int Id, string Name, int? Year, string? ImageUrl)
    {
        public OnlinePoster ToPoster() => new(Name, ImageUrl!);

        public static bool TryParse(JsonElement show, out Show parsed)
        {
            parsed = default!;
            if (!show.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number) return false;
            if (!show.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) return false;

            int? year = null;
            if (show.TryGetProperty("premiered", out var premiered) && premiered.ValueKind == JsonValueKind.String
                && premiered.GetString() is { Length: >= 4 } date && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y))
            {
                year = y;
            }

            string? image = null;
            if (show.TryGetProperty("image", out var images) && images.ValueKind == JsonValueKind.Object)
            {
                image = Url(images, "original") ?? Url(images, "medium");
            }
            parsed = new Show(id.GetInt32(), name.GetString()!, year, image);
            return true;
        }

        /// <summary>Nur https-Adressen: ein Bild-Link aus einer fremden Antwort darf nicht auf file: oder Ähnliches zeigen.</summary>
        private static string? Url(JsonElement images, string name) =>
            images.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            && Uri.TryCreate(v.GetString(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                ? uri.ToString()
                : null;
    }
}
