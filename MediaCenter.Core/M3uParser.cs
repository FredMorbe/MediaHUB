using System.Text.RegularExpressions;

namespace MediaCenter.Core;

/// <summary>Ein Sender einer Senderliste (M3U), wie die Datei ihn beschreibt.</summary>
/// <param name="Group">Gruppe („group-title“ oder „#EXTGRP“), z. B. „Nachrichten“; null, wenn keine angegeben ist.</param>
/// <param name="TvgId">Kennung im Programmführer („tvg-id“).</param>
/// <param name="LogoUrl">http- oder https-Adresse des Logos („tvg-logo“).</param>
/// <param name="Number">Senderplatz („tvg-chno“).</param>
public sealed record M3uChannel(string Name, string Url, string? Group, string? TvgId, string? LogoUrl, int? Number);

/// <param name="EpgUrl">Programmführer, den die Liste selbst nennt („url-tvg“ oder „x-tvg-url“); null, wenn keiner genannt ist.</param>
/// <param name="Skipped">Einträge, die nicht übernommen wurden (Adresse mit unbekanntem Schema).</param>
public sealed record M3uPlaylist(IReadOnlyList<M3uChannel> Channels, string? EpgUrl, int Skipped);

/// <summary>
/// Liest Senderlisten im M3U-Format (IPTV). Die Datei stammt von einem fremden Server und gilt als unvertrauenswürdig: Nur Adressen, die mpv
/// aus dem Netz abspielt (http, https, rtmp, rtsp, udp, rtp, srt), werden übernommen; <c>file:</c>, <c>edl:</c> und Ähnliches fliegen raus.
/// </summary>
public static class M3uParser
{
    /// <summary>So viele Sender liest der Parser höchstens (große Listen haben um die 20 000).</summary>
    public const int MaxChannels = 200_000;

    private static readonly HashSet<string> Schemes = new(StringComparer.OrdinalIgnoreCase)
    {
        Uri.UriSchemeHttp, Uri.UriSchemeHttps, "rtmp", "rtmps", "rtsp", "udp", "rtp", "srt",
    };

    private static readonly Regex Attribute = new("([A-Za-z0-9_-]+)=\"([^\"]*)\"", RegexOptions.CultureInvariant);

    /// <summary>Ist das eine Adresse, die mpv aus dem Netz abspielt?</summary>
    public static bool IsPlayableUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && Schemes.Contains(uri.Scheme);

    public static M3uPlaylist Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var channels = new List<M3uChannel>();
        string? epgUrl = null;
        var skipped = 0;
        PendingEntry? pending = null;
        string? pendingGroup = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            {
                var attributes = Attributes(line);
                epgUrl ??= FirstWeb(First(attributes, "url-tvg") ?? First(attributes, "x-tvg-url"));
                continue;
            }
            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                pending = ReadInfo(line["#EXTINF:".Length..]);
                continue;
            }
            if (line.StartsWith("#EXTGRP:", StringComparison.OrdinalIgnoreCase))
            {
                pendingGroup = line["#EXTGRP:".Length..].Trim() is { Length: > 0 } group ? group : null;
                continue;
            }
            if (line[0] == '#') continue; // EXTVLCOPT, EXT-X-… und Kommentare

            if (!IsPlayableUrl(line))
            {
                skipped++;
                pending = null;
                pendingGroup = null;
                continue;
            }

            // Eine Adresse ohne #EXTINF davor ist eine einfache Liste: der Name ist dann die Adresse (siehe unten).
            var entry = pending ?? new PendingEntry("", null, null, null, null);
            if (channels.Count >= MaxChannels) throw new InvalidDataException($"Die Senderliste hat mehr als {MaxChannels} Sender.");
            // Ein Eintrag ohne Namen heißt wie sein Server samt Pfad.
            var name = entry.Name.Length > 0 ? entry.Name : new Uri(line).Host + new Uri(line).AbsolutePath;
            channels.Add(new M3uChannel(name, line, entry.Group ?? pendingGroup, entry.TvgId, entry.LogoUrl, entry.Number));
            pending = null;
            pendingGroup = null;
        }
        return new M3uPlaylist(channels, epgUrl, skipped);
    }

    private sealed record PendingEntry(string Name, string? Group, string? TvgId, string? LogoUrl, int? Number);

    /// <summary>„-1 tvg-id="x" group-title="y",Name“ → die Angaben und der Name hinter dem ersten Komma außerhalb von Anführungszeichen.</summary>
    private static PendingEntry ReadInfo(string info)
    {
        var comma = -1;
        var inQuotes = false;
        for (var i = 0; i < info.Length; i++)
        {
            if (info[i] == '"') inQuotes = !inQuotes;
            else if (info[i] == ',' && !inQuotes)
            {
                comma = i;
                break;
            }
        }
        var meta = comma < 0 ? info : info[..comma];
        var title = comma < 0 ? "" : info[(comma + 1)..].Trim();

        var attributes = Attributes(meta);
        var name = title.Length > 0 ? title : First(attributes, "tvg-name") ?? "";
        int? number = int.TryParse(First(attributes, "tvg-chno"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
        return new PendingEntry(name, First(attributes, "group-title"), First(attributes, "tvg-id"), Web(First(attributes, "tvg-logo")), number);
    }

    private static Dictionary<string, string> Attributes(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Attribute.Matches(text)) map.TryAdd(match.Groups[1].Value, match.Groups[2].Value);
        return map;
    }

    private static string? First(Dictionary<string, string> attributes, string name) =>
        attributes.TryGetValue(name, out var value) && value.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    /// <summary>Nur http und https: ein Logo oder Programmführer aus einer fremden Liste darf nicht auf eine lokale Datei zeigen.</summary>
    private static string? Web(string? url) =>
        url is not null && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.ToString() : null;

    /// <summary>Der Programmführer der Liste; nennt sie mehrere („a,b“), gilt der erste.</summary>
    private static string? FirstWeb(string? urls) => Web(urls?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault());
}
