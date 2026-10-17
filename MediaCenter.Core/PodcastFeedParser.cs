using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace MediaCenter.Core;

/// <summary>Liest RSS-2.0-Podcast-Feeds mit iTunes-Erweiterung.</summary>
public static partial class PodcastFeedParser
{
    private static readonly XNamespace Itunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public static PodcastFeed Parse(string xml)
    {
        XDocument doc;
        try
        {
            // DTDs und externe Entitäten sind aus: ein Feed aus dem Netz darf keine Dateien lesen oder nachladen.
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            doc = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"Der Feed ist kein gültiges XML: {ex.Message}", ex);
        }

        var channel = doc.Root?.Element("channel")
            ?? throw new InvalidDataException("Der Feed enthält keinen RSS-Kanal (<channel>); es ist wohl kein Podcast-Feed.");

        var title = Text(channel.Element("title")) ?? throw new InvalidDataException("Der Feed hat keinen Titel.");
        var episodes = new List<FeedEpisode>();
        foreach (var item in channel.Elements("item"))
        {
            var enclosure = item.Element("enclosure");
            var audioUrl = enclosure?.Attribute("url")?.Value;
            if (audioUrl is null || !IsWebUrl(audioUrl)) continue; // Folge ohne abspielbare Adresse

            var guid = Text(item.Element("guid")) ?? audioUrl;
            episodes.Add(new FeedEpisode(
                guid,
                Text(item.Element("title")) ?? "(ohne Titel)",
                ParseDate(Text(item.Element("pubDate"))),
                audioUrl.Trim(),
                ParseDuration(Text(item.Element(Itunes + "duration"))),
                Plain(Text(item.Element("description")) ?? Text(item.Element(Itunes + "summary")))));
        }

        var image = channel.Element(Itunes + "image")?.Attribute("href")?.Value ?? Text(channel.Element("image")?.Element("url"));
        return new PodcastFeed(
            title,
            Text(channel.Element(Itunes + "author")) ?? Text(channel.Element("managingEditor")),
            Plain(Text(channel.Element("description")) ?? Text(channel.Element(Itunes + "summary"))),
            image is not null && IsWebUrl(image) ? image.Trim() : null,
            episodes);
    }

    private static bool IsWebUrl(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static string? Text(XElement? element)
    {
        var value = element?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>Beschreibungen enthalten oft HTML; für die Anzeige bleibt nur der Text.</summary>
    public static string? Plain(string? html)
    {
        if (html is null) return null;
        var text = System.Net.WebUtility.HtmlDecode(Tags().Replace(html, " "));
        text = Whitespace().Replace(text, " ").Trim();
        return text.Length == 0 ? null : text;
    }

    private static DateTime? ParseDate(string? text) =>
        text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date.UtcDateTime
            : null;

    /// <summary>"3600", "45:12" oder "1:02:03".</summary>
    public static double? ParseDuration(string? text)
    {
        if (text is null) return null;
        var parts = text.Split(':');
        if (parts.Length is < 1 or > 3) return null;

        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0) return null;
            total = total * 60 + value;
        }
        return total;
    }
}
