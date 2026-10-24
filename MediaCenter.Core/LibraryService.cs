namespace MediaCenter.Core;

public sealed record ScanFailure(string Path, string Error);

/// <param name="Offline">Das Laufwerk der Quelle ist nicht angeschlossen; es wurde nichts verändert.</param>
/// <param name="Moved">Dateien, die der Scan an einem neuen Ort wiedergefunden hat; ihre Nutzerdaten sind mitgewandert (zählt nicht zu <paramref name="Added"/>).</param>
/// <param name="Restored">Dateien, die als fehlend galten und unverändert wieder an ihrem Platz liegen (zählt zusätzlich zu <paramref name="Unchanged"/>).</param>
public sealed record ScanResult(
    int Added, int Updated, int Unchanged, int MarkedMissing, IReadOnlyList<ScanFailure> Failures, bool Offline = false, int Moved = 0, int Restored = 0)
{
    public static ScanResult OfflineResult { get; } = new(0, 0, 0, 0, Array.Empty<ScanFailure>(), Offline: true);
}

public sealed record UnresolvedVolume(VolumeInfo Volume, VolumeMatch Match);

/// <summary>Welche bekannten Laufwerke gerade angeschlossen sind und wo.</summary>
public sealed record VolumeStatus(IReadOnlyDictionary<long, VolumeInfo> Connected, IReadOnlyList<UnresolvedVolume> Unresolved)
{
    /// <summary>Absoluter Pfad aktuell, oder null, wenn das Laufwerk nicht angeschlossen ist.</summary>
    public string? ResolvePath(long volumeId, string relPath) =>
        Connected.TryGetValue(volumeId, out var volume) ? Path.Combine(volume.RootPath, relPath) : null;
}

/// <summary>Ein Laufwerk ist nicht eindeutig zuzuordnen; der Nutzer muss entscheiden.</summary>
public sealed class VolumeConfirmationRequiredException(VolumeInfo volume, VolumeMatch match)
    : Exception($"Laufwerk {volume.RootPath} ({volume.Label}, {volume.Serial}) nicht eindeutig: {match.Reason}")
{
    public VolumeInfo Volume { get; } = volume;

    public VolumeMatch Match { get; } = match;
}

/// <summary>Quellen hinzufügen, Laufwerke auflösen und Quellen scannen.</summary>
public sealed class LibraryService(LibraryDb db, IVolumeProvider volumes, IMediaProbe probe)
{
    private static HashSet<string> AudioExtensions => MediaExtensions.Audio;

    /// <summary>Gleicht angeschlossene Laufwerke mit den bekannten ab und merkt sich ihren aktuellen Einhängepunkt.</summary>
    public VolumeStatus RefreshVolumes()
    {
        var mounted = volumes.GetMountedVolumes();
        var known = db.GetVolumes();
        var connected = new Dictionary<long, VolumeInfo>();
        var unresolved = new List<UnresolvedVolume>();

        foreach (var volume in mounted)
        {
            var match = VolumeMatcher.Match(volume, known, mounted);
            switch (match.Kind)
            {
                case VolumeMatchKind.Matched:
                    db.TouchVolume(match.VolumeId!.Value, volume);
                    connected[match.VolumeId.Value] = volume;
                    break;
                case VolumeMatchKind.NeedsConfirmation when match.Candidates.Count > 0:
                    unresolved.Add(new UnresolvedVolume(volume, match));
                    break;
            }
        }
        return new VolumeStatus(connected, unresolved);
    }

