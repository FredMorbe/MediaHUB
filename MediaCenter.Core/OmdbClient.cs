using System.Globalization;
using System.Text.Json;

namespace MediaCenter.Core;

/// <summary>
/// Filmposter von OMDb (omdbapi.com). Der Schlüssel kommt aus der Umgebung des Aufrufers und steht in keiner Datei und in
/// keiner Fehlermeldung. Ein Treffer muss denselben Titel haben wie der Film in der Bibliothek (<see cref="PosterNames.MatchKey"/>:
/// Groß-/Kleinschreibung, Satzzeichen, Akzente, führender Artikel und römische/arabische Zahlen egal) und, bei bekanntem Jahr, aus
/// demselben Jahr sein (eins Abweichung nur bei der Abfrage ohne Jahr). OMDb kennt vor allem die englischen Originaltitel: ein
/// deutscher Titel findet dort oft nichts, das ergibt dann kein Poster statt eines geratenen.
/// </summary>
public sealed class OmdbClient(HttpClient http, string apiKey, TimeSpan? minGap = null) : IPosterSource, IMovieInfoSource
{
    private const long MaxJsonBytes = 1_000_000;
    private static readonly TimeSpan DefaultGap = TimeSpan.FromMilliseconds(150);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastCall = DateTime.MinValue;

    public string Provider => "omdb";

    /// <summary>Eine Abfrage an OMDb; <see cref="YearParam"/> null fragt ohne Jahr (der Dateiname kann ein Jahr daneben liegen).</summary>
    private sealed record Attempt(string Query, int? YearParam, bool SequelNumbersDropped, int YearTolerance);

    private sealed record Candidate(string Title, int? Year, string? PosterUrl);

    public async Task<OnlinePoster?> FindAsync(string title, int? year, CancellationToken ct = default)
    {
        // Gleicher Film, aber ohne Bild („N/A“) oder mit unsicherer Adresse: dann gibt es kein Poster.
        var movie = await LookupAsync(title, year, ct);
        return movie?.PosterUrl is { } poster ? new OnlinePoster(movie.Title, poster) : null;
    }

    /// <summary>Der Film mit seiner Schreibweise bei OMDb, auch wenn er kein Poster hat.</summary>
    public async Task<MovieInfo?> LookupAsync(string title, int? year, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Titel fehlt.", nameof(title));
        if (string.IsNullOrWhiteSpace(apiKey)) throw new PosterSourceUnavailableException("OMDb: kein Schlüssel gesetzt.");
        if (PosterNames.MatchKey(title).Length == 0) return null;

        foreach (var attempt in Attempts(title.Trim(), year))
        {
            var candidate = await QueryAsync(attempt, ct);
            // Gleicher Film gefunden: keine Suche nach anderen Filmen mehr.
            if (candidate is not null && Accepts(attempt, candidate, year)) return new MovieInfo(candidate.Title, candidate.Year, candidate.PosterUrl);
        }
        return null;
    }

    /// <summary>
    /// Höchstens vier Abfragen je Film (das Tageslimit ist knapp), in dieser Reihenfolge: der Titel mit Jahr; mit vertauschter
    /// Zahlenschreibweise („Ghostbusters 2“ → „Ghostbusters II“); ohne Folgenummer („Matrix 2 Reloaded“ → „Matrix Reloaded“); ohne Jahr,
    /// dann darf das Jahr um eins abweichen. Jede Antwort muss danach den Titel-Vergleich bestehen (<see cref="Accepts"/>).
    /// </summary>
    private static IEnumerable<Attempt> Attempts(string title, int? year)
    {
        yield return new Attempt(title, year, false, 0);
        if (PosterNames.WithSwappedNumbers(title) is { } swapped) yield return new Attempt(swapped, year, false, 0);
        if (PosterNames.WithoutSequelNumbers(title) is { } withoutNumbers) yield return new Attempt(withoutNumbers, year, true, 0);
        if (year is not null) yield return new Attempt(title, null, false, 1);
    }

    private static bool Accepts(Attempt attempt, Candidate candidate, int? year)
    {
        // Gleicher Titel im Sinne von MatchKey: „The Matrix Reloaded“ = „Matrix Reloaded“, „Ghostbusters II“ = „Ghostbusters 2“.
        if (PosterNames.MatchKey(attempt.Query) != PosterNames.MatchKey(candidate.Title)) return false;

        // Ohne Folgenummer ist „Ghostbusters“ der erste Film, nicht der zweite: ohne bekanntes Jahr nur bei mindestens zwei Wörtern.
        if (attempt.SequelNumbersDropped && year is null && PosterNames.Tokens(attempt.Query).Count < 2) return false;

        return year is not { } wanted || (candidate.Year is { } found && Math.Abs(found - wanted) <= attempt.YearTolerance);
    }

    private async Task<Candidate?> QueryAsync(Attempt attempt, CancellationToken ct)
    {
        var url = $"https://www.omdbapi.com/?apikey={Uri.EscapeDataString(apiKey)}&type=movie&t={Uri.EscapeDataString(attempt.Query)}"
                  + (attempt.YearParam is { } y ? $"&y={y.ToString(CultureInfo.InvariantCulture)}" : "");
        using var doc = await GetJsonAsync(url, ct);
        var root = doc.RootElement;

        if (!Text(root, "Response").Equals("True", StringComparison.OrdinalIgnoreCase))
        {
            var error = Text(root, "Error");
            if (error.Equals("Movie not found!", StringComparison.OrdinalIgnoreCase)) return null;
            throw new PosterSourceUnavailableException($"OMDb: {(error.Length > 0 ? error : "unbekannte Antwort")}");
        }

        int? year = Text(root, "Year") is { Length: >= 4 } text && int.TryParse(text.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        // „N/A“ heißt: OMDb kennt den Film, aber kein Bild. Nur https-Adressen: ein Link aus einer fremden Antwort darf nicht auf file: zeigen.
        var poster = Uri.TryCreate(Text(root, "Poster"), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.ToString() : null;
        return new Candidate(Text(root, "Title"), year, poster);
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var wait = _lastCall + (minGap ?? DefaultGap) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);

            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            _lastCall = DateTime.UtcNow;
            var bytes = await HttpFetch.ReadLimitedAsync(response, new Uri("https://www.omdbapi.com/"), MaxJsonBytes, ct);

            if (!response.IsSuccessStatusCode)
            {
                // Bei falschem Schlüssel und erreichtem Tageslimit antwortet OMDb mit 401 und einem JSON-Text; der steht in der Meldung (nie die Adresse).
                var reason = TryReadError(bytes);
                if ((int)response.StatusCode is 401 or 403) throw new PosterSourceUnavailableException($"OMDb: {reason ?? $"{(int)response.StatusCode} {response.ReasonPhrase}"}");
                throw new HttpRequestException($"www.omdbapi.com antwortete mit {(int)response.StatusCode} {response.ReasonPhrase}.");
            }
            return JsonDocument.Parse(bytes);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? TryReadError(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return Text(doc.RootElement, "Error") is { Length: > 0 } error ? error : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
