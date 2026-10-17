namespace MediaCenter.Core;

public sealed record ArtworkFailure(string Subject, string Error);

/// <param name="Created">Neu angelegte oder ersetzte Bilder.</param>
/// <param name="Skipped">Titel, die nichts brauchten.</param>
/// <param name="Unreachable">Titel, deren Dateien gerade nicht erreichbar sind (Laufwerk offline oder Datei fehlt).</param>
public sealed record ArtworkResult(int Created, int Skipped, int Unreachable, IReadOnlyList<ArtworkFailure> Failures);

/// <summary>Quellen für Poster aus dem Internet (je Titelart, null = keine) samt Ablage der geladenen Bilder im Cover-Cache.</summary>
public sealed record OnlineArtwork(IPosterSource? Series, IPosterSource? Movies, RemoteImages Images);

/// <summary>
/// Beschafft Cover. Reihenfolge je Platz: eigenes Bild → Bild im Ordner → eingebettet → Poster aus dem Internet → Standbild.
/// Das Internet wird nur gefragt, wenn <see cref="Online"/> gesetzt ist und lokal kein Poster da ist.
/// Bilder liegen verkleinert im Cache; die Datenbank merkt sich nur Verweis und Herkunft.
/// </summary>
public sealed class ArtworkService(LibraryDb db, IImageTool images, IMediaProbe probe, string cacheDir)
{
    /// <summary>Eine ergebnislose Suche im Internet gilt so lange, bevor erneut gefragt wird (neue Serien kommen später dazu).</summary>
    private static readonly TimeSpan NoMatchRetry = TimeSpan.FromDays(14);

    /// <summary>
    /// Version des Suchverfahrens in den Clients (Titelvergleich, Abfragevarianten). Bei jeder Änderung erhöhen: gemerkte
    /// ergebnislose Suchen älterer Versionen gelten dann nicht mehr und werden mit dem besseren Verfahren neu versucht.
    /// </summary>
    private const int MatchVersion = 2;

    /// <summary>Null schaltet das Internet ab; die Einstellung der App setzt es.</summary>
    public OnlineArtwork? Online { get; set; }

    /// <summary>Zustand eines Durchlaufs: fällt ein Anbieter aus, wird das einmal gemeldet und er nicht weiter gefragt (die anderen schon).</summary>
    private sealed class OnlineRun
    {
        public HashSet<string> Unavailable { get; } = new();
    }

    private static readonly (int W, int H) PosterSize = (400, 600);
    private static readonly (int W, int H) BackdropSize = (1280, 720);
    private static readonly (int W, int H) ThumbSize = (480, 270);

    public string CacheDir => cacheDir;

    /// <summary>Absoluter Pfad des Covers oder null, wenn es keins gibt.</summary>
    public string? GetPath(ArtworkOwner owner, long ownerId, ArtworkKind kind) =>
        db.GetArtwork(owner, ownerId, kind) is { } row ? Path.Combine(cacheDir, row.File) : null;

    /// <summary>Poster, sonst das Querformat-Bild (die Oberfläche schneidet es zu); null → Platzhalter mit Titel.</summary>
    public (string Path, ArtworkKind Kind)? GetBestForTitle(long titleId)
    {
        if (GetPath(ArtworkOwner.Title, titleId, ArtworkKind.Poster) is { } poster) return (poster, ArtworkKind.Poster);
        if (GetPath(ArtworkOwner.Title, titleId, ArtworkKind.Landscape) is { } landscape) return (landscape, ArtworkKind.Landscape);
        return null;
    }

    /// <summary>
    /// Füllt fehlende Cover. Vom Nutzer gesetzte Bilder werden nie angefasst; mit force werden automatisch
    /// beschaffte neu bestimmt (z. B. nachdem ein poster.jpg in den Ordner gelegt wurde).
    /// </summary>
    public async Task<ArtworkResult> RefreshAsync(VolumeStatus status, bool force = false, CancellationToken ct = default)
    {
        int created = 0, skipped = 0, unreachable = 0;
        var failures = new List<ArtworkFailure>();
        var online = new OnlineRun();

        foreach (var title in db.GetTitles())
        {
            ct.ThrowIfCancellationRequested();

            var reachable = db.GetTitleVideos(title.Id)
                .Select(v => (Video: v, Path: v.Missing ? null : status.ResolvePath(v.VolumeId, v.RelPath)))
                .Where(x => x.Path is not null && File.Exists(x.Path))
                .Select(x => (x.Video, Path: x.Path!))
                .ToList();
            if (reachable.Count == 0)
            {
                unreachable++;
                continue;
            }

            try
            {
                var made = await RefreshTitleAsync(title, reachable, force, online, failures, ct);
                made += await RefreshEpisodeThumbsAsync(title, reachable, force, ct, failures);
                if (made == 0) skipped++;
                created += made;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(new ArtworkFailure(title.Name, ex.Message));
            }
        }
        return new ArtworkResult(created, skipped, unreachable, failures);
    }

