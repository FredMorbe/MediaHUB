using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App.Pages;

/// <summary>
/// „Filme umbenennen“: sucht für die Filme der Bibliothek die richtige Schreibweise bei OMDb und benennt die Dateien (samt Untertiteln und
/// anderen Begleitdateien) nach dem Muster „Titel (Jahr) [Auflösung]“ um. Nichts geschieht ohne Vorschau und Bestätigung; die letzte
/// Umbenennung lässt sich zurücknehmen. Die Bibliothek behält dabei jeden Film (Fortschritt, Untertitel- und Farbeinstellungen bleiben).
/// </summary>
public sealed class RenamePage : UserControl
{
    private static string JournalPath => Path.Combine(AppSettings.DataDirectory, "rename-journal.json");

    private readonly IShell _shell;
    private readonly List<(RenameProposal Proposal, CheckBox Box)> _ready = new();
    private List<RenameProposal> _others = new();
    private readonly StackPanel _list = new();
    private readonly StackPanel _details = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _summary = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1, Height = 6, Margin = new Thickness(0, 12, 0, 0), BorderThickness = new Thickness(0), Visibility = Visibility.Collapsed };
    private readonly Button _search;
    private readonly Button _cancel;
    private readonly Button _apply;
    private readonly Button _all;
    private readonly Button _none;
    private readonly Button _undo;
    private readonly Button _toggleDetails;
    private CancellationTokenSource? _cts;
    private bool _running;

    public RenamePage(IShell shell)
    {
        _shell = shell;

        var back = new Button { Content = "", HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Zurück (Esc)", Margin = new Thickness(0, 0, 0, 14) };
        back.SetResourceReference(StyleProperty, "IconButton");
        AutomationProperties.SetName(back, "Zurück");
        back.Click += (_, _) =>
        {
            if (_running)
            {
                _shell.ShowMessage("Es läuft gerade eine Arbeit. Mit „Abbrechen“ lässt sie sich beenden.", isError: true);
                return;
            }
            _shell.Back();
        };

        var kicker = new TextBlock { Text = "DATEIEN UMBENENNEN", Margin = new Thickness(0, 0, 0, 6) };
        kicker.SetResourceReference(StyleProperty, "KickerText");
        var title = PageParts.Heading("Filme umbenennen", 52);
        var hint = PageParts.Muted(
            "Aus „the.matrix.reloaded.2003.1080p.h264.mkv“ wird „The Matrix Reloaded (2003) [1080p].mkv“. Die richtige Schreibweise liefert OMDb, " +
            "die Auflösung kommt aus der Datei. Untertitel und andere Dateien mit dem alten Namen werden mitumbenannt, der Ordner bleibt. " +
            "Es werden nur Filme mit sicherem Treffer vorgeschlagen (Titel und Jahr müssen passen); deutsche Titel findet OMDb oft nicht. " +
            "Nichts geschieht ohne deine Bestätigung, und die letzte Umbenennung lässt sich zurücknehmen. " +
            "Fortschritt, Untertitel- und Farbeinstellungen bleiben erhalten.", 13);
        hint.Margin = new Thickness(0, 10, 0, 6);

        _search = PageParts.Button("Vorschläge suchen", "PrimaryButton", async (_, _) => await SearchAsync(), new Thickness(0, 0, 10, 0));
        _cancel = PageParts.Button("Abbrechen", "GhostButton", (_, _) => _cts?.Cancel(), new Thickness(0, 0, 10, 0));
        _cancel.Visibility = Visibility.Collapsed;
        _apply = PageParts.Button("Umbenennen", "PrimaryButton", async (_, _) => await ApplyAsync(), new Thickness(0, 0, 10, 0));
        _all = PageParts.Button("Alle auswählen", "GhostButton", (_, _) => SetAll(true), new Thickness(0, 0, 10, 0));
        _none = PageParts.Button("Keine auswählen", "GhostButton", (_, _) => SetAll(false), new Thickness(0, 0, 10, 0));
        _undo = PageParts.Button("Letzte Umbenennung zurücknehmen", "GhostButton", async (_, _) => await UndoAsync(), new Thickness(0, 0, 10, 0));
        _toggleDetails = PageParts.Button("Nicht umbenannte anzeigen", "GhostButton", (_, _) => ToggleDetails(), new Thickness(0, 10, 0, 0));
        AutomationProperties.SetName(_search, "Vorschläge suchen");
        AutomationProperties.SetName(_cancel, "Suche abbrechen");
        AutomationProperties.SetName(_apply, "Umbenennen");
        AutomationProperties.SetName(_all, "Alle auswählen");
        AutomationProperties.SetName(_none, "Keine auswählen");
        AutomationProperties.SetName(_undo, "Letzte Umbenennung zurücknehmen");
        AutomationProperties.SetName(_toggleDetails, "Nicht umbenannte anzeigen");
        foreach (var button in new[] { _search, _cancel, _apply, _all, _none, _undo }) button.Padding = new Thickness(18, 8, 18, 8);
        _toggleDetails.Padding = new Thickness(14, 6, 14, 6);
        _apply.IsEnabled = _all.IsEnabled = _none.IsEnabled = false;
        _toggleDetails.Visibility = Visibility.Collapsed;

        _summary.SetResourceReference(StyleProperty, "MutedText");
        _status.SetResourceReference(StyleProperty, "MutedText");
        _bar.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
        _bar.SetResourceReference(Control.BackgroundProperty, "TrackBrush");

        var top = new WrapPanel { Margin = new Thickness(0, 12, 0, 0), Children = { _search, _cancel, _undo } };
        var selection = new WrapPanel { Margin = new Thickness(0, 16, 0, 0), Children = { _apply, _all, _none } };

        var stack = new StackPanel();
        stack.Children.Add(back);
        stack.Children.Add(kicker);
        stack.Children.Add(title);
        stack.Children.Add(hint);
        if (_shell.Services.Omdb is null)
        {
            var missing = PageParts.Muted($"Dafür braucht es einen OMDb-Schlüssel in der Umgebungsvariable {AppServices.OmdbKeyVariable} (kostenlos auf omdbapi.com/apikey.aspx). Danach die App neu starten.", 13);
            missing.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(missing);
            _search.IsEnabled = false;
        }
        stack.Children.Add(top);
        stack.Children.Add(_bar);
        stack.Children.Add(_summary);
        stack.Children.Add(selection);
        stack.Children.Add(_list);
        stack.Children.Add(_toggleDetails);
        stack.Children.Add(_details);
        stack.Children.Add(_status);
        Content = PageParts.Scrolling(stack);

        RefreshUndo();
    }

    // --- Suchen ---------------------------------------------------------------------------------------------

    private async Task SearchAsync()
    {
        if (_running || _shell.Services.Omdb is not { } omdb) return;

        var videos = _shell.Services.Db.ListVideos();
        var status = _shell.Services.Model.Library.RefreshVolumes();
        using var cts = new CancellationTokenSource();
        _cts = cts;
        SetRunning(true);
        _status.Text = "";
        _summary.Text = "Suche läuft …";
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            _bar.Value = p.Total == 0 ? 0 : (double)p.Done / p.Total;
            _summary.Text = $"Suche läuft … {p.Done} von {p.Total} Filmen";
        });

        IReadOnlyList<RenameProposal>? proposals = null;
        try
        {
            proposals = await Task.Run(() => new MovieRenamePlanner(omdb).PlanAsync(videos, status, progress, cts.Token));
        }
        catch (OperationCanceledException)
        {
            _summary.Text = "Suche abgebrochen.";
        }
        catch (Exception ex) when (ex is PosterSourceUnavailableException or HttpRequestException or TimeoutException or IOException)
        {
            _summary.Text = "";
            _status.Text = $"Die Suche ist fehlgeschlagen: {ex.Message}";
            _shell.ShowMessage($"Die Suche ist fehlgeschlagen: {ex.Message}", isError: true);
        }
        finally
        {
            _cts = null;
            SetRunning(false);
        }
        if (proposals is not null) Show(proposals);
    }

    private void Show(IReadOnlyList<RenameProposal> proposals)
    {
        _list.Children.Clear();
        _details.Children.Clear();
        _ready.Clear();

        foreach (var proposal in proposals.Where(p => p.Status == RenameStatus.Ready))
        {
            var oldName = Path.GetFileName(proposal.Video.RelPath);
            var newName = Path.GetFileName(proposal.NewRelPath!);
            var companions = proposal.SidecarMoves.Count;

            var oldText = PageParts.Muted(oldName, 12);
            var newText = PageParts.Semi("→ " + newName, 15);
            newText.TextWrapping = TextWrapping.Wrap;
            var lines = new StackPanel { Children = { oldText, newText } };
            if (companions > 0) lines.Children.Add(PageParts.Muted($"+ {companions} Begleitdatei{(companions == 1 ? "" : "en")}: {string.Join(", ", proposal.SidecarMoves.Select(s => Path.GetFileName(s.OldRel)))}", 12));

            var box = new CheckBox { IsChecked = true, Content = lines, Margin = new Thickness(0, 0, 0, 10) };
            AutomationProperties.SetName(box, $"Umbenennen: {oldName}");
            box.Checked += (_, _) => UpdateApplyButton();
            box.Unchecked += (_, _) => UpdateApplyButton();
            _list.Children.Add(box);
            _ready.Add((proposal, box));
        }

        _others = proposals.Where(p => p.Status != RenameStatus.Ready).ToList();
        foreach (var other in _others.OrderBy(p => p.Status).ThenBy(p => p.Video.RelPath, StringComparer.OrdinalIgnoreCase))
        {
            var line = PageParts.Muted($"{Path.GetFileName(other.Video.RelPath)}  —  {Describe(other)}", 12);
            line.Margin = new Thickness(0, 0, 0, 4);
            _details.Children.Add(line);
        }
        _toggleDetails.Visibility = _others.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _details.Visibility = Visibility.Collapsed;
        _toggleDetails.Content = "Nicht umbenannte anzeigen";

        var counts = new List<string>();
        void Count(RenameStatus status, string text)
        {
            var n = _others.Count(p => p.Status == status);
            if (n > 0) counts.Add($"{n} {text}");
        }
        Count(RenameStatus.AlreadyCorrect, "mit schon richtigem Namen");
        Count(RenameStatus.NoMatch, "ohne sicheren Treffer");
        Count(RenameStatus.TargetExists, "mit schon vorhandenem Zielnamen");
        Count(RenameStatus.Unreadable, "mit unlesbarem neuem Namen");
        Count(RenameStatus.Offline, "nicht erreichbar");

        _summary.Text = _ready.Count == 0
            ? $"Nichts zum Umbenennen gefunden.{(counts.Count > 0 ? " Übrig: " + string.Join(", ", counts) + "." : "")}"
            : $"{_ready.Count} Film{(_ready.Count == 1 ? "" : "e")} können umbenannt werden.{(counts.Count > 0 ? " Außerdem: " + string.Join(", ", counts) + "." : "")}";
        UpdateApplyButton();
    }

    private static string Describe(RenameProposal p) => p.Status switch
    {
        RenameStatus.AlreadyCorrect => "Name stimmt schon",
        RenameStatus.NoMatch => p.Detail ?? "Kein sicherer Treffer",
        RenameStatus.TargetExists => p.Detail ?? "Zielname schon vergeben",
        RenameStatus.Unreadable => p.Detail ?? "Neuer Name würde nicht gelesen",
        RenameStatus.Offline => p.Detail ?? "Nicht erreichbar",
        _ => "",
    };

    private void ToggleDetails()
    {
        var show = _details.Visibility != Visibility.Visible;
        _details.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _toggleDetails.Content = show ? "Nicht umbenannte ausblenden" : "Nicht umbenannte anzeigen";
    }

    private void SetAll(bool value)
    {
        foreach (var (_, box) in _ready) box.IsChecked = value;
    }

    private void UpdateApplyButton()
    {
        var selected = _ready.Count(r => r.Box.IsChecked == true);
        _apply.Content = selected == 0 ? "Umbenennen" : $"{selected} umbenennen";
        _apply.IsEnabled = selected > 0 && !_running;
        _all.IsEnabled = _none.IsEnabled = _ready.Count > 0 && !_running;
    }

    private void SetRunning(bool running)
    {
        _running = running;
        _search.IsEnabled = !running && _shell.Services.Omdb is not null;
        _cancel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        _bar.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        if (running) _bar.Value = 0;
        _undo.IsEnabled = !running && HasJournal();
        UpdateApplyButton();
    }

    // --- Umbenennen -----------------------------------------------------------------------------------------

    private async Task ApplyAsync()
    {
        if (_running) return;
        var selected = _ready.Where(r => r.Box.IsChecked == true).Select(r => r.Proposal).ToList();
        if (selected.Count == 0) return;

        var files = selected.Sum(p => 1 + p.SidecarMoves.Count);
        var question = $"{selected.Count} Film{(selected.Count == 1 ? "" : "e")} ({files} Dateien) auf den Laufwerken umbenennen?\n\n" +
                       "• Die Dateien werden wirklich umbenannt, nicht kopiert. Die Bibliothek führt jeden Film mit (Fortschritt und Einstellungen bleiben).\n" +
                       "• Ein Film, der gerade im Player läuft, lässt sich nicht umbenennen; dann bleibt er unverändert.\n" +
                       "• Mit „Letzte Umbenennung zurücknehmen“ geht alles wieder zurück.";
        if (MessageBox.Show(Window.GetWindow(this), question, "Filme umbenennen", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        var db = _shell.Services.Db;
        var status = _shell.Services.Model.Library.RefreshVolumes();
        using var cts = new CancellationTokenSource();
        _cts = cts;
        SetRunning(true);
        var progress = new Progress<int>(done =>
        {
            _bar.Value = (double)done / selected.Count;
            _summary.Text = $"Benenne um … {done} von {selected.Count}";
        });

        IReadOnlyList<RenameOutcome> outcomes = Array.Empty<RenameOutcome>();
        string? stopped = null;
        try
        {
            outcomes = await Task.Run(() => new MovieRenamer(db).Apply(selected, status, progress, cts.Token));
        }
        catch (OperationCanceledException)
        {
            stopped = "Abgebrochen; was bis dahin umbenannt war, bleibt umbenannt.";
        }
        finally
        {
            _cts = null;
        }

        // Auch bei einem Abbruch mitten drin: was schon umbenannt ist, steht im Protokoll und lässt sich zurücknehmen.
        var done = outcomes.Where(o => o.Success).Select(o => o.Record!).ToList();
        if (done.Count > 0) RenameJournal.Save(JournalPath, done);
        var failed = outcomes.Where(o => !o.Success).ToList();

        await FinishAsync(done.Count, failed.Select(f => f.Error!).ToList(), stopped);
        var renamed = done.Select(d => d.FileId).ToHashSet();
        Show(Remaining(renamed));
    }

    /// <summary>Die Vorschläge nach einer Umbenennung: bereits umbenannte fallen weg, der Rest bleibt.</summary>
    private IReadOnlyList<RenameProposal> Remaining(HashSet<long> renamed) =>
        _ready.Select(r => r.Proposal).Where(p => !renamed.Contains(p.Video.FileId)).Concat(_others).ToList();

    private async Task FinishAsync(int renamed, IReadOnlyList<string> errors, string? stopped)
    {
        var model = _shell.Services.Model;
        var artworkFailures = 0;
        if (renamed > 0)
        {
            // Umbenannte Filme können unter einem neuen Titel stehen: Bibliothek neu lesen und fehlende Cover (auch aus dem Netz) holen.
            await _shell.RunBusyAsync("Bibliothek wird aktualisiert …", async () =>
            {
                model.Reload();
                artworkFailures = (await model.Artwork.RefreshAsync(model.Volumes)).Failures.Count;
                model.Reload();
            });
        }
        SetRunning(false);

        var text = new List<string>();
        if (renamed > 0) text.Add($"{renamed} Film{(renamed == 1 ? "" : "e")} umbenannt.");
        if (errors.Count > 0) text.Add($"{errors.Count} nicht umbenannt: {string.Join(" | ", errors.Take(5))}{(errors.Count > 5 ? " …" : "")}");
        if (stopped is not null) text.Add(stopped);
        if (artworkFailures > 0) text.Add($"Für {artworkFailures} Einträge konnte kein Cover geholt werden.");
        _status.Text = string.Join(" ", text);
        _shell.ShowMessage(string.Join(" ", text), isError: errors.Count > 0);
        _summary.Text = "";
        RefreshUndo();
    }

    // --- Zurücknehmen ---------------------------------------------------------------------------------------

    private static bool HasJournal()
    {
        try
        {
            return RenameJournal.Load(JournalPath).Count > 0;
        }
        catch (InvalidDataException)
        {
            return true; // ein kaputtes Protokoll zeigt der Klick auf den Knopf als Fehler
        }
    }

    private void RefreshUndo() => _undo.IsEnabled = !_running && HasJournal();

    private async Task UndoAsync()
    {
        if (_running) return;

        IReadOnlyList<RenameRecord> records;
        try
        {
            records = RenameJournal.Load(JournalPath);
        }
        catch (InvalidDataException ex)
        {
            _status.Text = ex.Message;
            _shell.ShowMessage(ex.Message, isError: true);
            return;
        }
        if (records.Count == 0) return;

        var question = $"Die letzte Umbenennung ({records.Count} Film{(records.Count == 1 ? "" : "e")}) zurücknehmen? Die Dateien bekommen ihre alten Namen wieder.";
        if (MessageBox.Show(Window.GetWindow(this), question, "Umbenennung zurücknehmen", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        var db = _shell.Services.Db;
        var status = _shell.Services.Model.Library.RefreshVolumes();
        SetRunning(true);
        IReadOnlyList<UndoOutcome> outcomes;
        try
        {
            outcomes = await Task.Run(() => new MovieRenamer(db).Undo(records, status));
        }
        finally
        {
            SetRunning(false);
        }

        var failed = outcomes.Where(o => !o.Success).ToList();
        if (failed.Count == 0) RenameJournal.Clear(JournalPath);
        else RenameJournal.Save(JournalPath, failed.Select(f => f.Record).ToList()); // was sich nicht zurücknehmen ließ, bleibt im Protokoll

        _shell.Services.Model.Reload();
        var text = $"{outcomes.Count - failed.Count} von {outcomes.Count} Umbenennungen zurückgenommen." +
                   (failed.Count > 0 ? $" Nicht möglich: {string.Join(" | ", failed.Take(5).Select(f => f.Error))}" : "");
        _status.Text = text;
        _shell.ShowMessage(text, isError: failed.Count > 0);
        _list.Children.Clear();
        _ready.Clear();
        _summary.Text = "Die Liste ist nicht mehr aktuell: bitte erneut nach Vorschlägen suchen.";
        UpdateApplyButton();
        RefreshUndo();
    }
}
