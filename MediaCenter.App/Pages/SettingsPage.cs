using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MediaCenter.App.Services;
using MediaCenter.App.Theming;
using MediaCenter.Core;

namespace MediaCenter.App.Pages;

public sealed class SettingsPage : UserControl
{
    private readonly IShell _shell;
    private readonly ThemeService _theme;
    private readonly WrapPanel _modeChips = new();
    private readonly WrapPanel _paletteTiles = new();

    public SettingsPage(IShell shell)
    {
        _shell = shell;
        _theme = shell.Services.Theme;

        var stack = new StackPanel { MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
        stack.Children.Add(PageParts.Heading("Einstellungen"));

        stack.Children.Add(Section("Darstellung", new StackPanel
        {
            Children =
            {
                Label("Modus"), _modeChips,
                Label("Farbstil"), _paletteTiles,
            },
        }));
        stack.Children.Add(Section("Bibliothek", new StackPanel
        {
            Children =
            {
                PageParts.Muted($"Datenbank: {Path.Combine(AppSettings.DataDirectory, "library.db")}"),
                PageParts.Muted($"Cover-Cache: {_shell.Services.Model.Artwork.CacheDir}"),
                PageParts.Muted($"Einstellungen: {AppSettings.FilePath}"),
                PageParts.Button("Alle Cover neu erzeugen", "GhostButton", async (_, _) => await _shell.RefreshArtworkAsync(force: true), new Thickness(0, 14, 0, 0)),
            },
        }));
        stack.Children.Add(Section("Wiedergabe", PlaybackSettings()));
        stack.Children.Add(Section("Übersetzung", TranslationSettings()));
        stack.Children.Add(Section("Player", new StackPanel
        {
            Children =
            {
                PageParts.Muted($"mpv: {_shell.Services.MpvPath}" + (File.Exists(_shell.Services.MpvPath) ? "" : "  (nicht gefunden)")),
                PageParts.Muted("Anderen Pfad über die Umgebungsvariable MEDIACENTER_MPV setzen."),
            },
        }));
        stack.Children.Add(Section("Schriften", PageParts.Muted("Bebas Neue und Figtree, beide unter der SIL Open Font License (Lizenztexte im Ordner Fonts).")));

        Content = PageParts.Scrolling(stack);

        Build();
        Loaded += (_, _) => _theme.Changed += Build;
        Unloaded += (_, _) => _theme.Changed -= Build;
    }

    /// <summary>Sprachen, die als Schaltflächen angeboten werden; eine von Hand in die Datei geschriebene andere kommt dazu.</summary>
    private static readonly string[] OfferedLanguages = { "de", "en", "ja", "fr", "es", "it" };

    private UIElement PlaybackSettings()
    {
        var settings = _shell.Services.Settings;

        UIElement Group(string name, IEnumerable<(string Text, bool Selected, Action Apply)> items)
        {
            var row = new WrapPanel();
            foreach (var (text, selected, apply) in items)
            {
                var chip = new RadioButton { Content = text, GroupName = name, IsChecked = selected };
                chip.SetResourceReference(StyleProperty, "Chip");
                System.Windows.Automation.AutomationProperties.SetName(chip, $"{name} {text}");
                chip.Checked += (_, _) =>
                {
                    apply();
                    settings.Save();
                };
                row.Children.Add(chip);
            }
            return row;
        }

        IEnumerable<Language> Offered(string? current) =>
            OfferedLanguages.Concat(current is not null && !OfferedLanguages.Contains(current) ? new[] { current } : Array.Empty<string>())
                .Select(Languages.Get);

        var audio = new[] { ("Datei-Standard", settings.AudioLanguage is null, (Action)(() => settings.AudioLanguage = null)) }
            .Concat(Offered(settings.AudioLanguage).Select(l => (l.Name, settings.AudioLanguage == l.Id, (Action)(() => settings.AudioLanguage = l.Id))));
        var mode = new[]
        {
            ("Aus", SubtitleMode.Off),
            ("Bei fremdem Ton", SubtitleMode.Foreign),
            ("Immer", SubtitleMode.Always),
        }.Select(m => (m.Item1, settings.Subtitles == m.Item2, (Action)(() => settings.Subtitles = m.Item2)));
        var language = Offered(settings.SubtitleLanguage).Select(l => (l.Name, settings.SubtitleLanguage == l.Id, (Action)(() => settings.SubtitleLanguage = l.Id)));
        var autoplay = new[] { ("An", true), ("Aus", false) }.Select(a => (a.Item1, settings.AutoPlayNext == a.Item2, (Action)(() => settings.AutoPlayNext = a.Item2)));

        return new StackPanel
        {
            Children =
            {
                Label("Tonspur"), Group("Tonspur", audio),
                Label("Untertitel"), Group("Untertitel", mode),
                Label("Untertitelsprache"), Group("Untertitelsprache", language),
                Label("Nächste Folge automatisch starten"), Group("Nächste Folge", autoplay),
                PageParts.Muted("„Bei fremdem Ton“ zeigt Untertitel nur, wenn die Tonspur nicht in der Untertitelsprache ist (dann nur erzwungene). " +
                    "Fehlt die Tonsprache in der Datei, läuft die Spur der Datei. Gilt ab der nächsten Wiedergabe; im Player lässt sich jede Spur von Hand wählen. " +
                    "Untertitel-Dateien neben dem Video (Film.de.srt, Film.German.srt, Ordner Subs) werden automatisch geladen."),
            },
        };
    }

    private readonly WrapPanel _modelChips = new();
    private readonly WrapPanel _chineseModelChips = new();
    private readonly TextBlock _modelStatus = PageParts.Muted("");

    private UIElement TranslationSettings()
    {
        var settings = _shell.Services.Settings;
        _modelStatus.Margin = new Thickness(0, 10, 0, 0);

        var reload = PageParts.Button("Modelle von Ollama laden", "GhostButton", async (_, _) => await LoadModelsAsync(), new Thickness(0, 14, 0, 0));
        System.Windows.Automation.AutomationProperties.SetName(reload, "Modelle von Ollama laden");

        _ = LoadModelsAsync();
        return new StackPanel
        {
            Children =
            {
                PageParts.Muted($"Ollama: {settings.OllamaUrl}  (Adresse in {AppSettings.FilePath} unter OllamaUrl ändern)"),
                Label("Modell für Übersetzungen"), _modelChips,
                Label("Modell für Chinesisch"), _chineseModelChips,
                reload, _modelStatus,
                PageParts.Muted("Übersetzt wird im Player über das Untertitelmenü („Untertitel übersetzen“). Ein Film dauert je nach Modell und Grafikkarte einige Minuten bis eine Stunde; " +
                    "die Übersetzung läuft im Hintergrund und setzt nach einem Abbruch fort. Ollama muss laufen und das Modell geladen sein (ollama pull <Name>)."),
            },
        };
    }

    /// <summary>Füllt die Modellauswahl mit den in Ollama installierten Modellen; ist Ollama nicht erreichbar, steht der Grund da.</summary>
    private async Task LoadModelsAsync()
    {
        var settings = _shell.Services.Settings;
        _modelStatus.Text = "Lade Modelle …";
        try
        {
            var models = await _shell.Services.Ollama().ListModelsAsync();
            FillModels(_modelChips, "Übersetzungsmodell", models, settings.TranslationModel, includeSame: false, name => settings.TranslationModel = name);
            FillModels(_chineseModelChips, "Chinesisch-Modell", models, settings.TranslationModelChinese, includeSame: true, name => settings.TranslationModelChinese = name);
            _modelStatus.Text = models.Count == 0
                ? "Ollama läuft, aber es ist noch kein Modell installiert (ollama pull <Name>)."
                : $"{models.Count} Modell(e) installiert.";
        }
        catch (Exception ex) when (ex is OllamaException or ArgumentException)
        {
            _modelChips.Children.Clear();
            _chineseModelChips.Children.Clear();
            _modelStatus.Text = ex.Message;
            _shell.ShowMessage(ex.Message, isError: true);
        }
    }

    private void FillModels(WrapPanel panel, string group, IReadOnlyList<string> installed, string? selected, bool includeSame, Action<string?> apply)
    {
        var settings = _shell.Services.Settings;
        panel.Children.Clear();

        // Ein gewähltes, aber nicht (mehr) installiertes Modell bleibt sichtbar, damit klar ist, was die Einstellung sagt.
        var names = installed.ToList();
        if (selected is not null && !names.Contains(selected)) names.Add(selected);

        var options = (includeSame ? new[] { ((string?)null, "Wie oben") } : Array.Empty<(string?, string)>())
            .Concat(names.Select(n => ((string?)n, n + (installed.Contains(n) ? "" : "  (nicht installiert)"))));
        foreach (var (name, text) in options)
        {
            var chip = new RadioButton { Content = text, GroupName = group, IsChecked = name == selected };
            chip.SetResourceReference(StyleProperty, "Chip");
            System.Windows.Automation.AutomationProperties.SetName(chip, $"{group} {text}");
            chip.Checked += (_, _) =>
            {
                apply(name);
                settings.Save();
            };
            panel.Children.Add(chip);
        }
    }

    private static TextBlock Label(string text)
    {
        var label = PageParts.Muted(text, 13);
        label.Margin = new Thickness(0, 12, 0, 8);
        return label;
    }

    private static UIElement Section(string title, UIElement content)
    {
        var header = new TextBlock { Text = title, FontSize = 32, Margin = new Thickness(0, 0, 0, 8) };
        header.SetResourceReference(FrameworkElement.StyleProperty, "DisplayText");
        return PageParts.Card(new StackPanel { Children = { header, content } }, new Thickness(0, 22, 0, 0));
    }

    /// <summary>Baut Modus-Auswahl und Farbtafeln neu; die Punkte der Tafeln hängen vom Modus ab.</summary>
    private void Build()
    {
        _modeChips.Children.Clear();
        foreach (var (mode, text) in new[] { (ThemeMode.System, "System"), (ThemeMode.Light, "Hell"), (ThemeMode.Dark, "Dunkel") })
        {
            var chip = new RadioButton { Content = text, GroupName = "mode", IsChecked = _theme.Mode == mode };
            chip.SetResourceReference(StyleProperty, "Chip");
            chip.Checked += (_, _) =>
            {
                if (_theme.Mode != mode) _theme.SetMode(mode);
            };
            _modeChips.Children.Add(chip);
        }

        _paletteTiles.Children.Clear();
        foreach (var palette in Palettes.All) _paletteTiles.Children.Add(Tile(palette));
    }

    private UIElement Tile(AccentPalette palette)
    {
        var selected = palette.Id == _theme.Palette.Id;
        var accent = _theme.IsDark ? palette.DarkAccent : palette.LightAccent;
        var accent2 = _theme.IsDark ? palette.DarkAccent2 : palette.LightAccent2;

        var dots = new StackPanel { Orientation = Orientation.Horizontal };
        dots.Children.Add(Dot(accent, 0));
        dots.Children.Add(Dot(accent2, -8));
        var name = new TextBlock { Text = palette.Name, FontSize = 14, Margin = new Thickness(0, 8, 0, 0) };
        name.SetResourceReference(TextBlock.FontFamilyProperty, "BodySemiFont");
        name.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");

        var tile = new Button
        {
            Content = new StackPanel { Children = { dots, name } },
            Width = 118,
            Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(16, 14, 16, 14),
            Tag = selected ? "on" : null,
        };
        tile.SetResourceReference(StyleProperty, "PaletteTile");
        System.Windows.Automation.AutomationProperties.SetName(tile, $"Farbstil {palette.Name}");
        tile.Click += (_, _) => _theme.SetPalette(palette.Id);
        return tile;
    }

    private static System.Windows.Shapes.Ellipse Dot(Color color, double overlap) => new()
    {
        Width = 28,
        Height = 28,
        Margin = new Thickness(overlap, 0, 0, 0),
        Fill = new SolidColorBrush(color),
    };
}
