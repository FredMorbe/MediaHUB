using MediaCenter.Core;

namespace MediaCenter.App.Services;

/// <summary>Was "Abspielen" bei einem Titel starten soll.</summary>
public static class PlayTarget
{
    /// <summary>
    /// Angefangenes Video (mit Fortsetzen), sonst das erste ungesehene, sonst das erste überhaupt.
    /// Null, wenn der Titel keine Videos hat.
    /// </summary>
    public static (VideoRow Video, bool Resume)? For(LibraryModel model, long titleId)
    {
        var videos = model.VideosOf(titleId);
        if (videos.Count == 0) return null;

        if (model.Continue.FirstOrDefault(c => c.TitleId == titleId) is { } started
            && videos.FirstOrDefault(v => v.VideoId == started.VideoId) is { } resumable)
        {
            return (resumable, true);
        }

        var unwatched = videos.FirstOrDefault(v => model.Db.GetPlayback(v.VideoId) is not { Watched: true });
        return (unwatched ?? videos[0], false);
    }
}
