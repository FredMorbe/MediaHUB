namespace MediaCenter.Core;

/// <summary>Eine Senderliste (M3U), aus der die Sender stammen.</summary>
/// <param name="Location">Web-Adresse oder Dateipfad der Liste. Adressen von IPTV-Anbietern enthalten oft Zugangsdaten: sie werden nie in Meldungen oder Listen gezeigt.</param>
/// <param name="EpgLocation">Web-Adresse oder Dateipfad des Programmführers (XMLTV); null, wenn es keinen gibt.</param>
public sealed record LiveSource(long Id, string Name, string Location, string? EpgLocation, DateTime? RefreshedUtc, DateTime? GuideRefreshedUtc, int ChannelCount);

/// <param name="Url">Adresse des Streams; kann Zugangsdaten enthalten und wird nie angezeigt.</param>
/// <param name="EpgId">Kennung im Programmführer der Liste; null, solange kein Führer zugeordnet ist.</param>
public sealed record LiveChannel(
    long Id, long SourceId, string Name, string Url, string? Group, string? TvgId, string? EpgId, string? LogoUrl, int? Number, bool Favorite);

/// <param name="Group">Nur diese Gruppe; ein leerer Text heißt „ohne Gruppe“.</param>
/// <param name="Text">Suchtext; verglichen wird ohne Groß-/Kleinschreibung, Akzente, Satzzeichen und Leerzeichen.</param>
public sealed record LiveChannelFilter(long? SourceId = null, string? Group = null, bool FavoritesOnly = false, string? Text = null);

public sealed record LiveGroup(string Name, int Count);

public sealed record LiveProgramme(DateTime StartUtc, DateTime StopUtc, string Title, string? Description);

/// <summary>Was gerade läuft und was danach kommt; fehlende Angaben sind null.</summary>
public sealed record LiveNowNext(LiveProgramme? Now, LiveProgramme? Next);

public sealed record LiveRefresh(int Total, int Added, int Removed, int Skipped);

/// <param name="WithGuide">Sender, denen ein Eintrag im Programmführer zugeordnet werden konnte.</param>
public sealed record LiveGuideRefresh(int Channels, int WithGuide, int Programmes, int Skipped);
