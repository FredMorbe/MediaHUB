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
