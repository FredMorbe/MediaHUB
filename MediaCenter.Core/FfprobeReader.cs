using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace MediaCenter.Core;

public sealed record StreamInfo(
    int Index, string Type, string? Codec, string? Profile, int? Width, int? Height, int? Channels,
    string? Language, string? Title, bool IsDefault, bool IsForced, string? ColorTransfer);

/// <param name="Tags">Container-Tags (Titel, Künstler …), Schlüssel ohne Beachtung der Groß-/Kleinschreibung; null, wenn nicht gelesen.</param>
public sealed record ProbeResult(
    double? DurationSeconds, string? Container, IReadOnlyList<StreamInfo> Streams, IReadOnlyDictionary<string, string>? Tags = null)
{
    public bool HasAudio => Streams.Any(s => s.Type == "audio");

    public StreamInfo? Video => Streams.FirstOrDefault(s => s.Type == "video");

    public IEnumerable<StreamInfo> AttachedPictures => Streams.Where(s => s.Type == FfprobeReader.AttachedPictureType);

    public bool IsHdr => Video?.ColorTransfer is "smpte2084" or "arib-std-b67";
}

public interface IMediaProbe
{
    Task<ProbeResult> ProbeAsync(string file, CancellationToken ct = default);
}

/// <summary>Liest Container- und Streaminformationen über ffprobe.</summary>
public sealed class FfprobeReader(string ffprobePath) : IMediaProbe
{
    public const string AttachedPictureType = "attached_pic";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>Pfad aus MEDIACENTER_FFPROBE, sonst "ffprobe" aus dem PATH.</summary>
    public static FfprobeReader FromEnvironment() =>
        new(Environment.GetEnvironmentVariable("MEDIACENTER_FFPROBE") ?? "ffprobe");

    public async Task<ProbeResult> ProbeAsync(string file, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(ffprobePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // ffprobe schreibt UTF-8; ohne Angabe läse .NET die Ausgabe in der OEM-Codepage (Umlaute in Tags würden zu "├ƒ").
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", file })
        {
            psi.ArgumentList.Add(arg);
        }

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (Win32Exception ex)
        {
            throw new FileNotFoundException(
                $"ffprobe konnte nicht gestartet werden ('{ffprobePath}'). Im PATH bereitstellen oder MEDIACENTER_FFPROBE setzen.", ex);
        }

        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                var json = await stdout;
                var error = await stderr;
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException($"ffprobe Exit-Code {process.ExitCode} für '{file}': {error.Trim()}");
                }
                return Parse(json);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true); // auch bei Abbruch von außen: ein liegengebliebenes ffprobe hielte die Datei offen
                if (ct.IsCancellationRequested) throw;
                throw new TimeoutException($"ffprobe antwortete für '{file}' nicht innerhalb von {Timeout.TotalSeconds:0} s.");
            }
        }
    }

    public static ProbeResult Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        double? duration = null;
        string? container = null;
        var tagMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("format", out var format))
        {
            if (format.TryGetProperty("duration", out var d) &&
                double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                duration = seconds;
            }
            container = Str(format, "format_name");
            ReadTags(format, tagMap);
        }

        var streams = new List<StreamInfo>();
        if (root.TryGetProperty("streams", out var array))
        {
            foreach (var s in array.EnumerateArray())
            {
                string? language = null, title = null;
                if (s.TryGetProperty("tags", out var tags))
                {
                    // Ogg und Opus tragen die Tags am Audiostream statt am Container; Container-Tags haben Vorrang.
                    if (Str(s, "codec_type") == "audio") ReadTags(s, tagMap);
                    foreach (var tag in tags.EnumerateObject())
                    {
                        if (tag.Name.Equals("language", StringComparison.OrdinalIgnoreCase)) language = tag.Value.GetString();
                        else if (tag.Name.Equals("title", StringComparison.OrdinalIgnoreCase)) title = tag.Value.GetString();
                    }
                }

                var isDefault = false;
                var isForced = false;
                var isAttachedPicture = false;
                if (s.TryGetProperty("disposition", out var disposition))
                {
                    isDefault = Int(disposition, "default") == 1;
                    isForced = Int(disposition, "forced") == 1;
                    isAttachedPicture = Int(disposition, "attached_pic") == 1;
                }

                streams.Add(new StreamInfo(
                    Index: Int(s, "index") ?? throw new InvalidDataException("ffprobe-Stream ohne index."),
                    // Eingebettete Cover (MKV-Anhänge, Audio-Tags) meldet ffprobe als Video-Stream; sie sind keine Videospur.
                    Type: isAttachedPicture ? AttachedPictureType : Str(s, "codec_type") ?? "unknown",
                    Codec: Str(s, "codec_name"),
                    Profile: Str(s, "profile"),
                    Width: Int(s, "width"),
                    Height: Int(s, "height"),
                    Channels: Int(s, "channels"),
                    Language: language,
                    Title: title,
                    IsDefault: isDefault,
                    IsForced: isForced,
                    ColorTransfer: Str(s, "color_transfer")));
            }
        }

        return new ProbeResult(duration, container, streams, tagMap);
    }

    private static void ReadTags(JsonElement owner, Dictionary<string, string> into)
    {
        if (!owner.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object) return;
        foreach (var tag in tags.EnumerateObject())
        {
            if (tag.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(tag.Value.GetString()))
            {
                into.TryAdd(tag.Name, tag.Value.GetString()!.Trim());
            }
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
}
