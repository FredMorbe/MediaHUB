using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace MediaCenter.Core;

/// <summary>Holt Untertitel als SRT-Text aus einem Video oder wandelt eine Untertiteldatei in SRT um (ffmpeg).</summary>
public sealed partial class SubtitleExtractor(string ffmpegPath)
{
    [System.Text.RegularExpressions.GeneratedRegex(@"<font size=""\d+"">(.*?)</font>", System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex FontSize();

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    /// <summary>Codecs, aus denen sich Text gewinnen lässt. Bilduntertitel (PGS, DVD) bräuchten Texterkennung.</summary>
    private static readonly HashSet<string> TextCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "subrip", "srt", "ass", "ssa", "mov_text", "webvtt", "text", "subviewer", "microdvd", "sami", "ttml",
    };

    public static SubtitleExtractor FromEnvironment() => new(Environment.GetEnvironmentVariable("MEDIACENTER_FFMPEG") ?? "ffmpeg");

    public static bool IsText(string? codec) => codec is not null && TextCodecs.Contains(codec);

    /// <summary>Eine Spur der Videodatei (ffmpeg-Streamnummer) als SRT-Einträge.</summary>
    public async Task<IReadOnlyList<SrtCue>> FromStreamAsync(string video, int streamIndex, CancellationToken ct = default) =>
        await ConvertAsync(["-i", video, "-map", $"0:{streamIndex}"], ct);

    /// <summary>Eine Untertiteldatei; SRT wird direkt gelesen, andere Formate (ASS, VTT …) wandelt ffmpeg um.</summary>
    public async Task<IReadOnlyList<SrtCue>> FromFileAsync(string file, CancellationToken ct = default)
    {
        if (!File.Exists(file)) throw new FileNotFoundException($"Untertiteldatei nicht gefunden: {file}", file);
        if (string.Equals(Path.GetExtension(file), ".srt", StringComparison.OrdinalIgnoreCase)) return Srt.Parse(SubtitleText.ReadAsUtf8(file));

        return await ConvertAsync(["-i", file], ct);
    }

    private async Task<IReadOnlyList<SrtCue>> ConvertAsync(string[] input, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"mediacenter-{Guid.NewGuid():N}.srt");
        try
        {
            var psi = new ProcessStartInfo(ffmpegPath)
            {
                UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true,
                StandardErrorEncoding = Encoding.UTF8, CreateNoWindow = true,
            };
            foreach (var arg in new[] { "-nostdin", "-v", "error", "-y" }.Concat(input).Concat(["-c:s", "srt", "-f", "srt", temp])) psi.ArgumentList.Add(arg);

            Process process;
            try
            {
                process = Process.Start(psi)!;
            }
            catch (Win32Exception ex)
            {
                throw new FileNotFoundException($"'{ffmpegPath}' konnte nicht gestartet werden. Im PATH bereitstellen oder MEDIACENTER_FFMPEG setzen.", ex);
            }

            using (process)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(Timeout);
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                _ = process.StandardOutput.ReadToEndAsync(timeout.Token);
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    if (ct.IsCancellationRequested) throw;
                    throw new TimeoutException($"ffmpeg antwortete nicht innerhalb von {Timeout.TotalMinutes:0} Minuten.");
                }

                if (process.ExitCode != 0)
                {
                    throw new InvalidDataException($"Der Untertitel ließ sich nicht in Text umwandeln (Bilduntertitel wie PGS oder DVD gehen nicht): {(await stderr).Trim()}");
                }
            }
            // Aus ASS-Stilen macht ffmpeg <font size="20">…</font>; die feste Größe soll weder übersetzt noch im Ergebnis festgeschrieben werden.
            var cues = Srt.Parse(SubtitleText.ReadAsUtf8(temp));
            return cues.Select(c => c with { Text = FontSize().Replace(c.Text, "$1") }).ToList();
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Aufräumen darf den eigentlichen Fehler nicht überdecken */ }
        }
    }
}
