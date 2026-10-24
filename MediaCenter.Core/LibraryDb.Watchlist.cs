namespace MediaCenter.Core;

// Meine Liste (je Profil): Filme und Serien, die der Nutzer vormerkt (Nutzerdaten, ein Scan fasst sie nie an). Verschwindet ein Titel aus der Bibliothek,
// verschwindet er auch aus der Liste; beim Zusammenführen zweier Titel wandert der Eintrag zum Ziel (siehe MergeTitles).
public sealed partial class LibraryDb
{
    /// <summary>Setzt einen Titel auf die Liste oder nimmt ihn herunter; steht er schon darauf, bleibt sein Zeitpunkt.</summary>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">Beim Hinzufügen: den Titel gibt es nicht.</exception>
    public void SetOnWatchlist(long titleId, bool on)
    {
        if (on) Run("INSERT OR IGNORE INTO watchlist (profile_id, title_id, added_utc) VALUES ($profile, $id, $now)", ("$profile", ActiveProfileId), ("$id", titleId), ("$now", Utc(DateTime.UtcNow)));
        else Run("DELETE FROM watchlist WHERE title_id = $id AND profile_id = $profile", ("$id", titleId), ("$profile", ActiveProfileId));
    }

    /// <summary>Titel-Ids der Liste, der zuletzt vorgemerkte zuerst.</summary>
    public List<long> GetWatchlist()
    {
        var ids = new List<long>();
        using var cmd = Command("SELECT title_id FROM watchlist WHERE profile_id = $profile ORDER BY added_utc DESC, title_id DESC", ("$profile", ActiveProfileId));
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetInt64(0));
        return ids;
    }
}
