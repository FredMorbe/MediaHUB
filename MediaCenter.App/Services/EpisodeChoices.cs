using System.IO;
using MediaCenter.App.Player;
using MediaCenter.Core;

namespace MediaCenter.App.Services;

/// <summary>Die Folgen einer Serie als Auswahlliste (Player und „Folge wählen“ auf der Startseite).</summary>
public static class EpisodeChoices
{
    /// <summary>Alle Videos des Titels nach Staffel und Folge; ohne Folgennummer steht der Dateiname.</summary>
    public static IReadOnlyList<EpisodeChoice> For(LibraryModel model, long titleId) =>
        model.VideosOf(titleId)
            .OrderBy(v => v.Season ?? int.MaxValue)
            .ThenBy(v => v.Episode ?? int.MaxValue)
            .ThenBy(v => v.RelPath, StringComparer.OrdinalIgnoreCase)
            .Select(v => new EpisodeChoice(
                v.VideoId, v.Season, v.Episode is { } n ? $"Folge {n}" : Path.GetFileNameWithoutExtension(v.RelPath),
                model.Db.GetPlayback(v.VideoId) is { Watched: true }, model.PathOf(v) is not null))
            .ToList();
}
