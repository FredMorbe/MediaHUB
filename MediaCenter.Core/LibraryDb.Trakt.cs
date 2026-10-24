namespace MediaCenter.Core;

// Welche Videos der Bibliothek schon an Trakt gegangen sind (Abgleich „gesehen“), damit ein Eintrag nicht bei jedem Abgleich neu hinausgeht.
public sealed partial class LibraryDb
{
    public HashSet<long> GetTraktPushed()
    {
        var ids = new HashSet<long>();
        using var cmd = Command("SELECT video_id FROM trakt_pushed");
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetInt64(0));
        return ids;
    }

    public void MarkTraktPushed(IEnumerable<long> videoIds, DateTime nowUtc) => InTransaction(() =>
    {
        foreach (var id in videoIds)
        {
            Run("INSERT INTO trakt_pushed (video_id, pushed_utc) VALUES ($id, $now) ON CONFLICT (video_id) DO NOTHING", ("$id", id), ("$now", Utc(nowUtc)));
        }
    });

    /// <summary>Vergisst, was gesendet wurde (anderes Konto oder Profil verbunden): der nächste Abgleich schickt das Gesehene neu.</summary>
    public void ClearTraktPushed() => Run("DELETE FROM trakt_pushed");
}
