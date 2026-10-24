using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaCenter.Core;

public enum MkvAddKind
{
    Audio,
    Subtitle,
}

/// <summary>Eine vorhandene Spur im Zielzustand. Was null ist, bleibt wie im Original.</summary>
/// <param name="StreamIndex">Index der Spur im Original (ffprobe).</param>
/// <param name="Language">ISO-639-2-Kürzel („deu“).</param>
/// <param name="Title">Name der Spur; ein leerer Text löscht ihn.</param>
/// <param name="DelaySeconds">Wird dauerhaft in die Zeitstempel der Spur eingerechnet (positiv = später); nur Ton und Untertitel.</param>
public sealed record MkvTrackEdit(
    int StreamIndex, bool Remove = false, string? Language = null, string? Title = null,
    bool? IsDefault = null, bool? IsForced = null, double DelaySeconds = 0);

/// <param name="File">Untertitel-Datei (.srt, .ass …) oder Datei mit der Spur (Audiodatei, MKV, MP4 …).</param>
/// <param name="SourceStreamIndex">Welche Spur der Datei übernommen wird; null nimmt die erste passende. Bei Untertitel-Textdateien ohne Bedeutung.</param>
/// <param name="Position">Platz unter den Spuren derselben Art in der neuen Datei (0 = erste Tonspur bzw. erster Untertitel); null hängt hinten an.</param>
public sealed record MkvAddedTrack(
    string File, MkvAddKind Kind, string Language, string? Title = null,
    bool IsDefault = false, bool IsForced = false, double DelaySeconds = 0, int? SourceStreamIndex = null, int? Position = null);

/// <param name="Tracks">
/// Alle Video-, Ton- und Untertitelspuren des Originals in der gewünschten Reihenfolge (jede genau einmal, entfernte mit
/// <see cref="MkvTrackEdit.Remove"/>); null lässt alle unverändert. Anhänge (Schriften, Cover) und Kapitel bleiben immer.
/// </param>
/// <param name="CopyTo">Zieldatei für eine Kopie (das Original bleibt unberührt); null ersetzt das Original.</param>
public sealed record MkvEditPlan(IReadOnlyList<MkvTrackEdit>? Tracks, IReadOnlyList<MkvAddedTrack> Added, string? CopyTo = null);

/// <param name="VideoPath">Die bearbeitete Datei (Original oder Kopie).</param>
/// <param name="OriginalLeftBehind">Pfad der alten Datei, wenn sie sich nach dem Ersetzen nicht entfernen ließ (sonst null).</param>
public sealed record MkvEditResult(string VideoPath, string? OriginalLeftBehind);

/// <summary>
/// Bearbeitet eine MKV ohne Neukodierung (ffmpeg, <c>-c copy</c>): Spuren entfernen, hinzufügen, umbenennen, Sprache und Markierungen
/// ändern, Ton und Untertitel dauerhaft verschieben. Die neue Datei entsteht neben dem Ziel unter einem Namen, den der Scan ignoriert,
/// wird streng gegen den Plan geprüft und erst dann an ihre Stelle gesetzt. Jeder Fehler vorher lässt das Original unberührt.
/// </summary>
public sealed class MkvEditor(string ffmpegPath, string ffprobePath, IMediaProbe probe)
{
    /// <summary>Endung der Arbeitsdatei; kein Videoformat, damit ein Scan sie nie als Video aufnimmt.</summary>
    public const string TempSuffix = ".embed-tmp";

    /// <summary>Endung, unter der das Original kurz liegt, während die neue Datei an seine Stelle rückt.</summary>
    public const string OldSuffix = ".embed-old";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Ein Cover-Bild, das ffmpeg als Videospur mit Merker „attached_pic“ liest (Anhang „cover.jpg“ in der MKV).</summary>
    private sealed record Cover(int StreamIndex, string FileName, string MimeType, string Path = "");

