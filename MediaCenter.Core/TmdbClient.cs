using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaCenter.Core;

/// <summary>
/// Poster, Hintergrundbilder, deutsche Infotexte (Handlung, Wertung, Regie, Besetzung, Folgentexte) und Trailer von TMDB (themoviedb.org).
/// Der Schlüssel (v3-Schlüssel oder v4-Zugriffstoken) kommt vom Aufrufer und steht in keiner Meldung. Ein Treffer muss denselben Titel haben wie der
/// Titel in der Bibliothek, entweder in der deutschen oder in der Originalschreibweise (<see cref="PosterNames.MatchKey"/>), und bei bekanntem Jahr
/// aus demselben Jahr sein (eins Abweichung nur bei der Abfrage ohne Jahr): lieber kein Bild als ein falsches. <see cref="Movies"/> und
/// <see cref="Series"/> sind die Sichten für die beiden Titelarten; sie teilen sich die Abstände zwischen den Abrufen und die gemerkten Suchtreffer.
/// </summary>
/// <remarks>
/// TMDB ist nur für nicht-kommerzielle Nutzung kostenlos (themoviedb.org/api-terms-of-use). Verkauft jemand die App, braucht er dafür eine
/// schriftliche Lizenz von TMDB; die Anbindung ist deshalb abschaltbar (ohne Schlüssel gibt es sie nicht).
/// </remarks>
public sealed class TmdbClient(HttpClient http, string apiKey, TimeSpan? minGap = null)
{
    private const long MaxJsonBytes = 4_000_000;
    private const string ApiBase = "https://api.themoviedb.org/3/";
    private const string ImageBase = "https://image.tmdb.org/t/p/";
    private const string Language = "de-DE";
    private const int MaxCast = 20;
    private const int MaxDirectors = 3;
    private const int MaxSeasons = 40;

    /// <summary>Eine Wertung mit weniger Stimmen sagt nichts (neue Filme zeigen sonst 10.0 von drei Leuten).</summary>
    private const int MinVotes = 10;

    private static readonly TimeSpan DefaultGap = TimeSpan.FromMilliseconds(60);

    /// <summary>Bildpfade aus einer fremden Antwort: ein Dateiname, nichts, was den Pfad der Adresse verlassen könnte.</summary>
    private static readonly Regex ImagePath = new(@"^/[A-Za-z0-9_-]+\.(jpg|png)$", RegexOptions.CultureInvariant);

    private static readonly Regex YouTubeKey = new(@"^[A-Za-z0-9_-]{6,20}$", RegexOptions.CultureInvariant);

