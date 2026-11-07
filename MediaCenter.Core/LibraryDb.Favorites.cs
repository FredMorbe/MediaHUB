namespace MediaCenter.Core;

// Favoriten der Musik (je Profil): Titel, die der Nutzer mit dem Herz markiert (Nutzerdaten, ein Scan fasst sie nie an). Verschwindet ein Titel aus der
// Bibliothek, verschwindet er auch aus den Favoriten; wird eine Datei nur verschoben, bleibt ihr Titel und damit der Favorit.
public sealed partial class LibraryDb
{
    /// <summary>Markiert einen Titel als Favorit oder nimmt die Markierung zurück; ist er schon Favorit, bleibt sein Zeitpunkt.</summary>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">Beim Markieren: den Titel gibt es nicht.</exception>
    public void SetTrackFavorite(long trackId, bool on)
    {
        if (on) Run("INSERT OR IGNORE INTO track_favorites (profile_id, track_id, added_utc) VALUES ($profile, $id, $now)", ("$profile", ActiveProfileId), ("$id", trackId), ("$now", Utc(DateTime.UtcNow)));
        else Run("DELETE FROM track_favorites WHERE track_id = $id AND profile_id = $profile", ("$id", trackId), ("$profile", ActiveProfileId));
    }

    /// <summary>Titel-Ids der Favoriten des aktiven Profils, der zuletzt markierte zuerst.</summary>
    public List<long> GetFavoriteTrackIds()
    {
        var ids = new List<long>();
        using var cmd = Command("SELECT track_id FROM track_favorites WHERE profile_id = $profile ORDER BY added_utc DESC, track_id DESC", ("$profile", ActiveProfileId));
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetInt64(0));
        return ids;
    }
}
