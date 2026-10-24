namespace MediaCenter.Core;

/// <param name="MarkedWatched">Filme und Folgen, die bei Trakt als gesehen standen und in der Bibliothek jetzt als gesehen gelten.</param>
/// <param name="Pushed">Gesehene Filme und Folgen der Bibliothek, die an Trakt gingen.</param>
/// <param name="Unmatched">Einträge bei Trakt, die zu keinem Titel der Bibliothek passen (z. B. Filme, die nicht in der Bibliothek liegen); sie bleiben unberührt.</param>
/// <param name="NotFound">Titel, die Trakt beim Eintragen nicht kannte (meist deutsche Titel, die dort anders heißen).</param>
public sealed record TraktSyncResult(int MarkedWatched, int Pushed, int Unmatched, IReadOnlyList<string> NotFound);

/// <summary>
/// Gleicht „gesehen“ mit Trakt ab: Was dort als gesehen steht, gilt in der Bibliothek auch als gesehen (nie umgekehrt zurückgenommen), und was in der
/// Bibliothek gesehen ist und bei Trakt fehlt, wird dort eingetragen. Zugeordnet wird über Titel (ohne Akzente, Satzzeichen, Großschreibung) und Jahr;
/// die Bibliothek kennt keine Trakt-Kennungen. Einmal gesendete Einträge merkt sich die Datenbank, damit nichts doppelt ankommt.
/// </summary>
public sealed class TraktSync(LibraryDb db)
{
    private const int BatchSize = 200;

    public async Task<TraktSyncResult> SyncAsync(TraktClient client, IReadOnlyList<TitleRow> titles, IReadOnlyList<VideoRow> videos,
        IReadOnlyDictionary<long, PlaybackState> playback, CancellationToken ct = default)
    {
        var remoteMovies = await client.GetWatchedMoviesAsync(ct);
        var remoteShows = await client.GetWatchedShowsAsync(ct);

        var byTitle = videos.Where(v => v.TitleId is not null).GroupBy(v => v.TitleId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var movies = titles.Where(t => t.Kind == TitleKind.Movie).ToList();
        var series = titles.Where(t => t.Kind == TitleKind.Series).ToList();

        var onTrakt = new HashSet<long>(); // Videos, die Trakt schon kennt
        var markWatched = new HashSet<long>();
        var unmatched = 0;

        bool Watched(long videoId) => playback.TryGetValue(videoId, out var state) && state.Watched;

        foreach (var remote in remoteMovies)
        {
            if (FindTitle(movies, remote.Title, remote.Year) is not { } title) { unmatched++; continue; }
            foreach (var video in byTitle.GetValueOrDefault(title.Id) ?? new List<VideoRow>())
            {
                onTrakt.Add(video.VideoId);
                if (!Watched(video.VideoId)) markWatched.Add(video.VideoId);
            }
        }
        foreach (var remote in remoteShows)
        {
            if (FindTitle(series, remote.Title, remote.Year) is not { } title) { unmatched++; continue; }
            var episodes = byTitle.GetValueOrDefault(title.Id) ?? new List<VideoRow>();
            foreach (var watched in remote.Episodes)
            {
                foreach (var video in episodes.Where(v => v.Season == watched.Season && v.Episode == watched.Episode))
                {
                    onTrakt.Add(video.VideoId);
                    if (!Watched(video.VideoId)) markWatched.Add(video.VideoId);
                }
            }
        }
        if (markWatched.Count > 0) db.SetVideosWatched(markWatched, true);

        // Hin: gesehen in der Bibliothek, bei Trakt nicht bekannt und noch nicht gesendet
        var pushed = db.GetTraktPushed();
        var movieWatches = new List<(long VideoId, TraktMovieWatch Watch)>();
        var showWatches = new Dictionary<long, List<(long VideoId, TraktWatchedEpisode Episode)>>();
        foreach (var video in videos)
        {
            if (video.TitleId is not { } titleId || !Watched(video.VideoId) || onTrakt.Contains(video.VideoId) || pushed.Contains(video.VideoId) || markWatched.Contains(video.VideoId)) continue;

            var when = playback.TryGetValue(video.VideoId, out var state) && DateTime.TryParse(state.UpdatedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToUniversalTime() : DateTime.UtcNow;
            if (video.TitleKind == TitleKind.Movie && titles.FirstOrDefault(t => t.Id == titleId) is { } movie)
            {
                movieWatches.Add((video.VideoId, new TraktMovieWatch(movie.Name, movie.Year, when)));
            }
            else if (video.TitleKind == TitleKind.Series && video.Season is { } season && video.Episode is { } episode)
            {
                if (!showWatches.TryGetValue(titleId, out var list)) showWatches[titleId] = list = new();
                list.Add((video.VideoId, new TraktWatchedEpisode(season, episode, when)));
            }
        }

        var added = 0;
        var notFound = new List<string>();
        foreach (var batch in movieWatches.Chunk(BatchSize))
        {
            var result = await client.AddHistoryAsync(batch.Select(b => b.Watch).ToList(), Array.Empty<TraktShowWatch>(), ct);
            added += result.Added;
            notFound.AddRange(result.NotFound);
            db.MarkTraktPushed(batch.Select(b => b.VideoId), DateTime.UtcNow); // gleich merken: bricht ein späterer Abruf ab, geht nichts doppelt hin
        }
        foreach (var (titleId, list) in showWatches)
        {
            var title = titles.First(t => t.Id == titleId);
            foreach (var batch in list.Chunk(BatchSize))
            {
                var watch = new TraktShowWatch(title.Name, title.Year, batch.Select(b => b.Episode).ToList());
                var result = await client.AddHistoryAsync(Array.Empty<TraktMovieWatch>(), new[] { watch }, ct);
                added += result.Added;
                notFound.AddRange(result.NotFound);
                    db.MarkTraktPushed(batch.Select(b => b.VideoId), DateTime.UtcNow);
            }
        }
        return new TraktSyncResult(markWatched.Count, added, unmatched, notFound.Distinct().ToList());
    }

    /// <summary>Der Titel der Bibliothek mit gleichem Namen (ohne Akzente, Satzzeichen, Großschreibung) und, wo beide ein Jahr kennen, gleichem Jahr.</summary>
    private static TitleRow? FindTitle(IReadOnlyList<TitleRow> titles, string name, int? year)
    {
        var key = PosterNames.MatchKey(name);
        if (key.Length == 0) return null;
        return titles.FirstOrDefault(t => PosterNames.MatchKey(t.Name) == key && (t.Year is null || year is null || t.Year == year));
    }
}
