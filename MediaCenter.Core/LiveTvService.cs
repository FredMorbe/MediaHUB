using System.IO.Compression;
using System.Text;

namespace MediaCenter.Core;

/// <summary>
/// Senderlisten (M3U) und Programmführer (XMLTV) laden und in der Datenbank ablegen. Listen und Führer kommen von einer Web-Adresse (http, https) oder aus einer Datei.
/// Die Adressen von IPTV-Anbietern enthalten oft Zugangsdaten: sie stehen in keiner Meldung dieser Klasse.
/// </summary>
public sealed class LiveTvService(LibraryDb db, HttpClient http)
{
    private const long MaxPlaylistBytes = 80_000_000;
    private const long MaxGuideBytes = 250_000_000;
    private const long MaxPlaylistExpandedBytes = 400_000_000;

    /// <summary>So weit zurück bleiben Sendungen im Führer (laufende gehören dazu) und so weit voraus werden sie geladen.</summary>
    public static readonly TimeSpan GuidePast = TimeSpan.FromHours(3);

    public static readonly TimeSpan GuideFuture = TimeSpan.FromDays(7);

    /// <summary>Hinweise auf die Bildqualität im Sendernamen, die für den Vergleich mit dem Programmführer nichts bedeuten.</summary>
    private static readonly HashSet<string> QualityTags = new(StringComparer.Ordinal) { "hd", "fhd", "uhd", "sd", "4k", "hevc", "h265", "h264", "raw", "hq" };

    /// <summary>Länderkürzel vor dem Namen („DE: Das Erste“), die der Programmführer nicht führt.</summary>
    private static readonly HashSet<string> CountryTags = new(StringComparer.Ordinal) { "de", "deu", "ger", "at", "ch", "uk", "us", "fr", "it", "es", "tr" };

    /// <summary>
    /// Schlüssel für „ist derselbe Sender“: ohne Akzente, Satzzeichen, Großschreibung, Qualitätshinweise und führendes Länderkürzel
    /// („DE | Das Erste HD“ = „Das Erste“).
    /// </summary>
    public static string NameKey(string name)
    {
        var tokens = PosterNames.Tokens(name).Where(t => !QualityTags.Contains(t)).ToList();
        if (tokens.Count > 1 && CountryTags.Contains(tokens[0])) tokens.RemoveAt(0);
        return string.Join(' ', tokens);
    }

    /// <summary>Legt eine Senderliste an. Ist die Liste nicht lesbar oder leer, wird nichts angelegt.</summary>
    /// <param name="epgLocation">Programmführer; ohne Angabe gilt der, den die Liste selbst nennt (falls sie einen nennt).</param>
    /// <exception cref="ArgumentException">Die Adresse ist weder eine Web-Adresse noch eine vorhandene Datei.</exception>
    /// <exception cref="InvalidDataException">Die Liste enthält keinen abspielbaren Sender.</exception>
    /// <exception cref="InvalidOperationException">Diese Liste ist schon eingetragen.</exception>
    public async Task<(LiveSource Source, LiveRefresh Refresh)> AddSourceAsync(string? name, string location, string? epgLocation, CancellationToken ct = default)
    {
        location = location.Trim();
        var playlist = await LoadPlaylistAsync(location, ct);
        if (playlist.Channels.Count == 0) throw new InvalidDataException("Die Liste enthält keinen abspielbaren Sender (erwartet wird eine M3U-Datei mit Adressen).");

        var guide = string.IsNullOrWhiteSpace(epgLocation) ? playlist.EpgUrl : epgLocation.Trim();
        if (guide is not null) RequireLocation(guide);
        var id = db.AddLiveSource(string.IsNullOrWhiteSpace(name) ? DefaultName(location) : name.Trim(), location, guide);
        var refresh = db.ReplaceLiveChannels(id, playlist.Channels, playlist.Skipped);
        return (db.GetLiveSource(id)!, refresh);
    }

    /// <summary>Lädt die Liste neu: neue Sender kommen dazu, verschwundene gehen, Favoriten bleiben.</summary>
    public async Task<LiveRefresh> RefreshSourceAsync(long sourceId, CancellationToken ct = default)
    {
        var source = db.GetLiveSource(sourceId) ?? throw new InvalidOperationException($"Senderliste {sourceId} existiert nicht (mehr).");
        var playlist = await LoadPlaylistAsync(source.Location, ct);
        // Eine leere Antwort (Anbieter gerade gestört) darf die Liste nicht leeren.
        if (playlist.Channels.Count == 0) throw new InvalidDataException("Die geladene Liste enthält keinen abspielbaren Sender; die bisherigen Sender bleiben.");
        return db.ReplaceLiveChannels(sourceId, playlist.Channels, playlist.Skipped);
    }

