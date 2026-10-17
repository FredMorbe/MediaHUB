using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MediaCenter.Core;

/// <param name="OriginalLeftBehind">Pfad der alten Datei, wenn sie sich nach dem Ersetzen nicht entfernen ließ (sonst null).</param>
public sealed record EmbedResult(string VideoPath, int SubtitleStreamIndex, string? OriginalLeftBehind);

/// <summary>
/// Bettet eine Untertitel-Datei ohne Neukodierung in eine MKV ein (ffmpeg, <c>-c copy</c>). Die neue Datei entsteht neben dem Original
/// unter einem Namen, den der Scan ignoriert, wird streng gegen das Original geprüft und erst dann an dessen Stelle gesetzt.
/// Jeder Fehler vorher lässt das Original unberührt.
/// </summary>
public sealed class MkvSubtitleEmbedder(string ffmpegPath, string ffprobePath, IMediaProbe probe)
{
    /// <summary>Endung der Arbeitsdatei; kein Videoformat, damit ein Scan sie nie als Video aufnimmt.</summary>
    public const string TempSuffix = ".embed-tmp";

    /// <summary>Endung, unter der das Original kurz liegt, während die neue Datei an seine Stelle rückt.</summary>
    public const string OldSuffix = ".embed-old";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Ein Cover-Bild, das ffmpeg als Videospur mit Merker „attached_pic“ liest (Anhang „cover.jpg“ in der MKV).</summary>
    private sealed record Cover(int StreamIndex, string FileName, string MimeType, string Path = "");

    public static MkvSubtitleEmbedder FromEnvironment() => new(
        Environment.GetEnvironmentVariable("MEDIACENTER_FFMPEG") ?? "ffmpeg",
        Environment.GetEnvironmentVariable("MEDIACENTER_FFPROBE") ?? "ffprobe",
        FfprobeReader.FromEnvironment());

    public static bool CanEmbedInto(string videoPath) => Path.GetExtension(videoPath).Equals(".mkv", StringComparison.OrdinalIgnoreCase);

    /// <param name="language">Kommt als ISO-639-2-Kürzel in die Spur (z. B. „deu“).</param>
    /// <param name="title">Name der Spur in Playern; null lässt ihn leer.</param>
    /// <param name="delaySeconds">Wird in die Zeitstempel der eingebetteten Spur eingerechnet (positiv = später).</param>
    /// <param name="discardOriginal">Beseitigt die alte Datei nach erfolgreichem Ersetzen (Papierkorb oder Löschen).</param>
    /// <param name="progress">0 bis 1.</param>
    public async Task<EmbedResult> EmbedAsync(
        string video, string subtitle, Language language, string? title, double delaySeconds,
        Action<string> discardOriginal, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!CanEmbedInto(video)) throw new NotSupportedException("Einbetten ist nur bei MKV-Dateien möglich.");
        if (!File.Exists(video)) throw new FileNotFoundException($"Videodatei nicht gefunden: {video}", video);
        if (!File.Exists(subtitle)) throw new FileNotFoundException($"Untertiteldatei nicht gefunden: {subtitle}", subtitle);

        var extension = Path.GetExtension(subtitle).ToLowerInvariant();
        if (!SubtitleStore.Extensions.Contains(extension)) throw new InvalidDataException($"„{extension}“ ist kein unterstütztes Untertitelformat.");
        if (!double.IsFinite(delaySeconds) || Math.Abs(delaySeconds) > SubtitleAdjustment.MaxDelaySeconds)
            throw new ArgumentOutOfRangeException(nameof(delaySeconds), delaySeconds, "Zeitversatz außerhalb des erlaubten Bereichs.");

        var before = await probe.ProbeAsync(video, ct);
        if (before.DurationSeconds is not > 0) throw new InvalidDataException("Die Dauer der Videodatei ist nicht lesbar; es wird nichts verändert.");
        var beforeChapters = await CountChaptersAsync(video, ct);
        var originalSize = new FileInfo(video).Length;
        EnsureWritable(video);
        EnsureFreeSpace(video, originalSize);

        var temp = video + TempSuffix;
        var old = video + OldSuffix;
        if (File.Exists(old)) throw new IOException($"Reste eines früheren Versuchs: {old}. Bitte prüfen und entfernen (es könnte das Original sein).");

