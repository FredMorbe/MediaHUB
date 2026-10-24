using System.Text;

namespace MediaCenter.Core;

/// <param name="OriginalLeftBehind">Pfad der alten Datei, wenn sie sich nach dem Ersetzen nicht entfernen ließ (sonst null).</param>
public sealed record EmbedResult(string VideoPath, int SubtitleStreamIndex, string? OriginalLeftBehind);

/// <summary>
/// Bettet eine Untertitel-Datei ohne Neukodierung in eine MKV ein. Der Sonderfall des <see cref="MkvEditor"/>: eine Spur hinzufügen,
/// alles andere unverändert, das Original wird ersetzt.
/// </summary>
public sealed class MkvSubtitleEmbedder(string ffmpegPath, string ffprobePath, IMediaProbe probe)
{
    /// <summary>Endung der Arbeitsdatei; kein Videoformat, damit ein Scan sie nie als Video aufnimmt.</summary>
    public const string TempSuffix = MkvEditor.TempSuffix;

    /// <summary>Endung, unter der das Original kurz liegt, während die neue Datei an seine Stelle rückt.</summary>
    public const string OldSuffix = MkvEditor.OldSuffix;

    private readonly MkvEditor _editor = new(ffmpegPath, ffprobePath, probe);

    public static MkvSubtitleEmbedder FromEnvironment() => new(
        ToolLocator.Ffmpeg().Path,
        ToolLocator.Ffprobe().Path,
        FfprobeReader.FromEnvironment());

    public static bool CanEmbedInto(string videoPath) => MkvEditor.CanEdit(videoPath);

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

        var index = (await probe.ProbeAsync(video, ct)).Streams.Count(s => s.Type == "subtitle");
        var added = new MkvAddedTrack(subtitle, MkvAddKind.Subtitle, language.Codes.FirstOrDefault() ?? language.Id, title, DelaySeconds: delaySeconds);
        var result = await _editor.EditAsync(video, new MkvEditPlan(null, new[] { added }), discardOriginal, progress, ct);
        return new EmbedResult(result.VideoPath, index, result.OriginalLeftBehind);
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
