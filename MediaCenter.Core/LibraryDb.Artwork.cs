namespace MediaCenter.Core;

// Cover: Verweise auf Dateien im Cover-Cache. Einträge mit source = 'user' sind Nutzerdaten.
public sealed partial class LibraryDb
{
    private static string OwnerText(ArtworkOwner owner) => owner == ArtworkOwner.Title ? "title" : "video";

    private static string KindText(ArtworkKind kind) => kind == ArtworkKind.Poster ? "poster" : "landscape";

    private static string SourceText(ArtworkSource source) => source.ToString().ToLowerInvariant();

    public ArtworkRow? GetArtwork(ArtworkOwner owner, long ownerId, ArtworkKind kind)
    {
        using var cmd = Command(
            "SELECT source, file, updated_utc FROM artwork WHERE owner_kind = $owner AND owner_id = $id AND kind = $kind",
            ("$owner", OwnerText(owner)), ("$id", ownerId), ("$kind", KindText(kind)));
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new ArtworkRow(owner, ownerId, kind, Enum.Parse<ArtworkSource>(r.GetString(0), ignoreCase: true), r.GetString(1), r.GetString(2))
            : null;
    }

    public List<ArtworkRow> GetAllArtwork()
    {
        var rows = new List<ArtworkRow>();
        using var cmd = Command("SELECT owner_kind, owner_id, kind, source, file, updated_utc FROM artwork");
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new ArtworkRow(
                r.GetString(0) == "title" ? ArtworkOwner.Title : ArtworkOwner.Video, r.GetInt64(1),
                r.GetString(2) == "poster" ? ArtworkKind.Poster : ArtworkKind.Landscape,
                Enum.Parse<ArtworkSource>(r.GetString(3), ignoreCase: true), r.GetString(4), r.GetString(5)));
        }
        return rows;
    }

    public void SetArtwork(ArtworkOwner owner, long ownerId, ArtworkKind kind, ArtworkSource source, string file) => Run("""
        INSERT INTO artwork (owner_kind, owner_id, kind, source, file, updated_utc) VALUES ($owner, $id, $kind, $source, $file, $now)
        ON CONFLICT (owner_kind, owner_id, kind) DO UPDATE SET source = excluded.source, file = excluded.file, updated_utc = excluded.updated_utc
        """,
        ("$owner", OwnerText(owner)), ("$id", ownerId), ("$kind", KindText(kind)), ("$source", SourceText(source)),
        ("$file", file), ("$now", Utc(DateTime.UtcNow)));

    public void DeleteArtwork(ArtworkOwner owner, long ownerId, ArtworkKind kind) => Run(
        "DELETE FROM artwork WHERE owner_kind = $owner AND owner_id = $id AND kind = $kind",
        ("$owner", OwnerText(owner)), ("$id", ownerId), ("$kind", KindText(kind)));

    // --- Suchen im Internet (nur ergebnislose Suchen werden gemerkt) ----------------------------------------

    /// <summary>Letzte Suche dieses Titels bei diesem Anbieter, oder null.</summary>
    public OnlineLookup? GetOnlineLookup(long titleId, string provider)
    {
        using var cmd = Command(
            "SELECT query, found, checked_utc FROM online_lookups WHERE title_id = $id AND provider = $provider",
            ("$id", titleId), ("$provider", provider));
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new OnlineLookup(r.GetString(0), r.GetInt32(1) == 1, DateTime.Parse(r.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime())
            : null;
    }

    public void SetOnlineLookup(long titleId, string provider, string query, bool found) => Run("""
        INSERT INTO online_lookups (title_id, provider, query, found, checked_utc) VALUES ($id, $provider, $query, $found, $now)
        ON CONFLICT (title_id, provider) DO UPDATE SET query = excluded.query, found = excluded.found, checked_utc = excluded.checked_utc
        """,
        ("$id", titleId), ("$provider", provider), ("$query", query), ("$found", found ? 1 : 0), ("$now", Utc(DateTime.UtcNow)));

    public List<TitleRow> GetTitles()
    {
        var rows = new List<TitleRow>();
        using var cmd = Command("SELECT id, kind, name, year FROM titles ORDER BY name COLLATE NOCASE");
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new TitleRow(r.GetInt64(0), r.GetString(1) == "movie" ? TitleKind.Movie : TitleKind.Series, r.GetString(2), r.GetInt32OrNull(3)));
        }
        return rows;
    }

    /// <summary>Videos eines Titels, bei Serien nach Staffel und Episode sortiert.</summary>
    public List<TitleVideo> GetTitleVideos(long titleId)
    {
        var rows = new List<TitleVideo>();
        using var cmd = Command("""
            SELECT v.id, f.volume_id, f.rel_path, f.missing, f.duration_sec, v.season, v.episode, f.container
            FROM videos v JOIN files f ON f.id = v.file_id
            WHERE v.title_id = $id
            ORDER BY v.season, v.episode, f.rel_path
            """, ("$id", titleId));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new TitleVideo(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt32(3) == 1,
                r.GetDoubleOrNull(4), r.GetInt32OrNull(5), r.GetInt32OrNull(6), DiscDetector.FromContainer(r.GetStringOrNull(7))));
        }
        return rows;
    }

    /// <summary>Alle Videos (auch ohne Titel, z. B. Inbox) mit Ort, für Standbilder.</summary>
    public List<TitleVideo> GetAllVideos()
    {
        var rows = new List<TitleVideo>();
        using var cmd = Command("""
            SELECT v.id, f.volume_id, f.rel_path, f.missing, f.duration_sec, v.season, v.episode, f.container
            FROM videos v JOIN files f ON f.id = v.file_id
            ORDER BY v.id
            """);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new TitleVideo(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt32(3) == 1,
                r.GetDoubleOrNull(4), r.GetInt32OrNull(5), r.GetInt32OrNull(6), DiscDetector.FromContainer(r.GetStringOrNull(7))));
        }
        return rows;
    }

    /// <summary>Löscht Cover-Einträge, deren Titel oder Video nicht mehr existiert; liefert deren Dateinamen.</summary>
    public List<string> DeleteOrphanedArtwork() => InTransaction(() =>
    {
        var files = new List<string>();
        using (var cmd = Command("""
            SELECT file FROM artwork
            WHERE (owner_kind = 'title' AND owner_id NOT IN (SELECT id FROM titles))
               OR (owner_kind = 'video' AND owner_id NOT IN (SELECT id FROM videos))
            """))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read()) files.Add(r.GetString(0));
        }

        Run("""
            DELETE FROM artwork
            WHERE (owner_kind = 'title' AND owner_id NOT IN (SELECT id FROM titles))
               OR (owner_kind = 'video' AND owner_id NOT IN (SELECT id FROM videos))
            """);
        return files;
    });
}