    /// <summary>Namen, die TMDB für nicht übersetzte Folgen einsetzt („Folge 3“, „Episode 3“): sie sagen nichts.</summary>
    private static readonly Regex GenericEpisodeName = new(@"^(Episode|Folge)\s+\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Match?> _matches = new();
    private DateTime _lastCall = DateTime.MinValue;
    private Source? _movies;
    private Source? _series;

    /// <summary>Die Sicht für Filme.</summary>
    public IRichTitleSource Movies => _movies ??= new Source(this, series: false);

    /// <summary>Die Sicht für Serien.</summary>
    public IRichTitleSource Series => _series ??= new Source(this, series: true);

    /// <summary>Ein v4-Zugriffstoken ist ein JWT (mit Punkten) und geht in den Kopf; der v3-Schlüssel als Parameter.</summary>
    private bool IsToken => apiKey.Contains('.');

    private sealed class Source(TmdbClient client, bool series) : IRichTitleSource
    {
        public string Provider => "tmdb";

        public async Task<OnlinePoster?> FindAsync(string title, int? year, CancellationToken ct = default) =>
            await client.FindMatchAsync(series, title, year, ct) is { PosterPath: { } path } match
                ? new OnlinePoster(match.Title, ImageBase + "w500" + path)
                : null;

        public async Task<OnlinePoster?> FindBackdropAsync(string title, int? year, CancellationToken ct = default) =>
            await client.FindMatchAsync(series, title, year, ct) is { BackdropPath: { } path } match
                ? new OnlinePoster(match.Title, ImageBase + "w1280" + path)
                : null;

        public async Task<OnlineTitleInfo?> FindInfoAsync(string title, int? year, CancellationToken ct = default)
        {
            if (await client.FindMatchAsync(series, title, year, ct) is not { } match) return null;
            var info = series ? await client.ReadSeriesAsync(match, ct) : await client.ReadMovieAsync(match, ct);
            return info is null || info.IsEmpty ? null : info;
        }
    }

    /// <summary>Ein Suchtreffer; die Bildpfade sind geprüft (oder null).</summary>
    private sealed record Match(int Id, string Title, int? Year, string? PosterPath, string? BackdropPath);

    /// <summary>Eine Abfrage an die Suche; <see cref="YearParam"/> null fragt ohne Jahr (der Dateiname kann ein Jahr daneben liegen).</summary>
    private sealed record Attempt(string Query, int? YearParam, bool SequelNumbersDropped, int YearTolerance);

    // --- Suche ---------------------------------------------------------------------------------------------

    private async Task<Match?> FindMatchAsync(bool series, string title, int? year, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Titel fehlt.", nameof(title));
        if (string.IsNullOrWhiteSpace(apiKey)) throw new PosterSourceUnavailableException("TMDB: kein Schlüssel gesetzt.");
        if (PosterNames.MatchKey(title).Length == 0) return null;

        // Poster, Hintergrund und Infotexte eines Titels fragen dieselbe Suche: sie läuft nur einmal.
        var cacheKey = $"{(series ? 's' : 'm')}|{title.Trim()}|{year}";
        lock (_matches)
        {
            if (_matches.TryGetValue(cacheKey, out var cached)) return cached;
        }

        Match? found = null;
        foreach (var attempt in Attempts(title.Trim(), year))
        {
            found = await SearchAsync(series, attempt, year, ct);
            if (found is not null) break;
        }
        lock (_matches) _matches[cacheKey] = found;
        return found;
    }

    /// <summary>Wie bei OMDb: der Titel mit Jahr; mit vertauschter Zahlenschreibweise; ohne Folgenummer; ohne Jahr (dann darf das Jahr um eins abweichen).</summary>
    private static IEnumerable<Attempt> Attempts(string title, int? year)
    {
        yield return new Attempt(title, year, false, 0);
        if (PosterNames.WithSwappedNumbers(title) is { } swapped) yield return new Attempt(swapped, year, false, 0);
        if (PosterNames.WithoutSequelNumbers(title) is { } withoutNumbers) yield return new Attempt(withoutNumbers, year, true, 0);
        if (year is not null) yield return new Attempt(title, null, false, 1);
    }

    private async Task<Match?> SearchAsync(bool series, Attempt attempt, int? year, CancellationToken ct)
    {
        var query = $"query={Uri.EscapeDataString(attempt.Query)}&language={Language}&include_adult=false";
        if (attempt.YearParam is { } y) query += (series ? "&first_air_date_year=" : "&year=") + y.ToString(CultureInfo.InvariantCulture);

        using var doc = await GetJsonAsync(series ? "search/tv" : "search/movie", query, ct);
        if (doc is null || !doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return null;

        var wanted = PosterNames.MatchKey(attempt.Query);
        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number) continue;
            var title = Text(item, series ? "name" : "title");
            if (title is null) continue;

            // Der deutsche Titel oder das Original: „Der Pate“ und „The Godfather“ sind derselbe Film.
            var original = Text(item, series ? "original_name" : "original_title");
            if (PosterNames.MatchKey(title) != wanted && (original is null || PosterNames.MatchKey(original) != wanted)) continue;

            // Ohne Folgenummer ist „Ghostbusters“ der erste Film, nicht der zweite: ohne bekanntes Jahr nur bei mindestens zwei Wörtern.
            if (attempt.SequelNumbersDropped && year is null && PosterNames.Tokens(attempt.Query).Count < 2) continue;

            var found = YearOf(Text(item, series ? "first_air_date" : "release_date"));
            if (year is { } wantedYear && !(found is { } foundYear && Math.Abs(foundYear - wantedYear) <= attempt.YearTolerance)) continue;

            return new Match(id.GetInt32(), title, found, ImageFile(item, "poster_path"), ImageFile(item, "backdrop_path"));
        }
        return null;
    }

