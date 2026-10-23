namespace MediaCenter.Core;

// Infotexte aus dem Internet (Handlung, Wertung, Besetzung, Folgentexte). Abgeleitete Daten: sie lassen sich jederzeit neu holen.
public sealed partial class LibraryDb
{
    public TitleInfoRow? GetTitleInfo(long titleId)
    {
        using var cmd = Command("SELECT provider, plot, rating, director, actors FROM title_info WHERE title_id = $id", ("$id", titleId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new TitleInfoRow(titleId, r.GetString(0), r.GetStringOrNull(1), r.GetDoubleOrNull(2), r.GetStringOrNull(3), r.GetStringOrNull(4)) : null;
    }

    /// <summary>Ersetzt die Infotexte des Titels samt aller Folgentexte durch den neuen Stand.</summary>
    public void SetTitleInfo(long titleId, string provider, OnlineTitleInfo info) => InTransaction(() =>
    {
        Run("""
            INSERT INTO title_info (title_id, provider, plot, rating, director, actors, updated_utc)
            VALUES ($id, $provider, $plot, $rating, $director, $actors, $now)
            ON CONFLICT (title_id) DO UPDATE SET provider = excluded.provider, plot = excluded.plot, rating = excluded.rating,
                director = excluded.director, actors = excluded.actors, updated_utc = excluded.updated_utc
            """,
            ("$id", titleId), ("$provider", provider), ("$plot", info.Plot), ("$rating", info.Rating), ("$director", info.Director),
            ("$actors", info.Cast), ("$now", Utc(DateTime.UtcNow)));

        Run("DELETE FROM episode_info WHERE title_id = $id", ("$id", titleId));
        foreach (var e in info.Episodes)
        {
            Run("""
                INSERT INTO episode_info (title_id, season, episode, name, summary) VALUES ($id, $season, $episode, $name, $summary)
                ON CONFLICT (title_id, season, episode) DO UPDATE SET name = excluded.name, summary = excluded.summary
                """,
                ("$id", titleId), ("$season", e.Season), ("$episode", e.Episode), ("$name", e.Name), ("$summary", e.Summary));
        }
    });

    public List<EpisodeText> GetEpisodeInfos(long titleId)
    {
        var rows = new List<EpisodeText>();
        using var cmd = Command("SELECT season, episode, name, summary FROM episode_info WHERE title_id = $id ORDER BY season, episode", ("$id", titleId));
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(new EpisodeText(r.GetInt32(0), r.GetInt32(1), r.GetStringOrNull(2), r.GetStringOrNull(3)));
        return rows;
    }
}
