using System.IO;
using MediaCenter.Core;

namespace MediaCenter.App.Services;

/// <summary>
/// Kurze, tonlose Ausschnitte für die Vorschau beim Überfahren einer Karte. Ein Ausschnitt entsteht beim ersten Mal per ffmpeg
/// und liegt danach im Datenordner; es läuft immer nur ein ffmpeg gleichzeitig. Alle Aufrufe kommen vom UI-Thread.
/// </summary>
public sealed class PreviewClips(LibraryModel model, FfmpegTool ffmpeg, AppSettings settings, string cacheDir)
{
    private const double ClipSeconds = 10;
    private const int Height = 480;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Task<string?>> _running = new();
    private readonly HashSet<string> _failed = new();

    public bool Enabled => settings.PreviewOnHover;

    /// <summary>Ein Ausschnitt konnte nicht erzeugt werden (Fehlertext); pro Datei nur einmal je Sitzung.</summary>
    public event Action<string>? Failed;

    /// <summary>Pfad des Ausschnitts für den Titel; null, wenn keine Videodatei erreichbar ist oder der Ausschnitt fehlschlug.</summary>
    public Task<string?> GetAsync(long titleId)
    {
        // Bei Serien die erste erreichbare Folge, bei Filmen die Datei selbst.
        var video = model.VideosOf(titleId).Where(v => model.PathOf(v) is not null)
            .OrderBy(v => v.Season ?? 0).ThenBy(v => v.Episode ?? 0).FirstOrDefault();
        if (video is null) return Task.FromResult<string?>(null);

        var target = Path.Combine(cacheDir, $"{video.VideoId}-{video.FileId}.mp4");
        if (File.Exists(target)) return Task.FromResult<string?>(target);
        if (_failed.Contains(target)) return Task.FromResult<string?>(null);
        if (_running.TryGetValue(target, out var running)) return running;

        var task = CreateAsync(video, model.PathOf(video)!, target);
        // Ist der Task schon fertig, hat sich CreateAsync selbst aus der Liste genommen; sonst würde der Eintrag bleiben.
        if (!task.IsCompleted) _running[target] = task;
        return task;
    }

    private async Task<string?> CreateAsync(VideoRow video, string path, string target)
    {
        try
        {
            // Nicht ganz am Anfang (Vorspann), aber weit genug vor dem Ende; kurze oder unbekannte Dauer: ab Anfang.
            var duration = video.DurationSeconds ?? 0;
            var start = duration > 4 * ClipSeconds ? Math.Min(duration * (video.TitleKind == TitleKind.Movie ? 0.3 : 0.25), duration - 2 * ClipSeconds) : 0;
            var length = duration > 0 ? Math.Min(ClipSeconds, duration - start) : ClipSeconds;

            await _gate.WaitAsync();
            try
            {
                if (File.Exists(target)) return target;
                await ffmpeg.CreatePreviewAsync(path, start, length, target, Height, video.IsHdr);
            }
            finally
            {
                _gate.Release();
            }
            return target;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            _failed.Add(target);
            Failed?.Invoke($"Vorschau für „{video.TitleName ?? Path.GetFileName(path)}“ nicht möglich: {ex.Message}");
            return null;
        }
        finally
        {
            _running.Remove(target);
        }
    }

    /// <summary>Der Ausschnitt ließ sich nicht abspielen: Datei verwerfen (sie wird beim nächsten Überfahren neu erzeugt) und melden.</summary>
    public void ReportUnplayable(string clip, string reason)
    {
        try
        {
            File.Delete(clip);
        }
        catch (IOException)
        {
            // Wird noch abgespielt oder gelesen; der Fehler selbst wird trotzdem gemeldet.
        }
        _failed.Add(clip);
        Failed?.Invoke($"Vorschau „{Path.GetFileName(clip)}“ lässt sich nicht abspielen: {reason}");
    }
}