    private async Task<int> RefreshTitleAsync(
        TitleRow title, List<(TitleVideo Video, string Path)> reachable, bool force, OnlineRun online, List<ArtworkFailure> failures, CancellationToken ct)
    {
        var needPoster = Needs(ArtworkOwner.Title, title.Id, ArtworkKind.Poster, force);
        var needLandscape = Needs(ArtworkOwner.Title, title.Id, ArtworkKind.Landscape, force);
        if (!needPoster && !needLandscape) return 0;

        var paths = reachable.Select(r => r.Path).ToList();
        var sidecar = title.Kind == TitleKind.Movie ? SidecarFinder.ForMovie(paths[0]) : SidecarFinder.ForSeries(paths);
        var created = 0;

        // Eingebettete Bilder nur auslesen, wenn ein Platz noch offen ist.
        (StreamInfo? Poster, StreamInfo? Landscape)? embedded = null;
        async Task<(StreamInfo? Poster, StreamInfo? Landscape)> Embedded()
        {
            if (embedded is null) embedded = EmbeddedArtwork.Choose(await probe.ProbeAsync(paths[0], ct));
            return embedded.Value;
        }

        if (needPoster)
        {
            var file = CacheFile(ArtworkOwner.Title, title.Id, ArtworkKind.Poster);
            if (sidecar.Poster is { } poster)
            {
                await images.ScaleImageAsync(poster, Absolute(file), PosterSize.W, PosterSize.H, ct);
                db.SetArtwork(ArtworkOwner.Title, title.Id, ArtworkKind.Poster, ArtworkSource.Sidecar, file);
                created++;
            }
            else if ((await Embedded()).Poster is { } picture)
            {
                await images.ExtractStreamImageAsync(paths[0], picture.Index, Absolute(file), PosterSize.W, PosterSize.H, ct);
                db.SetArtwork(ArtworkOwner.Title, title.Id, ArtworkKind.Poster, ArtworkSource.Embedded, file);
                created++;
            }
            else if (await TryOnlinePosterAsync(title, file, force, online, failures, ct))
            {
                created++;
            }
            // Kein Poster gefunden: bleibt leer; die Oberfläche zeigt das Querformat-Bild oder den Platzhalter.
        }

        if (needLandscape)
        {
            var file = CacheFile(ArtworkOwner.Title, title.Id, ArtworkKind.Landscape);
            if (sidecar.Landscape is { } landscape)
            {
                await images.ScaleImageAsync(landscape, Absolute(file), BackdropSize.W, BackdropSize.H, ct);
                db.SetArtwork(ArtworkOwner.Title, title.Id, ArtworkKind.Landscape, ArtworkSource.Sidecar, file);
            }
            else if ((await Embedded()).Landscape is { } picture)
            {
                await images.ExtractStreamImageAsync(paths[0], picture.Index, Absolute(file), BackdropSize.W, BackdropSize.H, ct);
                db.SetArtwork(ArtworkOwner.Title, title.Id, ArtworkKind.Landscape, ArtworkSource.Embedded, file);
            }
            else
            {
                var (video, path) = reachable[0];
                await images.GrabFrameAsync(path, FramePosition(video.DurationSeconds), Absolute(file), BackdropSize.W, BackdropSize.H, ct);
                db.SetArtwork(ArtworkOwner.Title, title.Id, ArtworkKind.Landscape, ArtworkSource.Frame, file);
            }
            created++;
        }
        return created;
    }

