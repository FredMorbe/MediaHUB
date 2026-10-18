using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MediaCenter.Core;

/// <summary>Findet im Internet den Text zu einem Titel. Gibt null zurück, wenn es keinen gibt (kein Fehler).</summary>
public interface ILyricsSource
{
    /// <summary>Kennung des Anbieters; sie steht als Herkunft im Songtext.</summary>
    string Provider { get; }

    /// <returns>Der Text als LRC (mit Zeitmarken) oder einfacher Text; null, wenn es keinen gibt oder das Stück rein instrumental ist.</returns>
    Task<string?> FindAsync(string artist, string title, string? album, double? durationSeconds, CancellationToken ct = default);
}

/// <summary>
/// Songtexte von LRCLIB (lrclib.net/api, ohne Schlüssel, offene Datenbank mit zeitgesteuerten Texten). Gesendet werden Künstler, Titel, Album und Dauer.
/// LRCLIB trifft nur, wenn die Dauer auf ein paar Sekunden stimmt; das schützt vor Texten eines anderen Stücks gleichen Namens.
/// </summary>
/// <param name="contact">Kontakt für den User-Agent (LRCLIB bittet um die Angabe des Programms), z. B. die Projektadresse.</param>
public sealed class LrcLibClient(HttpClient http, string contact) : ILyricsSource
{
    private const long MaxJsonBytes = 1_000_000;

    public string Provider => "LRCLIB";

    public async Task<string?> FindAsync(string artist, string title, string? album, double? durationSeconds, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist)) throw new ArgumentException("Künstler fehlt.", nameof(artist));
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Titel fehlt.", nameof(title));
        if (string.IsNullOrWhiteSpace(contact)) throw new ArgumentException("Kontakt für den User-Agent fehlt.", nameof(contact));

        var url = new StringBuilder("https://lrclib.net/api/get?artist_name=").Append(Uri.EscapeDataString(artist.Trim()))
            .Append("&track_name=").Append(Uri.EscapeDataString(title.Trim()));
        if (!string.IsNullOrWhiteSpace(album)) url.Append("&album_name=").Append(Uri.EscapeDataString(album.Trim()));
        if (durationSeconds is > 0) url.Append("&duration=").Append(Math.Round(durationSeconds.Value).ToString(CultureInfo.InvariantCulture));

        using var request = new HttpRequestMessage(HttpMethod.Get, url.ToString());
        // Ein User-Agent an der Anfrage ersetzt den des Clients; deshalb dessen Teile zuerst übernehmen, dann den Kontakt anhängen.
        foreach (var product in http.DefaultRequestHeaders.UserAgent) request.Headers.UserAgent.Add(product);
        request.Headers.UserAgent.ParseAdd($"({contact.Trim()})");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"lrclib.net antwortete mit {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        using var json = JsonDocument.Parse(await HttpFetch.ReadLimitedAsync(response, request.RequestUri!, MaxJsonBytes, ct));
        var root = json.RootElement;
        if (root.TryGetProperty("instrumental", out var instrumental) && instrumental.ValueKind == JsonValueKind.True) return null;
        return Text(root, "syncedLyrics") ?? Text(root, "plainLyrics");
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;
}

/// <summary>
/// Songtext zu einem Titel: Datei neben dem Titel (<c>.lrc</c>), sonst ein in der Datei eingebetteter Text, sonst der gemerkte oder frisch
/// aus dem Internet geholte. Treffer und Absagen aus dem Internet liegen im Zwischenspeicher, damit nicht bei jedem Abspielen gefragt wird.
/// </summary>
public sealed class LyricsService(IMediaProbe probe, ILyricsSource? online, string cacheDir)
{
    /// <summary>Wie lange eine Absage des Internets gilt, bevor neu gefragt wird.</summary>
    public static readonly TimeSpan NoMatchRetry = TimeSpan.FromDays(14);

    public string CacheDir => cacheDir;

    /// <summary>Der Internet-Anbieter; null schaltet die Suche im Internet aus (Dateien, Tags und der Zwischenspeicher gelten weiter).</summary>
    public ILyricsSource? Online { get; set; } = online;

    /// <param name="trackId">Schlüssel im Zwischenspeicher.</param>
    /// <param name="audioPath">Ort der Audiodatei: daneben liegt ggf. die <c>.lrc</c>.</param>
    /// <returns>null, wenn es keinen Text gibt.</returns>
    /// <exception cref="HttpRequestException">Der Internet-Anbieter ist nicht erreichbar oder antwortet mit einem Fehler.</exception>
    public async Task<Lyrics?> GetAsync(long trackId, string audioPath, string artist, string title, string? album, double? durationSeconds, CancellationToken ct = default)
    {
        var sidecar = Path.ChangeExtension(audioPath, ".lrc");
        if (File.Exists(sidecar) && Read(DecodeText(await File.ReadAllBytesAsync(sidecar, ct)), "Datei (.lrc)") is { } fromFile) return fromFile;

        if ((await probe.ProbeAsync(audioPath, ct)).Tags is { } tags
            && tags.FirstOrDefault(t => t.Key.StartsWith("lyrics", StringComparison.OrdinalIgnoreCase) || t.Key.Equals("unsyncedlyrics", StringComparison.OrdinalIgnoreCase)) is { Value: { } embedded }
            && Read(embedded, "Tag") is { } fromTag)
        {
            return fromTag;
        }

        var cached = Path.Combine(cacheDir, $"{trackId}.txt");
        var none = Path.Combine(cacheDir, $"{trackId}.none");
        if (File.Exists(cached) && Read(await File.ReadAllTextAsync(cached, Encoding.UTF8, ct), Online?.Provider ?? "Zwischenspeicher") is { } fromCache) return fromCache;
        // Ohne Künstler im Tag trifft die Suche zu oft ein anderes Stück gleichen Namens.
        if (Online is not { } source || string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)) return null;
        if (File.Exists(none) && DateTime.UtcNow - File.GetLastWriteTimeUtc(none) < NoMatchRetry) return null;

        var found = await source.FindAsync(artist, title, album, durationSeconds, ct);
        Directory.CreateDirectory(cacheDir);
        if (found is null || Read(found, source.Provider) is not { } lyrics)
        {
            await File.WriteAllTextAsync(none, "", ct);
            return null;
        }
        await File.WriteAllTextAsync(cached, found, new UTF8Encoding(false), ct);
        if (File.Exists(none)) File.Delete(none);
        return lyrics;
    }

    /// <summary>Dateien von Hand (alte Programme) sind oft nicht UTF-8: ungültiges UTF-8 wird als Latin-1 gelesen, statt Umlaute zu zerstören.</summary>
    private static string DecodeText(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>Liest den Text; ein leerer Text (nur Leerraum) ist kein Songtext.</summary>
    private static Lyrics? Read(string text, string source)
    {
        text = text.TrimStart('﻿');
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lyrics = LyricsParser.Parse(text, source);
        return lyrics.Lines.Any(l => l.Text.Length > 0) ? lyrics : null;
    }
}
