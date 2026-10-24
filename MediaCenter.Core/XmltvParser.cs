using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace MediaCenter.Core;

/// <summary>Ein Sender im Programmführer.</summary>
public sealed record XmltvChannel(string Id, IReadOnlyList<string> DisplayNames);

/// <summary>Eine Sendung im Programmführer.</summary>
public sealed record XmltvProgramme(string ChannelId, DateTime StartUtc, DateTime StopUtc, string Title, string? Description);

/// <param name="Skipped">Sendungen mit unlesbarer Zeit oder ohne Titel, die nicht übernommen wurden.</param>
public sealed record XmltvProgrammes(IReadOnlyList<XmltvProgramme> Items, int Skipped);

/// <summary>
/// Liest Programmführer im XMLTV-Format (auch gzip-gepackt). Die Datei stammt von einem fremden Server: Dokumenttypen (DTD) sind verboten, gepackte Dateien
/// werden nur bis <see cref="MaxExpandedBytes"/> entpackt, und Titel und Beschreibung werden gekürzt. Gelesen wird als Strom; behalten werden nur Sendungen, die
/// ein gefragter Sender hat und die im gefragten Zeitraum laufen.
/// </summary>
public static class XmltvParser
{
    /// <summary>So viel entpackter Text wird höchstens gelesen (große Führer haben um die 300 MB).</summary>
    public const long MaxExpandedBytes = 1_500_000_000;

    private const int MaxTitleLength = 300;
    private const int MaxDescriptionLength = 2000;

    /// <summary>Öffnet die Daten, entpackt sie bei Bedarf (gzip erkennt es am Anfang) und begrenzt die entpackte Größe.</summary>
    public static Stream Open(byte[] bytes)
    {
        Stream stream = new MemoryStream(bytes, writable: false);
        if (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B) stream = new GZipStream(stream, CompressionMode.Decompress);
        return new LimitedStream(stream, MaxExpandedBytes);
    }

    private static XmlReader Reader(Stream stream) => XmlReader.Create(stream, new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreWhitespace = true,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    });

    /// <summary>Die Sender des Führers samt ihren Anzeigenamen; liest nur bis zur ersten Sendung (Sender stehen in XMLTV vorn).</summary>
    public static IReadOnlyList<XmltvChannel> ReadChannels(Stream stream)
    {
        var channels = new List<XmltvChannel>();
        using var reader = Reader(stream);
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "programme") break;
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "channel")
            {
                var element = (XElement)XNode.ReadFrom(reader);
                if (element.Attribute("id")?.Value is { Length: > 0 } id)
                {
                    var names = element.Elements("display-name").Select(e => e.Value.Trim()).Where(n => n.Length > 0).ToList();
                    channels.Add(new XmltvChannel(id, names));
                }
                continue;
            }
            reader.Read();
        }
        return channels;
    }

    /// <summary>
    /// Die Sendungen der gefragten Sender, die zwischen <paramref name="fromUtc"/> und <paramref name="toUtc"/> laufen (eine Sendung, die schon läuft,
    /// zählt; eine, die zu Ende ist, nicht).
    /// </summary>
    /// <param name="wanted">Fragt, ob die Sendungen dieses Senders gebraucht werden (die Kennung wie im Führer).</param>
    public static XmltvProgrammes ReadProgrammes(Stream stream, Func<string, bool> wanted, DateTime fromUtc, DateTime toUtc)
    {
        var items = new List<XmltvProgramme>();
        var skipped = 0;
        using var reader = Reader(stream);
        while (!reader.EOF)
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "programme")
            {
                reader.Read();
                continue;
            }

            var element = (XElement)XNode.ReadFrom(reader);
            if (element.Attribute("channel")?.Value is not { Length: > 0 } channel || !wanted(channel)) continue;

            var start = ParseTime(element.Attribute("start")?.Value);
            var stop = ParseTime(element.Attribute("stop")?.Value);
            var title = Text(element, "title", MaxTitleLength);
            if (start is null || title is null || (stop is not null && stop <= start))
            {
                skipped++;
                continue;
            }
            // Ohne Ende (manche Führer lassen es weg) gilt die Sendung bis zum Anfang der nächsten; hier bis zu einer Stunde.
            var end = stop ?? start.Value.AddHours(1);
            if (end <= fromUtc || start >= toUtc) continue;

            items.Add(new XmltvProgramme(channel, start.Value, end, title, Text(element, "desc", MaxDescriptionLength)));
        }
        return new XmltvProgrammes(items, skipped);
    }

    /// <summary>Der erste nicht leere Text des Elements, bei mehreren Sprachen der deutsche; auf <paramref name="max"/> Zeichen gekürzt.</summary>
    private static string? Text(XElement parent, string name, int max)
    {
        var candidates = parent.Elements(name).Where(e => e.Value.Trim().Length > 0).ToList();
        var chosen = candidates.FirstOrDefault(e => string.Equals(e.Attribute("lang")?.Value, "de", StringComparison.OrdinalIgnoreCase)) ?? candidates.FirstOrDefault();
        if (chosen is null) return null;
        var text = chosen.Value.Trim();
        return text.Length > max ? text[..max] : text;
    }

    /// <summary>
    /// „20261024180000 +0200“ (auch ohne Sekunden, ohne Versatz = UTC, „Z“) → UTC. Gibt null zurück, wenn die Angabe nicht lesbar ist.
    /// </summary>
    public static DateTime? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var parts = value.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var digits = parts[0];
        var offset = parts.Length > 1 ? parts[1].Trim() : "";
        if (digits.EndsWith('Z'))
        {
            digits = digits[..^1];
            offset = "+0000";
        }

        string[] formats = { "yyyyMMddHHmmss", "yyyyMMddHHmm", "yyyyMMdd" };
        if (!DateTime.TryParseExact(digits, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return null;

        var minutes = 0;
        if (offset.Length > 0)
        {
            if (offset.Length != 5 || offset[0] is not ('+' or '-')
                || !int.TryParse(offset.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var h)
                || !int.TryParse(offset.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var m)
                || h > 14 || m > 59)
            {
                return null;
            }
            minutes = (h * 60 + m) * (offset[0] == '-' ? -1 : 1);
        }
        return DateTime.SpecifyKind(local, DateTimeKind.Utc).AddMinutes(-minutes);
    }

    /// <summary>Bricht ab, wenn aus einer kleinen gepackten Datei mehr als erlaubt herauskommt (Zip-Bombe).</summary>
    private sealed class LimitedStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            _read += read;
            if (_read > limit) throw new InvalidDataException($"Der Programmführer ist entpackt größer als {limit / 1_000_000} MB.");
            return read;
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