    /// <summary>
    /// Sucht das Poster eines Titels im Internet (Serien bei TVmaze, Filme bei OMDb). Fehler stoppen den Durchlauf nicht: fällt ein
    /// Anbieter aus (Netz weg, falscher Schlüssel, Tageslimit), steht einmal eine Meldung in den Fehlern und er wird nicht weiter
    /// gefragt; ein Fehler bei einem Titel betrifft nur diesen.
    /// </summary>
    private async Task<bool> TryOnlinePosterAsync(TitleRow title, string file, bool force, OnlineRun run, List<ArtworkFailure> failures, CancellationToken ct)
    {
        if (Online is not { } online) return false;
        if ((title.Kind == TitleKind.Series ? online.Series : online.Movies) is not { } finder || run.Unavailable.Contains(finder.Provider)) return false;

        var provider = finder.Provider;
        var query = $"{title.Name}|{title.Year}|v{MatchVersion}";
        if (!force && db.GetOnlineLookup(title.Id, provider) is { Found: false } miss && miss.Query == query && DateTime.UtcNow - miss.CheckedUtc < NoMatchRetry)
        {
            return false;
        }

        try
        {
            var poster = await finder.FindAsync(title.Name, title.Year, ct);
            if (poster is null)
            {
                db.SetOnlineLookup(title.Id, provider, query, found: false);
                return false;
            }

            await online.Images.DownloadAsync(poster.ImageUrl, file, PosterSize.W, PosterSize.H, ct);
            db.SetArtwork(ArtworkOwner.Title, title.Id, ArtworkKind.Poster, ArtworkSource.Online, file);
            db.SetOnlineLookup(title.Id, provider, query, found: true);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or PosterSourceUnavailableException || (ex is TaskCanceledException or TimeoutException && !ct.IsCancellationRequested))
        {
            run.Unavailable.Add(provider);
            failures.Add(new ArtworkFailure($"Online-Cover ({provider})", $"nicht verfügbar: {ex.Message}"));
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failures.Add(new ArtworkFailure(title.Name, $"Online-Poster nicht übernommen: {ex.Message}"));
            return false;
        }
    }

    /// <summary>Standbilder je Episode für die Episodenliste.</summary>
    private async Task<int> RefreshEpisodeThumbsAsync(
        TitleRow title, List<(TitleVideo Video, string Path)> reachable, bool force, CancellationToken ct, List<ArtworkFailure> failures)
    {
        if (title.Kind != TitleKind.Series) return 0;

        var created = 0;
        foreach (var (video, path) in reachable)
        {
            if (!Needs(ArtworkOwner.Video, video.VideoId, ArtworkKind.Landscape, force)) continue;
            try
            {
                var file = CacheFile(ArtworkOwner.Video, video.VideoId, ArtworkKind.Landscape);
                await images.GrabFrameAsync(path, FramePosition(video.DurationSeconds), Absolute(file), ThumbSize.W, ThumbSize.H, ct);
                db.SetArtwork(ArtworkOwner.Video, video.VideoId, ArtworkKind.Landscape, ArtworkSource.Frame, file);
                created++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(new ArtworkFailure($"{title.Name} S{video.Season:00}E{video.Episode:00}", ex.Message));
            }
        }
        return created;
    }

    /// <summary>Ersetzt das Cover durch ein eigenes Bild; ab dann fasst keine automatische Suche es an.</summary>
    public async Task SetUserArtworkAsync(ArtworkOwner owner, long ownerId, ArtworkKind kind, string imagePath, CancellationToken ct = default)
    {
        if (!File.Exists(imagePath)) throw new FileNotFoundException($"Bild existiert nicht: {imagePath}", imagePath);

        var size = SizeOf(owner, kind);
        var file = CacheFile(owner, ownerId, kind);
        await images.ScaleImageAsync(imagePath, Absolute(file), size.W, size.H, ct);
        db.SetArtwork(owner, ownerId, kind, ArtworkSource.User, file);
    }

    /// <summary>Wie <see cref="SetUserArtworkAsync"/>, nur lädt es das Bild von einer Webadresse (z. B. Poster, das der Nutzer im Browser gewählt hat).</summary>
    public async Task SetUserArtworkFromUrlAsync(RemoteImages remote, ArtworkOwner owner, long ownerId, ArtworkKind kind, string url, CancellationToken ct = default)
    {
        var size = SizeOf(owner, kind);
        var file = CacheFile(owner, ownerId, kind);
        await remote.DownloadAsync(url, file, size.W, size.H, ct);
        db.SetArtwork(owner, ownerId, kind, ArtworkSource.User, file);
    }

    private static (int W, int H) SizeOf(ArtworkOwner owner, ArtworkKind kind) =>
        kind == ArtworkKind.Poster ? PosterSize : owner == ArtworkOwner.Title ? BackdropSize : ThumbSize;

    /// <summary>Entfernt das Cover samt Datei; ein späterer Refresh beschafft es wieder automatisch.</summary>
    public void ClearArtwork(ArtworkOwner owner, long ownerId, ArtworkKind kind)
    {
        if (db.GetArtwork(owner, ownerId, kind) is { } row && File.Exists(Path.Combine(cacheDir, row.File)))
        {
            File.Delete(Path.Combine(cacheDir, row.File));
        }
        db.DeleteArtwork(owner, ownerId, kind);
    }

