using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using MediaCenter.App.Controls;
using MediaCenter.App.Services;
using MediaCenter.Core;
using Microsoft.Win32;

namespace MediaCenter.App.Pages;

/// <summary>
/// MKV-Editor: Spuren entfernen, hinzufügen, umbenennen, Sprache und Markierungen ändern, Ton und Untertitel dauerhaft verschieben.
/// Gespeichert wird als Kopie oder in die Originaldatei; die Arbeit macht <see cref="MkvEditor"/> ohne Neukodierung.
/// Erscheint als Fenster über der Seite.
/// </summary>
public sealed class MkvEditorPage : UserControl, IModalContent
{
    private const string UnknownLanguage = "und";

    /// <summary>Eine Zeile der Spurliste: vorhandene Spur (<see cref="Original"/>) oder neu hinzugefügte (<see cref="File"/>).</summary>
    private sealed class Row
    {
        public required string Type { get; init; }            // video, audio, subtitle
        public StreamInfo? Original { get; init; }
        public string? File { get; init; }
        public int? SourceStreamIndex { get; init; }
        public string? StoredPath { get; init; }               // gespeicherter Untertitel der App: nach erfolgreichem Ersetzen überflüssig
        public required string Codec { get; init; }
        public required string Details { get; init; }
        public string? Language { get; set; }
        public string Title { get; set; } = "";
        public bool IsDefault { get; set; }
        public bool IsForced { get; set; }
        public string DelayText { get; set; } = "";
        public bool Remove { get; set; }

        public bool IsNew => Original is null;
    }

    private readonly IShell _shell;
    private readonly VideoRow _video;
    private readonly string _path;
    private readonly List<Row> _rows = new();
    private readonly StackPanel _rowsPanel = new();
    private readonly WrapPanel _storedPanel = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _summary = new() { FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
    private readonly TextBlock _status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1, Height = 6, Margin = new Thickness(0, 12, 0, 0), BorderThickness = new Thickness(0), Visibility = Visibility.Collapsed };
    private readonly Button _save;
    private readonly Button _cancel;
    private readonly RadioButton _copyChoice = new() { Content = "Als Kopie speichern", GroupName = "mkv-target", IsChecked = true, Margin = new Thickness(0, 0, 8, 0) };
    private readonly RadioButton _replaceChoice = new() { Content = "Original ersetzen", GroupName = "mkv-target" };
    private readonly TextBox _copyName = new();
    private readonly TextBlock _targetNote = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _chooseCopy;
    private readonly Button _addSubtitle;
    private readonly Button _addAudio;
    private ProbeResult? _probe;
    private CancellationTokenSource? _cts;
    private bool _running;

    /// <summary>Die Spaltenbreiten der Spurliste brauchen Platz; in schmaleren Fenstern scrollt das Fenster waagerecht.</summary>
    public double MinContentWidth => 980;

    public string? BlockClose => _running ? "Die Datei wird gerade bearbeitet. Mit „Abbrechen“ lässt sich das beenden." : null;

    public static void Show(IShell shell, VideoRow video, string path) =>
        shell.OpenModal("MKV bearbeiten", Path.GetFileNameWithoutExtension(path), new MkvEditorPage(shell, video, path), ModalSize.Large);

