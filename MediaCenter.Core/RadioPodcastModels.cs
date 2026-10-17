namespace MediaCenter.Core;

/// <summary>Ein Sender, wie ihn die Suche liefert oder der Nutzer ihn speichert.</summary>
/// <param name="Uuid">Kennung im Verzeichnis radio-browser.info; null bei selbst eingegebenen Sendern.</param>
public sealed record RadioStation(
    string? Uuid, string Name, string Url, string? FaviconUrl, string? Country, string? Tags, string? Codec, int? Bitrate, int Votes = 0);

public sealed record SavedStation(long Id, RadioStation Station, string? FaviconFile);

public sealed record PodcastInfo(
    long Id, string FeedUrl, string Title, string? Author, string? Description, string? ImageUrl, string? ImageFile, string? LastRefreshedUtc);

public sealed record EpisodeInfo(
    long Id, long PodcastId, string Guid, string Title, string? PublishedUtc, string AudioUrl, double? DurationSeconds, string? Description);

public sealed record EpisodeProgress(double PositionSeconds, bool Played);

/// <summary>Eine Folge, wie sie im Feed steht.</summary>
public sealed record FeedEpisode(string Guid, string Title, DateTime? PublishedUtc, string AudioUrl, double? DurationSeconds, string? Description);

public sealed record PodcastFeed(string Title, string? Author, string? Description, string? ImageUrl, IReadOnlyList<FeedEpisode> Episodes);

/// <summary>Treffer der Podcast-Suche.</summary>
public sealed record PodcastSearchResult(string Title, string? Author, string FeedUrl, string? ImageUrl);
