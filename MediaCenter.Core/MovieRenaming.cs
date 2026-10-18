using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaCenter.Core;

public enum RenameStatus
{
    /// <summary>Kann umbenannt werden.</summary>
    Ready,

    /// <summary>Der Name stimmt schon.</summary>
    AlreadyCorrect,

    /// <summary>Kein sicherer Treffer: der Film wird nicht angefasst.</summary>
    NoMatch,

    /// <summary>Eine Datei mit dem neuen Namen gibt es schon (oder mehrere Dateien würden gleich heißen).</summary>
    TargetExists,

    /// <summary>Laufwerk nicht angeschlossen oder Datei nicht mehr da.</summary>
    Offline,

    /// <summary>Der neue Name würde von der Bibliothek nicht wieder als derselbe Film gelesen.</summary>
    Unreadable,
}

/// <summary>Eine Begleitdatei (Untertitel, nfo, Bild), die mit dem Film umbenannt wird; Pfade relativ zum Laufwerk.</summary>
public sealed record SidecarMove(string OldRel, string NewRel);

/// <param name="NewRelPath">Neuer Pfad relativ zum Laufwerk; nur bei <see cref="RenameStatus.Ready"/> gesetzt.</param>
/// <param name="Title">Richtiger Titel laut Dienst.</param>
public sealed record RenameProposal(
    VideoRow Video, RenameStatus Status, string? NewRelPath = null, string? Title = null, int? Year = null, string? Detail = null,
    IReadOnlyList<SidecarMove>? Sidecars = null)
{
    public IReadOnlyList<SidecarMove> SidecarMoves => Sidecars ?? Array.Empty<SidecarMove>();
}

/// <summary>Was zum Zurücknehmen einer Umbenennung nötig ist.</summary>
/// <param name="TitleId">Titel, unter dem der Film vor der Umbenennung stand, samt Name und Jahr; die Umbenennung kann den Titel mitumbenennen.</param>
public sealed record RenameRecord(
    long VolumeId, long FileId, string OldRel, string NewRel, IReadOnlyList<SidecarMove> Sidecars,
    long? TitleId = null, string? TitleName = null, int? TitleYear = null);

public sealed record RenameOutcome(RenameProposal Proposal, RenameRecord? Record, string? Error)
{
    public bool Success => Record is not null;
}

public sealed record UndoOutcome(RenameRecord Record, string? Error)
{
    public bool Success => Error is null;
}

/// <summary>Bildet die Dateinamen: „Titel (Jahr) [Auflösung]“.</summary>
public static class MovieNaming
{
    private const int MaxTitleLength = 150;

    /// <summary>„The_Matrix_Reloaded_(2003)_[1080p]“ (Leerzeichen als Unterstriche); ohne bekannte Größe ohne den Teil in Klammern. Null, wenn aus dem Titel kein Name wird.</summary>
    public static string? BaseName(string title, int year, int? width, int? height)
    {
        var clean = Sanitize(title);
        if (clean.Length == 0) return null;

        var name = $"{clean} ({year})";
        return (ResolutionLabel(width, height) is { } resolution ? $"{name} [{resolution}]" : name).Replace(' ', '_');
    }

    /// <summary>Entfernt, was Windows in Dateinamen nicht erlaubt: ein Doppelpunkt wird zu „ -“, andere Zeichen entfallen.</summary>
    public static string Sanitize(string title)
    {
        var builder = new StringBuilder(title.Length);
        foreach (var c in title)
        {
            if (c == ':') builder.Append(" -");
            else if (c is '<' or '>' or '"' or '|' or '?' or '*' or '\0' || char.IsControl(c)) continue;
            else if (c is '/' or '\\') builder.Append(' ');
            else builder.Append(c);
        }
        var text = Regex.Replace(builder.ToString(), @"\s+", " ").Trim().TrimEnd('.', ' ');
        return text.Length > MaxTitleLength ? text[..MaxTitleLength].TrimEnd('.', ' ') : text;
    }