    public MkvEditorPage(IShell shell, VideoRow video, string path)
    {
        _shell = shell;
        _video = video;
        _path = path;

        var info = PageParts.Muted($"{path}  ·  {FormatSize(new FileInfo(path).Length)}", 13);
        info.Margin = new Thickness(0, 0, 0, 0);
        var hint = PageParts.Muted("Änderungen werden ohne Neukodierung in die Datei geschrieben (Bild und Ton bleiben unverändert). Anhänge wie Schriften, Cover und Kapitel bleiben erhalten. Ein im Player eingestellter Versatz bleibt davon unberührt.", 13);
        hint.Margin = new Thickness(0, 8, 0, 20);

        _addSubtitle = PageParts.Button("Untertitel hinzufügen …", "GhostButton", async (_, _) => await AddSubtitleFileAsync(), new Thickness(0, 0, 10, 0));
        _addAudio = PageParts.Button("Ton hinzufügen …", "GhostButton", async (_, _) => await AddAudioFileAsync(), new Thickness(0, 0, 10, 0));
        AutomationProperties.SetName(_addSubtitle, "Untertitel hinzufügen");
        AutomationProperties.SetName(_addAudio, "Ton hinzufügen");
        foreach (var button in new[] { _addSubtitle, _addAudio }) PageParts.Compact(button);
        var addButtons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0), Children = { _addSubtitle, _addAudio } };

        _chooseCopy = PageParts.Button("Wählen …", "GhostButton", (_, _) => ChooseCopyPath(), new Thickness(10, 0, 0, 0));
        PageParts.Compact(_chooseCopy);
        AutomationProperties.SetName(_chooseCopy, "Ziel der Kopie wählen");
        foreach (var choice in new[] { _copyChoice, _replaceChoice })
        {
            choice.SetResourceReference(StyleProperty, "Chip");
            choice.Checked += (_, _) => UpdateTargetNote();
        }
        AutomationProperties.SetName(_copyChoice, "Als Kopie speichern");
        AutomationProperties.SetName(_replaceChoice, "Original ersetzen");
        AutomationProperties.SetName(_copyName, "Name der Kopie");
        _copyName.Text = SuggestCopyPath(path);
        _targetNote.SetResourceReference(StyleProperty, "MutedText");

        _save = PageParts.Button("Speichern", "PrimaryButton", async (_, _) => await SaveAsync(), new Thickness(0, 0, 10, 0));
        _cancel = PageParts.Button("Abbrechen", "GhostButton", (_, _) => _cts?.Cancel(), new Thickness(0));
        _cancel.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(_save, "Speichern");
        AutomationProperties.SetName(_cancel, "Bearbeitung abbrechen");
        _save.IsEnabled = false;
        _summary.SetResourceReference(StyleProperty, "MutedText");
        _status.SetResourceReference(StyleProperty, "MutedText");
        _bar.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
        _bar.SetResourceReference(Control.BackgroundProperty, "TrackBrush");

        var targetRow = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        targetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        targetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_chooseCopy, 1);
        targetRow.Children.Add(_copyName);
        targetRow.Children.Add(_chooseCopy);
        var target = new StackPanel
        {
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Children = { _copyChoice, _replaceChoice } },
                targetRow,
                _targetNote,
            },
        };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0), Children = { _save, _cancel, _summary } };

        var stack = new StackPanel();
        stack.Children.Add(info);
        stack.Children.Add(hint);
        stack.Children.Add(Section("Spuren"));
        stack.Children.Add(_rowsPanel);
        stack.Children.Add(addButtons);
        stack.Children.Add(_storedPanel);
        stack.Children.Add(Section("Speichern"));
        stack.Children.Add(PageParts.Card(target, new Thickness(0, 8, 0, 0)));
        stack.Children.Add(actions);
        stack.Children.Add(_bar);
        stack.Children.Add(_status);
        Content = stack;

        UpdateTargetNote();
        Loaded += async (_, _) =>
        {
            if (_probe is null) await LoadAsync();
        };
    }

    private static TextBlock Section(string text)
    {
        var heading = PageParts.Heading(text, 30);
        heading.Margin = new Thickness(0, 6, 0, 4);
        return heading;
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0} MB" : $"{bytes / 1024} KB";

    // --- Laden ---------------------------------------------------------------------------------------------

    private async Task LoadAsync()
    {
        _status.Text = "Datei wird gelesen …";
        try
        {
            _probe = await _shell.Services.Probe.ProbeAsync(_path);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or TimeoutException)
        {
            SetStatus($"Die Datei lässt sich nicht lesen: {ex.Message}", isError: true);
            return;
        }

        _rows.Clear();
        foreach (var s in _probe.Streams.Where(s => s.Type is "video" or "audio" or "subtitle"))
        {
            _rows.Add(new Row
            {
                Type = s.Type, Original = s, Codec = CodecLabel(s.Codec), Details = DetailsOf(s),
                Language = s.Language, Title = s.Title ?? "", IsDefault = s.IsDefault, IsForced = s.IsForced,
            });
        }
        SetStatus("", isError: false);
        Rebuild();
        BuildStoredChips();
    }

    private static string CodecLabel(string? codec) => codec switch
    {
        null => "?",
        "subrip" => "SRT",
        "ass" or "ssa" => "ASS",
        "hdmv_pgs_subtitle" => "PGS (Bild)",
        "dvd_subtitle" => "DVD (Bild)",
        "dvb_subtitle" => "DVB (Bild)",
        "eac3" => "E-AC3",
        "truehd" => "TrueHD",
        "h264" => "H.264",
        "hevc" => "H.265",
        var other => other.ToUpperInvariant(),
    };

    private static string ChannelsLabel(int? channels) => channels switch
    {
        null => "",
        1 => "Mono",
        2 => "Stereo",
        6 => "5.1",
        8 => "7.1",
        var n => $"{n} Kanäle",
    };

    private static string DetailsOf(StreamInfo s) => s.Type switch
    {
        "video" => string.Join(" · ", new[] { CodecLabel(s.Codec), s.Width is { } w && s.Height is { } h ? $"{w}×{h}" : "" }.Where(t => t.Length > 0)),
        "audio" => string.Join(" · ", new[] { CodecLabel(s.Codec), ChannelsLabel(s.Channels) }.Where(t => t.Length > 0)),
        _ => CodecLabel(s.Codec),
    };

    // --- Spurliste -----------------------------------------------------------------------------------------

    private static string TypeLabel(string type) => type switch { "video" => "Video", "audio" => "Ton", _ => "Untertitel" };

    private static readonly double[] Widths = { 250, 140, double.NaN, 90, 100, 100, 150 };

    private static Grid ColumnGrid()
    {
        var grid = new Grid();
        foreach (var width in Widths)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = double.IsNaN(width) ? new GridLength(1, GridUnitType.Star) : new GridLength(width) });
        }
        return grid;
    }

    private void Rebuild()
    {
        _rowsPanel.Children.Clear();

        var header = ColumnGrid();
        header.Margin = new Thickness(18, 4, 18, 4);
        string[] names = { "Spur", "Sprache", "Name", "Standard", "Erzwungen", "Versatz (s)", "" };
        for (var i = 0; i < names.Length; i++)
        {
            var label = PageParts.Muted(names[i], 12);
            Grid.SetColumn(label, i);
            header.Children.Add(label);
        }
        _rowsPanel.Children.Add(header);

        var counters = new Dictionary<string, int>();
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            counters[row.Type] = counters.GetValueOrDefault(row.Type) + 1;
            _rowsPanel.Children.Add(BuildRow(row, i + 1, counters[row.Type]));
        }
        UpdateSummary();
    }

    private UIElement BuildRow(Row row, int number, int typeNumber)
    {
        var grid = ColumnGrid();
        var name = $"Spur {number}";
        var isVideo = row.Type == "video";

        // Spalte 0: Art, Nummer, Codec; neu hinzugefügte tragen eine Marke
        var heading = PageParts.Semi($"{TypeLabel(row.Type)} {typeNumber}", 15);
        var sub = PageParts.Muted(row.IsNew ? $"{row.Codec} · {Path.GetFileName(row.File)}" : row.Details, 12);
        sub.TextTrimming = TextTrimming.CharacterEllipsis;
        var headingLine = new StackPanel { Orientation = Orientation.Horizontal, Children = { heading } };
        if (row.IsNew) headingLine.Children.Add(Wrap(PageParts.Badge("neu", true), new Thickness(8, 0, 0, 0)));
        var infoColumn = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Children = { headingLine, sub } };
        grid.Children.Add(infoColumn);

        // Spalte 1: Sprache
        var language = new Button { Content = Languages.Display(row.Language) + "  ▾", Padding = new Thickness(12, 5, 12, 5), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Sprache der Spur ändern" };
        language.SetResourceReference(StyleProperty, "GhostButton");
        AutomationProperties.SetName(language, $"{name} Sprache");
        language.Click += (_, _) => ContextPopup.Show(language, LanguageChoices(row, language), $"{name} Sprache");
        Grid.SetColumn(language, 1);
        grid.Children.Add(language);

        // Spalte 2: Name
        var title = new TextBox { Text = row.Title, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), ToolTip = "Name der Spur, wie ihn Player anzeigen" };
        AutomationProperties.SetName(title, $"{name} Name");
        title.TextChanged += (_, _) =>
        {
            row.Title = title.Text;
            UpdateSummary();
        };
        Grid.SetColumn(title, 2);
        grid.Children.Add(title);

        if (!isVideo)
        {
            // Spalten 3 und 4: Standard, Erzwungen
            var isDefault = Check(row.IsDefault, $"{name} Standard", "Wird vom Player zuerst gewählt");
            isDefault.Checked += (_, _) =>
            {
                row.IsDefault = true;
                foreach (var other in _rows.Where(r => r != row && r.Type == row.Type)) other.IsDefault = false;
                Dispatcher.BeginInvoke(Rebuild); // die anderen Haken nachziehen
            };
            isDefault.Unchecked += (_, _) =>
            {
                row.IsDefault = false;
                UpdateSummary();
            };
            Grid.SetColumn(isDefault, 3);
            grid.Children.Add(isDefault);

            var isForced = Check(row.IsForced, $"{name} Erzwungen", "Wird immer angezeigt (z. B. fremdsprachige Szenen)");
            isForced.Checked += (_, _) => { row.IsForced = true; UpdateSummary(); };
            isForced.Unchecked += (_, _) => { row.IsForced = false; UpdateSummary(); };
            Grid.SetColumn(isForced, 4);
            grid.Children.Add(isForced);

            // Spalte 5: Versatz
            var delay = new TextBox { Text = row.DelayText, Width = 78, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, ToolTip = "Dauerhafter Versatz in Sekunden, z. B. 0,5 oder -1,2 (positiv = später, negativ = früher)" };
            AutomationProperties.SetName(delay, $"{name} Versatz");
            delay.TextChanged += (_, _) =>
            {
                row.DelayText = delay.Text;
                UpdateSummary();
            };
            Grid.SetColumn(delay, 5);
            grid.Children.Add(delay);

            // Spalte 6: Reihenfolge, Entfernen
            var up = IconButton("", $"{name} nach oben", "Nach oben");
            var down = IconButton("", $"{name} nach unten", "Nach unten");
            var remove = IconButton(row.Remove ? "" : "", $"{name} entfernen", row.Remove ? "Doch behalten" : "Spur entfernen");
            up.Click += (_, _) => Move(row, -1);
            down.Click += (_, _) => Move(row, +1);
            remove.Click += (_, _) =>
            {
                if (row.IsNew) _rows.Remove(row);
                else row.Remove = !row.Remove;
                Rebuild();
                BuildStoredChips();
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { up, down, remove } };
            Grid.SetColumn(buttons, 6);
            grid.Children.Add(buttons);
        }

        var card = PageParts.Card(grid, new Thickness(0, 0, 0, 8));
        card.Padding = new Thickness(18, 12, 18, 12);
        if (row.Remove) card.Opacity = 0.45;
        if (row.Remove)
        {
            heading.TextDecorations = TextDecorations.Strikethrough;
            sub.Text = "wird entfernt";
        }
        return card;
    }

    private static UIElement Wrap(UIElement element, Thickness margin)
    {
        if (element is FrameworkElement fe)
        {
            fe.Margin = margin;
            fe.VerticalAlignment = VerticalAlignment.Center;
        }
        return element;
    }

    private static CheckBox Check(bool isChecked, string automationName, string tooltip)
    {
        var box = new CheckBox { IsChecked = isChecked, VerticalAlignment = VerticalAlignment.Center, ToolTip = tooltip };
        AutomationProperties.SetName(box, automationName);
        return box;
    }

    private static Button IconButton(string glyph, string automationName, string tooltip)
    {
        var button = new Button { Content = glyph, ToolTip = tooltip };
        button.SetResourceReference(StyleProperty, "IconButton");
        AutomationProperties.SetName(button, automationName);
        return button;
    }

    private IEnumerable<(string Text, Action Click)> LanguageChoices(Row row, Button button)
    {
        void Choose(string code)
        {
            row.Language = code;
            button.Content = Languages.Display(code) + "  ▾";
            UpdateSummary();
        }
        foreach (var language in Languages.All)
        {
            var code = language.Codes.FirstOrDefault() ?? language.Id;
            yield return (language.Name, () => Choose(code));
        }
        yield return ("Unbekannt", () => Choose(UnknownLanguage));
    }

    /// <summary>Tauscht die Spur mit der nächsten gleicher Art (Ton bleibt bei Ton, Untertitel bei Untertitel).</summary>
    private void Move(Row row, int direction)
    {
        var index = _rows.IndexOf(row);
        for (var i = index + direction; i >= 0 && i < _rows.Count; i += direction)
        {
            if (_rows[i].Type != row.Type) continue;
            (_rows[index], _rows[i]) = (_rows[i], _rows[index]);
            Rebuild();
            return;
        }
    }

    // --- Spuren hinzufügen ---------------------------------------------------------------------------------

    private void InsertRow(Row row)
    {
        var last = _rows.FindLastIndex(r => r.Type == row.Type);
        if (last < 0) _rows.Add(row);
        else _rows.Insert(last + 1, row);
    }

    private static string CodeOf(Language? language) => language is null ? UnknownLanguage : language.Codes.FirstOrDefault() ?? language.Id;

    private async Task AddSubtitleFileAsync()
    {
        var textExtensions = string.Join(";", SubtitleStore.Extensions.Select(e => "*" + e));
        var dialog = new OpenFileDialog
        {
            Title = "Untertitel wählen",
            Filter = $"Untertitel und Container|{textExtensions};*.mkv;*.mks;*.mp4;*.m4v|Alle Dateien|*.*",
        };
        if (dialog.ShowDialog() != true) return;
        await AddFromFileAsync(dialog.FileName, MkvAddKind.Subtitle);
    }

    private async Task AddAudioFileAsync()
    {
        var audio = string.Join(";", MediaExtensions.Audio.Select(e => "*" + e));
        var dialog = new OpenFileDialog
        {
            Title = "Tonspur wählen",
            Filter = $"Ton und Container|{audio};*.ac3;*.eac3;*.dts;*.thd;*.mkv;*.mp4;*.m4v|Alle Dateien|*.*",
        };
        if (dialog.ShowDialog() != true) return;
        await AddFromFileAsync(dialog.FileName, MkvAddKind.Audio);
    }

    /// <summary>Fügt die Spuren einer Datei hinzu: eine Untertiteltextdatei als eine Zeile, sonst je passender Spur der Datei eine.</summary>
    private async Task AddFromFileAsync(string file, MkvAddKind kind)
    {
        try
        {
            if (kind == MkvAddKind.Subtitle && SubtitleStore.Extensions.Contains(Path.GetExtension(file)))
            {
                var language = SubtitleStore.DetectLanguage(file);
                InsertRow(new Row
                {
                    Type = "subtitle", File = file, Codec = Path.GetExtension(file).TrimStart('.').ToUpperInvariant(), Details = "",
                    Language = CodeOf(language), Title = SubtitleStore.DisplayName(file),
                });
            }
            else
            {
                var wanted = kind == MkvAddKind.Audio ? "audio" : "subtitle";
                var probe = await _shell.Services.Probe.ProbeAsync(file);
                var streams = probe.Streams.Where(s => s.Type == wanted).ToList();
                if (streams.Count == 0)
                {
                    SetStatus($"„{Path.GetFileName(file)}“ enthält {(kind == MkvAddKind.Audio ? "keine Tonspur" : "keinen Untertitel")}.", isError: true);
                    return;
                }
                foreach (var s in streams)
                {
                    InsertRow(new Row
                    {
                        Type = wanted, File = file, SourceStreamIndex = s.Index, Codec = CodecLabel(s.Codec), Details = DetailsOf(s),
                        Language = s.Language ?? CodeOf(SubtitleStore.DetectLanguage(file)),
                        // Eine Datei mit nur einer Spur und ohne eigenen Namen heißt wie die Datei („Kommentar.ac3“ → „Kommentar“).
                        Title = s.Title ?? (streams.Count == 1 ? Path.GetFileNameWithoutExtension(file) : ""),
                        IsForced = s.IsForced,
                    });
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or TimeoutException)
        {
            SetStatus($"„{Path.GetFileName(file)}“ lässt sich nicht lesen: {ex.Message}", isError: true);
            return;
        }
        SetStatus("", isError: false);
        Rebuild();
    }

    /// <summary>Untertitel, die in der App zu diesem Film gespeichert sind (auch KI-Übersetzungen), lassen sich mit einem Klick hinzufügen.</summary>
    private void BuildStoredChips()
    {
        _storedPanel.Children.Clear();
        var used = _rows.Where(r => r.StoredPath is not null).Select(r => r.StoredPath!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stored = _shell.Services.Subtitles.List(_video.VideoId).Where(f => !used.Contains(f)).ToList();
        if (stored.Count == 0) return;

        var label = PageParts.Muted("In der App gespeichert:", 13);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0, 0, 10, 8);
        _storedPanel.Children.Add(label);
        foreach (var file in stored)
        {
            var displayName = SubtitleStore.DisplayName(file);
            var chip = new System.Windows.Controls.Primitives.ToggleButton { Content = $"+ {displayName}", ToolTip = $"{Path.GetFileName(file)} als Spur hinzufügen", Margin = new Thickness(0, 0, 8, 8) };
            chip.SetResourceReference(StyleProperty, "Chip");
            AutomationProperties.SetName(chip, $"Gespeicherter Untertitel {displayName}");
            chip.Checked += (_, _) =>
            {
                InsertRow(new Row
                {
                    Type = "subtitle", File = file, StoredPath = file, Codec = Path.GetExtension(file).TrimStart('.').ToUpperInvariant(), Details = "",
                    Language = CodeOf(SubtitleStore.DetectLanguage(file)), Title = displayName,
                });
                Rebuild();
                BuildStoredChips();
            };
            _storedPanel.Children.Add(chip);
        }
    }

    // --- Änderungen sammeln --------------------------------------------------------------------------------

    private static string NormalLanguage(string? code) => string.IsNullOrWhiteSpace(code) ? UnknownLanguage : code.Trim();

    private static bool TryParseDelay(string text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return true;
        return double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) && double.IsFinite(seconds);
    }

    private static bool LanguageChanged(Row r) => NormalLanguage(r.Language) != NormalLanguage(r.Original!.Language);

    private static bool TitleChanged(Row r) => r.Title.Trim() != (r.Original!.Title ?? "").Trim();

    /// <summary>Beschreibt jede Änderung in einer Zeile (für die Zählung und die Rückfrage).</summary>
    private List<string> Changes()
    {
        var changes = new List<string>();
        var counters = new Dictionary<string, int>();
        foreach (var row in _rows)
        {
            counters[row.Type] = counters.GetValueOrDefault(row.Type) + 1;
            var label = $"{TypeLabel(row.Type)} {counters[row.Type]} ({Languages.Display(row.Language)}{(row.Title.Length > 0 ? $", „{row.Title}“" : "")})";
            if (row.IsNew)
            {
                changes.Add($"Hinzufügen: {label}");
                continue;
            }
            if (row.Remove)
            {
                changes.Add($"Entfernen: {label}");
                continue;
            }
            if (LanguageChanged(row)) changes.Add($"Sprache ändern: {label}");
            if (TitleChanged(row)) changes.Add($"Namen ändern: {label}");
            if (row.IsDefault != row.Original!.IsDefault) changes.Add($"„Standard“ {(row.IsDefault ? "setzen" : "entfernen")}: {label}");
            if (row.IsForced != row.Original.IsForced) changes.Add($"„Erzwungen“ {(row.IsForced ? "setzen" : "entfernen")}: {label}");
            if (TryParseDelay(row.DelayText, out var delay) && delay != 0) changes.Add($"Versatz {delay.ToString("+0.###;-0.###", CultureInfo.GetCultureInfo("de-DE"))} s: {label}");
        }
        if (OrderChanged()) changes.Add("Reihenfolge der Spuren ändern");
        return changes;
    }

    private bool OrderChanged() =>
        !_rows.Where(r => !r.IsNew).Select(r => r.Original!.Index).SequenceEqual(_probe!.Streams.Where(s => s.Type is "video" or "audio" or "subtitle").Select(s => s.Index));

    private void UpdateSummary()
    {
        if (_probe is null) return;
        var count = Changes().Count;
        _summary.Text = count == 0 ? "Keine Änderungen" : count == 1 ? "1 Änderung" : $"{count} Änderungen";
        _save.IsEnabled = count > 0 && !_running;
    }

    private MkvEditPlan BuildPlan(string? copyTo)
    {
        var tracks = new List<MkvTrackEdit>();
        var added = new List<MkvAddedTrack>();
        var ranks = new Dictionary<string, int>();
        foreach (var row in _rows)
        {
            TryParseDelay(row.DelayText, out var delay);
            if (row.IsNew)
            {
                added.Add(new MkvAddedTrack(
                    row.File!, row.Type == "audio" ? MkvAddKind.Audio : MkvAddKind.Subtitle, NormalLanguage(row.Language),
                    row.Title.Trim().Length == 0 ? null : row.Title.Trim(), row.IsDefault, row.IsForced, delay, row.SourceStreamIndex,
                    Position: ranks.GetValueOrDefault(row.Type)));
                ranks[row.Type] = ranks.GetValueOrDefault(row.Type) + 1;
                continue;
            }

            tracks.Add(new MkvTrackEdit(
                row.Original!.Index, row.Remove,
                Language: LanguageChanged(row) ? NormalLanguage(row.Language) : null,
                Title: TitleChanged(row) ? row.Title.Trim() : null,
                IsDefault: row.IsDefault != row.Original.IsDefault ? row.IsDefault : null,
                IsForced: row.IsForced != row.Original.IsForced ? row.IsForced : null,
                DelaySeconds: row.Remove ? 0 : delay));
            if (!row.Remove) ranks[row.Type] = ranks.GetValueOrDefault(row.Type) + 1;
        }
        return new MkvEditPlan(tracks, added, copyTo);
    }

    // --- Ziel ----------------------------------------------------------------------------------------------

    private static string SuggestCopyPath(string source)
    {
        var directory = Path.GetDirectoryName(source)!;
        var name = Path.GetFileNameWithoutExtension(source);
        for (var attempt = 1; ; attempt++)
        {
            var candidate = Path.Combine(directory, $"{name} (bearbeitet{(attempt == 1 ? "" : $" {attempt}")}).mkv");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private void UpdateTargetNote()
    {
        var copy = _copyChoice.IsChecked == true;
        _copyName.IsEnabled = copy;
        _chooseCopy.IsEnabled = copy;
        _targetNote.Text = copy
            ? "Das Original bleibt unverändert. Liegt die Kopie in einem Ordner der Bibliothek, erscheint sie beim nächsten Scan als eigener Eintrag."
            : "Die bearbeitete Datei ersetzt das Original. Das Original kommt in den Papierkorb, auf Laufwerken ohne Papierkorb (z. B. USB) wird es gelöscht. Es wird kurz so viel freier Platz wie die Datei gebraucht.";
    }

    private void ChooseCopyPath()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Kopie speichern unter",
            Filter = "MKV-Datei|*.mkv",
            OverwritePrompt = false,
            InitialDirectory = Path.GetDirectoryName(_copyName.Text) is { Length: > 0 } dir && Directory.Exists(dir) ? dir : Path.GetDirectoryName(_path),
            FileName = Path.GetFileName(_copyName.Text),
        };
        if (dialog.ShowDialog() == true) _copyName.Text = dialog.FileName;
    }

    // --- Speichern -----------------------------------------------------------------------------------------

    private void SetStatus(string text, bool isError)
    {
        _status.Text = text;
        // Fehler in der Zweitfarbe wie bei den Meldungen; sonst zurück auf die Farbe aus dem Stil.
        if (isError) _status.SetResourceReference(TextBlock.ForegroundProperty, "Accent2Brush");
        else _status.ClearValue(TextBlock.ForegroundProperty);
    }

    private void SetRunning(bool running)
    {
        _running = running;
        _rowsPanel.IsEnabled = !running;
        _storedPanel.IsEnabled = !running;
        _addSubtitle.IsEnabled = !running;
        _addAudio.IsEnabled = !running;
        _copyChoice.IsEnabled = !running;
        _replaceChoice.IsEnabled = !running;
        _copyName.IsEnabled = !running && _copyChoice.IsChecked == true;
        _chooseCopy.IsEnabled = !running && _copyChoice.IsChecked == true;
        _cancel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        _cancel.IsEnabled = true;
        _bar.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        _bar.Value = 0;
        UpdateSummary();
    }

    private async Task SaveAsync()
    {
        if (_running || _probe is null) return;

        foreach (var row in _rows)
        {
            if (!TryParseDelay(row.DelayText, out _))
            {
                SetStatus($"Der Versatz „{row.DelayText}“ ist keine Zahl. Erlaubt sind Sekunden wie 0,5 oder -1,2.", isError: true);
                return;
            }
        }

        string? copyTo = null;
        if (_copyChoice.IsChecked == true)
        {
            var text = _copyName.Text.Trim();
            if (text.Length == 0)
            {
                SetStatus("Gib einen Namen für die Kopie an.", isError: true);
                return;
            }
            copyTo = Path.IsPathRooted(text) ? text : Path.Combine(Path.GetDirectoryName(_path)!, text);
            if (!MkvEditor.CanEdit(copyTo))
            {
                SetStatus("Der Name der Kopie muss auf „.mkv“ enden.", isError: true);
                return;
            }
        }

        var changes = Changes();
        if (copyTo is null)
        {
            var question = new StringBuilder()
                .AppendLine("Die Datei mit diesen Änderungen ersetzen?")
                .AppendLine()
                .AppendLine($"{_path}  ({FormatSize(new FileInfo(_path).Length)})")
                .AppendLine();
            foreach (var change in changes.Take(12)) question.AppendLine("• " + change);
            if (changes.Count > 12) question.AppendLine($"• … und {changes.Count - 12} weitere");
            question.AppendLine()
                .AppendLine("Die MKV wird neu geschrieben (ohne Neukodierung). Das kann je nach Laufwerk einige Minuten dauern und braucht kurz so viel freien Platz wie die Datei. " +
                            "Die neue Datei wird geprüft und ersetzt dann das Original. Das Original kommt in den Papierkorb, auf Laufwerken ohne Papierkorb (z. B. USB) wird es gelöscht.");
            if (!_shell.Confirm("Datei ersetzen?", question.ToString(), "Ersetzen", danger: true)) return;
        }

        var plan = BuildPlan(copyTo);
        var storedToDrop = _rows.Where(r => r.StoredPath is not null).Select(r => r.StoredPath!).ToList();
        var fileName = Path.GetFileName(_path);
        using var cts = new CancellationTokenSource();
        _cts = cts;
        SetRunning(true);
        SetStatus($"{fileName} wird neu geschrieben …", isError: false);
        var progress = new Progress<double>(f =>
        {
            _bar.Value = f;
            _status.Text = $"{fileName} wird neu geschrieben … {f:P0}";
        });

        string message;
        var isError = false;
        var done = false;
        try
        {
            var result = await Task.Run(() => _shell.Services.Editor.EditAsync(_path, plan, LibraryActions.Recycle, progress, cts.Token));
            done = true;
            message = copyTo is null ? "Die Datei wurde bearbeitet." : $"Kopie gespeichert: {result.VideoPath}";
            if (result.OriginalLeftBehind is { } left)
            {
                message += $" Die alte Datei ließ sich nicht entfernen und liegt noch hier: {left}";
                isError = true;
            }
        }
        catch (OperationCanceledException)
        {
            message = copyTo is null ? "Abgebrochen. Die Datei ist unverändert." : "Abgebrochen. Es wurde keine Kopie angelegt.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or NotSupportedException
                                       or TimeoutException or UnauthorizedAccessException or ArgumentException)
        {
            message = $"Nicht möglich, {(copyTo is null ? "die Datei ist unverändert" : "es wurde keine Kopie angelegt")}: {ex.Message}";
            isError = true;
        }
        finally
        {
            _cts = null;
        }

        SetRunning(false);
        if (done && copyTo is null)
        {
            // Ersetzt: die in der App gespeicherten Kopien der übernommenen Untertitel wären jetzt doppelt vorhanden.
            foreach (var stored in storedToDrop)
            {
                try
                {
                    _shell.Services.Subtitles.Remove(stored);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    message += $" Die gespeicherte Kopie „{Path.GetFileName(stored)}“ ließ sich nicht entfernen: {ex.Message}";
                    isError = true;
                }
            }
            await LoadAsync(); // zeigt den neuen Stand der Datei
            BuildStoredChips();
        }
        SetStatus(message, isError);
        _shell.ShowMessage(message, isError);
    }
}