    /// <summary>
    /// Lädt den Programmführer der Liste und ordnet ihn den Sendern zu: erst über die „tvg-id“ der Liste, sonst über den Namen (ohne Qualitätshinweise).
    /// Behalten werden nur Sendungen der zugeordneten Sender im Zeitraum von <see cref="GuidePast"/> bis <see cref="GuideFuture"/> um <paramref name="nowUtc"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Für die Liste ist kein Programmführer eingetragen.</exception>
    public async Task<LiveGuideRefresh> RefreshGuideAsync(long sourceId, DateTime? nowUtc = null, CancellationToken ct = default)
    {
        var source = db.GetLiveSource(sourceId) ?? throw new InvalidOperationException($"Senderliste {sourceId} existiert nicht (mehr).");
        if (source.EpgLocation is not { } guideLocation) throw new InvalidOperationException("Für diese Senderliste ist kein Programmführer (XMLTV) eingetragen.");

        var now = nowUtc ?? DateTime.UtcNow;
        var bytes = await LoadBytesAsync(guideLocation, MaxGuideBytes, ct);

        IReadOnlyList<XmltvChannel> guideChannels;
        using (var stream = XmltvParser.Open(bytes)) guideChannels = XmltvParser.ReadChannels(stream);

        // Kennung im Führer in der Schreibweise der Datei; Vergleich ohne Großschreibung.
        var byId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var guideChannel in guideChannels)
        {
            byId.TryAdd(guideChannel.Id, guideChannel.Id);
            foreach (var displayName in guideChannel.DisplayNames.Append(guideChannel.Id))
            {
                if (NameKey(displayName) is { Length: > 0 } key) byName.TryAdd(key, guideChannel.Id);
            }
        }

        var channels = db.ListLiveChannels(new LiveChannelFilter(SourceId: sourceId));
        var assignments = new Dictionary<long, string?>();
        foreach (var channel in channels)
        {
            string? epgId = null;
            if (channel.TvgId is { Length: > 0 } tvg && byId.TryGetValue(tvg, out var byTvg)) epgId = byTvg;
            else if (NameKey(channel.Name) is { Length: > 0 } nameKey && byName.TryGetValue(nameKey, out var byDisplay)) epgId = byDisplay;
            if (epgId is not null) assignments[channel.Id] = epgId;
        }

        var wanted = new HashSet<string>(assignments.Values.OfType<string>(), StringComparer.OrdinalIgnoreCase);
        XmltvProgrammes programmes;
        using (var stream = XmltvParser.Open(bytes))
        {
            programmes = XmltvParser.ReadProgrammes(stream, wanted.Contains, now - GuidePast, now + GuideFuture);
        }

        // Die Sendungen heißen unter der Kennung, die die Zuordnung führt (gleiche Schreibweise wie in der Datei).
        var items = programmes.Items.Select(p => byId.TryGetValue(p.ChannelId, out var canonical) ? p with { ChannelId = canonical } : p).ToList();
        db.SetLiveEpgIds(sourceId, assignments);
        db.ReplaceLiveGuide(sourceId, items);
        db.PruneLiveGuide(now - GuidePast);
        return new LiveGuideRefresh(channels.Count, assignments.Count, items.Count, programmes.Skipped);
    }

    // --- Laden ---------------------------------------------------------------------------------------------

    private async Task<M3uPlaylist> LoadPlaylistAsync(string location, CancellationToken ct)
    {
        var bytes = await LoadBytesAsync(location, MaxPlaylistBytes, ct);
        if (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
        {
            using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var expanded = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await gzip.ReadAsync(chunk, ct)) > 0)
            {
                if (expanded.Length + read > MaxPlaylistExpandedBytes) throw new InvalidDataException("Die Senderliste ist entpackt zu groß.");
                expanded.Write(chunk, 0, read);
            }
            bytes = expanded.ToArray();
        }
        return await Task.Run(() => M3uParser.Parse(new UTF8Encoding(false).GetString(bytes)), ct);
    }

    /// <summary>Web-Adresse oder Datei als Bytes; Meldungen nennen den Rechnernamen, nie die ganze Adresse (Zugangsdaten).</summary>
    private async Task<byte[]> LoadBytesAsync(string location, long maxBytes, CancellationToken ct)
    {
        if (Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            return await HttpFetch.GetBytesAsync(http, uri, maxBytes, ct);
        }
        if (!File.Exists(location)) throw new FileNotFoundException("Die Datei der Senderliste gibt es nicht (mehr).", Path.GetFileName(location));

        var length = new FileInfo(location).Length;
        if (length > maxBytes) throw new InvalidDataException($"Die Datei ist mit {length / 1_000_000} MB zu groß (Limit {maxBytes / 1_000_000} MB).");
        return await File.ReadAllBytesAsync(location, ct);
    }

    /// <summary>Eine Adresse ist eine Web-Adresse (http, https) oder eine vorhandene Datei, sonst gilt sie nicht.</summary>
    private static void RequireLocation(string location)
    {
        if (Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return;
        if (File.Exists(location)) return;
        throw new ArgumentException("Das ist weder eine Web-Adresse (http oder https) noch eine vorhandene Datei.", nameof(location));
    }

    private static string DefaultName(string location) =>
        Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.Host : Path.GetFileNameWithoutExtension(location);
}