    /// <summary>
    /// Nach der größeren Seite im 16:9-Maß, damit ein Breitbildfilm (1920×800) als 1080p zählt: 2160p, 1440p, 1080p, 720p, 576p, sonst 480p.
    /// Null, wenn die Größe unbekannt ist.
    /// </summary>
    public static string? ResolutionLabel(int? width, int? height)
    {
        if (width is not > 0 || height is not > 0) return null;

        var effective = Math.Max(height.Value, (int)Math.Round(width.Value * 9.0 / 16));
        return effective switch
        {
            >= 2000 => "2160p",
            >= 1300 => "1440p",
            >= 1000 => "1080p",
            >= 650 => "720p",
            >= 500 => "576p",
            _ => "480p",
        };
    }
}

/// <summary>Findet die Begleitdateien eines Films, die mit ihm umbenannt werden sollen.</summary>
public static class MovieSidecars
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt", ".smi", ".sup", ".nfo", ".jpg", ".jpeg", ".png", ".webp",
    };

    /// <summary>Ordner neben dem Film, in denen der Player Untertitel sucht (mpv <c>sub-file-paths</c> der App).</summary>
    private static readonly string[] SubtitleFolders = { "sub", "subs", "subtitles", "untertitel" };

    private static readonly Regex ArtSuffix = new(@"^-(poster|fanart|backdrop|landscape)\.(jpg|jpeg|png|webp)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Dateien mit dem Namen des Films als Anfang, direkt dahinter ein Punkt und eine bekannte Endung („Film.de.srt“, „Film.nfo“, „Film.jpg“)
    /// oder „-poster.jpg“ und Ähnliches; im Ordner des Films und in dessen Untertitelordnern. Dateien, die zu einem anderen Video im selben
    /// Ordner gehören („Film.Teil2.mkv“ samt „Film.Teil2.srt“), bleiben unberührt.
    /// </summary>
    /// <returns>Alter Pfad und Rest des Namens hinter dem Filmnamen („.de.srt“).</returns>
    public static IReadOnlyList<(string Path, string Suffix)> Find(string videoPath)
    {
        var dir = Path.GetDirectoryName(videoPath);
        if (dir is null || !Directory.Exists(dir)) return Array.Empty<(string, string)>();

        var oldBase = Path.GetFileNameWithoutExtension(videoPath);
        var otherVideoBases = Directory.EnumerateFiles(dir)
            .Where(f => MediaExtensions.Video.Contains(Path.GetExtension(f)) && !string.Equals(f, videoPath, StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(b => b!.Length > oldBase.Length && b.StartsWith(oldBase, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var folders = new List<string> { dir };
        foreach (var name in SubtitleFolders)
        {
            var sub = Path.Combine(dir, name);
            if (Directory.Exists(sub)) folders.Add(sub);
        }

        var result = new List<(string, string)>();
        foreach (var folder in folders)
        {
            foreach (var file in Directory.EnumerateFiles(folder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(file);
                if (name.Length <= oldBase.Length || !name.StartsWith(oldBase, StringComparison.OrdinalIgnoreCase)) continue;
                if (otherVideoBases.Any(b => name.StartsWith(b!, StringComparison.OrdinalIgnoreCase))) continue;

                var suffix = name[oldBase.Length..];
                var extension = Path.GetExtension(name);
                var belongs = (suffix[0] == '.' && Extensions.Contains(extension) && !MediaExtensions.Video.Contains(extension))
                              || ArtSuffix.IsMatch(suffix);
                if (belongs) result.Add((file, suffix));
            }
        }
        return result;
    }
}

/// <summary>
/// Schlägt für Filme die richtige Schreibweise vor („the.matrix.reloaded.2003.1080p.h264.mkv“ → „The_Matrix_Reloaded_(2003)_[1080p].mkv“).
/// Es wird nichts verändert; <see cref="MovieRenamer"/> setzt ausgewählte Vorschläge um.
/// </summary>
public sealed class MovieRenamePlanner(IMovieInfoSource source)
{
    /// <exception cref="PosterSourceUnavailableException">Der Dienst lehnt ab (falscher Schlüssel, Tageslimit): die Planung bricht laut ab.</exception>
    public async Task<IReadOnlyList<RenameProposal>> PlanAsync(
        IEnumerable<VideoRow> videos, VolumeStatus status, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var candidates = videos.Where(v => v.ParsedKind == VideoKind.Movie && v.TitleKind != TitleKind.Series && !v.Missing).ToList();
        var proposals = new List<RenameProposal>();
        var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // schon vergebene neue Pfade dieses Durchlaufs

        for (var i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((i, candidates.Count));
            proposals.Add(await PlanOneAsync(candidates[i], status, planned, ct));
        }
        progress?.Report((candidates.Count, candidates.Count));
        return proposals;
    }

    private async Task<RenameProposal> PlanOneAsync(VideoRow video, VolumeStatus status, HashSet<string> planned, CancellationToken ct)
    {
        if (!status.Connected.TryGetValue(video.VolumeId, out var volume)) return new RenameProposal(video, RenameStatus.Offline, Detail: "Laufwerk nicht angeschlossen.");
        var path = Path.Combine(volume.RootPath, video.RelPath);
        if (!File.Exists(path)) return new RenameProposal(video, RenameStatus.Offline, Detail: "Datei nicht gefunden.");

        var info = await source.LookupAsync(video.TitleName ?? video.ParsedTitle, video.Year, ct);
        if (info is not { Year: { } year }) return new RenameProposal(video, RenameStatus.NoMatch, Detail: "Kein sicherer Treffer.");

        var baseName = MovieNaming.BaseName(info.Title, year, video.Width, video.Height);
        if (baseName is null) return new RenameProposal(video, RenameStatus.NoMatch, Title: info.Title, Year: year, Detail: "Der Titel lässt sich nicht als Dateiname verwenden.");

        var oldBase = Path.GetFileNameWithoutExtension(path);
        if (oldBase == baseName) return new RenameProposal(video, RenameStatus.AlreadyCorrect, Title: info.Title, Year: year);

        var newPath = Path.Combine(Path.GetDirectoryName(path)!, baseName + Path.GetExtension(path));
        var parsed = FileNameParser.Parse(newPath);
        if (!parsed.Certain || parsed.Kind != VideoKind.Movie || parsed.Year != year || PosterNames.MatchKey(parsed.Title) != PosterNames.MatchKey(info.Title))
        {
            return new RenameProposal(video, RenameStatus.Unreadable, Title: info.Title, Year: year,
                Detail: $"Als „{Path.GetFileName(newPath)}“ würde der Film nicht wieder als „{info.Title} ({year})“ erkannt.");
        }

        return PlanMoves(video, volume, path, newPath, baseName, info.Title, year, planned);
    }

    /// <summary>
    /// Plan für einen vom Nutzer gewählten Dateinamen (ohne Endung, die bleibt); Begleitdateien ziehen mit. Ändert nichts.
    /// Der Name muss von der Bibliothek als Film mit Jahr gelesen werden („Mayday (2026)“), sonst fiele der Film aus seinem Titel in die Inbox.
    /// </summary>
    /// <exception cref="ArgumentException">Der Name ist als Dateiname unbrauchbar.</exception>
    public static RenameProposal PlanManual(VideoRow video, VolumeStatus status, string newBaseName)
    {
        var baseName = newBaseName.Trim();
        if (baseName.Length == 0) throw new ArgumentException("Der Dateiname ist leer.");
        if (baseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("Der Dateiname darf keines der Zeichen \\ / : * ? \" < > | enthalten.");
        if (baseName.EndsWith('.')) throw new ArgumentException("Der Dateiname darf nicht mit einem Punkt enden.");
        if (MediaExtensions.Video.Contains(Path.GetExtension(baseName))) throw new ArgumentException("Bitte ohne Dateiendung eingeben; die Endung bleibt erhalten.");

        if (!status.Connected.TryGetValue(video.VolumeId, out var volume)) return new RenameProposal(video, RenameStatus.Offline, Detail: "Laufwerk nicht angeschlossen.");
        var path = Path.Combine(volume.RootPath, video.RelPath);
        if (!File.Exists(path)) return new RenameProposal(video, RenameStatus.Offline, Detail: "Datei nicht gefunden.");

        if (Path.GetFileNameWithoutExtension(path) == baseName) return new RenameProposal(video, RenameStatus.AlreadyCorrect);

        var newPath = Path.Combine(Path.GetDirectoryName(path)!, baseName + Path.GetExtension(path));
        var parsed = FileNameParser.Parse(newPath);
        if (parsed.Kind != VideoKind.Movie)
        {
            return new RenameProposal(video, RenameStatus.Unreadable,
                Detail: $"Als „{Path.GetFileName(newPath)}“ würde die Bibliothek die Datei als Serienfolge lesen (z. B. wegen „S01E02“ im Namen).");
        }
        if (!parsed.Certain)
        {
            return new RenameProposal(video, RenameStatus.Unreadable,
                Detail: $"Der Name braucht ein Jahr, z. B. „{parsed.Title.Replace(' ', '_')}_(2026)“. Sonst erkennt die Bibliothek den Film nicht sicher und er fiele in die Inbox.");
        }

        return PlanMoves(video, volume, path, newPath, baseName, parsed.Title, parsed.Year, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Film und Begleitdateien auf den neuen Namen; <see cref="RenameStatus.TargetExists"/>, wenn ein Ziel schon da ist oder doppelt vergeben würde.</summary>
    private static RenameProposal PlanMoves(VideoRow video, VolumeInfo volume, string path, string newPath, string baseName, string title, int? year, HashSet<string> planned)
    {
        var moves = new List<(string From, string To)> { (path, newPath) };
        foreach (var (sidecar, suffix) in MovieSidecars.Find(path)) moves.Add((sidecar, Path.Combine(Path.GetDirectoryName(sidecar)!, baseName + suffix)));

        foreach (var (from, to) in moves)
        {
            var sameFileOtherCase = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
            if ((!sameFileOtherCase && File.Exists(to)) || !planned.Add(to))
            {
                return new RenameProposal(video, RenameStatus.TargetExists, Title: title, Year: year,
                    Detail: $"„{Path.GetFileName(to)}“ gibt es schon oder würde mehrfach vergeben.");
            }
        }

        string Rel(string absolute) => Path.GetRelativePath(volume.RootPath, absolute);
        return new RenameProposal(video, RenameStatus.Ready, Rel(newPath), title, year,
            Sidecars: moves.Skip(1).Select(m => new SidecarMove(Rel(m.From), Rel(m.To))).ToList());
    }
}

/// <summary>
/// Benennt Dateien um und führt dabei die Bibliothek mit: derselbe Eintrag behält seine Video-ID, damit Fortschritt, Untertitel- und
/// Farbeinstellungen und Cover erhalten bleiben. Schlägt irgendein Schritt eines Films fehl, wird alles zu diesem Film zurückgenommen.
/// </summary>
public sealed class MovieRenamer(LibraryDb db)
{
    /// <summary>Setzt die umbenennbaren Vorschläge um; andere werden übersprungen (nicht im Ergebnis).</summary>
    public IReadOnlyList<RenameOutcome> Apply(IEnumerable<RenameProposal> proposals, VolumeStatus status, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var outcomes = new List<RenameOutcome>();
        var done = 0;
        foreach (var proposal in proposals.Where(p => p is { Status: RenameStatus.Ready, NewRelPath: not null }))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!status.Connected.TryGetValue(proposal.Video.VolumeId, out var volume)) throw new IOException("Das Laufwerk ist nicht angeschlossen.");
                var record = Execute(volume, proposal.Video.VolumeId, proposal.Video.FileId, proposal.Video.RelPath, proposal.NewRelPath!, proposal.SidecarMoves);
                if (proposal.Video is { TitleId: { } titleId, TitleName: { } titleName }) record = record with { TitleId = titleId, TitleName = titleName, TitleYear = proposal.Video.Year };
                outcomes.Add(new RenameOutcome(proposal, record, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                outcomes.Add(new RenameOutcome(proposal, null, ex.Message));
            }
            progress?.Report(++done);
        }
        return outcomes;
    }

    /// <summary>Nimmt Umbenennungen zurück (die jüngste zuerst).</summary>
    public IReadOnlyList<UndoOutcome> Undo(IEnumerable<RenameRecord> records, VolumeStatus status)
    {
        var outcomes = new List<UndoOutcome>();
        foreach (var record in records.Reverse())
        {
            try
            {
                if (!status.Connected.TryGetValue(record.VolumeId, out var volume)) throw new IOException("Das Laufwerk ist nicht angeschlossen.");
                Execute(volume, record.VolumeId, record.FileId, record.NewRel, record.OldRel, record.Sidecars.Select(s => new SidecarMove(s.NewRel, s.OldRel)).ToList());
                if (record.TitleId is { } titleId && record.TitleName is { } titleName) db.RestoreTitle(record.FileId, titleId, titleName, record.TitleYear);
                outcomes.Add(new UndoOutcome(record, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                outcomes.Add(new UndoOutcome(record, ex.Message));
            }
        }
        return outcomes;
    }

    private RenameRecord Execute(VolumeInfo volume, long volumeId, long fileId, string oldRel, string newRel, IReadOnlyList<SidecarMove> sidecars)
    {
        string Abs(string rel) => Path.Combine(volume.RootPath, rel);

        var moves = new List<(string From, string To)> { (Abs(oldRel), Abs(newRel)) };
        moves.AddRange(sidecars.Select(s => (Abs(s.OldRel), Abs(s.NewRel))));

        var done = new List<(string From, string To)>();
        try
        {
            foreach (var move in moves)
            {
                MoveFile(move.From, move.To);
                done.Add(move);
            }
            db.RenameVideoFile(fileId, newRel, FileNameParser.Parse(Abs(newRel)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            var problems = new List<string>();
            foreach (var move in Enumerable.Reverse(done))
            {
                try
                {
                    MoveFile(move.To, move.From);
                }
                catch (Exception back) when (back is IOException or UnauthorizedAccessException)
                {
                    problems.Add($"{Path.GetFileName(move.To)} ließ sich nicht zurückbenennen: {back.Message}");
                }
            }
            var message = $"{Path.GetFileName(Abs(oldRel))}: {ex.Message}";
            throw new IOException(problems.Count == 0 ? $"{message} Nichts wurde verändert." : $"{message} ACHTUNG, nicht alles ließ sich zurücknehmen: {string.Join(" ", problems)}", ex);
        }

        return new RenameRecord(volumeId, fileId, oldRel, newRel, sidecars);
    }

    /// <summary>Benennt um, ohne etwas zu überschreiben. Nur die Groß-/Kleinschreibung ändern geht über einen Zwischennamen.</summary>
    private static void MoveFile(string from, string to)
    {
        if (!File.Exists(from)) throw new FileNotFoundException($"Datei nicht gefunden: {from}", from);
        if (from == to) return;

        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            var temp = to + ".rename-tmp";
            File.Move(from, temp);
            File.Move(temp, to);
            return;
        }
        if (File.Exists(to) || Directory.Exists(to)) throw new IOException($"„{Path.GetFileName(to)}“ gibt es schon.");
        File.Move(from, to);
    }
}

/// <summary>Merkt sich die zuletzt vorgenommenen Umbenennungen in einer Datei, damit sie sich zurücknehmen lassen.</summary>
public static class RenameJournal
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void Save(string path, IReadOnlyList<RenameRecord> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(records, Json));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Leer, wenn es noch kein Protokoll gibt; eine kaputte Datei ist ein lauter Fehler.</summary>
    public static IReadOnlyList<RenameRecord> Load(string path)
    {
        if (!File.Exists(path)) return Array.Empty<RenameRecord>();
        try
        {
            return JsonSerializer.Deserialize<List<RenameRecord>>(File.ReadAllText(path)) ?? throw new InvalidDataException("Datei ist leer.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Das Umbenennungs-Protokoll ist fehlerhaft: {path}. ({ex.Message})", ex);
        }
    }

    public static void Clear(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
