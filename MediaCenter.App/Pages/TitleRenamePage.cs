using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using MediaCenter.App.Controls;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App.Pages;

/// <summary>
/// „Umbenennen“ für einen Film oder eine Serie von Hand. Der Anzeigename ändert nur den Namen in der Bibliothek (die Dateien bleiben);
/// er ist auch der Name, nach dem die Online-Suche Poster sucht. Der Dateiname eines Films benennt die Datei samt Begleitdateien wirklich
/// um, für Filme, bei denen die automatische Umbenennung (<see cref="RenamePage"/>) keinen sicheren Treffer hatte.
/// Erscheint als Fenster über der Seite.
/// </summary>
public sealed class TitleRenamePage : UserControl, IModalContent
{
    private readonly IShell _shell;
    private readonly TitleRow _title;
    private bool _busy;

    public string? BlockClose => _busy ? "Es wird gerade umbenannt. Bitte einen Moment warten." : null;

    /// <summary>Öffnet das Fenster zum Umbenennen von Hand.</summary>
    public static void Show(IShell shell, long titleId)
    {
        var title = shell.Services.Model.FindTitle(titleId) ?? throw new InvalidOperationException($"Titel {titleId} existiert nicht (mehr).");
        shell.OpenModal(title.Kind == TitleKind.Movie ? "Film umbenennen" : "Serie umbenennen", title.Name, new TitleRenamePage(shell, titleId), ModalSize.Medium);
    }

    public TitleRenamePage(IShell shell, long titleId)
    {
        _shell = shell;
        _title = shell.Services.Model.FindTitle(titleId) ?? throw new InvalidOperationException($"Titel {titleId} existiert nicht (mehr).");

        var sections = new List<UIElement> { NameSection() };
        if (_title.Kind == TitleKind.Movie)
        {
            foreach (var video in shell.Services.Model.VideosOf(_title.Id)) sections.Add(FileSection(video));
        }
        if (UndoSection() is { } undo) sections.Add(undo);

        var stack = new StackPanel();
        for (var i = 0; i < sections.Count; i++)
        {
            if (i > 0) stack.Children.Add(PageParts.Divider(new Thickness(0, 24, 0, 24)));
            stack.Children.Add(sections[i]);
        }
        Content = stack;
    }

    private static UIElement Block(string title, params UIElement[] content)
    {
        var header = new TextBlock { Text = title, FontSize = 26, Margin = new Thickness(0, 0, 0, 8) };
        header.SetResourceReference(StyleProperty, "DisplayText");
        var panel = new StackPanel { Children = { header } };
        foreach (var element in content) panel.Children.Add(element);
        return panel;
    }

    /// <summary>Eingabefeld, das die Zeile füllt, mit angehängten Elementen (Endung, Knopf) rechts.</summary>
    private static Grid FieldRow(UIElement field, params UIElement[] trailing)
    {
        var row = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(field);
        for (var i = 0; i < trailing.Length; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(trailing[i], i + 1);
            row.Children.Add(trailing[i]);
        }
        return row;
    }

    // --- Anzeigename -----------------------------------------------------------------------------------------

    private UIElement NameSection()
    {
        var hint = PageParts.Muted(
            "Ändert nur den Namen in der Bibliothek, die Dateien auf dem Laufwerk bleiben. Nach diesem Namen suchen auch die Online-Suche nach Postern " +
            "und die Vorschläge zum Umbenennen: bei einem deutschen Titel, den OMDb nicht kennt, hilft der englische Originaltitel.");

        var (field, box) = PageParts.HintBox("Anzeigename", double.NaN);
        box.Text = _title.Name;
        AutomationProperties.SetName(box, "Anzeigename");
        var save = PageParts.Button("Anzeigenamen speichern", "PrimaryButton", async (_, _) => await SaveNameAsync(box.Text), new Thickness(10, 0, 0, 0));
        AutomationProperties.SetName(save, "Anzeigenamen speichern");
        box.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await SaveNameAsync(box.Text);
        };

