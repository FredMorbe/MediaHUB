using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MediaCenter.Core;

/// <summary>Kopf einer Sicherung (Datei backup.json im Archiv).</summary>
/// <param name="Format">Aufbau des Archivs; steigt, wenn sich Inhalt oder Namen ändern.</param>
/// <param name="Schema">Schema der gesicherten Datenbank.</param>
public sealed record BackupManifest(int Format, int Schema, string AppVersion, DateTime CreatedUtc, int Files);

/// <summary>
/// Sichert die Daten, die sich nicht neu erzeugen lassen (Datenbank mit Fortschritt, Playlists, „Meine Liste“, eigenen Namen und Infotexten,
/// Einstellungen, selbst gewählte Poster, gespeicherte Untertitel, LUTs), und stellt sie wieder her. Nicht gesichert werden Zwischenspeicher
/// (Vorschauen, Songtexte, Shader, Übersetzungs-Zwischenstände) und Zugangsschlüssel (OMDb, OpenSubtitles): ein Archiv, das man weitergibt,
/// soll keine Geheimnisse enthalten.
/// </summary>
/// <remarks>
/// Wiederherstellen läuft in zwei Schritten, weil die Datenbank der laufenden App offen ist: <see cref="StageRestore"/> prüft das Archiv und legt
/// es in einen Zwischenordner, <see cref="ApplyPendingRestore"/> tauscht beim nächsten Start vor dem Öffnen der Datenbank aus. Der bisherige Stand
/// wird dabei nie gelöscht, sondern in einen Ordner neben den Daten verschoben.
/// </remarks>
public static class BackupService
{
    public const int FormatVersion = 1;

    public const string ManifestName = "backup.json";

    public const string DatabaseName = "library.db";

    public const string SettingsName = "settings.json";

    /// <summary>Ordner im Datenordner, die mitgesichert werden.</summary>
    public static readonly IReadOnlyList<string> Folders = new[] { "artwork", "subtitles", "luts" };

    /// <summary>Zwischenordner, in dem eine vorbereitete Wiederherstellung auf den nächsten Start wartet.</summary>
    public const string PendingFolder = "restore-pending";

    /// <summary>Wohin eine Wiederherstellung den alten Stand verschiebt: <c>vor-wiederherstellung-Datum</c>.</summary>
    public const string PreviousPrefix = "vor-wiederherstellung-";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Schreibt die Sicherung nach <paramref name="zipPath"/> (eine vorhandene Datei wird ersetzt, aber erst, wenn die neue fertig ist).</summary>
    public static BackupManifest Create(LibraryDb db, string dataDirectory, string zipPath, string appVersion, DateTime? now = null)
    {
        var created = (now ?? DateTime.UtcNow).ToUniversalTime();
        var work = Path.Combine(Path.GetTempPath(), $"mediacenter-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        var temp = zipPath + ".tmp";
        try
        {
            var snapshot = Path.Combine(work, DatabaseName);
            db.BackupTo(snapshot);

            var files = new List<(string Source, string Entry)> { (snapshot, DatabaseName) };
            var settings = Path.Combine(dataDirectory, SettingsName);
            if (File.Exists(settings)) files.Add((settings, SettingsName));
            foreach (var folder in Folders)
            {
                var root = Path.Combine(dataDirectory, folder);
                if (!Directory.Exists(root)) continue;
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    files.Add((file, folder + "/" + Path.GetRelativePath(root, file).Replace('\\', '/')));
            }

            var manifest = new BackupManifest(FormatVersion, LibraryDb.CurrentSchema, appVersion, created, files.Count);
            var directory = Path.GetDirectoryName(Path.GetFullPath(zipPath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            if (File.Exists(temp)) File.Delete(temp);
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                var head = archive.CreateEntry(ManifestName);
                using (var stream = head.Open()) JsonSerializer.Serialize(stream, manifest, Json);
                foreach (var (source, entry) in files)
                {
                    // Bilder sind schon komprimiert; nur die Datenbank lohnt den Aufwand.
                    archive.CreateEntryFromFile(source, entry, entry == DatabaseName ? CompressionLevel.Optimal : CompressionLevel.Fastest);
                }
            }
            File.Move(temp, zipPath, overwrite: true);
            return manifest;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
            Directory.Delete(work, recursive: true);
        }
    }

    /// <summary>Liest den Kopf eines Archivs und prüft, dass es eine Sicherung dieser App ist, die diese Version einlesen kann.</summary>
    /// <exception cref="InvalidDataException">Kein Archiv, keine Sicherung, unbekannter Aufbau, von einer neueren Version oder mit fremden Einträgen.</exception>
    public static BackupManifest Inspect(string zipPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var manifest = ReadManifest(archive);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue; // Ordnereintrag
                if (!IsAllowedEntry(entry.FullName)) throw new InvalidDataException($"Die Datei enthält einen unerwarteten Eintrag: {entry.FullName}");
            }
            if (archive.GetEntry(DatabaseName) is null) throw new InvalidDataException($"In der Sicherung fehlt {DatabaseName}.");
            return manifest;
        }
        catch (InvalidDataException) { throw; }
        catch (JsonException ex) { throw new InvalidDataException($"Der Kopf der Sicherung ({ManifestName}) ist unlesbar: {ex.Message}", ex); }
    }

