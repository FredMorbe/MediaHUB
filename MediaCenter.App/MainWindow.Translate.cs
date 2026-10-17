using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App;

// Untertitel mit einem lokalen Modell (Ollama) übersetzen. Das Video läuft währenddessen weiter.
public partial class MainWindow
{
    /// <summary>Dateiformate, die sich ohne Zusatzdatei in Text umwandeln lassen (.sub/.idx sind oft Bilder).</summary>
    private static readonly HashSet<string> TranslatableExtensions = new(StringComparer.OrdinalIgnoreCase) { ".srt", ".ass", ".ssa", ".vtt", ".smi" };

    private CancellationTokenSource? _translateCts;

    private static bool CanTranslate(MediaTrack track)
    {
        if (track.Type != "sub") return false;
        if (track.File is { } file) return TranslatableExtensions.Contains(Path.GetExtension(file));

        return !track.External && track.FfIndex is not null && SubtitleExtractor.IsText(track.Codec);
    }

    /// <summary>
    /// Übersetzt die gewählte Spur nacheinander in die gewählten Sprachen und legt jede als Untertitel dieses Videos ab.
    /// Alle erwartbaren Fehler (Ollama aus, Modell fehlt, Modell liefert Unbrauchbares) erscheinen im Panel und als Hinweis.
    /// </summary>
    private async Task TranslateSubtitleAsync(int trackId, IReadOnlyList<string> targetIds)
    {
        if (_translateCts is not null)
        {
            PlayerNotice("Es läuft bereits eine Übersetzung.", isError: true);
            return;
        }
        if (_playing is not { } video || _mpv is null) return;
        if (_tracks.FirstOrDefault(t => t.Type == "sub" && t.Id == trackId && CanTranslate(t)) is not { } track) return;

        var name = TitleOf(video);
        var videoPath = Services.Model.PathOf(video);
        using var cts = new CancellationTokenSource();
        _translateCts = cts;
        var finished = new List<string>();
        string message;
        var isError = false;
        try
        {
            var targets = targetIds.Select(TranslationTarget.Get).ToList();
            var ollama = Services.Ollama();

            _overlay.SetTranslateState(true, "Prüfe Ollama …");
            var installed = await ollama.ListModelsAsync(cts.Token);
            foreach (var target in targets)
            {
                var wanted = Services.Settings.ModelFor(target)
                    ?? throw new InvalidOperationException("Es ist noch kein Übersetzungsmodell gewählt (Einstellungen → Übersetzung).");
                if (!installed.Any(m => string.Equals(m, wanted, StringComparison.OrdinalIgnoreCase) || string.Equals(m, wanted + ":latest", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException($"Das Modell „{wanted}“ ist in Ollama nicht installiert (ollama pull {wanted}).");
                }
            }

            _overlay.SetTranslateState(true, "Lese den Untertitel …");
            var cues = track.File is { } file
                ? await Services.SubtitleText.FromFileAsync(file, cts.Token)
                : await Services.SubtitleText.FromStreamAsync(
                    videoPath ?? throw new FileNotFoundException("Die Videodatei ist nicht erreichbar."), track.FfIndex!.Value, cts.Token);
            var sourceLanguage = track.Language?.EnglishName;

            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                var modelName = Services.Settings.ModelFor(target)!;
                var stored = await TranslateOneAsync(video, name, cues, sourceLanguage, target, modelName, i, targets.Count, ollama, cts.Token);
                finished.Add(target.DisplayName);
                ShowTranslated(video, stored, target, select: finished.Count == 1);
            }
            message = $"Übersetzung fertig: {string.Join(", ", finished)}.";
        }
        catch (OperationCanceledException)
        {
            message = "Übersetzung abgebrochen. Bereits Übersetztes ist gesichert; ein neuer Start setzt dort fort.";
        }
        catch (Exception ex)
        {
            // Die Aufrufstelle wartet nicht auf die Aufgabe; jeder Fehler muss hier sichtbar werden, auch unerwartete.
            isError = true;
            var known = ex is OllamaException or TranslationException or InvalidOperationException or InvalidDataException or FileNotFoundException
                or TimeoutException or IOException or UnauthorizedAccessException or ArgumentException;
            message = known ? $"Übersetzung nicht möglich: {ex.Message}" : $"Übersetzung fehlgeschlagen ({ex.GetType().Name}): {ex.Message}";
        }
        finally
        {
            _translateCts = null;
        }

        _overlay.SetTranslateState(false, message);
        PlayerNotice(message, isError);
    }

    /// <summary>Übersetzt in eine Zielsprache und speichert das Ergebnis; liefert den Pfad der gespeicherten Datei.</summary>
    private async Task<string> TranslateOneAsync(
        VideoRow video, string name, IReadOnlyList<SrtCue> cues, string? sourceLanguage, TranslationTarget target, string modelName,
        int targetIndex, int targetCount, OllamaClient ollama, CancellationToken ct)
    {
        var translator = new SubtitleTranslator(ollama.Model(modelName));
        var checkpoint = Path.Combine(AppSettings.TranslationDirectory, $"{video.VideoId}-{target.Id}.json");
        var started = Stopwatch.StartNew();
        var firstDone = -1;
        string? placementText = null;

        var progress = new Progress<TranslationProgress>(p =>
        {
            if (firstDone < 0)
            {
                firstDone = p.DoneCues;
                return; // erste Meldung: Stand vor dem ersten Block
            }
            if (placementText is null)
            {
                placementText = "";
                _ = ShowPlacementAsync(ollama, modelName, text => placementText = text);
            }

            var fraction = p.TotalCues == 0 ? 1 : (double)p.DoneCues / p.TotalCues;
            var rate = (p.DoneCues - firstDone) / Math.Max(started.Elapsed.TotalSeconds, 1);
            var remaining = rate > 0 ? TimeSpan.FromSeconds((p.TotalCues - p.DoneCues) / rate) : (TimeSpan?)null;
            var status = new StringBuilder($"{target.DisplayName}: {p.DoneCues} von {p.TotalCues} Einträgen");
            if (remaining is { } left && p.DoneCues - firstDone >= 10)
            {
                status.Append(left.TotalSeconds < 60 ? " · gleich fertig" : $" · noch etwa {(int)Math.Round(left.TotalMinutes)} min");
            }
            if (!string.IsNullOrEmpty(placementText)) status.AppendLine().Append(placementText);
            _overlay.SetTranslateState(true, status.ToString(), (targetIndex + fraction) / targetCount);
        });

        _overlay.SetTranslateState(true, $"{target.DisplayName}: Modell „{modelName}“ wird geladen …", targetIndex / (double)targetCount);
        var translated = await translator.TranslateAsync(cues, target, new TranslationOptions(SourceLanguage: sourceLanguage), progress, checkpoint, ct);

        var temp = Directory.CreateTempSubdirectory("mediacenter-translation-");
        try
        {
            var file = Path.Combine(temp.FullName, target.FileLabel + ".srt");
            File.WriteAllText(file, Srt.Format(translated), new UTF8Encoding(false));
            return Services.Subtitles.Add(video.VideoId, file, target.Language);
        }
        finally
        {
            try { temp.Delete(recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Aufräumen darf das Ergebnis nicht verwerfen */ }
        }
    }

    /// <summary>Zeigt im Panel, ob das Modell auf der Grafikkarte läuft; auf der CPU dauert ein Film über eine Stunde.</summary>
    private async Task ShowPlacementAsync(OllamaClient ollama, string model, Action<string> set)
    {
        try
        {
            var placement = await ollama.GetPlacementAsync(model);
            set(placement.Kind is PlacementKind.Gpu or PlacementKind.NotLoaded
                ? $"Läuft auf: {placement.Describe()}"
                : $"Achtung, läuft auf: {placement.Describe()} – das dauert deutlich länger (Grafikkarte aktivieren?)");
        }
        catch (OllamaException ex)
        {
            set($"Wo das Modell läuft, ist unbekannt: {ex.Message}");
        }
    }

    /// <summary>Hängt die fertige Übersetzung an das laufende Video; ist das Video nicht mehr offen, bleibt sie für das nächste Mal gespeichert.</summary>
    private void ShowTranslated(VideoRow video, string stored, TranslationTarget target, bool select)
    {
        if (_playing?.VideoId != video.VideoId || _mpv is null) return;

        if (select) _subtitlePicked = true; // gewählt ist gewählt: die Automatik fasst die Spur nicht an
        _ = _mpv.CommandAsync("sub-add", stored, select ? "select" : "auto");
    }
}