    /// <summary>Eine Spur der neuen Datei in Ausgabereihenfolge.</summary>
    private sealed record OutTrack(
        int Input, int InputStream, char Letter, string? NewCodec, bool IsDefault, bool IsForced,
        string? Language, string? Title, string? ExpectedCodec, string ExpectedType, string? ExpectedLanguage, string? ExpectedTitle);

    public static MkvEditor FromEnvironment() => new(
        ToolLocator.Ffmpeg().Path,
        ToolLocator.Ffprobe().Path,
        FfprobeReader.FromEnvironment());

    public static bool CanEdit(string videoPath) => Path.GetExtension(videoPath).Equals(".mkv", StringComparison.OrdinalIgnoreCase);

    public async Task<MkvEditResult> EditAsync(
        string video, MkvEditPlan plan, Action<string> discardOriginal, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!CanEdit(video)) throw new NotSupportedException("Bearbeiten ist nur bei MKV-Dateien möglich.");
        if (!File.Exists(video)) throw new FileNotFoundException($"Videodatei nicht gefunden: {video}", video);
        ValidateAdded(plan.Added);

        var inPlace = plan.CopyTo is null;
        var target = inPlace ? video : Path.GetFullPath(plan.CopyTo!);
        if (!inPlace)
        {
            if (!CanEdit(target)) throw new ArgumentException("Die Kopie muss auf „.mkv“ enden.", nameof(plan));
            if (string.Equals(target, Path.GetFullPath(video), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Die Kopie muss einen anderen Namen haben als das Original.", nameof(plan));
            if (File.Exists(target)) throw new IOException($"Die Zieldatei gibt es schon: {target}");
            if (!Directory.Exists(Path.GetDirectoryName(target))) throw new DirectoryNotFoundException($"Der Zielordner fehlt: {Path.GetDirectoryName(target)}");
        }

        var before = await probe.ProbeAsync(video, ct);
        if (before.DurationSeconds is not > 0) throw new InvalidDataException("Die Dauer der Videodatei ist nicht lesbar; es wird nichts verändert.");
        var tracks = ResolveTracks(before, plan);
        var beforeChapters = await CountChaptersAsync(video, ct);
        var originalSize = new FileInfo(video).Length;
        if (inPlace) EnsureWritable(video);
        EnsureFreeSpace(target, originalSize);

        var temp = target + TempSuffix;
        var old = video + OldSuffix;
        if (inPlace && File.Exists(old)) throw new IOException($"Reste eines früheren Versuchs: {old}. Bitte prüfen und entfernen (es könnte das Original sein).");

        var work = Path.Combine(Path.GetTempPath(), "mc-edit-" + Guid.NewGuid().ToString("N"));
        var placed = false;
        try
        {
            Directory.CreateDirectory(work);
            var covers = await ExtractCoversAsync(video, work, ct);
            var (inputs, output) = await PrepareAsync(video, before, tracks, plan.Added, work, ct);

            await RunFfmpegAsync(video, inputs, output, before, covers, temp, before.DurationSeconds.Value, progress, ct);
            // Ein verschobener Ton, der später endet, verlängert die Datei um den Versatz.
            var allowance = tracks.Select(t => Math.Abs(t.DelaySeconds)).Concat(plan.Added.Select(a => Math.Abs(a.DelaySeconds))).Max();
            await VerifyAsync(temp, before, beforeChapters, originalSize, output, tracks.Any(t => t.Remove), allowance, ct);

            if (inPlace)
            {
                // Austausch in zwei Schritten, damit bei einem Fehler das Original zurückkehrt.
                File.Move(video, old);
                try
                {
                    File.Move(temp, video);
                    placed = true;
                }
                catch
                {
                    File.Move(old, video);
                    throw;
                }
            }
            else
            {
                File.Move(temp, target);
                placed = true;
            }
        }
        finally
        {
            TryDeleteDirectory(work);
            if (!placed) TryDelete(temp);
        }

        if (!inPlace) return new MkvEditResult(target, null);
        try
        {
            discardOriginal(old);
            return new MkvEditResult(video, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new MkvEditResult(video, old);
        }
    }

    // --- Plan prüfen ---------------------------------------------------------------------------------------

    private static readonly Regex LanguageCode = new("^[a-z]{3}$", RegexOptions.Compiled);

    private static void CheckDelay(double seconds)
    {
        if (!double.IsFinite(seconds) || Math.Abs(seconds) > SubtitleAdjustment.MaxDelaySeconds)
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "Zeitversatz außerhalb des erlaubten Bereichs.");
    }

    private static void CheckLanguage(string? code)
    {
        if (code is not null && !LanguageCode.IsMatch(code))
            throw new ArgumentException($"„{code}“ ist kein dreibuchstabiges Sprachkürzel (z. B. „deu“, „eng“, „und“).");
    }

    private static void ValidateAdded(IReadOnlyList<MkvAddedTrack> added)
    {
        foreach (var track in added)
        {
            if (!File.Exists(track.File)) throw new FileNotFoundException($"Datei nicht gefunden: {track.File}", track.File);
            CheckLanguage(track.Language);
            CheckDelay(track.DelaySeconds);
        }
    }

    private static bool IsSubtitleText(string file) => SubtitleStore.Extensions.Contains(Path.GetExtension(file));

    /// <summary>Prüft die Spurliste gegen das Original und liefert sie vollständig und in Ausgabereihenfolge.</summary>
    private static IReadOnlyList<MkvTrackEdit> ResolveTracks(ProbeResult before, MkvEditPlan plan)
    {
        var candidates = before.Streams.Where(s => s.Type is not ("attachment" or FfprobeReader.AttachedPictureType)).ToList();
        var tracks = plan.Tracks ?? candidates.Select(s => new MkvTrackEdit(s.Index)).ToList();

        if (tracks.Select(t => t.StreamIndex).Order().SequenceEqual(candidates.Select(s => s.Index).Order()) is false)
            throw new ArgumentException("Die Spurliste muss jede Video-, Ton- und Untertitelspur der Datei genau einmal enthalten.", nameof(plan));

        foreach (var edit in tracks)
        {
            var stream = candidates.First(s => s.Index == edit.StreamIndex);
            CheckLanguage(edit.Language);
            CheckDelay(edit.DelaySeconds);
            if (edit.DelaySeconds != 0 && stream.Type is not ("audio" or "subtitle"))
                throw new ArgumentException($"Spur {edit.StreamIndex} ({stream.Type}) lässt sich nicht verschieben; nur Ton und Untertitel.");
            if (edit.Remove && stream.Type == "video" && tracks.Count(t => !t.Remove && candidates.First(s => s.Index == t.StreamIndex).Type == "video") == 0)
                throw new ArgumentException("Die Videospur lässt sich nicht entfernen.");
            if (!edit.Remove && (edit.Language is not null || edit.Title is not null) && LetterOf(stream.Type) is null)
                throw new ArgumentException($"Spur {edit.StreamIndex} ({stream.Type}) lässt sich nicht umbenennen.");
        }
        if (!tracks.Any(t => !t.Remove && candidates.First(s => s.Index == t.StreamIndex).Type == "video"))
            throw new ArgumentException("Die Videospur lässt sich nicht entfernen.");

        var naturalOrder = tracks.Select(t => t.StreamIndex).SequenceEqual(candidates.Select(s => s.Index));
        var unchanged = plan.Added.Count == 0 && naturalOrder
            && tracks.All(t => t is { Remove: false, Language: null, Title: null, IsDefault: null, IsForced: null, DelaySeconds: 0 });
        if (unchanged) throw new ArgumentException("Es gibt keine Änderungen.", nameof(plan));
        return tracks;
    }

    private static char? LetterOf(string type) => type switch { "video" => 'v', "audio" => 'a', "subtitle" => 's', _ => null };

    // --- Eingaben und Ausgabespuren ------------------------------------------------------------------------

    private sealed record Input(string File, double Offset);

    private async Task<(IReadOnlyList<Input> Inputs, IReadOnlyList<OutTrack> Output)> PrepareAsync(
        string video, ProbeResult before, IReadOnlyList<MkvTrackEdit> tracks, IReadOnlyList<MkvAddedTrack> added, string work, CancellationToken ct)
    {
        var inputs = new List<Input> { new(video, 0) };
        int InputFor(double offset)
        {
            // Dieselbe Videodatei wird je Zeitversatz einmal geöffnet; ffmpeg verschiebt immer eine ganze Eingabe.
            var existing = inputs.FindIndex(i => i.File == video && i.Offset == offset);
            if (existing >= 0) return existing;
            inputs.Add(new Input(video, offset));
            return inputs.Count - 1;
        }

        var output = new List<OutTrack>();
        foreach (var edit in tracks.Where(t => !t.Remove))
        {
            var s = before.Streams.First(x => x.Index == edit.StreamIndex);
            var title = edit.Title is null ? s.Title : edit.Title.Length == 0 ? null : edit.Title;
            output.Add(new OutTrack(
                InputFor(edit.DelaySeconds), s.Index, LetterOf(s.Type) ?? 'd', NewCodec: null,
                edit.IsDefault ?? s.IsDefault, edit.IsForced ?? s.IsForced,
                edit.Language, edit.Title, s.Codec, s.Type, edit.Language ?? s.Language, title));
        }

        // Neue Spuren stehen bei Spuren derselben Art (Ton bei Ton, Untertitel bei Untertitel), ohne Angabe hinter der letzten.
        // Sie werden nach Position aufsteigend eingesetzt, damit jede Position am Ende stimmt.
        void Place(OutTrack track, int? position)
        {
            var sameKind = output.Select((o, i) => (o, i)).Where(x => x.o.Letter == track.Letter).Select(x => x.i).ToList();
            if (sameKind.Count == 0) output.Add(track);
            else if (position is { } p && p < sameKind.Count) output.Insert(sameKind[Math.Max(0, p)], track);
            else output.Insert(sameKind[^1] + 1, track);
        }

        foreach (var track in added.Select((a, i) => (a, i)).OrderBy(x => x.a.Position ?? int.MaxValue).ThenBy(x => x.i).Select(x => x.a))
        {
            var index = inputs.Count;
            if (track.Kind == MkvAddKind.Subtitle && IsSubtitleText(track.File))
            {
                var extension = Path.GetExtension(track.File).ToLowerInvariant();
                var normalized = Path.Combine(work, $"subtitle{index}{extension}");
                File.WriteAllText(normalized, SubtitleText.ReadAsUtf8(track.File), new UTF8Encoding(false));
                inputs.Add(new Input(normalized, track.DelaySeconds));
                var isAss = extension is ".ass" or ".ssa";
                Place(new OutTrack(index, 0, 's', isAss ? "ass" : "srt", track.IsDefault, track.IsForced,
                    track.Language, Blank(track.Title), isAss ? "ass" : "subrip", "subtitle", track.Language, Blank(track.Title)), track.Position);
                continue;
            }

            ProbeResult source;
            try
            {
                source = await probe.ProbeAsync(track.File, ct);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidDataException($"„{Path.GetFileName(track.File)}“ ist weder eine Untertitel- noch eine lesbare Mediendatei: {ex.Message}", ex);
            }
            var wanted = track.Kind == MkvAddKind.Audio ? "audio" : "subtitle";
            var stream = track.SourceStreamIndex is { } chosen
                ? source.Streams.FirstOrDefault(s => s.Index == chosen) ?? throw new InvalidDataException($"„{Path.GetFileName(track.File)}“ hat keine Spur {chosen}.")
                : source.Streams.FirstOrDefault(s => s.Type == wanted) ?? throw new InvalidDataException(
                    $"„{Path.GetFileName(track.File)}“ enthält {(wanted == "audio" ? "keine Tonspur" : "keinen Untertitel")}.");
            if (stream.Type != wanted)
                throw new InvalidDataException($"Spur {stream.Index} in „{Path.GetFileName(track.File)}“ ist keine {(wanted == "audio" ? "Tonspur" : "Untertitelspur")} (sondern {stream.Type}).");

            inputs.Add(new Input(track.File, track.DelaySeconds));
            // Text-Untertitel anderer Formate werden für MKV in SRT/ASS gewandelt, Bild-Untertitel (PGS, DVD) unverändert übernommen.
            var (newCodec, expected) = wanted == "subtitle" ? stream.Codec switch
            {
                "ass" or "ssa" => ("ass", "ass"),
                "subrip" or "mov_text" or "webvtt" or "text" or "srt" => ("srt", "subrip"),
                _ => ((string?)null, stream.Codec),
            } : ((string?)null, stream.Codec);
            Place(new OutTrack(index, stream.Index, wanted == "audio" ? 'a' : 's', newCodec, track.IsDefault, track.IsForced,
                track.Language, Blank(track.Title), expected, wanted, track.Language, Blank(track.Title)), track.Position);
        }
        return (inputs, output);
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private static string Seconds(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Schlägt jetzt fehl statt nach Minuten Arbeit: die Datei muss sich zum Ersetzen sperren lassen.</summary>
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

    private static void EnsureFreeSpace(string target, long size)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(target));
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

    // --- ffmpeg --------------------------------------------------------------------------------------------

    private async Task RunFfmpegAsync(
        string video, IReadOnlyList<Input> inputs, IReadOnlyList<OutTrack> output, ProbeResult before, IReadOnlyList<Cover> covers,
        string target, double duration, IProgress<double>? progress, CancellationToken ct)
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

        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-progress", "pipe:1", "-nostats", "-y" };
        foreach (var input in inputs)
        {
            if (input.Offset != 0) args.AddRange(new[] { "-itsoffset", Seconds(input.Offset) });
            args.AddRange(new[] { "-i", input.File });
        }
        foreach (var track in output) args.AddRange(new[] { "-map", $"{track.Input}:{track.InputStream}" });

        // Anhänge (Schriften) bleiben; die Cover fehlen hier und kommen unten mit Namen und Typ zurück.
        var attachments = before.Streams.Where(s => s.Type == "attachment").ToList();
        foreach (var attachment in attachments) args.AddRange(new[] { "-map", $"0:{attachment.Index}" });
        args.AddRange(new[] { "-c", "copy" });

        var ordinals = new Dictionary<char, int>();
        for (var i = 0; i < output.Count; i++)
        {
            var track = output[i];
            var ordinal = ordinals.GetValueOrDefault(track.Letter);
            ordinals[track.Letter] = ordinal + 1;
            var specifier = $"{track.Letter}:{ordinal}";

            if (track.NewCodec is not null) args.AddRange(new[] { $"-c:{specifier}", track.NewCodec });
            if (track.Language is not null) args.AddRange(new[] { $"-metadata:s:{specifier}", $"language={track.Language}" });
            if (track.Title is not null) args.AddRange(new[] { $"-metadata:s:{specifier}", $"title={track.Title}" });

            // ffmpeg setzt beim Remux von sich aus "Standard"-Markierungen. Alle Spuren bekommen ausdrücklich ihren Zielwert
            // (+/- ändert nur diese beiden Merkmale, andere wie "Kommentar" bleiben).
            args.AddRange(new[] { $"-disposition:{i}", $"{(track.IsDefault ? "+" : "-")}default{(track.IsForced ? "+" : "-")}forced" });
        }

        // Die Cover kommen als neue Anhänge hinter die vorhandenen Anhänge (z. B. Schriften).
        for (var j = 0; j < covers.Count; j++)
        {
            args.AddRange(new[] { "-attach", covers[j].Path });
            args.AddRange(new[] { $"-metadata:s:t:{attachments.Count + j}", $"mimetype={covers[j].MimeType}" });
            args.AddRange(new[] { $"-metadata:s:t:{attachments.Count + j}", $"filename={covers[j].FileName}" });
        }
        args.AddRange(new[] { "-ignore_unknown", "-f", "matroska", target });
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

    private static string Describe(string type, string? codec, string? language, string? title, bool isDefault, bool isForced) =>
        $"{type} {codec}, Sprache {language ?? "–"}, Name „{title}“, Standard {isDefault}, erzwungen {isForced}";

    private static string Normal(string? text) => string.IsNullOrWhiteSpace(text) ? "" : text.Trim();

    private static string NormalLanguage(string? code) => string.IsNullOrWhiteSpace(code) ? "und" : code.Trim();

    /// <summary>Prüft die neue Datei gegen den Plan; jede Abweichung wirft, dann bleibt das Original unangetastet.</summary>
    private async Task VerifyAsync(
        string temp, ProbeResult before, int beforeChapters, long originalSize, IReadOnlyList<OutTrack> expected,
        bool removedTracks, double delayAllowance, CancellationToken ct)
    {
        var after = await probe.ProbeAsync(temp, ct);
        var tracks = after.Streams.Where(s => KindOf(s) != "attachment").ToList();

        if (tracks.Count != expected.Count)
            throw new InvalidDataException($"Prüfung fehlgeschlagen: {tracks.Count} Spuren statt {expected.Count}.");
        for (var i = 0; i < expected.Count; i++)
        {
            var want = expected[i];
            var got = tracks[i];
            var wantLanguage = NormalLanguage(want.ExpectedLanguage);
            if (got.Type != want.ExpectedType || (want.ExpectedCodec is not null && got.Codec != want.ExpectedCodec)
                || NormalLanguage(got.Language) != wantLanguage || Normal(got.Title) != Normal(want.ExpectedTitle)
                || got.IsDefault != want.IsDefault || got.IsForced != want.IsForced)
            {
                throw new InvalidDataException(
                    $"Prüfung fehlgeschlagen: Spur {i} ist nicht wie geplant. Erwartet: {Describe(want.ExpectedType, want.ExpectedCodec, wantLanguage, want.ExpectedTitle, want.IsDefault, want.IsForced)}; " +
                    $"tatsächlich: {Describe(got.Type, got.Codec, NormalLanguage(got.Language), got.Title, got.IsDefault, got.IsForced)}.");
            }
        }

        // Anhänge und Cover bleiben vollständig erhalten (als Menge; ihre Lage in der Datei darf sich ändern).
        var attachmentsBefore = before.Streams.Where(s => KindOf(s) == "attachment").Select(s => s.Codec ?? "").Order(StringComparer.Ordinal).ToList();
        var attachmentsAfter = after.Streams.Where(s => KindOf(s) == "attachment").Select(s => s.Codec ?? "").Order(StringComparer.Ordinal).ToList();
        if (!attachmentsBefore.SequenceEqual(attachmentsAfter))
            throw new InvalidDataException($"Prüfung fehlgeschlagen: {attachmentsAfter.Count} Anhänge statt {attachmentsBefore.Count}.");

        var duration = before.DurationSeconds!.Value;
        var tolerance = Math.Max(1.0, duration * 0.005) + delayAllowance;
        if (after.DurationSeconds is not { } afterDuration || Math.Abs(afterDuration - duration) > tolerance)
            throw new InvalidDataException($"Prüfung fehlgeschlagen: Dauer {after.DurationSeconds:0.##} s statt {duration:0.##} s.");

        // Ohne entfernte Spuren darf die Datei nicht kleiner werden; mit ihnen ist die Größe kein Maß (die Dauer oben fängt Abbrüche ab).
        var size = new FileInfo(temp).Length;
        if (!removedTracks && size < originalSize * 0.9)
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
