namespace MediaCenter.Core;

// Infotexte aus dem Internet (Handlung, Wertung, Besetzung, Folgentexte). Abgeleitete Daten: sie lassen sich jederzeit neu holen,
// außer was der Nutzer selbst geschrieben hat (user_edited): das fasst kein automatischer Durchlauf an.
public sealed partial class LibraryDb
{
    private const string TitleInfoColumns = "title_id, provider, plot, rating, director, actors, user_edited";

    private static TitleInfoRow ReadTitleInfo(Microsoft.Data.Sqlite.SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetStringOrNull(2), r.GetDoubleOrNull(3), r.GetStringOrNull(4), r.GetStringOrNull(5), r.GetInt32(6) == 1);

    public TitleInfoRow? GetTitleInfo(long titleId)
    {
        using var cmd = Command($"SELECT {TitleInfoColumns} FROM title_info WHERE title_id = $id", ("$id", titleId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadTitleInfo(r) : null;
    }

    /// <summary>Die Infotexte aller Titel auf einmal: Titel-Id → Stand (für Wertung und Schauspielersuche).</summary>
    public Dictionary<long, TitleInfoRow> ListTitleInfos()
    {
        var rows = new Dictionary<long, TitleInfoRow>();
        using var cmd = Command($"SELECT {TitleInfoColumns} FROM title_info");
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows[r.GetInt64(0)] = ReadTitleInfo(r);
        return rows;
    }

    /// <summary>
    /// Speichert Handlung, Regie und Besetzung, wie der Nutzer sie geschrieben hat. Wertung, Quelle und Folgentexte bleiben, wie sie sind.
    /// Sind alle drei leer, verschwindet der Eintrag samt Folgentexten (der nächste Durchlauf holt die Texte dann wieder aus dem Internet).
    /// </summary>
    public void SetUserTitleInfo(long titleId, string? plot, string? director, string? cast) => InTransaction(() =>
    {
        plot = string.IsNullOrWhiteSpace(plot) ? null : plot.Trim();
        director = string.IsNullOrWhiteSpace(director) ? null : director.Trim();
        cast = string.IsNullOrWhiteSpace(cast) ? null : cast.Trim();
        if (plot is null && director is null && cast is null)
        {
            Run("DELETE FROM title_info WHERE title_id = $id", ("$id", titleId));
            Run("DELETE FROM episode_info WHERE title_id = $id", ("$id", titleId));
            return;
        }
        Run("""
            INSERT INTO title_info (title_id, provider, plot, rating, director, actors, user_edited, updated_utc)
            VALUES ($id, 'user', $plot, NULL, $director, $actors, 1, $now)
            ON CONFLICT (title_id) DO UPDATE SET plot = excluded.plot, director = excluded.director, actors = excluded.actors,
                user_edited = 1, updated_utc = excluded.updated_utc
            """,
            ("$id", titleId), ("$plot", plot), ("$director", director), ("$actors", cast), ("$now", Utc(DateTime.UtcNow)));
    });

    /// <summary>Ersetzt die Infotexte des Titels samt aller Folgentexte durch den neuen Stand (auch Texte, die der Nutzer geschrieben hat).</summary>
    public void SetTitleInfo(long titleId, string provider, OnlineTitleInfo info) => InTransaction(() =>
    {
        Run("""
            INSERT INTO title_info (title_id, provider, plot, rating, director, actors, user_edited, updated_utc)
            VALUES ($id, $provider, $plot, $rating, $director, $actors, 0, $now)
            ON CONFLICT (title_id) DO UPDATE SET provider = excluded.provider, plot = excluded.plot, rating = excluded.rating,
                director = excluded.director, actors = excluded.actors, user_edited = 0, updated_utc = excluded.updated_utc
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