    public async Task<(long SourceId, ScanResult Scan)> AddFolderAsync(string folder, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(folder);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"Ordner existiert nicht: {full}");
        return await AddAndScanAsync(full, SourceKind.Folder, ct);
    }

    public async Task<(long SourceId, ScanResult Scan)> AddFileAsync(string file, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(file);
        if (!File.Exists(full)) throw new FileNotFoundException($"Datei existiert nicht: {full}", full);
        return await AddAndScanAsync(full, SourceKind.File, ct);
    }

    private async Task<(long, ScanResult)> AddAndScanAsync(string fullPath, SourceKind kind, CancellationToken ct)
    {
        var mounted = volumes.GetMountedVolumes();
        var volume = FindVolume(fullPath, mounted)
            ?? throw new NotSupportedException(
                $"'{fullPath}' liegt auf keinem unterstützten lokalen Laufwerk (Netzlaufwerke werden noch nicht unterstützt).");

        var match = VolumeMatcher.Match(volume, db.GetVolumes(), mounted);
        var volumeId = match.Kind switch
        {
            VolumeMatchKind.Matched => match.VolumeId!.Value,
            VolumeMatchKind.New => db.AddVolume(volume),
            _ => throw new VolumeConfirmationRequiredException(volume, match),
        };

        var rel = Path.GetRelativePath(volume.RootPath, fullPath);
        if (rel == ".") rel = "";
        if (kind == SourceKind.File) db.UnremoveFile(volumeId, rel); // wer eine entfernte Datei ausdrücklich hinzufügt, will sie wieder
        var sourceId = db.AddSource(volumeId, kind, rel);

        var status = RefreshVolumes();
        var source = db.GetSources().Single(s => s.Id == sourceId);
        return (sourceId, await ScanAsync(source, status, ct));
    }

    /// <summary>Scannt alle Quellen; Quellen auf abgesteckten Laufwerken werden übersprungen.</summary>
    public async Task<IReadOnlyList<(Source Source, ScanResult Result)>> RescanAllAsync(CancellationToken ct = default)
    {
        var status = RefreshVolumes();
        var results = new List<(Source, ScanResult)>();
        foreach (var source in db.GetSources())
        {
            results.Add((source, await ScanAsync(source, status, ct)));
        }
        return results;
    }

    /// <summary>Nur ein Scan zur Zeit: die automatische Aktualisierung und der Knopf „Neu scannen“ dürfen sich nicht überlappen.</summary>
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    public async Task<ScanResult> ScanAsync(Source source, VolumeStatus status, CancellationToken ct = default)
    {
        await _scanLock.WaitAsync(ct);
        try
        {
            return await ScanCoreAsync(source, status, ct);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private async Task<ScanResult> ScanCoreAsync(Source source, VolumeStatus status, CancellationToken ct)
    {
        if (!status.Connected.TryGetValue(source.VolumeId, out var volume)) return ScanResult.OfflineResult;

        var absolute = Path.Combine(volume.RootPath, source.RelPath);
        var known = db.GetFilesUnder(source.VolumeId, source.RelPath);
        var removed = db.GetRemovedFiles(source.VolumeId);
        var withoutFingerprint = db.GetFileIdsWithoutFingerprint();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var movedIds = new HashSet<long>();
        var failures = new List<ScanFailure>();
        int added = 0, updated = 0, unchanged = 0, moved = 0, restored = 0;

        foreach (var entry in EnumerateEntries(source, absolute))
        {
            ct.ThrowIfCancellationRequested();

            var path = entry.Path;
            var rel = Path.GetRelativePath(volume.RootPath, path);
            if (removed.Contains(rel)) continue; // vom Nutzer aus der Bibliothek entfernt, liegt aber noch auf der Platte
            seen.Add(rel);

            // Eine Disc als Ordner ist eine Einheit: Größe und Zeit stehen für alle ihre Dateien.
            long length;
            DateTime written;
            try
            {
                (length, written) = entry.Disc != DiscKind.None ? DiscDetector.Signature(path) : (new FileInfo(path).Length, new FileInfo(path).LastWriteTimeUtc);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add(new ScanFailure(path, ex.Message));
                continue;
            }

            if (known.TryGetValue(rel, out var existing) && existing.Size == length &&
                existing.MtimeUtc == written.ToString("O"))
            {
                if (existing.Missing)
                {
                    db.SetMissing(existing.Id, false);
                    restored++;
                }
                if (withoutFingerprint.Contains(existing.Id))
                {
                    try
                    {
                        db.SetFingerprint(existing.Id, entry.Disc != DiscKind.None ? DiscDetector.Fingerprint(path) : FileFingerprint.Compute(path));
                    }
                    catch (IOException ex)
                    {
                        failures.Add(new ScanFailure(path, ex.Message));
                    }
                }
                unchanged++;
                continue;
            }

            try
            {
                var fingerprint = entry.Disc != DiscKind.None ? DiscDetector.Fingerprint(path) : FileFingerprint.Compute(path);
                var wasKnown = known.ContainsKey(rel);
                if (!wasKnown && FindMovedFile(fingerprint, length, status) is { } before)
                {
                    db.MoveFile(before.FileId, source.VolumeId, rel);
                    movedIds.Add(before.FileId);
                    moved++;
                }

                ProbeResult result;
                if (entry.IsDisc)
                {
                    if (probe is not IDiscProbe discProbe) throw new NotSupportedException("Dieser Scan kann keine DVD und Blu-ray lesen.");
                    result = (await discProbe.ProbeDiscAsync(path, entry.Disc, ct)).Result;
                }
                else
                {
                    result = await probe.ProbeAsync(path, ct);
                }
                var isAudio = !entry.IsDisc && AudioExtensions.Contains(Path.GetExtension(path));
                if (isAudio && !result.HasAudio) throw new InvalidDataException("Datei enthält keinen Audiostream.");
                if (!isAudio && result.Video is null) throw new InvalidDataException("Datei enthält keinen Videostream.");
                var fileId = db.UpsertFile(source.VolumeId, rel, length, written, result, fingerprint);
                if (isAudio) db.ApplyTrack(fileId, TrackTags.From(result.Tags, path));
                else db.ApplyScanned(fileId, FileNameParser.Parse(path));
                if (wasKnown) updated++;
                else if (!movedIds.Contains(fileId)) added++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(new ScanFailure(path, ex.Message));
            }
        }

        // Fehlt etwas auf einem angeschlossenen Laufwerk, wird es nur markiert, nie gelöscht.
        var missing = known.Where(k => !seen.Contains(k.Key) && !k.Value.Missing && !movedIds.Contains(k.Value.Id)).Select(k => k.Value.Id).ToList();
        foreach (var id in missing) db.SetMissing(id, true);

        return new ScanResult(added, updated, unchanged, missing.Count, failures, Moved: moved, Restored: restored);
    }

    /// <summary>
    /// Eine bekannte Datei mit gleichem Fingerabdruck, die an ihrem alten Ort nicht mehr liegt: dann wurde sie verschoben.
    /// Liegt das Laufwerk der alten Datei nicht an, bleibt es bei „unbekannt“ (die Datei könnte dort noch sein); Kopien, bei denen das
    /// Original noch liegt, bleiben eigene Einträge.
    /// </summary>
    private FileLocation? FindMovedFile(string fingerprint, long size, VolumeStatus status)
    {
        foreach (var candidate in db.FindFilesByFingerprint(fingerprint, size))
        {
            var oldPath = status.ResolvePath(candidate.VolumeId, candidate.RelPath);
            if (oldPath is not null && !File.Exists(oldPath)) return candidate;
        }
        return null;
    }

    private static IEnumerable<ScanEntry> EnumerateEntries(Source source, string absolute)
    {
        if (source.Kind == SourceKind.File)
            return File.Exists(absolute) ? new[] { new ScanEntry(absolute, DiscKind.None, DiscDetector.IsImage(absolute)) } : Array.Empty<ScanEntry>();
        if (!Directory.Exists(absolute)) return Array.Empty<ScanEntry>();

        return DiscDetector.Enumerate(absolute);
    }

    /// <summary>Das Laufwerk mit dem längsten passenden Einhängepunkt.</summary>
    private static VolumeInfo? FindVolume(string fullPath, IReadOnlyList<VolumeInfo> mounted)
    {
        var path = fullPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return mounted
            .Where(v => path.StartsWith(v.RootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v.RootPath.Length)
            .FirstOrDefault();
    }
}
