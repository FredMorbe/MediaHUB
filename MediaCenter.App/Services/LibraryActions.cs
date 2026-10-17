using Microsoft.Win32;
using MediaCenter.Core;

namespace MediaCenter.App.Services;

/// <summary>Bibliothek erweitern und neu scannen, mit Rückmeldung an den Nutzer.</summary>
public static class LibraryActions
{
    public static async Task AddFolderAsync(IShell shell)
    {
        var dialog = new OpenFolderDialog { Title = "Ordner mit Filmen und Serien wählen" };
        if (dialog.ShowDialog() != true) return;
        await AddAsync(shell, "Ordner wird gescannt …", () => shell.Services.Model.Library.AddFolderAsync(dialog.FolderName));
    }

    public static async Task AddFileAsync(IShell shell)
    {
        var extensions = string.Join(";", MediaExtensions.Video.Select(e => "*" + e));
        var dialog = new OpenFileDialog { Title = "Videodatei wählen", Filter = $"Videos|{extensions}|Alle Dateien|*.*" };
        if (dialog.ShowDialog() != true) return;
        await AddAsync(shell, "Datei wird gelesen …", () => shell.Services.Model.Library.AddFileAsync(dialog.FileName));
    }

    public static async Task RescanAsync(IShell shell)
    {
        IReadOnlyList<(Source Source, ScanResult Result)>? results = null;
        await shell.RunBusyAsync("Bibliothek wird neu gescannt …", async () => results = await shell.Services.Model.Library.RescanAllAsync());
        if (results is null) return;

        shell.ReloadView();
        Report(shell, results.Select(r => r.Result).ToList());
        await shell.RefreshArtworkAsync();
    }

    /// <summary>Papierkorb, wo es einen gibt; auf Laufwerken ohne Papierkorb (USB) wird endgültig gelöscht.</summary>
    public static void Recycle(string path) => Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
        path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

    /// <summary>Öffnet den MKV-Editor für den Film eines Titels; sagt dem Nutzer, warum es nicht geht (offline, keine MKV).</summary>
    public static void EditMovie(IShell shell, long titleId)
    {
        if (shell.Services.Model.VideosOf(titleId).FirstOrDefault() is not { } video)
        {
            shell.ShowMessage("Zu diesem Titel gibt es keine Videodatei.", isError: true);
            return;
        }
        EditVideo(shell, video);
    }

    public static void EditVideo(IShell shell, VideoRow video)
    {
        if (shell.Services.Model.PathOf(video) is not { } path)
        {
            shell.ShowMessage($"Die Datei ist nicht erreichbar (Laufwerk abgesteckt oder Datei fehlt): {video.RelPath}", isError: true);
            return;
        }
        if (!MkvEditor.CanEdit(path))
        {
            shell.ShowMessage("Bearbeiten ist nur bei MKV-Dateien möglich.", isError: true);
            return;
        }
        shell.Open(() => new Pages.MkvEditorPage(shell, video, path));
    }

    /// <summary>Der Nutzer wählt eine Bilddatei als Poster oder Hintergrund des Titels; sie ersetzt das automatische Bild.</summary>
    public static async Task ReplaceArtworkAsync(IShell shell, long titleId, ArtworkKind kind)
    {
        var filter = string.Join(";", MediaExtensions.Image.Select(e => "*" + e));
        var dialog = new OpenFileDialog { Title = kind == ArtworkKind.Poster ? "Poster wählen" : "Hintergrundbild wählen", Filter = $"Bilder|{filter}" };
        if (dialog.ShowDialog() != true) return;

        await shell.RunBusyAsync("Bild wird übernommen …",
            () => shell.Services.Model.Artwork.SetUserArtworkAsync(ArtworkOwner.Title, titleId, kind, dialog.FileName));
        shell.ReloadView();
    }

    /// <summary>Öffnet den eingebetteten Browser auf der IMDb-Suche zum Titel; dort lässt sich ein Bild per Rechtsklick als Poster übernehmen.</summary>
    public static void SearchPosterOnline(IShell shell, long titleId)
    {
        if (shell.Services.Model.Titles.FirstOrDefault(t => t.Id == titleId) is not { } title)
        {
            shell.ShowMessage("Der Titel ist nicht mehr in der Bibliothek.", isError: true);
            return;
        }
        shell.Open(() => new Pages.PosterBrowserPage(shell, title));
    }

    /// <summary>Kontextmenü einer Titelkarte; leer für Karten ohne Titel in der Bibliothek (Alben, Podcasts, Sender).</summary>
    public static IReadOnlyList<(string Text, Action Click)> CardMenu(IShell shell, CardItem item)
    {
        if (item.TitleId <= 0 || item.Tag is not null) return Array.Empty<(string, Action)>();

        var menu = new List<(string, Action)>
        {
            ("Poster wählen …", async () => await ReplaceArtworkAsync(shell, item.TitleId, ArtworkKind.Poster)),
            ("Poster auf IMDb suchen", () => SearchPosterOnline(shell, item.TitleId)),
        };
        if (item.Kind == TitleKind.Movie) menu.Add(("Bearbeiten …", () => EditMovie(shell, item.TitleId)));
        return menu;
    }

    private static async Task AddAsync(IShell shell, string busyText, Func<Task<(long SourceId, ScanResult Scan)>> add)
    {
        ScanResult? scan = null;
        await shell.RunBusyAsync(busyText, async () => scan = (await add()).Scan);
        if (scan is null) return; // Fehler wurde bereits angezeigt

        shell.ReloadView();
        Report(shell, new[] { scan });
        await shell.RefreshArtworkAsync();
    }

    private static void Report(IShell shell, IReadOnlyList<ScanResult> results)
    {
        var added = results.Sum(r => r.Added);
        var updated = results.Sum(r => r.Updated);
        var missing = results.Sum(r => r.MarkedMissing);
        var offline = results.Count(r => r.Offline);
        var parts = new List<string> { $"{added} neu", $"{updated} aktualisiert" };
        if (missing > 0) parts.Add($"{missing} fehlen");
        if (offline > 0) parts.Add($"{offline} Quelle(n) offline");
        shell.ShowMessage("Scan fertig: " + string.Join(", ", parts) + ".");

        var failures = results.SelectMany(r => r.Failures).ToList();
        if (failures.Count > 0)
        {
            shell.ShowMessage(
                $"{failures.Count} Datei(en) konnten nicht gelesen werden: " +
                string.Join("; ", failures.Take(3).Select(f => $"{System.IO.Path.GetFileName(f.Path)} ({f.Error})")),
                isError: true);
        }
    }
}
