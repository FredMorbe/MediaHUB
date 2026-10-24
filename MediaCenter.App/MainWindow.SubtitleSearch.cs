using System.IO;
using MediaCenter.Core;

namespace MediaCenter.App;

// Untertitel online suchen (OpenSubtitles): Suche und Download im Player. Der Treffer wird wie eine selbst geladene Datei gespeichert (AddSubtitle).
public partial class MainWindow
{
    /// <summary>Angemeldeter Client der letzten Suche; ein Download nutzt dasselbe Token.</summary>
    private OpenSubtitlesClient? _subtitleClient;

    private bool _subtitleSearching;

    private void InitSubtitleSearch()
    {
        _overlay.SubtitleSearchRequested += () => _ = SearchSubtitlesAsync();
        _overlay.SubtitleHitChosen += hit => _ = DownloadSubtitleAsync(hit);
    }

    private async Task SearchSubtitlesAsync()
    {
        if (_playing is not { } video || _subtitleSearching) return;

        var language = Languages.Get(Services.Settings.SubtitleLanguage);
        var title = video.TitleName ?? video.ParsedTitle;
        _overlay.ShowSubtitleSearch($"Suche nach Untertiteln ({language.Name}) für „{title}“ …");
        _subtitleSearching = true;
        try
        {
            _subtitleClient = null;
            var client = await Services.OpenSubtitlesAsync();
            _subtitleClient = client;

            // Die Prüfsumme findet genau die Fassung dieser Datei; ohne lesbare Datei bleibt es bei Titel und Jahr bzw. Staffel und Folge.
            string? hash = null;
            if (Services.Model.PathOf(video) is { } path)
            {
                try
                {
                    hash = await Task.Run(() => OpenSubtitlesClient.MovieHash(path));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    hash = null;
                }
            }

            var hits = await client.SearchAsync(new SubtitleQuery(title, video.Year, video.Season, video.Episode, language.Id, hash));
            var matches = hits.Count(h => h.HashMatch);
            _overlay.SetSubtitleHits(hits, hits.Count == 0
                ? $"Keine Untertitel ({language.Name}) gefunden. Du kannst eine eigene Datei laden: Untertitel → „Untertitel-Datei laden …“."
                : $"{hits.Count} Treffer" + (matches > 0 ? $", {matches} passen nachweislich zu dieser Datei." : ". Welcher zur Fassung deiner Datei passt, zeigt sich beim Ausprobieren: „Anpassen“ verschiebt die Zeit."));
        }
        catch (OpenSubtitlesException ex)
        {
            _overlay.SetSubtitleSearchStatus(ex.Message);
        }
        finally
        {
            _subtitleSearching = false;
        }
    }

    private async Task DownloadSubtitleAsync(SubtitleHit hit)
    {
        if (_subtitleClient is not { } client || _playing is null) return;

        _overlay.SetSubtitleSearchBusy(true);
        _overlay.SetSubtitleSearchStatus($"Lade „{hit.Release}“ …");
        var temp = Path.Combine(Path.GetTempPath(), $"opensubtitles-{hit.FileId}.{hit.Language}.srt");
        try
        {
            var subtitle = await client.DownloadAsync(hit.FileId);
            await File.WriteAllBytesAsync(temp, subtitle.Content);
            AddSubtitle(temp);
            _overlay.SetSubtitleSearchStatus(subtitle.Remaining is { } left ? $"Geladen und eingeblendet. Heute noch {left} Downloads." : "Geladen und eingeblendet.");
        }
        catch (OpenSubtitlesException ex)
        {
            _overlay.SetSubtitleSearchStatus(ex.Message);
        }
        catch (IOException ex)
        {
            _overlay.SetSubtitleSearchStatus($"Der Untertitel ließ sich nicht speichern: {ex.Message}");
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
                // Eine Rest-Datei im temporären Ordner ist harmlos.
            }
            _overlay.SetSubtitleSearchBusy(false);
        }
    }
}
