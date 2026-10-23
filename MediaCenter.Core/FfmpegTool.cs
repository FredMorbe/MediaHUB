using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace MediaCenter.Core;

/// <summary>Bildoperationen für Cover. Jede Ausgabe ist ein JPEG, das in die angegebene Größe passt (nie hochskaliert).</summary>
public interface IImageTool
{
    Task ScaleImageAsync(string input, string output, int maxWidth, int maxHeight, CancellationToken ct = default);

    Task GrabFrameAsync(string video, double seconds, string output, int maxWidth, int maxHeight, CancellationToken ct = default);

    /// <summary>Holt ein eingebettetes Bild (Stream mit attached_pic) aus einer Datei.</summary>
    Task ExtractStreamImageAsync(string video, int streamIndex, string output, int maxWidth, int maxHeight, CancellationToken ct = default);
}

public sealed class FfmpegTool(string ffmpegPath) : IImageTool
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>Pfad aus MEDIACENTER_FFMPEG, sonst "ffmpeg" aus dem PATH.</summary>
    public static FfmpegTool FromEnvironment() =>
        new(Environment.GetEnvironmentVariable("MEDIACENTER_FFMPEG") ?? "ffmpeg");

    public Task ScaleImageAsync(string input, string output, int maxWidth, int maxHeight, CancellationToken ct = default) =>
        RunAsync(output, ct, "-i", input, "-frames:v", "1", "-vf", ScaleFilter(maxWidth, maxHeight));

    public async Task GrabFrameAsync(string video, double seconds, string output, int maxWidth, int maxHeight, CancellationToken ct = default)
    {
        var position = seconds.ToString("F3", CultureInfo.InvariantCulture);
        // Schwarze Balken (Cinemascope in 16:9-Bild) würden als Cover mit abgebildet; sie werden vorher abgeschnitten.
        var crop = await DetectCropAsync(video, position, ct);
        var filter = (crop is null ? "" : $"crop={crop},") + ScaleFilter(maxWidth, maxHeight);
        // -ss vor -i: schnelles Springen, auch in sehr großen Dateien.
        await RunAsync(output, ct, "-ss", position, "-i", video, "-frames:v", "1", "-vf", filter);
    }

    public Task ExtractStreamImageAsync(string video, int streamIndex, string output, int maxWidth, int maxHeight, CancellationToken ct = default) =>
        RunAsync(output, ct, "-i", video, "-map", $"0:{streamIndex}", "-frames:v", "1", "-vf", ScaleFilter(maxWidth, maxHeight));

    private static readonly System.Text.RegularExpressions.Regex CropLine = new(@"crop=(\d+):(\d+):(\d+):(\d+)");

    /// <summary>
    /// Misst schwarze Ränder über einige Sekunden ab der Position. Liefert "w:h:x:y" oder null, wenn kein nennenswerter
    /// Rand da ist. Ein unplausibel kleiner Bildbereich (z. B. bei einer schwarzen Szene) wird verworfen.
    /// </summary>
    private async Task<string?> DetectCropAsync(string video, string position, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpegPath) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, StandardErrorEncoding = System.Text.Encoding.UTF8, CreateNoWindow = true };
        foreach (var arg in new[] { "-hide_banner", "-v", "info", "-ss", position, "-i", video, "-t", "3", "-an", "-sn", "-vf", "cropdetect=limit=24:round=2:reset=0", "-f", "null", "-" })
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg konnte nicht gestartet werden.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        _ = process.StandardOutput.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var log = await stderr;
            if (process.ExitCode != 0) throw new InvalidOperationException($"ffmpeg (Randerkennung) schlug fehl (Exit-Code {process.ExitCode}): {log.Trim()}");

            var last = CropLine.Matches(log).LastOrDefault();
            if (last is null) return null;
            int w = int.Parse(last.Groups[1].Value), h = int.Parse(last.Groups[2].Value);

            // Gesamtgröße steht in der Eingabezeile ("Stream #0:0 ... 1920x1080").
            var size = System.Text.RegularExpressions.Regex.Match(log, @"Video:.*?, (\d{2,5})x(\d{2,5})");
            if (!size.Success) return null;
            int fullW = int.Parse(size.Groups[1].Value), fullH = int.Parse(size.Groups[2].Value);

            var cutsLittle = w >= fullW * 0.98 && h >= fullH * 0.98;
            var implausible = w < fullW * 0.4 || h < fullH * 0.4;
            return cutsLittle || implausible ? null : $"{last.Groups[1]}:{last.Groups[2]}:{last.Groups[3]}:{last.Groups[4]}";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"ffmpeg brauchte für die Randerkennung in '{video}' länger als {Timeout.TotalSeconds:0} s.");
        }
    }

    // Die Anführungszeichen schützen das Komma in min() vor dem Filtergraph-Parser.
    private static string ScaleFilter(int width, int height) =>
        $"scale=w='min({width},iw)':h='min({height},ih)':force_original_aspect_ratio=decrease";

    private static readonly TimeSpan PreviewTimeout = TimeSpan.FromSeconds(120);

    // Umwandlung von HDR (PQ/HLG, BT.2020) nach SDR (BT.709); ohne sie wirkt das Bild blass.
    internal const string HdrToSdr = "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p";

    /// <summary>
    /// Schneidet einen kurzen Ausschnitt ohne Ton als MP4 (H.264) heraus, den WPF ohne weitere Codecs abspielt. Die Höhe wird
    /// nie hochskaliert; HDR-Quellen werden nach SDR umgerechnet.
    /// </summary>
    public Task CreatePreviewAsync(string video, double startSeconds, double lengthSeconds, string output, int height, bool hdr, CancellationToken ct = default)
    {
        var scale = $"scale=w=-2:h='min({height},ih)'";
        var filter = hdr ? $"{HdrToSdr},{scale}" : scale;
        // Die Vorschau entsteht nebenbei, während der Nutzer die Oberfläche bedient: wenige Threads und niedrige Priorität, damit sie das Scrollen nicht stört.
        return RunAsync(output, PreviewTimeout, new[]
        {
            "-t", lengthSeconds.ToString("F3", CultureInfo.InvariantCulture), "-map", "0:v:0", "-vf", filter,
            "-an", "-sn", "-dn", "-c:v", "libx264", "-threads", PreviewThreads, "-preset", "veryfast", "-crf", "24", "-pix_fmt", "yuv420p", "-movflags", "+faststart",
        }, "keine Vorschau", ct, lowPriority: true, "-threads", PreviewThreads, "-ss", startSeconds.ToString("F3", CultureInfo.InvariantCulture), "-i", video);
    }

    private const string PreviewThreads = "2";

    private Task RunAsync(string output, CancellationToken ct, params string[] inputArgs) =>
        RunAsync(output, Timeout, new[] { "-q:v", "3" }, "kein Bild", ct, lowPriority: false, inputArgs);

    private async Task RunAsync(string output, TimeSpan timeoutAfter, string[] outputArgs, string noResult, CancellationToken ct, bool lowPriority, params string[] inputArgs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        // ffmpeg erkennt das Format an der Endung; erst in eine Temp-Datei schreiben, dann atomar ersetzen.
        var temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, $"{Path.GetFileNameWithoutExtension(output)}.tmp{Path.GetExtension(output)}");

        var psi = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = System.Text.Encoding.UTF8, // Fehlermeldungen nennen Dateinamen mit Umlauten
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-v", "error", "-y" }.Concat(inputArgs).Concat(outputArgs).Concat(new[] { temp })) psi.ArgumentList.Add(arg);

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (Win32Exception ex)
        {
            throw new FileNotFoundException(
                $"ffmpeg konnte nicht gestartet werden ('{ffmpegPath}'). Im PATH bereitstellen oder MEDIACENTER_FFMPEG setzen.", ex);
        }

        using (process)
        {
            if (lowPriority) process.PriorityClass = ProcessPriorityClass.BelowNormal;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutAfter);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            _ = process.StandardOutput.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                var error = (await stderr).Trim();
                if (process.ExitCode != 0 || !File.Exists(temp) || new FileInfo(temp).Length == 0)
                {
                    throw new InvalidOperationException($"ffmpeg lieferte {noResult} (Exit-Code {process.ExitCode}): {error}");
                }
                File.Move(temp, output, overwrite: true);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"ffmpeg brauchte für '{output}' länger als {timeoutAfter.TotalSeconds:0} s.");
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }
}
