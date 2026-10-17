using MediaCenter.Core;

// Aufruf: MediaCenter.Cli <befehl> [argument] [--db <pfad>]
//   add-folder <ordner>   add-file <datei>   rescan   volumes   list   cleanup
var arguments = args.ToList();
var dbPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaCenter", "library.db");
var dbIndex = arguments.IndexOf("--db");
if (dbIndex >= 0)
{
    dbPath = arguments.ElementAtOrDefault(dbIndex + 1) ?? throw new ArgumentException("--db braucht einen Pfad.");
    arguments.RemoveRange(dbIndex, 2);
}

var artDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaCenter", "artwork");
var artIndex = arguments.IndexOf("--art");
if (artIndex >= 0)
{
    artDir = arguments.ElementAtOrDefault(artIndex + 1) ?? throw new ArgumentException("--art braucht einen Ordner.");
    arguments.RemoveRange(artIndex, 2);
}
var force = arguments.Remove("--force");

using var db = new LibraryDb(dbPath);
var service = new LibraryService(db, new WindowsVolumeProvider(), FfprobeReader.FromEnvironment());
var artwork = new ArtworkService(db, FfmpegTool.FromEnvironment(), FfprobeReader.FromEnvironment(), artDir);

switch (arguments.FirstOrDefault())
{
    case "add-folder":
    {
        var folder = arguments.ElementAtOrDefault(1) ?? throw new ArgumentException("add-folder braucht einen Ordner.");
        var (_, scan) = await service.AddFolderAsync(folder);
        return PrintScan("Ordner", scan);
    }

    case "add-file":
    {
        var file = arguments.ElementAtOrDefault(1) ?? throw new ArgumentException("add-file braucht eine Datei.");
        var (_, scan) = await service.AddFileAsync(file);
        return PrintScan("Datei", scan);
    }

    case "rescan":
    {
        var failed = 0;
        foreach (var (source, result) in await service.RescanAllAsync())
        {
            failed += PrintScan($"Quelle {source.Id} ({source.RelPath.Length switch { 0 => "Root", _ => source.RelPath }})", result);
        }
        return failed == 0 ? 0 : 1;
    }

    case "volumes":
    {
        var status = service.RefreshVolumes();
        foreach (var v in db.GetVolumes())
        {
            var where = status.Connected.TryGetValue(v.Id, out var mounted) ? $"angeschlossen als {mounted.RootPath}" : "nicht angeschlossen";
            Console.WriteLine($"{v.Id}: {v.Name} [{v.Serial}, {v.FileSystem}, {v.Capacity / 1_000_000_000} GB] – {where}");
        }
        foreach (var u in status.Unresolved) Console.WriteLine($"ACHTUNG {u.Volume.RootPath}: {u.Match.Reason}");
        return 0;
    }

    case "list":
    {
        var status = service.RefreshVolumes();
        foreach (var v in db.ListVideos())
        {
            var label = v switch
            {
                { IsInbox: true } => $"[Inbox] {v.ParsedTitle}",
                { TitleKind: TitleKind.Series } => $"{v.TitleName} S{v.Season:00}E{v.Episode:00}",
                _ => $"{v.TitleName} ({v.Year?.ToString() ?? "?"})",
            };
            var flags = (status.Connected.ContainsKey(v.VolumeId) ? "" : " [offline]") + (v.Missing ? " [fehlt]" : "") + (v.Locked ? " [gesperrt]" : "");
            var genres = v.TitleId is { } id && db.GetGenres(id) is { Count: > 0 } g ? "  {" + string.Join(", ", g) + "}" : "";
            var duration = v.DurationSeconds is { } s ? TimeSpan.FromSeconds(s).ToString(@"h\:mm\:ss") : "?";
            Console.WriteLine($"{label,-38} {v.Width}x{v.Height} {v.VideoCodec}{(v.IsHdr ? " HDR" : "")} {duration}{flags}{genres}");
        }
        return 0;
    }

    case "titles":
    {
        var all = db.GetAllArtwork();
        foreach (var t in db.GetTitles())
        {
            string Art(ArtworkKind kind) => all.FirstOrDefault(a => a.Owner == ArtworkOwner.Title && a.OwnerId == t.Id && a.Kind == kind)?.Source.ToString() ?? "–";
            var genres = db.GetGenres(t.Id) is { Count: > 0 } g ? "  {" + string.Join(", ", g) + "}" : "";
            Console.WriteLine($"{t.Id,4} {(t.Kind == TitleKind.Movie ? "Film " : "Serie")} {t.Name,-30} Poster: {Art(ArtworkKind.Poster),-8} Landscape: {Art(ArtworkKind.Landscape),-8}{genres}");
        }
        return 0;
    }

    case "artwork":
    {
        var status = service.RefreshVolumes();
        var result = await artwork.RefreshAsync(status, force);
        Console.WriteLine($"Cover: neu {result.Created}, übersprungen {result.Skipped}, nicht erreichbar {result.Unreachable}");
        var albums = await artwork.RefreshAlbumsAsync(status, force);
        Console.WriteLine($"Album-Cover: neu {albums.Created}, übersprungen {albums.Skipped}, nicht erreichbar {albums.Unreachable}");
        var failures = result.Failures.Concat(albums.Failures).ToList();
        foreach (var f in failures) Console.Error.WriteLine($"FEHLER {f.Subject}: {f.Error}");
        return failures.Count == 0 ? 0 : 1;
    }

    case "tracks":
    {
        foreach (var album in MusicIndex.BuildAlbums(db.ListTracks()))
        {
            var cover = db.GetAlbumArt(album.Key) is { } file ? $"Cover: {file}" : "kein Cover";
            Console.WriteLine($"{album.Artist} – {album.Title} ({album.Year?.ToString() ?? "?"}), {album.Tracks.Count} Titel, {cover}");
            foreach (var t in album.Tracks) Console.WriteLine($"   {t.DiscNo?.ToString() ?? "-"}/{t.TrackNo?.ToString() ?? "-"}  {t.Title}  [{t.Artist ?? "kein Künstler"}]  {t.DurationSeconds:0}s{(t.Missing ? "  FEHLT" : "")}");
        }
        return 0;
    }

    case "set-cover":
    {
        // set-cover <title|video> <id> <poster|landscape> <bild>
        if (arguments.Count != 5) throw new ArgumentException("set-cover <title|video> <id> <poster|landscape> <bild>");
        var owner = arguments[1] switch { "title" => ArtworkOwner.Title, "video" => ArtworkOwner.Video, _ => throw new ArgumentException("Besitzer: title oder video.") };
        var kind = arguments[3] switch { "poster" => ArtworkKind.Poster, "landscape" => ArtworkKind.Landscape, _ => throw new ArgumentException("Art: poster oder landscape.") };
        await artwork.SetUserArtworkAsync(owner, long.Parse(arguments[2]), kind, arguments[4]);
        Console.WriteLine("Eigenes Cover gesetzt.");
        return 0;
    }

    case "cleanup":
        Console.WriteLine($"Entfernt: {db.RemoveMissingFiles()} als fehlend markierte Dateien, {artwork.PruneOrphans()} verwaiste Cover.");
        return 0;

    default:
        Console.Error.WriteLine(
            "Verwendung: add-folder <ordner> | add-file <datei> | rescan | volumes | list | titles | artwork [--force] |\n" +
            "            set-cover <title|video> <id> <poster|landscape> <bild> | cleanup   [--db <pfad>] [--art <ordner>]");
        return 2;
}

static int PrintScan(string what, ScanResult r)
{
    if (r.Offline)
    {
        Console.WriteLine($"{what}: Laufwerk nicht angeschlossen – übersprungen.");
        return 0;
    }
    Console.WriteLine($"{what}: neu {r.Added}, aktualisiert {r.Updated}, unverändert {r.Unchanged}, als fehlend markiert {r.MarkedMissing}");
    foreach (var f in r.Failures) Console.Error.WriteLine($"FEHLER {f.Path}: {f.Error}");
    return r.Failures.Count == 0 ? 0 : 1;
}