    private static readonly (int W, int H) AlbumSize = (480, 480);

    /// <summary>Album-Cover im Cache oder null.</summary>
    public string? GetAlbumPath(string albumKey) => db.GetAlbumArt(albumKey) is { } file ? Path.Combine(cacheDir, file) : null;

    /// <summary>
    /// Füllt fehlende Album-Cover: Bild im Ordner (nur wenn alle erreichbaren Titel des Albums dort liegen), sonst ein in einem
    /// Titel eingebettetes Bild. Ein Album ohne Bild bleibt leer; die Oberfläche zeigt dann einen Platzhalter.
    /// </summary>
    public async Task<ArtworkResult> RefreshAlbumsAsync(VolumeStatus status, bool force = false, CancellationToken ct = default)
    {
        int created = 0, skipped = 0, unreachable = 0;
        var failures = new List<ArtworkFailure>();
        var known = db.GetAllAlbumArt();

        foreach (var album in MusicIndex.BuildAlbums(db.ListTracks()))
        {
            ct.ThrowIfCancellationRequested();
            if (!force && known.TryGetValue(album.Key, out var existing) && File.Exists(Path.Combine(cacheDir, existing.File)))
            {
                skipped++;
                continue;
            }

            var reachable = album.Tracks
                .Select(t => t.Missing ? null : status.ResolvePath(t.VolumeId, t.RelPath))
                .Where(p => p is not null && File.Exists(p))
                .Select(p => p!)
                .ToList();
            if (reachable.Count == 0)
            {
                unreachable++;
                continue;
            }

            try
            {
                var file = $"album-{System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(album.Key)).Take(8).Aggregate("", (s, b) => s + b.ToString("x2"))}.jpg";
                var output = Path.Combine(cacheDir, file);

                var folders = reachable.Select(p => Path.GetDirectoryName(p)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var sidecar = folders.Count == 1 ? AlbumCoverFinder.Find(folders[0]) : null;
                if (sidecar is not null)
                {
                    await images.ScaleImageAsync(sidecar, output, AlbumSize.W, AlbumSize.H, ct);
                    db.SetAlbumArt(album.Key, "sidecar", file);
                    created++;
                    continue;
                }

                foreach (var track in reachable.Take(3))
                {
                    var picture = (await probe.ProbeAsync(track, ct)).AttachedPictures.FirstOrDefault();
                    if (picture is null) continue;

                    await images.ExtractStreamImageAsync(track, picture.Index, output, AlbumSize.W, AlbumSize.H, ct);
                    db.SetAlbumArt(album.Key, "embedded", file);
                    created++;
                    break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(new ArtworkFailure($"{album.Artist} – {album.Title}", ex.Message));
            }
        }
        return new ArtworkResult(created, skipped, unreachable, failures);
    }

    /// <summary>Löscht Cover von Titeln und Videos, die es nicht mehr gibt (nach dem Aufräumen der Bibliothek).</summary>
    public int PruneOrphans()
    {
        var files = db.DeleteOrphanedArtwork();
        var liveAlbums = MusicIndex.BuildAlbums(db.ListTracks()).Select(a => a.Key).ToHashSet();
        files.AddRange(db.DeleteOrphanedAlbumArt(liveAlbums));
        foreach (var file in files)
        {
            var path = Path.Combine(cacheDir, file);
            if (File.Exists(path)) File.Delete(path);
        }
        return files.Count;
    }

    private bool Needs(ArtworkOwner owner, long ownerId, ArtworkKind kind, bool force)
    {
        var existing = db.GetArtwork(owner, ownerId, kind);
        if (existing is null) return true;
        if (existing.Source == ArtworkSource.User) return false;
        // Die Datei kann aus dem Cache verschwunden sein (manuell gelöscht): dann neu beschaffen.
        return force || !File.Exists(Path.Combine(cacheDir, existing.File));
    }

    private static string CacheFile(ArtworkOwner owner, long ownerId, ArtworkKind kind) =>
        $"{(owner == ArtworkOwner.Title ? "title" : "video")}-{ownerId}-{(kind == ArtworkKind.Poster ? "poster" : "landscape")}.jpg";

    private string Absolute(string cacheFile) => Path.Combine(cacheDir, cacheFile);

    /// <summary>Zehn Prozent der Laufzeit, mindestens 1 s und höchstens 10 Min.; ohne bekannte Dauer 30 s.</summary>
    private static double FramePosition(double? durationSeconds) =>
        durationSeconds is { } d ? Math.Clamp(d * 0.1, 1, 600) : 30;
}
