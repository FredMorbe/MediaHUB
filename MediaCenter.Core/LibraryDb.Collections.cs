namespace MediaCenter.Core;

/// <summary>Eine vom Nutzer angelegte Sammlung (Filmreihe), ohne ihre Filme; siehe <see cref="LibraryDb.ListCollectionItems"/>.</summary>
public sealed record CollectionRow(long Id, string Name, string CreatedUtc);

// Sammlungen: Filmreihen wie „Matrix“, die der Nutzer anlegt (Nutzerdaten, ein Scan fasst sie nie an). Ein Film kann in mehreren Sammlungen stehen,
// in jeder höchstens einmal. Verschwindet er aus der Bibliothek, verschwindet er auch aus den Sammlungen; beim Zusammenführen zweier Titel wandert
// die Zugehörigkeit zum Ziel (siehe MergeTitles).
public sealed partial class LibraryDb
{
    public const int MaxCollectionNameLength = 80;

    /// <summary>Legt eine Sammlung an, optional gleich mit Filmen. Der Name ist Pflicht und je Sammlung eindeutig (Groß-/Kleinschreibung egal).</summary>
    /// <exception cref="ArgumentException">Name leer, zu lang oder schon vergeben; ein Titel ist kein Film.</exception>
    /// <returns>Die Id der neuen Sammlung.</returns>
    public long CreateCollection(string name, IEnumerable<long>? titleIds = null) => InTransaction(() =>
    {
        name = ValidatedCollectionName(name, exceptId: null);
        Run("INSERT INTO collections (name, created_utc) VALUES ($name, $now)", ("$name", name), ("$now", Utc(DateTime.UtcNow)));
        var id = Scalar<long>("SELECT last_insert_rowid()");
        if (titleIds is not null) AddTitlesToCollection(id, titleIds);
        return id;
    });

    /// <exception cref="ArgumentException">Name leer, zu lang oder schon vergeben.</exception>
    /// <exception cref="InvalidOperationException">Die Sammlung gibt es nicht (mehr).</exception>
    public void RenameCollection(long collectionId, string name) => InTransaction(() =>
    {
        name = ValidatedCollectionName(name, exceptId: collectionId);
        if (Run("UPDATE collections SET name = $name WHERE id = $id", ("$name", name), ("$id", collectionId)) == 0) throw MissingCollection(collectionId);
    });

    /// <summary>Löscht die Sammlung; die Filme bleiben in der Bibliothek.</summary>
    /// <exception cref="InvalidOperationException">Die Sammlung gibt es nicht (mehr).</exception>
    public void DeleteCollection(long collectionId)
    {
        if (Run("DELETE FROM collections WHERE id = $id", ("$id", collectionId)) == 0) throw MissingCollection(collectionId);
    }

    /// <summary>Alle Sammlungen nach Name.</summary>
    public List<CollectionRow> ListCollections()
    {
        var rows = new List<CollectionRow>();
        using var cmd = Command("SELECT id, name, created_utc FROM collections ORDER BY name COLLATE NOCASE");
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(new CollectionRow(r.GetInt64(0), r.GetString(1), r.GetString(2)));
        return rows;
    }

    /// <summary>Film-Ids je Sammlung, nach Erscheinungsjahr und Name (so liegt eine Reihe in ihrer Reihenfolge); leere Sammlungen fehlen.</summary>
    public Dictionary<long, List<long>> ListCollectionItems()
    {
        var map = new Dictionary<long, List<long>>();
        using var cmd = Command("""
            SELECT ct.collection_id, ct.title_id
            FROM collection_titles ct JOIN titles t ON t.id = ct.title_id
            ORDER BY ct.collection_id, t.year IS NULL, t.year, t.name COLLATE NOCASE
            """);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var id = r.GetInt64(0);
            if (!map.TryGetValue(id, out var list)) map[id] = list = new List<long>();
            list.Add(r.GetInt64(1));
        }
        return map;
    }

    /// <summary>Nimmt Filme in die Sammlung auf; Filme, die schon darin sind, bleiben.</summary>
    /// <returns>Anzahl der tatsächlich hinzugefügten Filme.</returns>
    /// <exception cref="ArgumentException">Ein Titel gibt es nicht oder ist eine Serie; dann ändert sich nichts.</exception>
    /// <exception cref="InvalidOperationException">Die Sammlung gibt es nicht (mehr).</exception>
    public int AddTitlesToCollection(long collectionId, IEnumerable<long> titleIds) => InTransaction(() =>
    {
        if (Scalar<long>("SELECT COUNT(*) FROM collections WHERE id = $id", ("$id", collectionId)) == 0) throw MissingCollection(collectionId);
        var added = 0;
        foreach (var titleId in titleIds.Distinct())
        {
            var kind = Scalar<string?>("SELECT kind FROM titles WHERE id = $id", ("$id", titleId));
            if (kind is null) throw new ArgumentException($"Der Titel {titleId} ist nicht in der Bibliothek.");
            if (kind != "movie") throw new ArgumentException("In eine Sammlung gehören nur Filme, keine Serien.");
            added += Run("INSERT OR IGNORE INTO collection_titles (collection_id, title_id, added_utc) VALUES ($c, $t, $now)",
                ("$c", collectionId), ("$t", titleId), ("$now", Utc(DateTime.UtcNow)));
        }
        return added;
    });

    public void RemoveTitleFromCollection(long collectionId, long titleId) =>
        Run("DELETE FROM collection_titles WHERE collection_id = $c AND title_id = $t", ("$c", collectionId), ("$t", titleId));

    private string ValidatedCollectionName(string name, long? exceptId)
    {
        name = name?.Trim() ?? "";
        if (name.Length == 0) throw new ArgumentException("Der Name der Sammlung darf nicht leer sein.");
        if (name.Length > MaxCollectionNameLength) throw new ArgumentException($"Der Name der Sammlung darf höchstens {MaxCollectionNameLength} Zeichen lang sein.");
        var taken = Scalar<long>(
            "SELECT COUNT(*) FROM collections WHERE name = $name COLLATE NOCASE AND ($except IS NULL OR id <> $except)",
            ("$name", name), ("$except", exceptId));
        if (taken > 0) throw new ArgumentException($"Es gibt schon eine Sammlung „{name}“.");
        return name;
    }

    private static InvalidOperationException MissingCollection(long collectionId) => new($"Die Sammlung {collectionId} gibt es nicht (mehr).");
}