    private static BackupManifest ReadManifest(ZipArchive archive)
    {
        var entry = archive.GetEntry(ManifestName) ?? throw new InvalidDataException("Das ist keine Sicherung des Mediacenters (backup.json fehlt).");
        using var stream = entry.Open();
        var manifest = JsonSerializer.Deserialize<BackupManifest>(stream) ?? throw new InvalidDataException("Der Kopf der Sicherung ist leer.");
        if (manifest.Format != FormatVersion)
            throw new InvalidDataException($"Die Sicherung hat Aufbau {manifest.Format}, diese Version kennt nur {FormatVersion}.");
        if (manifest.Schema > LibraryDb.CurrentSchema)
            throw new InvalidDataException($"Die Sicherung stammt von einer neueren Version des Mediacenters (Datenbank-Schema {manifest.Schema}, diese Version versteht bis {LibraryDb.CurrentSchema}). Bitte erst die App aktualisieren.");
        return manifest;
    }

    private static bool IsAllowedEntry(string name)
    {
        if (name is ManifestName or DatabaseName or SettingsName) return true;
        var parts = name.Split('/');
        // Nur unterhalb der bekannten Ordner, nie mit „..“ oder Laufwerksangaben (Zip-Slip).
        return parts.Length >= 2 && Folders.Contains(parts[0]) && parts.All(p => p.Length > 0 && p != ".." && p != "." && !p.Contains(':') && !p.Contains('\\'));
    }

    /// <summary>
    /// Entpackt die Sicherung in den Zwischenordner und prüft, dass die Datenbank sich öffnen lässt. Der Austausch geschieht erst beim
    /// nächsten Start (<see cref="ApplyPendingRestore"/>); bis dahin ändert sich nichts an den laufenden Daten.
    /// </summary>
    /// <exception cref="InvalidDataException">Siehe <see cref="Inspect"/>, oder die Datenbank in der Sicherung ist unbrauchbar.</exception>
    public static BackupManifest StageRestore(string zipPath, string dataDirectory)
    {
        var manifest = Inspect(zipPath);
        var pending = Path.Combine(dataDirectory, PendingFolder);
        if (Directory.Exists(pending)) Directory.Delete(pending, recursive: true);
        Directory.CreateDirectory(pending);
        try
        {
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith('/')) continue;
                    var target = Path.GetFullPath(Path.Combine(pending, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(Path.GetFullPath(pending) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Unzulässiger Pfad im Archiv: {entry.FullName}");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
            }

            // Öffnen prüft Datei und Schema (und hebt ein älteres Schema gleich an).
            try
            {
                using var check = new LibraryDb(Path.Combine(pending, DatabaseName));
            }
            catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
            {
                throw new InvalidDataException($"Die Datenbank in der Sicherung ist unbrauchbar: {ex.Message}", ex);
            }
            finally
            {
                SqliteConnection.ClearAllPools(); // sonst hält der Pool die Datei offen und der Austausch scheitert
            }
            return manifest;
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(pending, recursive: true);
            throw;
        }
    }

    /// <summary>Wartet im Datenordner eine vorbereitete Wiederherstellung?</summary>
    public static bool HasPendingRestore(string dataDirectory) => Directory.Exists(Path.Combine(dataDirectory, PendingFolder));

    /// <summary>
    /// Tauscht eine vorbereitete Wiederherstellung ein. Muss laufen, bevor die Datenbank geöffnet wird. Der bisherige Stand wandert in einen Ordner
    /// <c>vor-wiederherstellung-…</c> im Datenordner und bleibt dort, bis der Nutzer ihn löscht. Scheitert ein Schritt, wird alles zurückverschoben.
    /// </summary>
    /// <returns>Der Ordner mit dem bisherigen Stand; null, wenn nichts vorbereitet war.</returns>
    /// <exception cref="InvalidDataException">Der Zwischenordner ist unvollständig (er wird zur Seite gelegt, damit der nächste Start gelingt).</exception>
    public static string? ApplyPendingRestore(string dataDirectory, DateTime? now = null)
    {
        var pending = Path.Combine(dataDirectory, PendingFolder);
        if (!Directory.Exists(pending)) return null;

        if (!File.Exists(Path.Combine(pending, DatabaseName)) || !File.Exists(Path.Combine(pending, ManifestName)))
        {
            var aside = Path.Combine(dataDirectory, "restore-fehlgeschlagen-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.Move(pending, aside);
            throw new InvalidDataException($"Die vorbereitete Wiederherstellung war unvollständig und wurde nicht eingespielt. Sie liegt zur Ansicht in {aside}; die Daten sind unverändert.");
        }

        var stamp = (now ?? DateTime.Now).ToString("yyyyMMdd-HHmmss");
        var previous = Path.Combine(dataDirectory, PreviousPrefix + stamp);
        Directory.CreateDirectory(previous);

        var names = new List<string> { DatabaseName, SettingsName };
        names.AddRange(Folders);
        var done = new List<(string From, string To)>();
        try
        {
            // Erst den alten Stand beiseite, dann den neuen an seine Stelle.
            foreach (var name in names)
            {
                var current = Path.Combine(dataDirectory, name);
                if (File.Exists(current) || Directory.Exists(current)) Move(current, Path.Combine(previous, name), done);
            }
            foreach (var name in names)
            {
                var staged = Path.Combine(pending, name);
                if (File.Exists(staged) || Directory.Exists(staged)) Move(staged, Path.Combine(dataDirectory, name), done);
            }
        }
        catch
        {
            for (var i = done.Count - 1; i >= 0; i--)
            {
                try { Move(done[i].To, done[i].From, null); } catch (IOException) { /* der Fehler oben ist wichtiger */ }
            }
            throw;
        }

        Directory.Delete(pending, recursive: true);
        return previous;
    }

    private static void Move(string from, string to, List<(string From, string To)>? done)
    {
        // Direkt nach dem Beenden der alten App hält Windows die Datenbank noch kurz offen.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(from)) Directory.Move(from, to);
                else File.Move(from, to);
                break;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(250);
            }
        }
        done?.Add((from, to));
    }
}
