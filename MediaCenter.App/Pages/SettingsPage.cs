using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MediaCenter.App.Services;
using MediaCenter.App.Theming;
using MediaCenter.Core;

namespace MediaCenter.App.Pages;

/// <summary>Einstellungen in Reitern; jeder Reiter fasst zusammen, was zusammengehört (Darstellung, Bibliothek, Wiedergabe …).</summary>
public sealed class SettingsPage : UserControl
{
    private static readonly string[] Tabs = { "Darstellung", "Bibliothek", "Wiedergabe", "Übersetzung", "Tastenkürzel", "Über" };

    /// <summary>Der zuletzt gewählte Reiter bleibt beim Wiederöffnen der Seite erhalten.</summary>
    private static string _lastTab = Tabs[0];

    private readonly IShell _shell;
    private readonly ThemeService _theme;
    // Die Inhalte der Reiter werden bei jedem Wechsel neu gebaut; Felder, die ein Reiter hält, entstehen darum jeweils neu (ein Element hat nur einen Eltern-Knoten).
    private WrapPanel _paletteTiles = new();
    private readonly ContentControl _body = new() { IsTabStop = false };
    private bool _building;

    public SettingsPage(IShell shell)
    {
        _shell = shell;
        _theme = shell.Services.Theme;

        var tabBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-13, 0, 0, 0) };
        foreach (var name in Tabs)
        {
            var tab = new RadioButton { Content = name, GroupName = "settings-tab", Height = 46, IsChecked = name == _lastTab };
            tab.SetResourceReference(StyleProperty, "NavToggle");
            AutomationProperties.SetName(tab, name);
            tab.Checked += (_, _) =>
            {
                if (_building) return;
                _lastTab = name;
                ShowTab(name, animate: true);
            };
            tabBar.Children.Add(tab);
        }
        // Die Linie unter den Reitern; die Akzentlinie des gewählten Reiters liegt genau darauf.
        var rule = PageParts.Divider();
        rule.VerticalAlignment = VerticalAlignment.Bottom;
        var tabStrip = new Grid { Margin = new Thickness(0, 0, 0, 24), Children = { rule, tabBar } };

        var stack = new StackPanel();
        stack.Children.Add(PageParts.Header("Programm", "Einstellungen"));
        stack.Children.Add(tabStrip);
        stack.Children.Add(_body);

        // Feste Höchstbreite, die in schmalen Fenstern nachgibt; die Breite hängt nicht vom Inhalt des Reiters ab.
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 920 });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.Children.Add(stack);
        Content = PageParts.Scrolling(layout);

        _building = true;
        ShowTab(_lastTab, animate: false);
        _building = false;
        Loaded += (_, _) => _theme.Changed += RebuildTiles;
        Unloaded += (_, _) => _theme.Changed -= RebuildTiles;
    }

    /// <summary>Wählt den Reiter, der beim nächsten Öffnen der Seite erscheint (z. B. um aus einem Hinweis direkt zur passenden Einstellung zu führen).</summary>
    public static void SelectTab(string name)
    {
        if (!Tabs.Contains(name)) throw new ArgumentException($"Unbekannter Reiter '{name}'. Gültig: {string.Join(", ", Tabs)}.", nameof(name));
        _lastTab = name;
    }

    private void ShowTab(string name, bool animate)
    {
        _body.Content = name switch
        {
            "Darstellung" => Appearance(),
            "Bibliothek" => LibraryTab(),
            "Wiedergabe" => PlaybackSettings(),
            "Übersetzung" => TranslationSettings(),
            "Tastenkürzel" => Hotkeys(),
            "Über" => About(),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unbekannter Reiter."),
        };
        if (!animate) return;
        var lift = new TranslateTransform();
        _body.RenderTransform = lift;
        _body.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)));
        lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // --- Darstellung ---------------------------------------------------------------------------------------

    private UIElement Appearance()
    {
        _paletteTiles = new WrapPanel();
        var modes = PageParts.Segmented("mode",
            new[] { (ThemeMode.System, "System"), (ThemeMode.Light, "Hell"), (ThemeMode.Dark, "Dunkel") }
                .Select(m => (m.Item2, _theme.Mode == m.Item1, (Action)(() =>
                {
                    if (_theme.Mode != m.Item1) _theme.SetMode(m.Item1);
                }))),
            "Modus");
        RebuildTiles();

        var settings = _shell.Services.Settings;
        var gpu = PageParts.Segmented("gpu-rendering",
            new[] { ("Automatisch", AppSettings.GpuAuto), ("An", AppSettings.GpuOn), ("Aus", AppSettings.GpuOff) }.Select(o => (o.Item1, settings.GpuMode == o.Item2, (Action)(() =>
            {
                settings.GpuMode = o.Item2;
                settings.GpuAutoResult = null; // „Automatisch“ prüft beim nächsten Start neu
                settings.Save();
            }))),
            "GPU-Beschleunigung");
        var fullscreen = PageParts.Segmented("start-fullscreen",
            new[] { ("An", true), ("Aus", false) }.Select(o => (o.Item1, settings.StartFullscreen == o.Item2, (Action)(() =>
            {
                settings.StartFullscreen = o.Item2;
                settings.Save();
            }))),
            "Immer im Vollbild starten");
        var gpuNote = settings.GpuMode == AppSettings.GpuAuto
            ? settings.GpuAutoResult switch
            {
                "ok" => " Letzter Test: das Fenster zeigt Inhalt, die Grafikkarte wird genutzt.",
                "failed" => " Letzter Test: das Fenster blieb leer, die App zeichnet per Software.",
                _ => " Der Test läuft beim nächsten Start.",
            }
            : "";
        return new StackPanel
        {
            Children =
            {
                PageParts.Group(
                    PageParts.SettingRow("Modus", "„System“ folgt der Einstellung von Windows.", modes),
                    PageParts.SettingRow("Farbstil", "Akzentfarbe für Knöpfe, Hervorhebungen und Farbverläufe; je Modus auf Kontrast abgestimmt.", _paletteTiles, stacked: true),
                    PageParts.SettingRow("Immer im Vollbild starten", "Die App öffnet sich ohne Fensterrahmen über den ganzen Bildschirm. Im Betrieb schaltet der Knopf oben rechts in der Navigation (oder F11) das Vollbild um.", fullscreen),
                    PageParts.SettingRow("GPU-Beschleunigung", "Oberfläche über die Grafikkarte zeichnen. Automatisch nutzt sie und prüft einmal, ob das Fenster etwas zeigt: Auf manchen Rechnern (z. B. Intel Arc mit NVIDIA) bleibt es sonst weiß, dann wechselt die App von selbst auf Software. Gilt nach einem Neustart; bei weißem Fenster in settings.json „GpuMode“ auf „off“ setzen. Videos laufen immer über die GPU." + gpuNote, gpu)),
            },
        };
    }

    /// <summary>Baut die Farbtafeln neu; die Punkte hängen vom Modus ab.</summary>
    private void RebuildTiles()
    {
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
        var name = new TextBlock { Text = palette.Name, FontSize = 13, Margin = new Thickness(0, 10, 0, 0) };
        name.SetResourceReference(TextBlock.FontFamilyProperty, "BodySemiFont");
        name.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");

        var tile = new Button
        {
            Content = new StackPanel { Children = { dots, name } },
            Width = 112,
            Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(16, 14, 16, 14),
            Tag = selected ? "on" : null,
        };
        tile.SetResourceReference(StyleProperty, "PaletteTile");
        AutomationProperties.SetName(tile, $"Farbstil {palette.Name}");
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

    // --- Bibliothek ----------------------------------------------------------------------------------------

    private UIElement LibraryTab()
    {
        var settings = _shell.Services.Settings;

        var preview = PageParts.Segmented("preview-on-hover",
            new[] { ("An", true), ("Aus", false) }.Select(o => (o.Item1, settings.PreviewOnHover == o.Item2, (Action)(() =>
            {
                settings.PreviewOnHover = o.Item2;
                settings.Save();
            }))),
            "Vorschau beim Überfahren");

        var online = PageParts.Segmented("online-covers",
            new[] { ("An", true), ("Aus", false) }.Select(o => (o.Item1, settings.OnlineCovers == o.Item2, (Action)(() =>
            {
                settings.OnlineCovers = o.Item2;
                settings.Save();
                _shell.Services.ApplyOnlineCovers();
            }))),
            "Poster online");

        var lyrics = PageParts.Segmented("online-lyrics",
            new[] { ("An", true), ("Aus", false) }.Select(o => (o.Item1, settings.OnlineLyrics == o.Item2, (Action)(() =>
            {
                settings.OnlineLyrics = o.Item2;
                settings.Save();
                _shell.Services.ApplyOnlineLyrics();
            }))),
            "Songtexte online");

        var keyStatus = PageParts.Muted("", 13);
        void ShowKeyStatus() => keyStatus.Text = _shell.Services.Omdb is null
            ? "Kein OMDb-Schlüssel hinterlegt. Einen kostenlosen gibt es auf omdbapi.com/apikey.aspx."
            : _shell.Services.OmdbKeyFromVariable
                ? $"Aus der Umgebungsvariable {AppServices.OmdbKeyVariable} (hat Vorrang vor einem hier gespeicherten)."
                : $"Gespeichert in {AppServices.OmdbKeyFile}.";
        ShowKeyStatus();

        var (keyField, keyBox) = PageParts.HintBox("OMDb-Schlüssel", 240);
        AutomationProperties.SetName(keyBox, "OMDb-Schlüssel");
        void SaveKey(string? key)
        {
            try
            {
                _shell.Services.SetOmdbKey(key);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                _shell.ShowMessage(ex.Message, isError: true);
                return;
            }
            keyBox.Clear();
            ShowKeyStatus();
        }
        var save = PageParts.Compact(PageParts.Button("Speichern", "PrimaryButton", (_, _) => SaveKey(keyBox.Text), new Thickness(10, 0, 0, 0)));
        var remove = PageParts.Compact(PageParts.Button("Entfernen", "GhostButton", (_, _) => SaveKey(null), new Thickness(8, 0, 0, 0)));
        AutomationProperties.SetName(save, "OMDb-Schlüssel speichern");
        AutomationProperties.SetName(remove, "OMDb-Schlüssel entfernen");
        var keyRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), Children = { keyField, save, remove } };
        keyStatus.Margin = new Thickness(0, 0, 0, 0);

        var refresh = PageParts.Compact(PageParts.Button("Fehlende Cover suchen", "GhostButton", async (_, _) => await _shell.RefreshArtworkAsync(), new Thickness(0)));
        var rebuild = PageParts.Compact(PageParts.Button("Alle Cover neu erzeugen", "GhostButton", async (_, _) => await _shell.RefreshArtworkAsync(force: true), new Thickness(0)));

        return new StackPanel
        {
            Children =
            {
                PageParts.Group(
                    PageParts.SettingRow("Cover aus dem Internet",
                        "Nur für Titel ohne eigenes Poster im Ordner oder in der Datei. Serien kommen von TVmaze (api.tvmaze.com), Filme von OMDb (omdbapi.com); " +
                        "gesendet werden nur Titel und Jahr. Ein Treffer muss genau so heißen wie der Titel in der Bibliothek, sonst bleibt es ohne Poster; " +
                        "deutsche Filmtitel findet OMDb oft nicht. Album-Cover kommen von iTunes (itunes.apple.com), sonst von MusicBrainz und dem Cover Art Archive (musicbrainz.org, coverartarchive.org); gesendet werden Künstler und Albumtitel; " +
                        "Alben ohne Künstler im Tag werden nicht gesucht. Ein selbst gesetztes Poster bleibt immer. " +
                        "Von denselben Diensten (und mit denselben Treffer-Regeln) kommen auf die Seite eines Films oder einer Serie Handlung, Wertung, Besetzung und bei Serien die Folgentexte, jeweils auf Englisch.", online),
                    PageParts.SettingRow("OMDb-Schlüssel", null, new StackPanel { Children = { keyStatus, keyRow } }, stacked: true)),
                PageParts.Group(
                    PageParts.SettingRow("Songtexte aus dem Internet",
                        "Songtexte zeigt das Panel „Jetzt läuft“ der Musik. Zuerst zählt eine .lrc-Datei neben dem Titel (zeitgesteuert), dann ein Text im Tag der Datei; " +
                        "fehlt beides, fragt die App LRCLIB (lrclib.net) mit Künstler, Titel, Album und Dauer. Gefundene Texte und Absagen werden gemerkt (Ordner „lyrics“ im Datenordner).", lyrics)),
                PageParts.Group(
                    PageParts.SettingRow("Vorschau beim Überfahren",
                        "Die Karte eines Films oder einer Serie wächst zur Seite und zeigt einen stummen Ausschnitt. Der Ausschnitt (10 s, 480p) wird beim ersten Mal " +
                        "mit ffmpeg erzeugt und liegt danach im Ordner „previews“ im Datenordner.", preview)),
                PageParts.Group(
                    PageParts.SettingRow("Fehlende Cover suchen",
                        "Erzeugt und holt Cover für Titel, die noch keins haben, und Infotexte für Titel ohne. Ohne Treffer wird erst nach zwei Wochen neu gefragt.", refresh),
                    PageParts.SettingRow("Alle Cover neu erzeugen", "Ersetzt automatisch erzeugte Cover und lädt die Infotexte neu; selbst gewählte Poster bleiben.", rebuild)),
                PageParts.Group(
                    PageParts.SettingRow("Datenbank", Path.Combine(AppSettings.DataDirectory, "library.db"), null),
                    PageParts.SettingRow("Cover-Cache", _shell.Services.Model.Artwork.CacheDir, null),
                    PageParts.SettingRow("Einstellungen", AppSettings.FilePath, null)),
            },
        };
    }

    // --- Wiedergabe ----------------------------------------------------------------------------------------

    /// <summary>Sprachen, die als Schaltflächen angeboten werden; eine von Hand in die Datei geschriebene andere kommt dazu.</summary>
    private static readonly string[] OfferedLanguages = { "de", "en", "ja", "fr", "es", "it" };

    private UIElement PlaybackSettings()
    {
        var settings = _shell.Services.Settings;

        UIElement Chips(string name, IEnumerable<(string Text, bool Selected, Action Apply)> items)
        {
            var row = new WrapPanel();
            foreach (var (text, selected, apply) in items)
            {
                var chip = new RadioButton { Content = text, GroupName = name, IsChecked = selected };
                chip.SetResourceReference(StyleProperty, "Chip");
                AutomationProperties.SetName(chip, $"{name} {text}");
                chip.Checked += (_, _) =>
                {
                    apply();
                    settings.Save();
                };
                row.Children.Add(chip);
            }
            return row;
        }

        UIElement Choice(string name, IEnumerable<(string Text, bool Selected, Action Apply)> items) =>
            PageParts.Segmented(name, items.Select(i => (i.Text, i.Selected, (Action)(() =>
            {
                i.Apply();
                settings.Save();
            }))));

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
        var castHevc = new[] { ("Umwandeln", false), ("Direkt senden", true) }.Select(a => (a.Item1, settings.CastHevc == a.Item2, (Action)(() => settings.CastHevc = a.Item2)));

        var note = PageParts.Muted(
            "Gilt ab der nächsten Wiedergabe; im Player lässt sich jede Spur von Hand wählen. Fehlt die Tonsprache in der Datei, läuft die Spur der Datei. " +
            "Untertitel-Dateien neben dem Video (Film.de.srt, Film.German.srt, Ordner Subs) werden automatisch geladen.", 13);
        note.Margin = new Thickness(4, 0, 0, 0);

        return new StackPanel
        {
            Children =
            {
                PageParts.Group(
                    PageParts.SettingRow("Tonspur", "Bevorzugte Sprache der Tonspur.", Chips("Tonspur", audio), stacked: true),
                    PageParts.SettingRow("Untertitel",
                        "„Bei fremdem Ton“ zeigt Untertitel nur, wenn die Tonspur nicht in der Untertitelsprache ist (dann nur erzwungene).",
                        Choice("Untertitel", mode)),
                    PageParts.SettingRow("Untertitelsprache", null, Chips("Untertitelsprache", language), stacked: true),
                    PageParts.SettingRow("Nächste Folge automatisch", "Nach einer Folge startet nach einem Countdown von 8 Sekunden die nächste.", Choice("Nächste Folge", autoplay)),
                    PageParts.SettingRow("Auf den Fernseher übertragen: HEVC",
                        "„Direkt senden“ schickt HEVC-Videos (auch HDR) unverändert an den Fernseher, wenn er sie abspielt; schont den PC. „Umwandeln“ macht per ffmpeg H.264 daraus, das jedes Cast-Gerät kann.",
                        Choice("HEVC beim Übertragen", castHevc))),
                note,
                PageParts.Group(
                    PageParts.SettingRow("Equalizer", "Klang für Musik, Radio, Podcasts und Video; Änderungen sind sofort zu hören. Auch über den Knopf „Klang“ in der Musikleiste wählbar.", EqualizerPanel(), stacked: true)),
            },
        };
    }

    // --- Equalizer -----------------------------------------------------------------------------------------

    private UIElement EqualizerPanel()
    {
        var sound = _shell.Services.Sound;
        var presets = new WrapPanel();
        var sliders = new List<Slider>();
        var values = new List<TextBlock>();
        var chips = new List<RadioButton>();
        var building = false; // Setzen von Reglern und Wahlfeldern beim Aufbau oder Umschalten ist keine Eingabe des Nutzers

        static string Db(int gain) => gain > 0 ? $"+{gain} dB" : $"{gain} dB";

        void ShowGains()
        {
            building = true;
            for (var i = 0; i < sliders.Count; i++)
            {
                sliders[i].Value = sound.Gains[i];
                values[i].Text = Db(sound.Gains[i]);
            }
            foreach (var chip in chips) chip.IsChecked = (string)chip.Tag == sound.Preset;
            building = false;
        }

        // Gespeichert wird erst, wenn der Regler ruht; der Klang ändert sich schon beim Ziehen.
        var saveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        saveTimer.Tick += (_, _) =>
        {
            saveTimer.Stop();
            sound.Save();
        };

        foreach (var preset in Equalizer.Presets)
        {
            var chip = new RadioButton { Content = preset.Name, GroupName = "eq-preset", Tag = preset.Id };
            chip.SetResourceReference(StyleProperty, "Chip");
            AutomationProperties.SetName(chip, $"Klang {preset.Name}");
            chip.Checked += (_, _) =>
            {
                if (building) return;
                sound.SetPreset(preset.Id);
                ShowGains();
            };
            chips.Add(chip);
            presets.Children.Add(chip);
        }

        var rows = new StackPanel { Margin = new Thickness(0, 14, 0, 0), Width = 520, HorizontalAlignment = HorizontalAlignment.Left };
        for (var i = 0; i < Equalizer.Bands.Count; i++)
        {
            var band = i;
            var hz = Equalizer.Bands[i];
            var label = PageParts.Muted(hz >= 1000 ? $"{hz / 1000} kHz" : $"{hz} Hz", 13);
            label.VerticalAlignment = VerticalAlignment.Center;
            var slider = new Slider
            {
                Minimum = Equalizer.MinGain, Maximum = Equalizer.MaxGain, TickFrequency = 1, IsSnapToTickEnabled = true,
                SmallChange = 1, LargeChange = 3, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0),
            };
            slider.SetResourceReference(StyleProperty, "SeekSlider");
            AutomationProperties.SetName(slider, $"Equalizer {hz} Hz");
            var value = PageParts.Muted("", 13);
            value.VerticalAlignment = VerticalAlignment.Center;
            value.TextAlignment = TextAlignment.Right;
            slider.ValueChanged += (_, e) =>
            {
                if (building) return;
                var gains = sound.Gains.ToArray();
                gains[band] = (int)Math.Round(e.NewValue);
                sound.SetGains(gains, save: false);
                values[band].Text = Db(gains[band]);
                building = true;
                foreach (var chip in chips) chip.IsChecked = (string)chip.Tag == sound.Preset;
                building = false;
                saveTimer.Stop();
                saveTimer.Start();
            };
            sliders.Add(slider);
            values.Add(value);

            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            Grid.SetColumn(slider, 1);
            Grid.SetColumn(value, 2);
            row.Children.Add(label);
            row.Children.Add(slider);
            row.Children.Add(value);
            rows.Children.Add(row);
        }
        ShowGains();
        Unloaded += (_, _) =>
        {
            if (!saveTimer.IsEnabled) return;
            saveTimer.Stop();
            sound.Save();
        };
        return new StackPanel { Children = { presets, rows } };
    }

    // --- Übersetzung ---------------------------------------------------------------------------------------

    private WrapPanel _modelChips = new();
    private WrapPanel _chineseModelChips = new();
    private TextBlock _modelStatus = PageParts.Muted("", 13);
    private UIElement? _modelRows;

    /// <summary>Die zuletzt von Ollama gelieferten Modelle; null, solange nicht gefragt wurde.</summary>
    private IReadOnlyList<string>? _installed;

    private UIElement TranslationSettings()
    {
        var settings = _shell.Services.Settings;
        _modelChips = new WrapPanel();
        _chineseModelChips = new WrapPanel();
        _modelStatus = PageParts.Muted("", 13);

        var reload = PageParts.Compact(PageParts.Button("Modelle von Ollama laden", "GhostButton", async (_, _) => await LoadModelsAsync(), new Thickness(0)));
        AutomationProperties.SetName(reload, "Modelle von Ollama laden");

        // Ollama wird erst auf den Knopf hin gefragt, nicht beim Öffnen der Seite: ohne Übersetzung braucht die App keine Verbindung.
        _modelStatus.Text = settings.TranslationModel is null
            ? "Noch kein Modell gewählt. „Modelle von Ollama laden“ zeigt die installierten Modelle."
            : $"Gewählt: {settings.TranslationModel}" + (settings.TranslationModelChinese is { } chinese ? $", Chinesisch: {chinese}" : "") + ". „Modelle von Ollama laden“ zum Ändern.";

        _modelRows = new StackPanel
        {
            Visibility = _installed is not null ? Visibility.Visible : Visibility.Collapsed,
            Children =
            {
                PageParts.Divider(),
                PageParts.SettingRow("Modell für Übersetzungen", null, _modelChips, stacked: true),
                PageParts.Divider(),
                PageParts.SettingRow("Modell für Chinesisch", null, _chineseModelChips, stacked: true),
            },
        };

        var note = PageParts.Muted(
            "Übersetzt wird im Player über das Untertitelmenü („Untertitel übersetzen“). Ein Film dauert je nach Modell und Grafikkarte einige Minuten bis eine Stunde; " +
            "die Übersetzung läuft im Hintergrund und setzt nach einem Abbruch fort. Ollama muss laufen und das Modell geladen sein (ollama pull <Name>).", 13);
        note.Margin = new Thickness(4, 0, 0, 0);

        var group = PageParts.Group(
            PageParts.SettingRow("Ollama", $"Adresse: {settings.OllamaUrl}. Zu ändern in {AppSettings.FilePath} unter OllamaUrl.", reload),
            PageParts.SettingRow("Gewähltes Modell", null, _modelStatus, stacked: true));
        // Die Modellauswahl hängt in derselben Fläche, sobald Ollama Modelle geliefert hat.
        ((StackPanel)group.Child).Children.Add(_modelRows);
        if (_installed is not null) ShowModels(_installed);

        return new StackPanel { Children = { group, note } };
    }

    /// <summary>Füllt die Modellauswahl mit den in Ollama installierten Modellen; ist Ollama nicht erreichbar, steht der Grund da.</summary>
    private async Task LoadModelsAsync()
    {
        _modelStatus.Text = "Lade Modelle …";
        try
        {
            var models = await _shell.Services.Ollama().ListModelsAsync();
            _installed = models;
            ShowModels(models);
            _modelStatus.Text = models.Count == 0
                ? "Ollama läuft, aber es ist noch kein Modell installiert (ollama pull <Name>)."
                : $"{models.Count} Modell(e) installiert.";
        }
        catch (Exception ex) when (ex is OllamaException or ArgumentException)
        {
            _installed = null;
            _modelChips.Children.Clear();
            _chineseModelChips.Children.Clear();
            if (_modelRows is not null) _modelRows.Visibility = Visibility.Collapsed;
            _modelStatus.Text = ex.Message;
        }
    }

    private void ShowModels(IReadOnlyList<string> models)
    {
        var settings = _shell.Services.Settings;
        FillModels(_modelChips, "Übersetzungsmodell", models, settings.TranslationModel, includeSame: false, name => settings.TranslationModel = name);
        FillModels(_chineseModelChips, "Chinesisch-Modell", models, settings.TranslationModelChinese, includeSame: true, name => settings.TranslationModelChinese = name);
        if (_modelRows is not null) _modelRows.Visibility = Visibility.Visible;
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
            AutomationProperties.SetName(chip, $"{group} {text}");
            chip.Checked += (_, _) =>
            {
                apply(name);
                settings.Save();
            };
            panel.Children.Add(chip);
        }
    }

    // --- Tastenkürzel --------------------------------------------------------------------------------------

    // Muss zu den Tasten in MainWindow.Player.cs (OnPlayerKey), CardView und MainWindow.OnPreviewKeyDown passen.
    private static readonly (string Group, (string Keys, string Action)[] Items)[] HotkeyGroups =
    {
        ("Wiedergabe", new[]
        {
            ("Leertaste · K", "Wiedergabe / Pause"),
            ("Klick auf das Video", "Wiedergabe / Pause; ist ein Menü oder Einstellungsfenster offen, schließt der Klick nur dieses"),
            ("Doppelklick auf das Video", "Vollbild an / aus"),
            ("F · F11", "Vollbild an / aus (F11 auch außerhalb des Players: ganze App)"),
            ("P · Knopf „Bild-in-Bild“", "Schwebenden Player an / aus; bleibt über anderen Fenstern. Am Titel ziehen, am Fensterrand die Größe ändern"),
            ("Esc", "Bild-in-Bild oder Vollbild des Players verlassen, sonst Player schließen"),
            ("←  /  →", "10 Sekunden zurück / vor"),
            ("Umschalt + ←  /  →", "60 Sekunden zurück / vor"),
            ("↑  /  ↓", "Lautstärke um 5 erhöhen / senken"),
            ("M", "Ton aus / an"),
            ("N", "Nächste Folge (nur bei Serien)"),
            ("I", "Statistik (Hardware-Decoding, verworfene Bilder) ein- / ausblenden"),
        }),
        ("Medientasten (auch bei minimiertem Fenster)", new[]
        {
            ("Wiedergabe / Pause", "Video, Musik, Radio und Podcasts anhalten oder fortsetzen"),
            ("Weiter", "Nächste Folge, nächster Titel"),
            ("Zurück", "Zum Anfang; bei Musik nach den ersten Sekunden zum vorigen Titel"),
            ("Stopp", "Wiedergabe beenden"),
        }),
        ("Klang und Timer", new[]
        {
            ("Knopf „Klang“", "Equalizer-Voreinstellung wählen (Musikleiste und Videoplayer); die Regler stehen unter Einstellungen → Wiedergabe"),
            ("Knopf „Timer“ · Mond-Symbol", "Einschlaf-Timer: nach 15 bis 90 Minuten oder am Ende des Titels bzw. der Folge anhalten"),
            ("Jetzt läuft → Songtext", "Songtext zum laufenden Titel anzeigen (mitlaufend bei zeitgesteuerten Texten)"),
        }),
        ("Bild", new[]
        {
            ("A", "Nächstes Seitenverhältnis (Original, 4:3, 5:4, 16:9, 16:10, 21:9, 2,35:1, 1,85:1, Füllen, Strecken)"),
            ("Strg + ↑  /  ↓", "Tempo schneller / langsamer (0,25× bis 8×)"),
            ("Strg + R", "Tempo auf 1× zurücksetzen"),
            ("Strg + →  /  ←", "Ein Bild vor / zurück (pausiert)"),
            (".  /  ,", "Ein Bild vor / zurück (pausiert)"),
            ("+  /  −", "Zoom vergrößern / verkleinern (10-%-Schritte, 50 bis 400 %)"),
            ("0", "Zoom und Ausschnitt zurücksetzen"),
            ("Strg + Umschalt + Pfeiltasten", "Ausschnitt des gezoomten Bildes verschieben"),
            ("R  /  Umschalt + R", "Bild um 90° im / gegen den Uhrzeigersinn drehen"),
            ("Knopf „Bild“", "Alle Bildeinstellungen als Panel, mit Reglern für den Ausschnitt"),
            ("Knopf „Farbe“", "Helligkeit, Kontrast, Sättigung, Farbton, Gamma, Farbtemperatur, Tönung, Schärfe, Invertieren"),
        }),
        ("Untertitel", new[]
        {
            ("Z  /  X", "Untertitel 0,1 Sekunden früher / später"),
            ("Datei auf das Video ziehen", "Untertitel-Datei hinzufügen"),
            ("Knopf „Anpassen“", "Zeitversatz, Größe und Höhe der Untertitel"),
        }),
        ("Bibliothek", new[]
        {
            ("Esc", "Eine Seite zurück, bei einem offenen Fenster: Fenster schließen"),
            ("Enter · Leertaste", "Karte oder Zeile öffnen"),
            ("Rechtsklick · Menütaste · Umschalt + F10", "Menü einer Filmkarte („Meine Liste“, „Bearbeiten …“ öffnet den MKV-Editor) oder eines Titels in einer Musikliste (zu einer Playlist hinzufügen; in einer Playlist verschieben oder entfernen)"),
            ("Enter", "In den Suchfeldern von Radio und Podcasts: suchen"),
        }),
    };

    private static UIElement Hotkeys()
    {
        var panel = new StackPanel();
        var intro = PageParts.Muted("Gelten im Player und in der Bibliothek. Die Tasten lassen sich noch nicht ändern.", 13);
        intro.Margin = new Thickness(4, 0, 0, 0);
        panel.Children.Add(intro);

        foreach (var (group, items) in HotkeyGroups)
        {
            var label = new TextBlock { Text = group.ToUpperInvariant(), Margin = new Thickness(4, 24, 0, 10) };
            label.SetResourceReference(StyleProperty, "KickerText");
            panel.Children.Add(label);

            var rows = new List<UIElement>();
            foreach (var (keys, action) in items)
            {
                var row = new Grid { Margin = new Thickness(0, 11, 0, 11) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(310) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var cap = KeyCap(keys);
                var actionText = PageParts.Muted(action, 13);
                actionText.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(actionText, 1);
                row.Children.Add(cap);
                row.Children.Add(actionText);
                rows.Add(row);
            }
            panel.Children.Add(PageParts.Group(rows.ToArray()));
        }
        return panel;
    }

    /// <summary>Tastenangabe als kleine, abgesetzte Marke.</summary>
    private static UIElement KeyCap(string keys)
    {
        var text = PageParts.Semi(keys, 12.5);
        text.TextWrapping = TextWrapping.Wrap;
        text.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        var cap = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(10, 4, 10, 4), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0), Child = text };
        cap.SetResourceReference(Border.BackgroundProperty, "LineBrush");
        cap.SetResourceReference(Border.BorderBrushProperty, "GlassEdgeBrush");
        return cap;
    }

    // --- Über ----------------------------------------------------------------------------------------------

    private UIElement About()
    {
        var version = PageParts.Compact(PageParts.Button($"Version {AppInfo.Version}", "GhostButton", (_, _) => ChangelogPage.Show(_shell), new Thickness(0)));
        version.ToolTip = "Changelog anzeigen";
        AutomationProperties.SetName(version, "Version und Changelog");

        return new StackPanel
        {
            Children =
            {
                PageParts.Group(
                    PageParts.SettingRow("Mediacenter", "Ein Klick auf die Versionsnummer zeigt, was sich in welcher Version geändert hat.", version),
                    PageParts.SettingRow("Player", $"mpv: {_shell.Services.MpvPath}" + (File.Exists(_shell.Services.MpvPath) ? "" : "  (nicht gefunden)") +
                        "\nEinen anderen Pfad gibt die Umgebungsvariable MEDIACENTER_MPV vor.", null),
                    PageParts.SettingRow("Schriften", "Bebas Neue und Figtree, beide unter der SIL Open Font License (Lizenztexte im Ordner Fonts).", null)),
            },
        };
    }
}
