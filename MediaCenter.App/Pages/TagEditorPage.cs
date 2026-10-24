using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MediaCenter.App.Controls;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App.Pages;

/// <summary>
/// „Tags ändern“ für einen Song oder ein ganzes Album: schreibt die Angaben in die Audiodateien selbst (ffmpeg ohne Neukodierung, Ton und Cover
/// bleiben). Anders als „Bearbeiten“ (nur der Anzeigename in der Bibliothek) sehen andere Programme die Änderung auch.
/// Beim Song steht in jedem Feld der aktuelle Wert, ein geleertes Feld löscht den Tag; beim Album gilt, was ausgefüllt ist, für alle Titel,
/// ein leeres Feld lässt die Titel unverändert.
/// </summary>
public sealed class TagEditorPage : UserControl, IModalContent
{
    private sealed record Field(string Label, Func<TagChanges, string?, TagChanges> Apply, Func<TrackRow, string> Read);

    private static readonly Field[] TrackFields =
    {
        new("Titel", (c, v) => c with { Title = v }, t => t.TagTitle ?? t.Title),
        new("Künstler", (c, v) => c with { Artist = v }, t => t.TagArtist ?? t.Artist ?? ""),
        new("Album", (c, v) => c with { Album = v }, t => t.TagAlbum ?? t.Album ?? ""),
        new("Album-Künstler", (c, v) => c with { AlbumArtist = v }, t => Known(t.TagAlbumArtist ?? t.AlbumArtist)),
        new("Titelnummer", (c, v) => c with { TrackNo = v }, t => t.TrackNo?.ToString() ?? ""),
        new("Disc", (c, v) => c with { DiscNo = v }, t => t.DiscNo?.ToString() ?? ""),
        new("Jahr", (c, v) => c with { Year = v }, t => t.Year?.ToString() ?? ""),
        new("Genre", (c, v) => c with { Genre = v }, t => t.Genre ?? ""),
    };

    /// <summary>Beim Album sind nur Angaben sinnvoll, die für alle Titel gelten.</summary>
    private static readonly Field[] AlbumFields =
    {
        new("Album", (c, v) => c with { Album = v }, t => t.TagAlbum ?? t.Album ?? ""),
        new("Künstler (alle Titel)", (c, v) => c with { Artist = v }, t => t.TagArtist ?? t.Artist ?? ""),
        new("Album-Künstler", (c, v) => c with { AlbumArtist = v }, t => Known(t.TagAlbumArtist ?? t.AlbumArtist)),
        new("Jahr", (c, v) => c with { Year = v }, t => t.Year?.ToString() ?? ""),
        new("Genre", (c, v) => c with { Genre = v }, t => t.Genre ?? ""),
    };

    private readonly IShell _shell;
    private readonly IReadOnlyList<TrackRow> _tracks;
    private readonly bool _isAlbum;
    private readonly Field[] _fields;
    private readonly List<(Field Field, TextBox Box, string Original)> _boxes = new();
    private bool _busy;

    public string? BlockClose => _busy ? "Die Tags werden gerade geschrieben. Bitte einen Moment warten." : null;

    public static void ShowTrack(IShell shell, long trackId)
    {
        var track = shell.Services.Model.Tracks.FirstOrDefault(t => t.TrackId == trackId) ?? throw new InvalidOperationException($"Titel {trackId} existiert nicht (mehr).");
        shell.OpenModal("Tags ändern", track.Title, new TagEditorPage(shell, new[] { track }, isAlbum: false), ModalSize.Medium);
    }

    public static void ShowAlbum(IShell shell, string albumKey)
    {
        var album = shell.Services.Model.Albums.FirstOrDefault(a => a.Key == albumKey) ?? throw new InvalidOperationException($"Album '{albumKey}' existiert nicht (mehr).");
        shell.OpenModal("Tags ändern", album.Title, new TagEditorPage(shell, album.Tracks, isAlbum: true), ModalSize.Medium);
    }

    private static string Known(string? artist) => artist is null || artist is MusicIndex.UnknownArtist or MusicIndex.VariousArtists ? "" : artist;

