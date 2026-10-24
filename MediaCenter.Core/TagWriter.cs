using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace MediaCenter.Core;

/// <summary>
/// Gewünschte Änderungen an den Tags einer Audiodatei. null lässt das Feld unverändert, ein leerer Text löscht es.
/// Titel- und Disc-Nummer dürfen „3“ oder „3/12“ sein, das Jahr vierstellig (oder ein Datum „2021-05-14“).
/// </summary>
public sealed record TagChanges(
    string? Title = null, string? Artist = null, string? AlbumArtist = null, string? Album = null,
    string? TrackNo = null, string? DiscNo = null, string? Year = null, string? Genre = null)
{
    private static readonly Regex Number = new(@"^\d{1,4}(/\d{1,4})?$", RegexOptions.Compiled);

    private static readonly Regex Date = new(@"^\d{4}(-\d{2}(-\d{2})?)?$", RegexOptions.Compiled);

    public const int MaxTextLength = 300;

    /// <summary>Die Felder, die sich ändern, als ffmpeg-Metadaten-Schlüssel und Wert (leer = löschen).</summary>
    public IReadOnlyList<(string Key, string Value)> Fields
    {
        get
        {
            var fields = new List<(string, string)>();
            void Add(string key, string? value)
            {
                if (value is not null) fields.Add((key, value.Trim()));
            }
            Add("title", Title);
            Add("artist", Artist);
            Add("album_artist", AlbumArtist);
            Add("album", Album);
            Add("track", TrackNo);
            Add("disc", DiscNo);
            Add("date", Year);
            Add("genre", Genre);
            return fields;
        }
    }

    public bool IsEmpty => Fields.Count == 0;

    /// <exception cref="ArgumentException">Nichts zu ändern, Text zu lang oder ungültige Nummer bzw. Jahreszahl.</exception>
    public TagChanges Validated()
    {
        if (IsEmpty) throw new ArgumentException("Es gibt nichts zu ändern.");
        foreach (var (key, value) in Fields)
        {
            if (value.Length > MaxTextLength) throw new ArgumentException($"{key}: höchstens {MaxTextLength} Zeichen.");
            if (value.Any(char.IsControl)) throw new ArgumentException($"{key}: enthält Steuerzeichen.");
        }
        if (TrackNo?.Trim() is { Length: > 0 } t && !Number.IsMatch(t)) throw new ArgumentException($"Titelnummer „{t}“: erwartet 3 oder 3/12.");
        if (DiscNo?.Trim() is { Length: > 0 } d && !Number.IsMatch(d)) throw new ArgumentException($"Disc „{d}“: erwartet 1 oder 1/2.");
        if (Year?.Trim() is { Length: > 0 } y && !Date.IsMatch(y)) throw new ArgumentException($"Jahr „{y}“: erwartet 2021 oder 2021-05-14.");
        return this;
    }
}

/// <summary>
/// Schreibt Tags in Audiodateien (MP3, FLAC, M4A, Ogg, Opus …) per ffmpeg ohne Neukodierung: Die Datei wird neben dem Original
/// als Kopie mit neuen Tags erzeugt und ersetzt es erst, wenn ffmpeg fehlerfrei fertig ist. Bilder (Cover) und Ton bleiben unverändert.
/// </summary>
public sealed class TagWriter(string ffmpegPath)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    /// <summary>Ogg und Opus tragen die Tags am Audiostream statt am Container.</summary>
    private static readonly HashSet<string> StreamTagged = new(StringComparer.OrdinalIgnoreCase) { ".ogg", ".oga", ".opus" };

    public static TagWriter FromEnvironment() => new(Environment.GetEnvironmentVariable("MEDIACENTER_FFMPEG") ?? "ffmpeg");

    /// <summary>Die ffmpeg-Argumente für diese Änderung (ohne Programmnamen); für Tests sichtbar.</summary>
    public static IReadOnlyList<string> BuildArguments(string input, string output, TagChanges changes)
    {
        var args = new List<string>
        {
            "-hide_banner", "-v", "error", "-y", "-i", input,
            // Ton und eingebettete Bilder, nichts sonst (Datenströme würden manche Container ablehnen).
            "-map", "0:a", "-map", "0:v?", "-c", "copy", "-map_metadata", "0",
        };
        var perStream = StreamTagged.Contains(Path.GetExtension(input));
        foreach (var (key, value) in changes.Fields)
        {
            args.Add("-metadata");
            args.Add($"{key}={value}");
            if (!perStream) continue;
            args.Add("-metadata:s:a:0");
            args.Add($"{key}={value}");
        }
        args.Add(output);
        return args;
    }

    /// <exception cref="ArgumentException">Ungültige Änderung.</exception>
    /// <exception cref="FileNotFoundException">Die Datei oder ffmpeg fehlt.</exception>
    /// <exception cref="InvalidOperationException">ffmpeg ist fehlgeschlagen; die Datei bleibt unverändert.</exception>
    /// <exception cref="IOException">Die Datei ist gesperrt (läuft sie gerade im Player?); sie bleibt unverändert.</exception>
    public async Task WriteAsync(string path, TagChanges changes, CancellationToken ct = default)
    {
        changes.Validated();
        if (!File.Exists(path)) throw new FileNotFoundException($"Datei nicht gefunden: {path}", path);

        var temp = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".mc-tags" + Path.GetExtension(path));
        try
        {
            await RunAsync(BuildArguments(path, temp, changes), ct);
            if (!File.Exists(temp) || new FileInfo(temp).Length == 0) throw new InvalidOperationException("ffmpeg hat keine Ausgabedatei erzeugt.");

            try
            {
                File.Replace(temp, path, null);
            }
            catch (IOException ex)
            {
                throw new IOException($"Die Datei ist in Benutzung und ließ sich nicht ersetzen (spielt sie gerade? Dann erst die Wiedergabe beenden): {ex.Message}", ex);
            }
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Aufräumen darf den eigentlichen Fehler nicht überdecken */ }
        }
    }

    private async Task RunAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (Win32Exception ex)
        {
            throw new FileNotFoundException("ffmpeg konnte nicht gestartet werden. Im PATH bereitstellen oder MEDIACENTER_FFMPEG setzen.", ex);
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
                if (process.ExitCode != 0) throw new InvalidOperationException($"ffmpeg schlug fehl (Exit-Code {process.ExitCode}): {(await stderr).Trim()}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"ffmpeg antwortete nicht innerhalb von {Timeout.TotalSeconds:0} s.");
            }
        }
    }
}
