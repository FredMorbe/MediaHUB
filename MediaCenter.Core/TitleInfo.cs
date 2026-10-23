namespace MediaCenter.Core;

/// <summary>Infotext einer Folge von einem Online-Dienst.</summary>
public sealed record EpisodeText(int Season, int Episode, string? Name, string? Summary);

/// <summary>Was ein Dienst über einen Film oder eine Serie weiß; fehlende Angaben sind null.</summary>
/// <param name="Title">Schreibweise beim Dienst (zur Anzeige in Meldungen).</param>
/// <param name="Rating">Bewertung von 0 bis 10 (OMDb: IMDb, TVmaze: TVmaze-Nutzer).</param>
/// <param name="Cast">Die ersten Schauspieler, mit Komma getrennt.</param>
/// <param name="Episodes">Nur bei Serien: Namen und Zusammenfassungen der Folgen.</param>
public sealed record OnlineTitleInfo(string Title, string? Plot, double? Rating, string? Director, string? Cast, IReadOnlyList<EpisodeText> Episodes)
{
    /// <summary>Der Dienst kennt den Titel, hat aber nichts, was sich anzuzeigen lohnt.</summary>
    public bool IsEmpty => Plot is null && Rating is null && Director is null && Cast is null && Episodes.Count == 0;
}

/// <summary>Gespeicherte Infotexte eines Titels.</summary>
/// <param name="Provider">Von welchem Dienst sie stammen (für die Quellenangabe).</param>
public sealed record TitleInfoRow(long TitleId, string Provider, string? Plot, double? Rating, string? Director, string? Cast);

/// <summary>Findet Infotexte im Internet. Gibt null zurück, wenn es keinen sicheren Treffer gibt (nie Texte eines anderen Titels).</summary>
public interface ITitleInfoSource
{
    /// <summary>Kennung des Anbieters, auch für die Quellenangabe auf der Detailseite.</summary>
    string Provider { get; }

    /// <exception cref="PosterSourceUnavailableException">Der Dienst lehnt ab (falscher Schlüssel, Tageslimit): weitere Anfragen sind in diesem Durchlauf sinnlos.</exception>
    Task<OnlineTitleInfo?> FindInfoAsync(string title, int? year, CancellationToken ct = default);
}