        var work = Path.Combine(Path.GetTempPath(), "mc-embed-" + Guid.NewGuid().ToString("N"));
        var swapped = false;
        try
        {
            Directory.CreateDirectory(work);
            var normalized = Path.Combine(work, "subtitle" + extension);
            File.WriteAllText(normalized, SubtitleText.ReadAsUtf8(subtitle), new UTF8Encoding(false));
            var covers = await ExtractCoversAsync(video, work, ct);

            await RunFfmpegAsync(video, normalized, temp, language, title, delaySeconds, extension, before.Streams, covers, before.DurationSeconds.Value, progress, ct);
            await VerifyAsync(temp, before, beforeChapters, originalSize, language, ct);

            // Austausch in zwei Schritten, damit bei einem Fehler das Original zurückkehrt.
            File.Move(video, old);
            try
            {
                File.Move(temp, video);
                swapped = true;
            }
            catch
            {
                File.Move(old, video);
                throw;
            }
        }
        finally
        {
            TryDeleteDirectory(work);
            if (!swapped) TryDelete(temp);
        }

        var index = before.Streams.Count(s => s.Type == "subtitle");
        try
        {
            discardOriginal(old);
            return new EmbedResult(video, index, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new EmbedResult(video, index, old);
        }
    }

    /// <summary>
    /// Schlägt jetzt fehl statt nach Minuten Arbeit: die Datei muss sich zum Ersetzen sperren lassen (kein Player hat sie offen,
    /// das Laufwerk ist beschreibbar).
    /// </summary>
    private static void EnsureWritable(string video)
    {
        try
        {
            using var stream = new FileStream(video, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Die Datei ist gesperrt oder schreibgeschützt und lässt sich nicht ersetzen: {video} ({ex.Message})", ex);
        }
    }

    private static void EnsureFreeSpace(string video, long size)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(video));
        if (root is null || root.StartsWith(@"\\")) return; // Netzwerkpfade: kein Laufwerksmaß, ffmpeg meldet den Fehler selbst