    // --- Einzelheiten --------------------------------------------------------------------------------------

    private async Task<OnlineTitleInfo?> ReadMovieAsync(Match match, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"movie/{match.Id}", $"language={Language}&append_to_response=credits,videos&include_video_language=de,en,null", ct);
        if (doc is null) return null;
        var root = doc.RootElement;

        var directors = new List<string>();
        if (root.TryGetProperty("credits", out var credits) && credits.ValueKind == JsonValueKind.Object
            && credits.TryGetProperty("crew", out var crew) && crew.ValueKind == JsonValueKind.Array)
        {
            foreach (var member in crew.EnumerateArray())
            {
                if (member.ValueKind == JsonValueKind.Object && Text(member, "job") == "Director" && Text(member, "name") is { } name
                    && !directors.Contains(name) && directors.Count < MaxDirectors)
                {
                    directors.Add(name);
                }
            }
        }

        return new OnlineTitleInfo(match.Title, Known(root, "overview"), Rating(root), directors.Count > 0 ? string.Join(", ", directors) : null,
            Cast(root, "credits"), [], Trailer(root));
    }

    private async Task<OnlineTitleInfo?> ReadSeriesAsync(Match match, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"tv/{match.Id}", $"language={Language}&append_to_response=aggregate_credits,videos&include_video_language=de,en,null", ct);
        if (doc is null) return null;
        var root = doc.RootElement;

        var seasons = new List<int>();
        if (root.TryGetProperty("seasons", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var season in list.EnumerateArray())
            {
                // Staffel 0 sind Sonderfolgen: die Bibliothek kennt sie nicht als Staffel/Folge.
                if (season.ValueKind == JsonValueKind.Object && season.TryGetProperty("season_number", out var number) && number.ValueKind == JsonValueKind.Number
                    && number.GetInt32() is > 0 and var n && seasons.Count < MaxSeasons)
                {
                    seasons.Add(n);
                }
            }
        }

        var episodes = new List<EpisodeText>();
        foreach (var season in seasons)
        {
            ct.ThrowIfCancellationRequested();
            using var seasonDoc = await GetJsonAsync($"tv/{match.Id}/season/{season}", $"language={Language}", ct);
            if (seasonDoc is null || !seasonDoc.RootElement.TryGetProperty("episodes", out var items) || items.ValueKind != JsonValueKind.Array) continue;

            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("episode_number", out var number) || number.ValueKind != JsonValueKind.Number) continue;
                var name = Known(item, "name");
                if (name is not null && GenericEpisodeName.IsMatch(name)) name = null;
                var summary = Known(item, "overview");
                if (name is not null || summary is not null) episodes.Add(new EpisodeText(season, number.GetInt32(), name, summary));
            }
        }

        return new OnlineTitleInfo(match.Title, Known(root, "overview"), Rating(root), null, Cast(root, "aggregate_credits"), episodes, Trailer(root));
    }

    private static double? Rating(JsonElement root) =>
        root.TryGetProperty("vote_count", out var votes) && votes.ValueKind == JsonValueKind.Number && votes.GetInt32() >= MinVotes
        && root.TryGetProperty("vote_average", out var average) && average.ValueKind == JsonValueKind.Number
            ? average.GetDouble()
            : null;

    /// <summary>Die ersten Schauspieler in der Reihenfolge von TMDB, mit Komma getrennt.</summary>
    private static string? Cast(JsonElement root, string creditsName)
    {
        if (!root.TryGetProperty(creditsName, out var credits) || credits.ValueKind != JsonValueKind.Object
            || !credits.TryGetProperty("cast", out var cast) || cast.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var names = new List<string>();
        foreach (var member in cast.EnumerateArray())
        {
            if (member.ValueKind == JsonValueKind.Object && Text(member, "name") is { } name && !names.Contains(name) && names.Count < MaxCast) names.Add(name);
        }
        return names.Count > 0 ? string.Join(", ", names) : null;
    }

    /// <summary>
    /// Der beste YouTube-Trailer: deutsch vor englisch vor dem Rest, offizielle vor inoffiziellen, neuere vor älteren. Ein Teaser oder Clip zählt nicht.
    /// </summary>
    private static string? Trailer(JsonElement root)
    {
        if (!root.TryGetProperty("videos", out var videos) || videos.ValueKind != JsonValueKind.Object
            || !videos.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? best = null;
        (int Language, bool Official, string Published) bestRank = default;
        foreach (var video in results.EnumerateArray())
        {
            if (video.ValueKind != JsonValueKind.Object || Text(video, "site") != "YouTube" || Text(video, "type") != "Trailer") continue;
            if (Text(video, "key") is not { } key || !YouTubeKey.IsMatch(key)) continue;

            var language = Text(video, "iso_639_1") switch { "de" => 2, "en" => 1, _ => 0 };
            var official = video.TryGetProperty("official", out var o) && o.ValueKind == JsonValueKind.True;
            var rank = (language, official, Text(video, "published_at") ?? "");
            if (best is null || Compare(rank, bestRank) > 0)
            {
                best = key;
                bestRank = rank;
            }
        }
        return best;
    }

    private static int Compare((int Language, bool Official, string Published) a, (int Language, bool Official, string Published) b)
    {
        if (a.Language != b.Language) return a.Language.CompareTo(b.Language);
        if (a.Official != b.Official) return a.Official.CompareTo(b.Official);
        return string.CompareOrdinal(a.Published, b.Published);
    }

    // --- Hilfen --------------------------------------------------------------------------------------------

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Ein Text; leer heißt: TMDB weiß es (auf Deutsch) nicht.</summary>
    private static string? Known(JsonElement e, string name) => Text(e, name)?.Trim() is { Length: > 0 } text ? text : null;

    private static int? YearOf(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : null;

    private static string? ImageFile(JsonElement e, string name) => Text(e, name) is { } path && ImagePath.IsMatch(path) ? path : null;

    /// <returns>Die Antwort, oder null, wenn TMDB den Eintrag nicht (mehr) kennt (404).</returns>
    /// <exception cref="PosterSourceUnavailableException">Der Schlüssel wurde abgelehnt oder TMDB nimmt gerade keine Anfragen an.</exception>
    private async Task<JsonDocument?> GetJsonAsync(string path, string query, CancellationToken ct)
    {
        var url = ApiBase + path + "?" + query + (IsToken ? "" : "&api_key=" + Uri.EscapeDataString(apiKey));
        await _gate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var wait = _lastCall + (minGap ?? DefaultGap) - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (IsToken) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                _lastCall = DateTime.UtcNow;

                var code = (int)response.StatusCode;
                if (code == 429 && attempt == 0)
                {
                    // Zu viele Abrufe: einmal kurz warten (Retry-After, höchstens 10 s), dann genau einen weiteren Versuch.
                    var pause = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                    await Task.Delay(pause > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : pause, ct);
                    continue;
                }
                if (code == 429) throw new PosterSourceUnavailableException("TMDB: zu viele Anfragen, später erneut versuchen.");
                if (code == 401) throw new PosterSourceUnavailableException("TMDB: Der Schlüssel wurde abgelehnt.");
                if (code == 404) return null;
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"api.themoviedb.org antwortete mit {code} {response.ReasonPhrase}.");

                var bytes = await HttpFetch.ReadLimitedAsync(response, new Uri(ApiBase), MaxJsonBytes, ct);
                return JsonDocument.Parse(bytes);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
