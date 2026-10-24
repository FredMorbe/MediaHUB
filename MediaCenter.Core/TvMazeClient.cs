using System.Globalization;
using System.Text.Json;

namespace MediaCenter.Core;

/// <summary>
/// Serienposter und Infotexte (Handlung, Wertung, Besetzung, Folgen) von TVmaze (api.tvmaze.com, ohne Schlüssel). Ein Treffer muss genau so heißen wie die Serie in der Bibliothek
/// (Groß-/Kleinschreibung, Satzzeichen und Akzente egal); bei bekanntem Jahr muss auch das Startjahr stimmen. Deutsche Titel
/// finden sich über die alternativen Namen der Serie (Die Simpsons → The Simpsons).
/// </summary>
public sealed class TvMazeClient(HttpClient http, TimeSpan? minGap = null) : IPosterSource, ITitleInfoSource
{
    private const long MaxJsonBytes = 2_000_000;

    /// <summary>TVmaze erlaubt 20 Abrufe in 10 Sekunden; mit Abstand bleiben wir sicher darunter.</summary>
    private static readonly TimeSpan DefaultGap = TimeSpan.FromMilliseconds(550);

    /// <summary>Wie viele Suchtreffer ohne gleichen Namen auf alternative Namen geprüft werden (je einer ist ein weiterer Abruf).</summary>
    private const int MaxAkaChecks = 3;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastCall = DateTime.MinValue;

    public string Provider => "tvmaze";

    public async Task<OnlinePoster?> FindAsync(string title, int? year, CancellationToken ct = default) =>
        (await FindShowAsync(title, year, requireImage: true, ct))?.ToPoster();

    /// <summary>Handlung, Wertung, Besetzung und die Folgen (Name und Zusammenfassung) der Serie; ein einziger Abruf dazu, nachdem die Serie gefunden ist.</summary>
    public async Task<OnlineTitleInfo?> FindInfoAsync(string title, int? year, CancellationToken ct = default)
    {
        if (await FindShowAsync(title, year, requireImage: false, ct) is not { } show) return null;

        using var doc = await GetJsonAsync($"https://api.tvmaze.com/shows/{show.Id}?embed%5B%5D=episodes&embed%5B%5D=cast", ct);
        var embedded = doc.RootElement.TryGetProperty("_embedded", out var e) && e.ValueKind == JsonValueKind.Object ? e : default;

        var episodes = new List<EpisodeText>();
        if (embedded.ValueKind == JsonValueKind.Object && embedded.TryGetProperty("episodes", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                // Sonderfolgen haben keine Nummer: die Bibliothek kennt sie nicht als Staffel/Folge.
                if (item.TryGetProperty("season", out var s) && s.ValueKind == JsonValueKind.Number
                    && item.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Number)
                {
                    var name = Text(item, "name");
                    var summary = PodcastFeedParser.Plain(Text(item, "summary"));
                    if (name is not null || summary is not null) episodes.Add(new EpisodeText(s.GetInt32(), n.GetInt32(), name, summary));
                }
            }
        }

        var cast = new List<string>();
        if (embedded.ValueKind == JsonValueKind.Object && embedded.TryGetProperty("cast", out var members) && members.ValueKind == JsonValueKind.Array)
        {
            foreach (var member in members.EnumerateArray())
            {
                if (member.TryGetProperty("person", out var person) && person.ValueKind == JsonValueKind.Object
                    && Text(person, "name") is { } actor && !cast.Contains(actor) && cast.Count < MaxCast)
                {
                    cast.Add(actor);
                }
            }
        }

        var info = new OnlineTitleInfo(show.Name, PodcastFeedParser.Plain(show.Summary), show.Rating, null, cast.Count > 0 ? string.Join(", ", cast) : null, episodes);
        return info.IsEmpty ? null : info;
    }

    /// <summary>So viele Schauspieler werden gespeichert (die Suche nach Schauspielern findet nur, wen sie kennt); die Seite zeigt die ersten.</summary>
    private const int MaxCast = 20;

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <param name="requireImage">Fürs Poster zählen nur Serien mit Bild; für Infotexte reicht jede Serie mit dem richtigen Namen.</param>
    private async Task<Show?> FindShowAsync(string title, int? year, bool requireImage, CancellationToken ct)
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
        var matching = candidates.Where(c => (!requireImage || c.ImageUrl is not null) && YearFits(c.Year, year)).ToList();
        if (matching.FirstOrDefault(c => PosterNames.MatchKey(c.Name) == wanted) is { } exact) return exact;

        foreach (var candidate in matching.Take(MaxAkaChecks))
        {
            ct.ThrowIfCancellationRequested();
            using var akas = await GetJsonAsync($"https://api.tvmaze.com/shows/{candidate.Id}/akas", ct);
            if (akas.RootElement.EnumerateArray().Any(a => a.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && PosterNames.MatchKey(n.GetString()!) == wanted))
            {
                return candidate;
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

    private sealed record Show(int Id, string Name, int? Year, string? ImageUrl, string? Summary, double? Rating)
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
            double? rating = show.TryGetProperty("rating", out var r) && r.ValueKind == JsonValueKind.Object
                && r.TryGetProperty("average", out var average) && average.ValueKind == JsonValueKind.Number ? average.GetDouble() : null;
            parsed = new Show(id.GetInt32(), name.GetString()!, year, image, Text(show, "summary"), rating);
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