    private TagEditorPage(IShell shell, IReadOnlyList<TrackRow> tracks, bool isAlbum)
    {
        _shell = shell;
        _tracks = tracks;
        _isAlbum = isAlbum;
        _fields = isAlbum ? AlbumFields : TrackFields;

        var hint = PageParts.Muted(isAlbum
            ? $"Schreibt die ausgefüllten Angaben in die Dateien aller {tracks.Count} Titel dieses Albums. Ein leeres Feld lässt die Titel unverändert; unterscheiden sie sich, steht „verschieden“ im Feld. "
            : "Schreibt die Angaben in die Datei selbst; ein geleertes Feld löscht den Tag. ", 13);
        hint.Inlines.Add(new System.Windows.Documents.Run("Der Ton wird nicht neu kodiert, eingebettete Cover bleiben. Das ändert die Dateien auf der Platte – andere Programme sehen die Angaben ebenfalls. " +
            "Der Anzeigename aus „Bearbeiten“ wird dabei für diese Titel zurückgesetzt, damit die Tags gelten."));

        var stack = new StackPanel { Children = { hint } };
        foreach (var field in _fields)
        {
            var values = tracks.Select(field.Read).Distinct().ToList();
            var shared = values.Count == 1 ? values[0] : "";
            var (host, box) = PageParts.HintBox(isAlbum && values.Count > 1 ? "verschieden" : field.Label, double.NaN);
            box.Text = shared;
            AutomationProperties.SetName(box, field.Label);
            box.KeyDown += async (_, e) =>
            {
                if (e.Key != System.Windows.Input.Key.Enter) return;
                e.Handled = true;
                await SaveAsync();
            };
            var label = PageParts.Muted(field.Label, 12);
            label.Margin = new Thickness(0, 12, 0, 4);
            stack.Children.Add(label);
            stack.Children.Add(host);
            _boxes.Add((field, box, shared));
        }

        var save = PageParts.Button(isAlbum ? $"In {tracks.Count} Dateien schreiben" : "In die Datei schreiben", "PrimaryButton", async (_, _) => await SaveAsync(), new Thickness(0, 20, 0, 0));
        AutomationProperties.SetName(save, "Tags schreiben");
        save.HorizontalAlignment = HorizontalAlignment.Left;
        stack.Children.Add(save);
        Content = stack;
    }

    private LibraryModel Model => _shell.Services.Model;

    /// <summary>Die Änderungen aus den Feldern: beim Song jede Abweichung vom Stand beim Öffnen, beim Album jedes ausgefüllte Feld.</summary>
    private TagChanges CollectChanges()
    {
        var changes = new TagChanges();
        foreach (var (field, box, original) in _boxes)
        {
            var text = box.Text.Trim();
            if (_isAlbum ? text.Length > 0 && text != original : text != original) changes = field.Apply(changes, text);
        }
        return changes;
    }

    private async Task SaveAsync()
    {
        if (_busy) return;

        var changes = CollectChanges();
        if (changes.IsEmpty)
        {
            _shell.ShowMessage("Es wurde nichts geändert.");
            return;
        }
        try
        {
            changes.Validated();
        }
        catch (ArgumentException ex)
        {
            _shell.ShowMessage(ex.Message, isError: true);
            return;
        }

        var targets = _tracks.Select(t => (Track: t, Path: Model.PathOf(t))).ToList();
        if (targets.Any(t => t.Path is null))
        {
            var missing = targets.First(t => t.Path is null).Track.Title;
            _shell.ShowMessage($"„{missing}“ ist gerade nicht erreichbar (Laufwerk abgesteckt oder Datei fehlt). Es wurde nichts geändert.", isError: true);
            return;
        }

        var audio = _shell.Services.Audio;
        var playing = audio.Queue.Any(item => targets.Any(t => string.Equals(t.Path, item.Source, StringComparison.OrdinalIgnoreCase)));
        var question = $"Tags in {targets.Count} Datei{(targets.Count == 1 ? "" : "en")} schreiben?\n\n" +
                       "• Die Dateien werden neu geschrieben (Ton und Cover bleiben unverändert).\n" +
                       "• Das lässt sich nicht automatisch zurücknehmen; die alten Angaben stehen nur in deinen Sicherungen.\n" +
                       (playing ? "• Die Wiedergabe wird dafür beendet, weil mindestens ein Titel in der Warteschlange liegt." : "");
        if (!_shell.Confirm("Tags in die Dateien schreiben?", question, "Schreiben", danger: true)) return;

        _busy = true;
        var written = new List<TrackRow>();
        var failures = new List<string>();
        try
        {
            var writer = TagWriter.FromEnvironment();
            await _shell.RunBusyAsync("Tags werden geschrieben …", async () =>
            {
                if (playing) await audio.StopAsync();
                foreach (var (track, path) in targets)
                {
                    try
                    {
                        await writer.WriteAsync(path!, changes);
                        written.Add(track);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or ArgumentException)
                    {
                        failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
                    }
                }
            });

            if (written.Count > 0)
            {
                // Die Tags sind jetzt die Wahrheit: ein früher vergebener Anzeigename für Titel oder Künstler würde sie überdecken.
                if (changes.Title is not null || changes.Artist is not null)
                {
                    foreach (var track in written) Model.Db.ResetTrack(track.TrackId);
                }

                IReadOnlyList<(Source Source, ScanResult Result)>? scan = null;
                await _shell.RunBusyAsync("Bibliothek wird aktualisiert …", async () => scan = await Model.Library.RescanAllAsync());
                _shell.ReloadView();
                await _shell.RefreshArtworkAsync();
                if (scan is not null && scan.SelectMany(s => s.Result.Failures).FirstOrDefault() is { } failure)
                {
                    failures.Add($"Neu einlesen: {Path.GetFileName(failure.Path)}: {failure.Error}");
                }
            }
        }
        finally
        {
            _busy = false;
        }

        if (written.Count > 0 && failures.Count == 0) _shell.CloseModal();
        _shell.ShowMessage(
            failures.Count == 0
                ? $"Tags geschrieben: {written.Count} Datei{(written.Count == 1 ? "" : "en")}."
                : $"{written.Count} Datei(en) geschrieben, {failures.Count} Fehler: " + string.Join("; ", failures.Take(3)) + (failures.Count > 3 ? " …" : ""),
            isError: failures.Count > 0);
    }
}