        var free = new DriveInfo(root).AvailableFreeSpace;
        var needed = size + size / 50 + 64L * 1024 * 1024;
        if (free < needed)
        {
            throw new IOException($"Nicht genug freier Platz auf {root}: {free / 1_000_000} MB frei, etwa {needed / 1_000_000} MB nötig.");
        }
    }

    // --- Cover-Anhänge -------------------------------------------------------------------------------------
    // ffmpeg liest einen Anhang "cover.jpg" als Videospur (attached_pic) und schriebe ihn beim Remux als echte Videospur zurück;
    // das Cover ginge damit verloren. Darum wird es ausgenommen, einzeln herausgeholt und mit Namen und Typ wieder angehängt.

    private async Task<IReadOnlyList<Cover>> ExtractCoversAsync(string video, string workDir, CancellationToken ct)
    {
        var json = await RunProbeAsync(new[] { "-v", "error", "-print_format", "json", "-show_streams", video }, ct);
        using var doc = JsonDocument.Parse(json);
        var covers = new List<Cover>();
        if (!doc.RootElement.TryGetProperty("streams", out var streams)) return covers;

        foreach (var s in streams.EnumerateArray())
        {
            if (!s.TryGetProperty("disposition", out var disposition) || !disposition.TryGetProperty("attached_pic", out var flag) || flag.GetInt32() != 1) continue;

            string? name = null, mime = null;
            if (s.TryGetProperty("tags", out var tags))
            {
                foreach (var tag in tags.EnumerateObject())
                {
                    if (tag.Name.Equals("filename", StringComparison.OrdinalIgnoreCase)) name = tag.Value.GetString();
                    else if (tag.Name.Equals("mimetype", StringComparison.OrdinalIgnoreCase)) mime = tag.Value.GetString();
                }
            }
            var index = s.GetProperty("index").GetInt32();
            var extension = mime switch { "image/jpeg" => ".jpg", "image/png" => ".png", "image/webp" => ".webp", _ => null };
            if (name is null || mime is null || extension is null)
                throw new NotSupportedException($"Der Bildanhang (Stream {index}) hat keinen lesbaren Namen oder Typ ({mime ?? "?"}); es wird nichts verändert.");

            var path = Path.Combine(workDir, $"cover{covers.Count}{extension}");
            await RunToolAsync(ffmpegPath, new[] { "-v", "error", "-y", "-i", video, "-map", $"0:{index}", "-c", "copy", "-frames:v", "1", "-f", "image2", path }, ct);
            covers.Add(new Cover(index, name, mime, path));
        }
        return covers;
    }

    private async Task RunFfmpegAsync(
        string video, string subtitleUtf8, string output, Language language, string? title, double delaySeconds,
        string extension, IReadOnlyList<StreamInfo> originalStreams, IReadOnlyList<Cover> covers,
        double duration, IProgress<double>? progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        var newSubtitleIndex = originalStreams.Count(s => s.Type == "subtitle");
        var coverIndexes = covers.Select(c => c.StreamIndex).ToHashSet();

        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-progress", "pipe:1", "-nostats", "-y", "-i", video };
        if (delaySeconds != 0) args.AddRange(new[] { "-itsoffset", delaySeconds.ToString("0.###", CultureInfo.InvariantCulture) });
        args.AddRange(new[] { "-i", subtitleUtf8, "-map", "0" });
        foreach (var cover in covers) args.AddRange(new[] { "-map", $"-0:{cover.StreamIndex}" });
        args.AddRange(new[] { "-map", "1:0", "-c", "copy" });
        args.AddRange(new[] { $"-c:s:{newSubtitleIndex}", extension is ".ass" or ".ssa" ? "ass" : "srt" });
        args.AddRange(new[] { $"-metadata:s:s:{newSubtitleIndex}", $"language={language.Codes.FirstOrDefault() ?? language.Id}" });
        if (!string.IsNullOrWhiteSpace(title)) args.AddRange(new[] { $"-metadata:s:s:{newSubtitleIndex}", $"title={title}" });

        // ffmpeg setzt beim Remux von sich aus "Standard"-Markierungen. Die ursprünglichen werden ausdrücklich festgeschrieben
        // (+/- ändert nur diese beiden Merkmale, andere wie "Kommentar" bleiben), die neue Spur ist weder Standard noch erzwungen.
        // Ausgabeindex: die ausgenommenen Cover fehlen, die neue Spur kommt als letzte der gemappten Streams.
        var outputIndex = 0;
        for (var i = 0; i < originalStreams.Count; i++)
        {
            if (coverIndexes.Contains(i)) continue;
            var s = originalStreams[i];
            args.AddRange(new[] { $"-disposition:{outputIndex++}", $"{(s.IsDefault ? "+" : "-")}default{(s.IsForced ? "+" : "-")}forced" });
        }
        args.AddRange(new[] { $"-disposition:{outputIndex}", "-default-forced" });

        // Die Cover kommen als neue Anhänge hinter die vorhandenen Anhänge (z. B. Schriften).
        var existingAttachments = originalStreams.Count(s => s.Type == "attachment");
        for (var j = 0; j < covers.Count; j++)
        {
            args.AddRange(new[] { "-attach", covers[j].Path });
            args.AddRange(new[] { $"-metadata:s:t:{existingAttachments + j}", $"mimetype={covers[j].MimeType}" });
            args.AddRange(new[] { $"-metadata:s:t:{existingAttachments + j}", $"filename={covers[j].FileName}" });
        }
        args.AddRange(new[] { "-ignore_unknown", "-f", "matroska", output });
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (Win32Exception ex)
        {
            throw new FileNotFoundException($"ffmpeg konnte nicht gestartet werden ('{ffmpegPath}'). Im PATH bereitstellen oder MEDIACENTER_FFMPEG setzen.", ex);
        }

        using (process)
        {
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var reader = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
                {
                    // out_time_us=1234567 (Mikrosekunden); "N/A" zu Beginn
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(12), out var micro) && micro >= 0)
                    {
                        progress?.Report(Math.Clamp(micro / 1_000_000.0 / duration, 0, 1));
                    }
                }
            });

            using var registration = ct.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* schon beendet */ }
            });
            await process.WaitForExitAsync(CancellationToken.None);
            await reader;
            var log = await stderr;

            ct.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidOperationException($"ffmpeg schlug fehl (Exit-Code {process.ExitCode}): {log.Trim()}");
            progress?.Report(1);
        }
    }

    // --- Prüfung -------------------------------------------------------------------------------------------

    /// <summary>Cover (attached_pic) und gewöhnliche Anhänge zählen als dasselbe, denn beim Zurückschreiben ändert sich ihre Lage.</summary>
    private static string KindOf(StreamInfo s) => s.Type == FfprobeReader.AttachedPictureType ? "attachment" : s.Type;

    private static string Describe(StreamInfo s) =>
        $"{KindOf(s)} {s.Codec}, Sprache {s.Language ?? "–"}, Standard {s.IsDefault}, erzwungen {s.IsForced}";

    /// <summary>Prüft die neue Datei gegen das Original; jede Abweichung wirft, dann bleibt das Original unangetastet.</summary>
    private async Task VerifyAsync(string temp, ProbeResult before, int beforeChapters, long originalSize, Language language, CancellationToken ct)
    {
        var after = await probe.ProbeAsync(temp, ct);

        if (after.Streams.Count != before.Streams.Count + 1)
            throw new InvalidDataException($"Prüfung fehlgeschlagen: {after.Streams.Count} Streams statt {before.Streams.Count + 1}.");

        // Je Art in der ursprünglichen Reihenfolge vergleichen; Anhänge als Menge, denn die Cover stehen jetzt bei den Anhängen.
        var expectedCode = language.Codes.FirstOrDefault() ?? language.Id;
        foreach (var kind in before.Streams.Concat(after.Streams).Select(KindOf).Distinct())
        {
            var original = before.Streams.Where(s => KindOf(s) == kind).ToList();
            var now = after.Streams.Where(s => KindOf(s) == kind).ToList();
            if (kind == "subtitle")
            {
                var added = now.Count > 0 ? now[^1] : null;
                if (now.Count != original.Count + 1 || added is null || added.Language != expectedCode)
                    throw new InvalidDataException($"Prüfung fehlgeschlagen: die neue Spur fehlt oder hat die Sprache „{added?.Language}“ statt „{expectedCode}“.");
                now = now.Take(original.Count).ToList();
            }
            else if (now.Count != original.Count)
            {
                throw new InvalidDataException($"Prüfung fehlgeschlagen: {now.Count} Streams der Art {kind} statt {original.Count}.");
            }

            if (kind == "attachment")
            {
                original = original.OrderBy(s => s.Codec, StringComparer.Ordinal).ToList();
                now = now.OrderBy(s => s.Codec, StringComparer.Ordinal).ToList();
            }
            for (var i = 0; i < original.Count; i++)
            {
                var a = original[i];
                var b = now[i];
                if (a.Codec != b.Codec || a.Language != b.Language || a.IsDefault != b.IsDefault || a.IsForced != b.IsForced)
                    throw new InvalidDataException($"Prüfung fehlgeschlagen: ein Stream der Art {kind} hat sich verändert: {Describe(a)} → {Describe(b)}.");
            }
        }

        var tolerance = Math.Max(1.0, before.DurationSeconds!.Value * 0.005);
        if (after.DurationSeconds is not { } afterDuration || Math.Abs(afterDuration - before.DurationSeconds.Value) > tolerance)
            throw new InvalidDataException($"Prüfung fehlgeschlagen: Dauer {after.DurationSeconds:0.##} s statt {before.DurationSeconds:0.##} s.");

        var size = new FileInfo(temp).Length;
        if (size < originalSize * 0.9)
            throw new InvalidDataException($"Prüfung fehlgeschlagen: die neue Datei ist mit {size / 1_000_000} MB viel kleiner als das Original ({originalSize / 1_000_000} MB).");

        var afterChapters = await CountChaptersAsync(temp, ct);
        if (afterChapters != beforeChapters)
            throw new InvalidDataException($"Prüfung fehlgeschlagen: {afterChapters} Kapitel statt {beforeChapters}.");
    }

    // --- Werkzeug-Aufrufe ----------------------------------------------------------------------------------

    private async Task<int> CountChaptersAsync(string file, CancellationToken ct)
    {
        var json = await RunProbeAsync(new[] { "-v", "error", "-print_format", "json", "-show_chapters", file }, ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("chapters", out var chapters) ? chapters.GetArrayLength() : 0;
    }

    private async Task<string> RunProbeAsync(IEnumerable<string> args, CancellationToken ct) => await RunToolAsync(ffprobePath, args, ct);

    /// <summary>Startet ein Werkzeug, liefert seine Ausgabe und wirft bei Fehler mit dessen Fehlertext.</summary>
    private static async Task<string> RunToolAsync(string tool, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(tool)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (Win32Exception ex)
        {
            throw new FileNotFoundException($"'{tool}' konnte nicht gestartet werden. Im PATH bereitstellen oder MEDIACENTER_FFMPEG / MEDIACENTER_FFPROBE setzen.", ex);
        }

        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeout);
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(tool)} schlug fehl (Exit-Code {process.ExitCode}): {(await stderr).Trim()}");
                return await stdout;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"{Path.GetFileName(tool)} antwortete nicht innerhalb von {ProbeTimeout.TotalSeconds:0} s.");
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Aufräumen darf den eigentlichen Fehler nicht überdecken */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* siehe oben */ }
    }
}

/// <summary>Liest Untertiteltext als UTF-8, auch wenn die Datei in Windows-1252 vorliegt (ältere deutsche SRT).</summary>
public static class SubtitleText
{
    /// <summary>Gültiges UTF-8 (mit oder ohne BOM) bleibt; alles andere gilt als Windows-1252.</summary>
    public static string ReadAsUtf8(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }
}
