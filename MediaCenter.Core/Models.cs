namespace MediaCenter.Core;

public enum SourceKind
{
    Folder,
    File,
}

public enum TitleKind
{
    Movie,
    Series,
}

public sealed record KnownVolume(
    long Id, string Serial, string Label, string FileSystem, long Capacity, string Name, string? LastRoot, string? LastSeenUtc);

/// <param name="RelPath">Pfad relativ zum Laufwerks-Root; "" steht für den Root selbst.</param>
public sealed record Source(long Id, long VolumeId, SourceKind Kind, string RelPath);

public sealed record KnownFile(long Id, long Size, string MtimeUtc, bool Missing);

/// <summary>Wo eine bekannte Datei zuletzt lag.</summary>
public sealed record FileLocation(long FileId, long VolumeId, string RelPath);

/// <summary>Ein Video mit abgeleiteten (Parsed*) und wirksamen (Title*, Season, Episode) Werten.</summary>
/// <remarks>TitleId == null bedeutet: Inbox, noch nicht zugeordnet.</remarks>
public sealed record VideoRow(
    long VideoId, long FileId, long VolumeId, string RelPath, bool Missing,
    long? TitleId, TitleKind? TitleKind, string? TitleName, int? Year, int? Season, int? Episode, bool Locked,
    string ParsedTitle, VideoKind ParsedKind,
    double? DurationSeconds, int? Width, int? Height, string? VideoCodec, HdrFormat Hdr, DiscKind Disc = DiscKind.None)
{
    public bool IsInbox => TitleId is null;

    /// <summary>Das Bild trägt PQ oder HLG und lässt sich mit ffmpeg nach SDR umrechnen (Vorschau, Übertragen).</summary>
    public bool IsHdr => Hdr.IsToneMappable();
}

public sealed record PlaybackState(double PositionSeconds, bool Watched, string UpdatedUtc);

/// <summary>Angefangenes, noch nicht fertig gesehenes Video eines Titels (für "Weiterschauen").</summary>
public sealed record ContinueItem(long TitleId, long VideoId, double PositionSeconds, double? DurationSeconds, int? Season, int? Episode);

public enum ArtworkOwner
{
    Title,
    Video,
}

public enum ArtworkKind
{
    /// <summary>Hochformat (2:3).</summary>
    Poster,

    /// <summary>Querformat (16:9): Backdrop eines Titels oder Standbild einer Episode.</summary>
    Landscape,
}

/// <summary>Herkunft eines Covers; in dieser Reihenfolge gewinnt der erste Treffer, User schlägt alles.</summary>
public enum ArtworkSource
{
    User,
    Sidecar,
    Embedded,

    /// <summary>Aus dem Internet geholt (bisher: Serienposter von TVmaze).</summary>
    Online,
    Frame,
}

/// <param name="File">Dateiname im Cover-Cache, nicht der Originalpfad.</param>
public sealed record ArtworkRow(ArtworkOwner Owner, long OwnerId, ArtworkKind Kind, ArtworkSource Source, string File, string UpdatedUtc);

/// <summary>Eine Suche im Internet nach dem Cover eines Titels.</summary>
/// <param name="Query">Wonach gesucht wurde (Name und Jahr); ändert sich der Titel, gilt die Suche als neu.</param>
public sealed record OnlineLookup(string Query, bool Found, DateTime CheckedUtc);

public sealed record TitleRow(long Id, TitleKind Kind, string Name, int? Year);

/// <summary>Eine Videodatei eines Titels samt Ort; für Cover-Suche und Wiedergabe.</summary>
public sealed record TitleVideo(
    long VideoId, long VolumeId, string RelPath, bool Missing, double? DurationSeconds, int? Season, int? Episode, DiscKind Disc = DiscKind.None);

/// <summary>Zeitversatz, Größe und Höhe der Untertitel eines Videos; die Standardwerte ändern nichts.</summary>
/// <param name="DelaySeconds">Positiv zeigt die Untertitel später (mpv: sub-delay).</param>
/// <param name="Scale">1 = Standardgröße (mpv: sub-scale).</param>
/// <param name="Position">0 = oben, 100 = unten, der Standard (mpv: sub-pos).</param>
public sealed record SubtitleAdjustment(double DelaySeconds = 0, double Scale = 1, int Position = 100)
{
    public const double MaxDelaySeconds = 600;
    public const double MinScale = 0.3;
    public const double MaxScale = 3;

    public static readonly SubtitleAdjustment Default = new();

    public bool IsDefault => this == Default;

    /// <summary>Wirft bei Werten außerhalb des sinnvollen Bereichs (nie still klemmen).</summary>
    public SubtitleAdjustment Validated()
    {
        if (!double.IsFinite(DelaySeconds) || Math.Abs(DelaySeconds) > MaxDelaySeconds)
            throw new ArgumentOutOfRangeException(nameof(DelaySeconds), DelaySeconds, $"Zeitversatz höchstens ±{MaxDelaySeconds:0} s.");
        if (!double.IsFinite(Scale) || Scale < MinScale || Scale > MaxScale)
            throw new ArgumentOutOfRangeException(nameof(Scale), Scale, $"Größe zwischen {MinScale} und {MaxScale}.");
        if (Position is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(Position), Position, "Position zwischen 0 (oben) und 100 (unten).");
        return this;
    }
}