        save.VerticalAlignment = VerticalAlignment.Center;
        return Block("Anzeigename", hint, FieldRow(field, save));
    }

    private async Task SaveNameAsync(string text)
    {
        if (_busy) return;
        var name = text.Trim();
        if (name.Length == 0)
        {
            _shell.ShowMessage("Der Anzeigename ist leer.", isError: true);
            return;
        }
        if (name == _title.Name)
        {
            _shell.ShowMessage("Der Anzeigename ist schon so.");
            return;
        }

        _busy = true;
        try
        {
            _shell.Services.Db.RenameTitle(_title.Id, name);
            _shell.ShowMessage($"Anzeigename geändert: {name}.");
            // Die Online-Suche merkt sich einen Titelwechsel: mit dem neuen Namen wird nach dem fehlenden Poster neu gesucht. Lädt die Seite dahinter neu.
            await _shell.RefreshArtworkAsync();
            Show(_shell, _title.Id); // das Fenster zeigt sonst weiter den alten Namen
        }
        finally
        {
            _busy = false;
        }
    }

    // --- Dateiname -------------------------------------------------------------------------------------------

    private UIElement FileSection(VideoRow video)
    {
        var extension = Path.GetExtension(video.RelPath);
        var online = _shell.Services.Model.PathOf(video) is not null;

        var hint = PageParts.Muted(
            "Benennt die Datei auf dem Laufwerk wirklich um, samt Untertiteln und anderen Dateien mit dem alten Namen. Der Name braucht ein Jahr, " +
            "z. B. „Mayday_(2026)“, sonst erkennt die Bibliothek den Film nicht sicher und er fiele in die Inbox. Fortschritt und Einstellungen bleiben erhalten. " +
            "Ist der neue Name noch unbekannt und steht der Film allein unter seinem Titel, wird auch der Anzeigename angepasst; " +
            "gehört der Name schon einem anderen Titel, wird der Film diesem zugeordnet.");
        var current = PageParts.Muted($"Jetzt: {Path.GetFileName(video.RelPath)}", 13);
        current.Margin = new Thickness(0, 10, 0, 0);

        var (field, box) = PageParts.HintBox("Dateiname", double.NaN);
        box.Text = Path.GetFileNameWithoutExtension(video.RelPath);
        AutomationProperties.SetName(box, "Dateiname");
        var ending = PageParts.Muted(extension, 14);
        ending.Margin = new Thickness(10, 0, 0, 0);
        ending.VerticalAlignment = VerticalAlignment.Center;
        var rename = PageParts.Button("Datei umbenennen", "PrimaryButton", async (_, _) => await RenameFileAsync(video, box.Text), new Thickness(10, 0, 0, 0));
        AutomationProperties.SetName(rename, "Datei umbenennen");
        box.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await RenameFileAsync(video, box.Text);
        };

        rename.VerticalAlignment = VerticalAlignment.Center;
        var parts = new List<UIElement> { hint, current, FieldRow(field, ending, rename) };
        if (!online)
        {
            box.IsEnabled = rename.IsEnabled = false;
            parts.Add(PageParts.Muted("Die Datei ist gerade nicht erreichbar (Laufwerk abgesteckt oder Datei fehlt).", 13));
        }
        return Block("Dateiname", parts.ToArray());
    }

    // --- Zurücknehmen ----------------------------------------------------------------------------------------

    /// <summary>Die letzte Umbenennung aus dem gemeinsamen Protokoll (auch die von „Filme umbenennen“); null, wenn es keine gibt.</summary>
    private UIElement? UndoSection()
    {
        IReadOnlyList<RenameRecord> records;
        try
        {
            records = RenameJournal.Load(RenamePage.JournalPath);
        }
        catch (InvalidDataException ex)
        {
            return Block("Zurücknehmen", PageParts.Muted(ex.Message));
        }
        if (records.Count == 0) return null;

        var last = records.Count == 1 ? $"{Path.GetFileName(records[0].OldRel)} → {Path.GetFileName(records[0].NewRel)}" : $"{records.Count} Filme";
        var hint = PageParts.Muted($"Zuletzt umbenannt: {last}. Die Dateien bekommen ihre alten Namen wieder; war der Titel mitumbenannt, bekommt er seinen alten Namen zurück.");
        var undo = PageParts.Button("Letzte Umbenennung zurücknehmen", "GhostButton", async (_, _) => await UndoAsync(records), new Thickness(0, 12, 0, 0));
        AutomationProperties.SetName(undo, "Letzte Umbenennung zurücknehmen");
        return Block("Zurücknehmen", hint, undo);
    }

    private async Task UndoAsync(IReadOnlyList<RenameRecord> records)
    {
        if (_busy) return;
        var question = $"Die letzte Umbenennung ({records.Count} Film{(records.Count == 1 ? "" : "e")}) zurücknehmen? Die Dateien bekommen ihre alten Namen wieder.";
        if (!_shell.Confirm("Umbenennung zurücknehmen?", question, "Zurücknehmen")) return;

        _busy = true;
        try
        {
            var db = _shell.Services.Db;
            var model = _shell.Services.Model;
            var status = model.Library.RefreshVolumes();
            var outcomes = await Task.Run(() => new MovieRenamer(db).Undo(records, status));

            var failed = outcomes.Where(o => !o.Success).ToList();
            if (failed.Count == 0) RenameJournal.Clear(RenamePage.JournalPath);
            else RenameJournal.Save(RenamePage.JournalPath, failed.Select(f => f.Record).ToList()); // was sich nicht zurücknehmen ließ, bleibt im Protokoll

            // Der Film kann wieder unter einem anderen Titel stehen: zur Filmliste, wie nach dem Umbenennen.
            model.Reload();
            _shell.Navigate(Section.Movies);
            _shell.ShowMessage($"{outcomes.Count - failed.Count} von {outcomes.Count} Umbenennungen zurückgenommen." +
                               (failed.Count > 0 ? $" Nicht möglich: {string.Join(" | ", failed.Take(5).Select(f => f.Error))}" : ""), isError: failed.Count > 0);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RenameFileAsync(VideoRow video, string text)
    {
        if (_busy) return;
        var model = _shell.Services.Model;
        var status = model.Library.RefreshVolumes();

        RenameProposal proposal;
        try
        {
            proposal = MovieRenamePlanner.PlanManual(video, status, text);
        }
        catch (ArgumentException ex)
        {
            _shell.ShowMessage(ex.Message, isError: true);
            return;
        }
        if (proposal.Status == RenameStatus.AlreadyCorrect)
        {
            _shell.ShowMessage("Die Datei heißt schon so.");
            return;
        }
        if (proposal.Status != RenameStatus.Ready)
        {
            _shell.ShowMessage(proposal.Detail ?? "Die Datei lässt sich so nicht umbenennen.", isError: true);
            return;
        }

        var companions = proposal.SidecarMoves.Count;
        var question = $"Datei auf dem Laufwerk umbenennen?\n\n{Path.GetFileName(video.RelPath)}\n→ {Path.GetFileName(proposal.NewRelPath!)}" +
                       (companions > 0 ? $"\n\nMitumbenannt werden {companions} Begleitdatei{(companions == 1 ? "" : "en")}: {string.Join(", ", proposal.SidecarMoves.Select(s => Path.GetFileName(s.OldRel)))}" : "") +
                       "\n\nEin Film, der gerade im Player läuft, lässt sich nicht umbenennen.";
        if (!_shell.Confirm("Datei umbenennen?", question, "Umbenennen")) return;

        _busy = true;
        try
        {
            var db = _shell.Services.Db;
            var outcome = await Task.Run(() => new MovieRenamer(db).Apply(new[] { proposal }, status).Single());
            if (!outcome.Success)
            {
                _shell.ShowMessage(outcome.Error!, isError: true);
                return;
            }

            // Dasselbe Protokoll wie „Filme umbenennen“: es hält die letzte Umbenennung, die sich zurücknehmen lässt.
            var message = $"Umbenannt: {Path.GetFileName(proposal.NewRelPath!)}";
            try
            {
                RenameJournal.Save(RenamePage.JournalPath, new[] { outcome.Record! });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                message += $" Das Protokoll ließ sich nicht speichern, die Umbenennung lässt sich nicht zurücknehmen: {ex.Message}";
            }

            // Der Film kann unter einem anderen Titel stehen (oder dieser Titel wurde zusammengeführt): diese Seite gibt es dann nicht mehr.
            // Darum zurück zur Filmliste und von dort auf die Seite des Titels, unter dem der Film jetzt steht.
            model.Reload();
            _shell.Navigate(Section.Movies);
            if (_shell.Services.Db.ListVideos().FirstOrDefault(v => v.VideoId == video.VideoId)?.TitleId is { } titleId)
            {
                Show(_shell, titleId);
            }
            _shell.ShowMessage(message);
            await _shell.RefreshArtworkAsync();
        }
        finally
        {
            _busy = false;
        }
    }
}
