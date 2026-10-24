using System.IO;
using System.IO.Compression;
using System.Text;
using Microsoft.Win32;
using MediaCenter.Core;

namespace MediaCenter.App.Services;

/// <summary>Sichern und Wiederherstellen mit Rückfragen und Meldungen (Einstellungen → Bibliothek → Sicherung).</summary>
public static class BackupActions
{
    public static async Task CreateAsync(IShell shell)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Sicherung speichern",
            Filter = "Mediacenter-Sicherung (*.zip)|*.zip",
            FileName = $"mediacenter-sicherung-{DateTime.Now:yyyy-MM-dd}.zip",
            DefaultExt = ".zip",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != true) return;

        BackupManifest? manifest = null;
        await shell.RunBusyAsync("Sicherung wird geschrieben …", () =>
        {
            // Die Datenbank-Verbindung gehört dem Oberflächen-Thread; die Sicherung ist wenige Sekunden schnell.
            manifest = BackupService.Create(shell.Services.Db, AppSettings.DataDirectory, dialog.FileName, AppInfo.Version);
            return Task.CompletedTask;
        });
        if (manifest is null) return;

        shell.Services.Settings.LastBackupUtc = manifest.CreatedUtc;
        shell.Services.Settings.Save();
        var size = new FileInfo(dialog.FileName).Length;
        shell.ShowMessage($"Sicherung gespeichert: {dialog.FileName} ({size / 1024.0 / 1024.0:0.0} MB).");
    }

    /// <summary>Wählt eine Sicherung, bereitet sie vor und startet die App nach Rückfrage neu; eingespielt wird beim Start.</summary>
    public static void Restore(IShell shell)
    {
        var dialog = new OpenFileDialog { Title = "Sicherung wählen", Filter = "Mediacenter-Sicherung (*.zip)|*.zip", CheckFileExists = true };
        if (dialog.ShowDialog() != true) return;

        BackupManifest manifest;
        try
        {
            manifest = BackupService.Inspect(dialog.FileName);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            shell.ShowMessage($"Das ist keine brauchbare Sicherung: {ex.Message}", isError: true);
            return;
        }

        var question = new StringBuilder()
            .AppendLine($"Sicherung vom {manifest.CreatedUtc.ToLocalTime():dd.MM.yyyy HH:mm} (Mediacenter {manifest.AppVersion}) wiederherstellen?")
            .AppendLine()
            .AppendLine("• Fortschritt, Playlists, „Meine Liste“, Sammlungen, eigene Namen, Infotexte, Einstellungen, selbst gewählte Poster und gespeicherte Untertitel werden durch den Stand der Sicherung ersetzt.")
            .AppendLine("• Der jetzige Stand wird nicht gelöscht, sondern in einen Ordner „vor-wiederherstellung-…“ im Datenordner verschoben.")
            .AppendLine("• Die App startet dafür neu. Laufwerke und Dateien bleiben unberührt, ein Scan danach gleicht die Bibliothek ab.");
        if (!shell.Confirm("Sicherung wiederherstellen?", question.ToString(), "Wiederherstellen", danger: true)) return;

        try
        {
            BackupService.StageRestore(dialog.FileName, AppSettings.DataDirectory);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            shell.ShowMessage($"Die Wiederherstellung ließ sich nicht vorbereiten, die Daten sind unverändert: {ex.Message}", isError: true);
            return;
        }
        App.Restart();
    }
}
