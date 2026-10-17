using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaCenter.Core;

/// <summary>Ollama (lokaler Modellserver) nicht erreichbar, Modell fehlt oder Antwort unbrauchbar. Die Meldung ist für den Nutzer gedacht.</summary>
public sealed class OllamaException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Ein Chatmodell, das auf eine Anweisung mit JSON antwortet.</summary>
public interface IChatModel
{
    /// <summary>Name des Modells; fließt in den Schlüssel des Zwischenstands ein (anderes Modell = neue Übersetzung).</summary>
    string Name { get; }

    /// <param name="jsonSchema">JSON-Schema, dem die Antwort entsprechen muss.</param>
    Task<string> ChatJsonAsync(string system, string user, string jsonSchema, CancellationToken ct);
}

public enum PlacementKind
{
    /// <summary>Das Modell ist gerade nicht geladen (vor der ersten Anfrage).</summary>
    NotLoaded,
    Gpu,
    Cpu,
    Mixed,
}

/// <param name="GpuPercent">Anteil des Modells im Grafikspeicher.</param>
public sealed record ModelPlacement(PlacementKind Kind, int GpuPercent)
{
    public string Describe() => Kind switch
    {
        PlacementKind.Gpu => "Grafikkarte",
        PlacementKind.Cpu => "Prozessor (CPU)",
        PlacementKind.Mixed => $"Grafikkarte zu {GpuPercent} %, Rest Prozessor",
        _ => "nicht geladen",
    };
}

/// <summary>Spricht mit einem lokalen Ollama (<c>http://localhost:11434</c>).</summary>
public sealed class OllamaClient(HttpClient http, Uri baseUrl)
{
    /// <summary>Die erste Anfrage lädt das Modell in den Speicher; bei großen Modellen dauert das mehrere Minuten.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Kleiner Kontext genügt (ein Block hat rund 2000 Token) und spart Grafikspeicher für größere Modelle.</summary>
    private const int ContextTokens = 4096;

    public static readonly JsonSerializerOptions JsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Mit abschließendem „/“, damit relative Pfade an die Adresse angehängt werden.</summary>
    public Uri BaseUrl { get; } = baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");

    /// <summary>Namen der installierten Modelle („qwen3:14b“).</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        using var doc = await GetAsync("api/tags", ct);
        return doc.RootElement.TryGetProperty("models", out var models)
            ? models.EnumerateArray().Select(m => m.GetProperty("name").GetString()!).Order(StringComparer.OrdinalIgnoreCase).ToList()
            : Array.Empty<string>();
    }

    /// <summary>Ob das (geladene) Modell im Grafikspeicher oder auf dem Prozessor läuft – auf der CPU dauert ein Film über eine Stunde.</summary>
    public async Task<ModelPlacement> GetPlacementAsync(string model, CancellationToken ct = default)
    {
        using var doc = await GetAsync("api/ps", ct);
        if (!doc.RootElement.TryGetProperty("models", out var models)) return new ModelPlacement(PlacementKind.NotLoaded, 0);

        foreach (var m in models.EnumerateArray())
        {
            if (!SameModel(m.GetProperty("name").GetString(), model)) continue;

            var size = m.GetProperty("size").GetInt64();
            var vram = m.TryGetProperty("size_vram", out var v) ? v.GetInt64() : 0;
            if (size <= 0) return new ModelPlacement(PlacementKind.NotLoaded, 0);

            var percent = (int)Math.Round(100.0 * Math.Min(vram, size) / size);
            return percent switch
            {
                0 => new ModelPlacement(PlacementKind.Cpu, 0),
                >= 98 => new ModelPlacement(PlacementKind.Gpu, 100),
                _ => new ModelPlacement(PlacementKind.Mixed, percent),
            };
        }
        return new ModelPlacement(PlacementKind.NotLoaded, 0);
    }

    public IChatModel Model(string model) => new Chat(this, model);

    private async Task<string> ChatAsync(string model, string system, string user, string jsonSchema, CancellationToken ct)
    {
        using var schema = JsonDocument.Parse(jsonSchema);
        var body = new
        {
            model,
            stream = false,
            think = false, // Denkmodelle würden sonst vor jeder Antwort überlegen: dreimal so viele Token für dieselbe Übersetzung
            format = schema.RootElement,
            keep_alive = "30m",
            options = new { temperature = 0, num_ctx = ContextTokens },
            messages = new[] { new { role = "system", content = system }, new { role = "user", content = user } },
        };

        // StringContent statt JsonContent: der Body geht mit Content-Length raus (nicht chunked), das verträgt jeder Server und Proxy.
        var json = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        using var response = await SendAsync(HttpMethod.Post, "api/chat", json, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw ErrorFor(response.StatusCode, text, model);

        try
        {
            using var doc = JsonDocument.Parse(text);
            var content = doc.RootElement.GetProperty("message").GetProperty("content").GetString()
                ?? throw new OllamaException("Ollama lieferte eine leere Antwort.");
            // Denkmodelle liefern ihre Überlegung vor der Antwort; sie gehört nicht zum Ergebnis.
            return Regex.Replace(content, @"<think>.*?</think>", "", RegexOptions.Singleline).Trim();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new OllamaException($"Ollama lieferte eine unerwartete Antwort: {Shorten(text)}", ex);
        }
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw ErrorFor(response.StatusCode, text, null);

        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new OllamaException($"Ollama lieferte eine unerwartete Antwort: {Shorten(text)}", ex);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(method, new Uri(BaseUrl, path)) { Content = content };
            return await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
        }
        catch (HttpRequestException ex)
        {
            throw new OllamaException($"Ollama ist unter {BaseUrl} nicht erreichbar. Läuft Ollama? ({ex.Message})", ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new OllamaException($"Ollama antwortete nicht innerhalb von {RequestTimeout.TotalMinutes:0} Minuten.");
        }
    }

    private static OllamaException ErrorFor(HttpStatusCode status, string body, string? model)
    {
        var message = ErrorText(body);
        if (status == HttpStatusCode.NotFound && model is not null)
        {
            return new OllamaException($"Das Modell „{model}“ ist in Ollama nicht installiert. Mit „ollama pull {model}“ herunterladen. ({message})");
        }
        return new OllamaException($"Ollama meldet Fehler {(int)status}: {message}");
    }

    private static string ErrorText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String) return error.GetString()!;
        }
        catch (JsonException)
        {
            // kein JSON: der Text selbst ist die Meldung
        }
        return Shorten(body);
    }

    /// <summary>„qwen3“ und „qwen3:latest“ sind dasselbe Modell.</summary>
    private static bool SameModel(string? a, string? b) =>
        string.Equals(WithTag(a), WithTag(b), StringComparison.OrdinalIgnoreCase);

    private static string WithTag(string? name) => name is null ? "" : name.Contains(':') ? name : name + ":latest";

    private static string Shorten(string text)
    {
        text = text.Trim();
        return text.Length <= 300 ? text : text[..300] + "…";
    }

    private sealed class Chat(OllamaClient client, string model) : IChatModel
    {
        public string Name => model;

        public Task<string> ChatJsonAsync(string system, string user, string jsonSchema, CancellationToken ct) =>
            client.ChatAsync(model, system, user, jsonSchema, ct);
    }
}
