using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MediaCenter.App.Services;
using MediaCenter.App.Theming;
using MediaCenter.Core;

namespace MediaCenter.App.Pages;

/// <summary>Einstellungen in Reitern; jeder Reiter fasst zusammen, was zusammengehört (Darstellung, Bibliothek, Wiedergabe …).</summary>
public sealed class SettingsPage : UserControl
{
    private static readonly string[] Tabs = { "Darstellung", "Bibliothek", "Wiedergabe", "Profile", "Übersetzung", "Steuerung", "Tastenkürzel", "Über" };

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
            "Profile" => new ProfileSettingsPanel(_shell),
            "Übersetzung" => TranslationSettings(),
            "Steuerung" => Steering(),
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

        var watch = PageParts.Segmented("watch-folders",
            new[] { ("An", true), ("Aus", false) }.Select(o => (o.Item1, settings.WatchFolders == o.Item2, (Action)(() =>
            {
                settings.WatchFolders = o.Item2;
                settings.Save();
            }))),
            "Ordner überwachen");

        var upcoming = PageParts.Segmented("upcoming-episodes",
            new[] { ("An", true), ("Aus", false) }.Select(o => (o.Item1, settings.UpcomingEpisodes == o.Item2, (Action)(() =>
            {
                settings.UpcomingEpisodes = o.Item2;
                settings.Save();
            }))),
            "Kommende Folgen");

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

        var tmdbStatus = PageParts.Muted("", 13);
        void ShowTmdbStatus() => tmdbStatus.Text = _shell.Services.Tmdb is null
            ? "Kein TMDB-Schlüssel hinterlegt. Den „API-Schlüssel“ oder das „API Read Access Token“ gibt es kostenlos nach der Anmeldung auf themoviedb.org unter Einstellungen → API."
            : _shell.Services.TmdbKeyFromVariable
                ? $"Aus der Umgebungsvariable {AppServices.TmdbKeyVariable} (hat Vorrang vor einem hier gespeicherten)."
                : $"Gespeichert in {AppServices.TmdbKeyFile}.";
        ShowTmdbStatus();

        var (tmdbField, tmdbBox) = PageParts.HintBox("TMDB-Schlüssel", 240);
        AutomationProperties.SetName(tmdbBox, "TMDB-Schlüssel");
        void SaveTmdbKey(string? key)
        {
            try
            {
                _shell.Services.SetTmdbKey(key);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                _shell.ShowMessage(ex.Message, isError: true);
                return;
            }
            tmdbBox.Clear();
            ShowTmdbStatus();
        }
        var tmdbSave = PageParts.Compact(PageParts.Button("Speichern", "PrimaryButton", (_, _) => SaveTmdbKey(tmdbBox.Text), new Thickness(10, 0, 0, 0)));
        var tmdbRemove = PageParts.Compact(PageParts.Button("Entfernen", "GhostButton", (_, _) => SaveTmdbKey(null), new Thickness(8, 0, 0, 0)));
        AutomationProperties.SetName(tmdbSave, "TMDB-Schlüssel speichern");
        AutomationProperties.SetName(tmdbRemove, "TMDB-Schlüssel entfernen");
        var tmdbRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), Children = { tmdbField, tmdbSave, tmdbRemove } };

        var refresh = PageParts.Compact(PageParts.Button("Fehlende Cover suchen", "GhostButton", async (_, _) => await _shell.RefreshArtworkAsync(), new Thickness(0)));
        var rebuild = PageParts.Compact(PageParts.Button("Alle Cover neu erzeugen", "GhostButton", async (_, _) => await _shell.RefreshArtworkAsync(force: true), new Thickness(0)));

        var backup = PageParts.Compact(PageParts.Button("Sichern …", "GhostButton", async (_, _) =>
        {
            await BackupActions.CreateAsync(_shell);
            ShowTab("Bibliothek", animate: false);
        }, new Thickness(0)));
        var restore = PageParts.Compact(PageParts.Button("Wiederherstellen …", "GhostButton", (_, _) => BackupActions.Restore(_shell), new Thickness(0)));
        var duplicates = PageParts.Compact(PageParts.Button("Duplikate suchen", "GhostButton", (_, _) => _shell.Open(() => new DuplicatesPage(_shell)), new Thickness(0)));
        AutomationProperties.SetName(backup, "Sicherung erstellen");
        AutomationProperties.SetName(restore, "Sicherung wiederherstellen");
        AutomationProperties.SetName(duplicates, "Duplikate suchen");

        return new StackPanel
        {
            Children =
            {
                PageParts.Group(
                    PageParts.SettingRow("Cover aus dem Internet",
                        "Nur für Titel ohne eigenes Poster im Ordner oder in der Datei. Mit TMDB-Schlüssel (unten) kommt alles zuerst von TMDB; sonst und wo TMDB nichts findet kommen Serien von TVmaze (api.tvmaze.com) und Filme von OMDb (omdbapi.com); " +
                        "gesendet werden nur Titel und Jahr. Ein Treffer muss genau so heißen wie der Titel in der Bibliothek, sonst bleibt es ohne Poster; " +
                        "deutsche Filmtitel findet OMDb oft nicht. Album-Cover kommen von iTunes (itunes.apple.com), sonst von MusicBrainz und dem Cover Art Archive (musicbrainz.org, coverartarchive.org); gesendet werden Künstler und Albumtitel; " +
                        "Alben ohne Künstler im Tag werden nicht gesucht. Ein selbst gesetztes Poster bleibt immer. " +
                        "Von denselben Diensten (und mit denselben Treffer-Regeln) kommen auf die Seite eines Films oder einer Serie Handlung, Wertung, Besetzung und bei Serien die Folgentexte: von TMDB auf Deutsch, von OMDb und TVmaze auf Englisch.", online),
                    PageParts.SettingRow("TMDB-Schlüssel",
                        "Mit Schlüssel fragt die App zuerst TMDB (themoviedb.org): Poster, Hintergrundbilder, deutsche Handlung, Besetzung, Folgentexte und einen Trailer. " +
                        "Gesendet werden Titel und Jahr. TVmaze und OMDb springen ein, wo TMDB nichts findet. TMDB erlaubt die kostenlose Nutzung nur nicht-kommerziell.",
                        new StackPanel { Children = { tmdbStatus, tmdbRow } }, stacked: true),
                    PageParts.SettingRow("OMDb-Schlüssel", null, new StackPanel { Children = { keyStatus, keyRow } }, stacked: true)),
                PageParts.Group(
                    PageParts.SettingRow("Trakt",
                        "Gleicht „gesehen“ mit trakt.tv ab: Was dort als gesehen steht, gilt hier auch, und was hier gesehen ist und dort fehlt, wird eingetragen. " +
                        "Zugeordnet wird über Titel und Jahr; Titel, die bei Trakt anders heißen (deutsche Namen), kennt der Dienst nicht und die App meldet sie. " +
                        "Eigene Trakt-App nötig (kostenlos, trakt.tv/oauth/applications). Das Konto gehört zum Profil, das es verbindet; Kinderprofile gleichen nicht ab. " +
                        "Zugangsdaten liegen in trakt.json im Datenordner, nicht in der Sicherung.",
                        TraktControls(), stacked: true)),
                PageParts.Group(
                    PageParts.SettingRow("Kommende Folgen",
                        "Fragt TVmaze (api.tvmaze.com) nach den Sendeplänen deiner Serien; gesendet werden nur Titel und Jahr, laufende Serien halbtäglich. " +
                        "Der Kalender (Serien → Kalender) zeigt, was in den nächsten Wochen läuft; neue Folgen, die noch nicht in der Bibliothek liegen, meldet die App beim Start. " +
                        "Wo Dateinamen anders zählen als TVmaze (z. B. Anime durchnummeriert), kann eine Folge fälschlich als fehlend gelten.", upcoming)),
                PageParts.Group(
                    PageParts.SettingRow("Ordner überwachen",
                        "Neue, entfernte oder umbenannte Dateien in den Ordnern unter „Laufwerke“ nimmt die App nach einer kurzen Ruhepause (8 Sekunden ohne Änderung) selbst auf; " +
                        "beim Start und bei einem angeschlossenen Laufwerk gleicht sie alles einmal ab. Während ein Video läuft, wartet sie bis zum Ende. Gilt nach spätestens 30 Sekunden. " +
                        "Nur Ordner auf lokalen Laufwerken; einzelne Dateien und Netzlaufwerke beobachtet die App nicht.", watch)),
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
                    PageParts.SettingRow("Sichern",
                        "Schreibt Datenbank (Fortschritt, Playlists, „Meine Liste“, Sammlungen, eigene Namen, Infotexte, Senderlisten von Live-TV samt ihren Adressen, die Zugangsdaten enthalten können), Einstellungen, selbst gewählte Poster, gespeicherte Untertitel und LUTs in eine ZIP-Datei. " +
                        "Schlüssel und Zwischenspeicher (Vorschauen, Songtexte) sind nicht dabei. " + LastBackupText(settings), backup),
                    PageParts.SettingRow("Wiederherstellen",
                        "Spielt eine Sicherung ein. Der jetzige Stand wird nicht gelöscht, sondern in einen Ordner „vor-wiederherstellung-…“ im Datenordner verschoben; die App startet dafür neu.", restore)),
                PageParts.Group(
                    PageParts.SettingRow("Duplikate",
                        "Zeigt Filme und Folgen, die in mehreren Dateien liegen (mit dem Platz, den man gewänne), und gleichnamige Einträge. Es wird nichts gelöscht, ohne dass du es bestätigst.", duplicates)),
                PageParts.Group(
                    PageParts.SettingRow("Datenbank", Path.Combine(AppSettings.DataDirectory, "library.db"), null),
                    PageParts.SettingRow("Cover-Cache", _shell.Services.Model.Artwork.CacheDir, null),
                    PageParts.SettingRow("Einstellungen", AppSettings.FilePath, null)),
            },
        };
    }

    /// <summary>Zugangsdaten der Trakt-App, Verbinden, Abgleichen und Trennen.</summary>
    private UIElement TraktControls()
    {
        var trakt = _shell.Services.Trakt;
        var account = trakt.Account;
        var status = PageParts.Muted(account switch
        {
            null => "Nicht eingerichtet. Lege auf trakt.tv/oauth/applications eine App an (Redirect URI: urn:ietf:wg:oauth:2.0:oob), trage Client-ID und Client-Secret ein und verbinde dann das Konto.",
            { Tokens: null } => "Client-ID und Client-Secret sind gespeichert. Als Nächstes „Mit Trakt verbinden“.",
            _ => $"Verbunden{(account.Username is { } name ? $" als {name}" : "")}. " +
                 (trakt.BelongsToCurrentProfile ? "Der Abgleich läuft nach dem Start, nach jedem Player und mit „Jetzt abgleichen“." : "Das Konto gehört zu einem anderen Profil: in diesem Profil findet kein Abgleich statt."),
        }, 13);

        var (idField, idBox) = PageParts.HintBox("Client-ID", 240);
        var (secretField, secretBox) = PageParts.HintBox("Client-Secret", 240);
        AutomationProperties.SetName(idBox, "Trakt Client-ID");
        AutomationProperties.SetName(secretBox, "Trakt Client-Secret");
        var save = PageParts.Compact(PageParts.Button("Speichern", "PrimaryButton", (_, _) =>
        {
            try
            {
                trakt.SaveApp(idBox.Text, secretBox.Text);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                _shell.ShowMessage(ex.Message, isError: true);
                return;
            }
            ShowTab("Bibliothek", animate: false);
        }, new Thickness(10, 0, 0, 0)));
        AutomationProperties.SetName(save, "Trakt Zugangsdaten speichern");
        var fields = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), Children = { idField, new Border { Width = 10 }, secretField, save } };

        var connect = PageParts.Compact(PageParts.Button(trakt.IsConnected ? "Neu verbinden" : "Mit Trakt verbinden", "GhostButton", async (_, _) => await ConnectTraktAsync(), new Thickness(0, 0, 8, 0)));
        connect.IsEnabled = account is not null;
        var sync = PageParts.Compact(PageParts.Button("Jetzt abgleichen", "GhostButton", async (_, _) => await _shell.Services.SyncTraktAsync(_shell, automatic: false), new Thickness(0, 0, 8, 0)));
        sync.IsEnabled = trakt.IsConnected && trakt.BelongsToCurrentProfile;
        var disconnect = PageParts.Compact(PageParts.Button("Trennen", "GhostButton", (_, _) =>
        {
            trakt.Disconnect();
            ShowTab("Bibliothek", animate: false);
        }, new Thickness(0)));
        disconnect.IsEnabled = trakt.IsConnected;
        AutomationProperties.SetName(connect, "Mit Trakt verbinden");
        AutomationProperties.SetName(sync, "Trakt jetzt abgleichen");
        AutomationProperties.SetName(disconnect, "Trakt trennen");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), Children = { connect, sync, disconnect } };

        return new StackPanel { Children = { status, fields, buttons } };
    }

    /// <summary>Zeigt den Code zum Eingeben auf trakt.tv/activate und wartet auf die Bestätigung.</summary>
    private async Task ConnectTraktAsync()
    {
        var trakt = _shell.Services.Trakt;
        using var cts = new CancellationTokenSource();
        try
        {
            var code = await trakt.BeginConnectAsync(cts.Token);

            var codeText = PageParts.Semi(code.UserCode, 44);
            codeText.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            codeText.Margin = new Thickness(0, 6, 0, 18);
            var open = PageParts.Compact(PageParts.Button("trakt.tv/activate öffnen", "PrimaryButton", (_, _) => OpenTraktPage(code.VerificationUrl), new Thickness(0, 12, 0, 18)));
            AutomationProperties.SetName(open, "Trakt-Seite öffnen");
            var waiting = PageParts.Muted("Warte auf die Bestätigung … (das Fenster schließen bricht ab)", 13);
            var content = new UserControl
            {
                Content = new StackPanel
                {
                    Margin = new Thickness(0, 4, 0, 4),
                    Children =
                    {
                        PageParts.Muted("1. Öffne trakt.tv/activate und melde dich bei Trakt an.", 14), open,
                        PageParts.Muted("2. Gib diesen Code ein:", 14), codeText,
                        waiting,
                    },
                },
            };
            content.Unloaded += (_, _) => cts.Cancel();
            _shell.OpenModal("Trakt", "Mit Trakt verbinden", content, dismissOnScrim: false);

            var username = await trakt.CompleteConnectAsync(code, cts.Token);
            _shell.CloseModal();
            _shell.ShowMessage($"Mit Trakt verbunden{(username is null ? "" : $" als {username}")}.");
            ShowTab("Bibliothek", animate: false);
            await _shell.Services.SyncTraktAsync(_shell, automatic: false);
        }
        catch (OperationCanceledException)
        {
            // Der Nutzer hat das Fenster geschlossen: nichts zu melden.
        }
        catch (Exception ex) when (ex is TraktAuthException or System.Net.Http.HttpRequestException or InvalidOperationException or InvalidDataException or System.Text.Json.JsonException or IOException)
        {
            _shell.CloseModal();
            _shell.ShowMessage($"Verbinden mit Trakt fehlgeschlagen: {ex.Message}", isError: true);
        }
    }

    /// <summary>Öffnet die Aktivierungsseite im Browser; nur Adressen auf trakt.tv (der Link kommt aus einer Antwort des Dienstes).</summary>
    private void OpenTraktPage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !(uri.Host == "trakt.tv" || uri.Host.EndsWith(".trakt.tv", StringComparison.Ordinal)))
        {
            _shell.ShowMessage($"Die Adresse „{url}“ gehört nicht zu trakt.tv und wird nicht geöffnet. Öffne trakt.tv/activate selbst im Browser.", isError: true);
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private static string LastBackupText(AppSettings settings) =>
        settings.LastBackupUtc is { } when ? $"Letzte Sicherung: {when.ToLocalTime():dd.MM.yyyy HH:mm}." : "Es gibt noch keine Sicherung.";

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
        var crossfade = Crossfade.Options.Select(sec => (sec == 0 ? "Aus" : $"{sec} s", settings.AudioCrossfadeSeconds == sec, (Action)(() => settings.AudioCrossfadeSeconds = sec)));
        var hdr = new[] { ("Automatisch", HdrOutput.Auto), ("Immer SDR", HdrOutput.Sdr) }.Select(h => (h.Item1, settings.Hdr == h.Item2, (Action)(() => settings.Hdr = h.Item2)));
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
                    PageParts.SettingRow("HDR-Bild", HdrNote(), Choice("HDR-Bild", hdr)),
                    PageParts.SettingRow("Auf den Fernseher übertragen: HEVC",
                        "„Direkt senden“ schickt HEVC-Videos (auch HDR) unverändert an den Fernseher, wenn er sie abspielt; schont den PC. „Umwandeln“ macht per ffmpeg H.264 daraus, das jedes Cast-Gerät kann.",
                        Choice("HEVC beim Übertragen", castHevc))),
                note,
                PageParts.Group(
                    PageParts.SettingRow("OpenSubtitles: API-Schlüssel und Konto", SubtitleAccountNote(), SubtitleAccount(), stacked: true)),
                PageParts.Group(
                    PageParts.SettingRow("Untertitel-Aussehen",
                        "Schrift, Größe, Farben und Hintergrund für alle Videos; die Vorschau zeigt eine helle und eine dunkle Szene. Zeit, Maßstab und Höhe stellst du pro Video im Player ein (Knopf „Anpassen“).",
                        SubtitleLook(), stacked: true)),
                PageParts.Group(
                    PageParts.SettingRow("Musik: Überblenden",
                        "Der nächste Titel setzt ein, während der laufende ausklingt (Sinus-Überblendung, gleichbleibende Lautstärke). „Aus“ spielt lückenlos hintereinander. " +
                        "Gilt ab dem nächsten Titel; Titel unter dem Dreifachen der Länge sowie Radio und Podcasts werden nicht überblendet. Dafür läuft kurz vor dem Übergang ein zweiter Player (mpv).",
                        Choice("Überblenden", crossfade))),
                PageParts.Group(
                    PageParts.SettingRow("Equalizer", "Klang für Musik, Radio, Podcasts und Video; Änderungen sind sofort zu hören. Auch über den Knopf „Klang“ in der Musikleiste wählbar.", EqualizerPanel(), stacked: true)),
            },
        };
    }

    /// <summary>Erklärt die HDR-Wahl und sagt, was Windows für den Bildschirm dieses Fensters gerade meldet.</summary>
    private string HdrNote()
    {
        var handle = Application.Current.MainWindow is { } window ? new System.Windows.Interop.WindowInteropHelper(window).Handle : IntPtr.Zero;
        var state = DisplayHdr.IsActive(handle) switch
        {
            true => "Windows-HDR ist an diesem Bildschirm an.",
            false => "Windows-HDR ist an diesem Bildschirm aus (Einstellungen → System → Bildschirm → HDR verwenden); HDR-Videos werden deshalb umgerechnet.",
            null => "Ob Windows-HDR an ist, lässt sich nicht feststellen.",
        };
        return "„Automatisch“ gibt HDR-Videos (HDR10, HLG, Dolby Vision) als HDR an den Bildschirm, wenn Windows-HDR an ist; sonst rechnet der Player sie in normale Farben um. " +
            "„Immer SDR“ rechnet immer um. Mit LUT oder Farbfiltern (Weißabgleich, Schärfe, Invertieren) bleibt das Bild SDR. " + state;
    }

    // --- Untertitel-Aussehen -------------------------------------------------------------------------------

    private static readonly (string Name, string Hex)[] TextColors =
    {
        ("Weiß", "#FFFFFF"), ("Hellgrau", "#E6E6E6"), ("Gelb", "#FFE14D"), ("Orange", "#FFB347"), ("Grün", "#9BFF9B"), ("Cyan", "#7FE7FF"),
    };

    private static readonly (string Name, string Hex)[] OutlineColors =
    {
        ("Schwarz", "#000000"), ("Dunkelgrau", "#333333"), ("Dunkelblau", "#0A1F44"), ("Weiß", "#FFFFFF"),
    };

    private static readonly string[] OfferedFonts = { "Arial", "Segoe UI", "Verdana", "Georgia", "Consolas" };

    private UIElement SubtitleLook()
    {
        var settings = _shell.Services.Settings;
        var building = true; // Setzen von Reglern und Wahlfeldern beim Aufbau oder nach einer Vorlage ist keine Eingabe des Nutzers
        var refreshers = new List<Action>();
        var preview = new Controls.SubtitlePreview { HorizontalAlignment = HorizontalAlignment.Left };

        var saveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        saveTimer.Tick += (_, _) =>
        {
            saveTimer.Stop();
            settings.Save();
        };

        void Set(SubtitleStyle style)
        {
            settings.SubtitleStyle = style.Validated();
            preview.Look = settings.SubtitleStyle;
            saveTimer.Stop();
            saveTimer.Start();
        }

        void Show()
        {
            building = true;
            preview.Look = settings.SubtitleStyle;
            foreach (var refresh in refreshers) refresh();
            building = false;
        }

        UIElement Pick<T>(string name, IEnumerable<(string Text, T Value, Brush? Swatch)> items, Func<SubtitleStyle, T> read, Func<SubtitleStyle, T, SubtitleStyle> write)
        {
            var row = new WrapPanel();
            var chips = new List<RadioButton>();
            foreach (var (text, value, swatch) in items)
            {
                UIElement content = new TextBlock { Text = text };
                if (swatch is not null)
                {
                    var dot = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = swatch, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
                    dot.SetResourceReference(Border.BorderBrushProperty, "GlassEdgeBrush");
                    content = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, new TextBlock { Text = text } } };
                }
                var chip = new RadioButton { Content = content, GroupName = "sub-look-" + name, Tag = value };
                chip.SetResourceReference(StyleProperty, "Chip");
                AutomationProperties.SetName(chip, $"{name} {text}");
                chip.Checked += (_, _) =>
                {
                    if (!building) Set(write(settings.SubtitleStyle, value));
                };
                chips.Add(chip);
                row.Children.Add(chip);
            }
            refreshers.Add(() =>
            {
                foreach (var chip in chips) chip.IsChecked = Equals(chip.Tag, read(settings.SubtitleStyle));
            });
            return row;
        }

        UIElement Slide(string name, double min, double max, double step, Func<SubtitleStyle, double> read, Func<SubtitleStyle, double, SubtitleStyle> write, Func<double, string> format)
        {
            var slider = new Slider
            {
                Minimum = min, Maximum = max, TickFrequency = step, IsSnapToTickEnabled = true, SmallChange = step, LargeChange = step * 5,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0),
            };
            slider.SetResourceReference(StyleProperty, "SeekSlider");
            AutomationProperties.SetName(slider, "Untertitel " + name);
            var value = PageParts.Muted("", 13);
            value.VerticalAlignment = VerticalAlignment.Center;
            value.TextAlignment = TextAlignment.Right;
            slider.ValueChanged += (_, e) =>
            {
                value.Text = format(e.NewValue);
                if (!building) Set(write(settings.SubtitleStyle, e.NewValue));
            };
            refreshers.Add(() => slider.Value = read(settings.SubtitleStyle));

            var label = PageParts.Muted(name, 13);
            label.VerticalAlignment = VerticalAlignment.Center;
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2), Width = 520, HorizontalAlignment = HorizontalAlignment.Left };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            Grid.SetColumn(slider, 1);
            Grid.SetColumn(value, 2);
            grid.Children.Add(label);
            grid.Children.Add(slider);
            grid.Children.Add(value);
            return grid;
        }

        Brush Swatch(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

        IEnumerable<(string, string, Brush?)> Colors((string Name, string Hex)[] offered, string current) =>
            offered.Concat(offered.Any(c => c.Hex.Equals(current, StringComparison.OrdinalIgnoreCase)) ? Array.Empty<(string, string)>() : new[] { ("Eigene", current) })
                .Select(c => (c.Item1, c.Item2.ToUpperInvariant(), (Brush?)Swatch(c.Item2)));

        var fonts = new (string, string?, Brush?)[] { ("Standard", null, null) }
            .Concat(OfferedFonts.Concat(settings.SubtitleStyle.Font is { } own && !OfferedFonts.Contains(own) ? new[] { own } : Array.Empty<string>())
                .Select(f => (f, (string?)f, (Brush?)null)));

        var presets = new WrapPanel { Margin = new Thickness(0, 14, 0, 6) };
        foreach (var (name, style) in SubtitleStyle.Presets)
        {
            var preset = style;
            var button = PageParts.Compact(PageParts.Button(name, "GhostButton", (_, _) =>
            {
                Set(preset);
                Show();
            }, new Thickness(0, 0, 8, 8)));
            AutomationProperties.SetName(button, "Untertitel-Vorlage " + name);
            presets.Children.Add(button);
        }

        TextBlock Label(string text)
        {
            var block = PageParts.Muted(text, 13);
            block.Margin = new Thickness(0, 10, 0, 6);
            return block;
        }

        var panel = new StackPanel
        {
            Children =
            {
                preview,
                presets,
                Slide("Größe", SubtitleStyle.MinFontSize, SubtitleStyle.MaxFontSize, 1, s => s.FontSize, (s, v) => s with { FontSize = v }, v => $"{v:0}"),
                Slide("Rand", 0, SubtitleStyle.MaxOutlineSize, 0.1, s => s.OutlineSize, (s, v) => s with { OutlineSize = Math.Round(v, 1) }, v => $"{v:0.0}"),
                Slide("Schatten", 0, SubtitleStyle.MaxShadowOffset, 0.5, s => s.ShadowOffset, (s, v) => s with { ShadowOffset = v }, v => $"{v:0.0}"),
                Slide("Hintergrund", 0, 100, 5, s => s.BackgroundOpacity, (s, v) => s with { BackgroundOpacity = (int)v }, v => $"{v:0} %"),
                Label("Schriftfarbe"),
                Pick("Schriftfarbe", Colors(TextColors, settings.SubtitleStyle.TextColor), s => s.TextColor.ToUpperInvariant(), (s, v) => s with { TextColor = v }),
                Label("Randfarbe"),
                Pick("Randfarbe", Colors(OutlineColors, settings.SubtitleStyle.OutlineColor), s => s.OutlineColor.ToUpperInvariant(), (s, v) => s with { OutlineColor = v }),
                Label("Hintergrund"),
                Pick("Hintergrund", new (string, SubtitleBackground, Brush?)[] { ("Rand und Schatten", SubtitleBackground.Outline, null), ("Kasten hinter dem Text", SubtitleBackground.Box, null) },
                    s => s.Background, (s, v) => s with { Background = v }),
                Label("Schrift"),
                Pick("Schrift", fonts, s => s.Font, (s, v) => s with { Font = v }),
                Label("Fettdruck"),
                Pick("Fettdruck", new (string, bool, Brush?)[] { ("Aus", false, null), ("An", true, null) }, s => s.Bold, (s, v) => s with { Bold = v }),
                Label("Eigene Stile von Untertiteln (ASS/SSA, oft bei Anime)"),
                Pick("Eigene Stile", new (string, bool, Brush?)[] { ("Behalten", false, null), ("Durch dieses Aussehen ersetzen", true, null) }, s => s.ForceStyle, (s, v) => s with { ForceStyle = v }),
            },
        };

        Show();
        Unloaded += (_, _) =>
        {
            if (!saveTimer.IsEnabled) return;
            saveTimer.Stop();
            settings.Save();
        };
        return panel;
    }

    // --- Untertitel aus dem Internet -----------------------------------------------------------------------

    private TextBlock _accountStatus = PageParts.Muted("", 13);

    private static string SubtitleAccountNote() =>
        "Im Player: Untertitel → „Online suchen …“. Gesucht wird bei OpenSubtitles (opensubtitles.com); gesendet werden Titel, Jahr, Staffel, Folge, Sprache und eine Prüfsumme der Datei, nie Pfade. " +
        "Die Suche braucht einen kostenlosen API-Schlüssel (opensubtitles.com → Konto → API-Consumers), der Download zusätzlich Benutzername und Passwort des Kontos; ein Gratiskonto hat ein Tageslimit. " +
        "Die Angaben liegen mit deinem Windows-Konto verschlüsselt im Datenordner. Umgebungsvariablen haben Vorrang: " +
        $"{OpenSubtitlesAccount.KeyVariable}, {OpenSubtitlesAccount.UserVariable}, {OpenSubtitlesAccount.PasswordVariable}.";

    private UIElement SubtitleAccount()
    {
        _accountStatus = PageParts.Muted(OpenSubtitlesAccount.Describe(), 13);
        var (keyField, keyBox) = PageParts.HintBox("API-Schlüssel", 230);
        var (userField, userBox) = PageParts.HintBox("Benutzername", 190);
        var password = new PasswordBox { Width = 190 };
        AutomationProperties.SetName(keyBox, "OpenSubtitles API-Schlüssel");
        AutomationProperties.SetName(userBox, "OpenSubtitles Benutzername");
        AutomationProperties.SetName(password, "OpenSubtitles Passwort");

        var save = PageParts.Compact(PageParts.Button("Speichern", "PrimaryButton", (_, _) =>
        {
            try
            {
                OpenSubtitlesAccount.Save(keyBox.Text, userBox.Text, password.Password);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _shell.ShowMessage(ex.Message, isError: true);
                return;
            }
            keyBox.Clear();
            userBox.Clear();
            password.Clear();
            _accountStatus.Text = OpenSubtitlesAccount.Describe();
        }, new Thickness(10, 0, 0, 0)));
        var remove = PageParts.Compact(PageParts.Button("Entfernen", "GhostButton", (_, _) =>
        {
            try
            {
                OpenSubtitlesAccount.Clear();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _shell.ShowMessage(ex.Message, isError: true);
                return;
            }
            _accountStatus.Text = OpenSubtitlesAccount.Describe();
        }, new Thickness(8, 0, 0, 0)));
        AutomationProperties.SetName(save, "OpenSubtitles-Zugang speichern");
        AutomationProperties.SetName(remove, "OpenSubtitles-Zugang entfernen");
        keyField.Margin = new Thickness(0, 0, 8, 0);
        userField.Margin = new Thickness(0, 0, 8, 0);

        var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 0), Children = { keyField, userField, password, save, remove } };
        return new StackPanel { Children = { _accountStatus, row } };
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

    // --- Steuerung: Controller und Tasten -------------------------------------------------------------------

    private UIElement Steering()
    {
        var settings = _shell.Services.Settings;
        var controller = _shell.Services.Controller;
        var panel = new StackPanel();
        panel.Children.Add(HomeServerRow());

        // Controller: angeschlossene Geräte und eine Anzeige, was gerade gedrückt wird (zum Prüfen der Belegung)
        var devices = PageParts.Muted("", 13);
        var live = PageParts.Muted("", 13);
        live.Margin = new Thickness(0, 10, 0, 0);
        var family = ControllerFamily.Xbox;
        void Refresh()
        {
            var list = controller.Controllers;
            family = list.FirstOrDefault(c => c.Supported)?.Family ?? ControllerFamily.Xbox;
            devices.Text = list.Count == 0
                ? "Kein Controller gefunden. Xbox-Controller und PlayStation-Controller (DualSense, DualShock 4) werden per USB oder Bluetooth erkannt. Die App reagiert nur, solange ihr Fenster vorn liegt."
                : string.Join("\n", list.Select(c => c.Supported
                    ? $"• {c.Name} ({(c.Family == ControllerFamily.PlayStation ? "PlayStation" : "Xbox")}) – bereit"
                    : $"• {c.Name} ({c.VendorId:X4}:{c.ProductId:X4}) – nicht unterstützt: {c.Note}"));
            if (controller.Latest is not { } snapshot)
            {
                live.Text = controller.RawDump is { } dump ? $"Rohwerte des unbekannten Controllers: {dump}" : "";
                return;
            }
            var pressed = snapshot.WithStickAsDpad().Buttons;
            live.Text = pressed.Count == 0
                ? "Test: Drücke eine Taste am Controller – sie erscheint hier."
                : "Gedrückt: " + string.Join(", ", Enum.GetValues<ControllerButton>().Where(pressed.Contains).Select(b => ControllerLayout.Label(b, family)));
        }
        Refresh();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => Refresh();
        panel.Loaded += (_, _) => timer.Start();
        panel.Unloaded += (_, _) => timer.Stop();

        panel.Children.Add(PageParts.Group(PageParts.SettingRow("Controller",
            "Im Player und in der Bibliothek lässt sich die App mit einem Controller bedienen. In der Bibliothek bewegen Kreuz und linker Stick die Auswahl, " +
            "Kreuz (A) öffnet, Kreis (B) geht zurück, Quadrat (X) öffnet das Menü einer Karte, Dreieck (Y) die Suche, L1/R1 wechseln den Reiter, L2/R2 blättern, Options das Vollbild.",
            new StackPanel { Children = { devices, live } }, stacked: true)));

        // Belegung im Player
        var mapRows = new List<UIElement>();
        foreach (var (button, effect) in ControllerPlayerMap.Overview)
        {
            var row = new Grid { Margin = new Thickness(0, 11, 0, 11) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(310) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var playStation = ControllerLayout.Label(button, ControllerFamily.PlayStation);
            var xbox = ControllerLayout.Label(button, ControllerFamily.Xbox);
            var cap = KeyCap(playStation == xbox ? playStation : $"{playStation}  ·  {xbox}");
            var text = PageParts.Muted(effect, 13);
            text.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(text, 1);
            row.Children.Add(cap);
            row.Children.Add(text);
            mapRows.Add(row);
        }
        var mapLabel = new TextBlock { Text = "CONTROLLER IM PLAYER (PLAYSTATION · XBOX)", Margin = new Thickness(4, 24, 0, 10) };
        mapLabel.SetResourceReference(StyleProperty, "KickerText");
        panel.Children.Add(mapLabel);
        panel.Children.Add(PageParts.Group(mapRows.ToArray()));

        // Tasten des Players ändern
        var keyLabel = new TextBlock { Text = "TASTEN IM PLAYER", Margin = new Thickness(4, 24, 0, 10) };
        keyLabel.SetResourceReference(StyleProperty, "KickerText");
        panel.Children.Add(keyLabel);
        var keyIntro = PageParts.Muted("„Ändern“ wählen und die neue Taste drücken (Esc bricht ab). Eine Taste je Aktion; Strg-Kombinationen (Einzelbild, Tempo, Ausschnitt) und die Umschalt-Varianten bleiben fest.", 13);
        keyIntro.Margin = new Thickness(4, 0, 0, 10);
        panel.Children.Add(keyIntro);

        var keyHost = new StackPanel();
        PlayerAction? capturing = null;
        void BuildKeyRows()
        {
            keyHost.Children.Clear();
            Button? captureButton = null;
            var rows = new List<UIElement>();
            foreach (var action in Enum.GetValues<PlayerAction>())
            {
                var row = new Grid { Margin = new Thickness(0, 8, 0, 8) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(310) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var keys = PlayerKeys.KeysOf(action, settings.KeyBindings);
                var cap = KeyCap(capturing == action ? "Taste drücken …" : string.Join("  ·  ", keys.Select(KeyName)));
                var label = PageParts.Muted(PlayerKeys.Label(action), 13);
                label.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(label, 1);
                var change = PageParts.Compact(PageParts.Button(capturing == action ? "Abbrechen" : "Ändern", "GhostButton", (_, _) =>
                {
                    capturing = capturing == action ? null : action;
                    BuildKeyRows();
                }, new Thickness(0, 0, 8, 0)));
                AutomationProperties.SetName(change, $"Taste ändern {PlayerKeys.Label(action)}");
                if (capturing == action) captureButton = change;
                var reset = PageParts.Compact(PageParts.Button("Standard", "GhostButton", (_, _) =>
                {
                    settings.KeyBindings = PlayerKeys.Reset(settings.KeyBindings, action, out var conflict);
                    if (conflict is { } other) _shell.ShowMessage($"Die Standardtaste gehört jetzt zu „{PlayerKeys.Label(other)}“. Ändere zuerst jene Aktion.", isError: true);
                    else settings.Save();
                    capturing = null;
                    BuildKeyRows();
                }, new Thickness(0)));
                reset.IsEnabled = settings.KeyBindings.ContainsKey(action.ToString());
                AutomationProperties.SetName(reset, $"Standardtaste {PlayerKeys.Label(action)}");
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { change, reset } };
                Grid.SetColumn(buttons, 2);
                row.Children.Add(cap);
                row.Children.Add(label);
                row.Children.Add(buttons);
                rows.Add(row);
            }
            keyHost.Children.Add(PageParts.Group(rows.ToArray()));

            // Der Knopf, den der Nutzer gedrückt hat, wurde mit den Zeilen neu gebaut: der Fokus muss wieder hinein, sonst kommt die nächste Taste nicht an.
            if (captureButton is { } focusTarget)
                Dispatcher.BeginInvoke(() => focusTarget.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }
        BuildKeyRows();
        panel.Children.Add(keyHost);

        var resetAll = PageParts.Compact(PageParts.Button("Alle Tasten zurücksetzen", "GhostButton", (_, _) =>
        {
            settings.KeyBindings = new Dictionary<string, string[]>();
            settings.Save();
            capturing = null;
            BuildKeyRows();
        }, new Thickness(0)));
        AutomationProperties.SetName(resetAll, "Alle Tasten zurücksetzen");
        panel.Children.Add(resetAll);

        panel.PreviewKeyDown += (_, e) =>
        {
            if (capturing is not { } action) return;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            e.Handled = true;
            if (key == Key.Escape)
            {
                capturing = null;
                BuildKeyRows();
                return;
            }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.None) return; // erst die eigentliche Taste
            if (Keyboard.Modifiers != ModifierKeys.None)
            {
                _shell.ShowMessage("Bitte die Taste allein drücken, ohne Strg, Umschalt oder Alt.", isError: true);
                return;
            }
            if (key is Key.Tab or Key.Apps)
            {
                _shell.ShowMessage($"Die Taste {KeyName(key.ToString())} ist für die Bedienung der Oberfläche reserviert.", isError: true);
                return;
            }

            var updated = PlayerKeys.Assign(settings.KeyBindings, action, key.ToString(), out var conflict);
            if (conflict is { } other)
            {
                _shell.ShowMessage($"{KeyName(key.ToString())} gehört schon zu „{PlayerKeys.Label(other)}“. Wähle eine andere Taste oder ändere zuerst jene Aktion.", isError: true);
                return;
            }
            settings.KeyBindings = updated;
            settings.Save();
            capturing = null;
            BuildKeyRows();
        };
        return panel;
    }

    /// <summary>Der Heimnetz-Server: ein- und ausschalten, Reichweite, Anschluss, Adresse, PIN und angemeldete Geräte.</summary>
    private UIElement HomeServerRow()
    {
        var services = _shell.Services;
        var settings = services.Settings;

        async Task Apply()
        {
            settings.Save();
            if (await services.ApplyHomeServerAsync() is { } problem) _shell.ShowMessage(problem, isError: true);
            ShowTab("Steuerung", animate: false);
        }

        var onOff = PageParts.Segmented("home-server",
            new[] { ("Aus", false), ("An", true) }.Select(o => (o.Item1, settings.HomeServer == o.Item2, (Action)(async () =>
            {
                if (settings.HomeServer == o.Item2) return;
                settings.HomeServer = o.Item2;
                await Apply();
            }))),
            "Heimnetz-Server");
        var reach = PageParts.Segmented("home-server-reach",
            new[] { ("Im Heimnetz", true), ("Nur dieser PC", false) }.Select(o => (o.Item1, settings.HomeServerLan == o.Item2, (Action)(async () =>
            {
                if (settings.HomeServerLan == o.Item2) return;
                settings.HomeServerLan = o.Item2;
                await Apply();
            }))),
            "Erreichbar");
        reach.Margin = new Thickness(0, 12, 0, 0);

        var (portField, portBox) = PageParts.HintBox("Anschluss", 120);
        portBox.Text = settings.HomeServerPort.ToString();
        AutomationProperties.SetName(portBox, "Anschluss des Heimnetz-Servers");
        var portApply = PageParts.Compact(PageParts.Button("Übernehmen", "GhostButton", async (_, _) =>
        {
            if (!int.TryParse(portBox.Text.Trim(), out var port) || port is < 1024 or > 65535)
            {
                _shell.ShowMessage("Der Anschluss muss eine Zahl von 1024 bis 65535 sein.", isError: true);
                return;
            }
            settings.HomeServerPort = port;
            await Apply();
        }, new Thickness(10, 0, 0, 0)));
        AutomationProperties.SetName(portApply, "Anschluss übernehmen");
        var portRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), Children = { portField, portApply } };

        var status = PageParts.Muted("", 13);
        status.Margin = new Thickness(0, 14, 0, 0);
        var remote = services.Remote;
        if (!settings.HomeServer) status.Text = "Der Server ist aus.";
        else if (remote is null || !remote.Running) status.Text = "Der Server läuft nicht (siehe Meldung beim Start).";
        else
        {
            var addresses = remote.Addresses();
            status.Text = (addresses.Count == 0 ? "Läuft, aber kein Heimnetz gefunden (kein Netzwerkanschluss?)." : "Adresse im Browser des Handys: " + string.Join("   oder   ", addresses)) +
                          $"{'\n'}PIN: {services.RemoteAccess.Pin}   ·   {services.RemoteAccess.DeviceCount} Gerät{(services.RemoteAccess.DeviceCount == 1 ? "" : "e")} angemeldet";
        }

        var revoke = PageParts.Compact(PageParts.Button("Alle Geräte abmelden (neue PIN)", "GhostButton", (_, _) =>
        {
            services.RemoteAccess.RevokeAll();
            ShowTab("Steuerung", animate: false);
        }, new Thickness(0)));
        AutomationProperties.SetName(revoke, "Alle Geräte abmelden");
        revoke.Margin = new Thickness(0, 12, 0, 0);

        var controls = new StackPanel { Children = { onOff, reach, portRow, status, revoke } };
        return PageParts.Group(PageParts.SettingRow("Heimnetz-Server",
            "Weboberfläche und Fernbedienung fürs Handy: Im Browser Filme und Serien durchsuchen, auf dem PC abspielen oder im Handy ansehen (MP4 unverändert, sonst von ffmpeg umgewandelt), " +
            "und Player und Bibliothek der App wie mit einem Controller bedienen. Zugang nur mit der PIN unten; die Verbindung ist unverschlüsselt (HTTP) und gehört ins eigene Heimnetz, nicht ins Internet. " +
            "Der Server zeigt die Bibliothek des gerade gewählten Profils. Beim ersten Einschalten fragt die Windows-Firewall, ob MediaCenter im privaten Netz erreichbar sein darf.",
            controls, stacked: true));
    }

    /// <summary>Anzeigename einer Taste: „Space“ → „Leertaste“, „OemComma“ → „,“.</summary>
    private static string KeyName(string key) => key switch
    {
        "Space" => "Leertaste",
        "Escape" => "Esc",
        "Return" => "Enter",
        "OemComma" => ",",
        "OemPeriod" => ".",
        "OemPlus" => "+",
        "OemMinus" => "−",
        "Add" => "+ (Ziffernblock)",
        "Subtract" => "− (Ziffernblock)",
        "Left" => "←",
        "Right" => "→",
        "Up" => "↑",
        "Down" => "↓",
        "PageUp" => "Bild auf",
        "PageDown" => "Bild ab",
        "D0" => "0",
        "NumPad0" => "0 (Ziffernblock)",
        _ => key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1]) ? key[1..] : key,
    };

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
            ("S", "Intro überspringen: ans Ende des Vorspann-Kapitels, sonst 85 Sekunden vor"),
            ("Bild auf  /  Bild ab", "Voriges / nächstes Kapitel (bei Dateien mit Kapiteln)"),
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
        var intro = PageParts.Muted("Gelten im Player und in der Bibliothek; hier die Standardtasten. Die Tasten des Players lassen sich im Reiter „Steuerung“ ändern, dort steht auch die Belegung für Controller.", 13);
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
                    PageParts.SettingRow("Player", $"mpv: {ToolLocator.Mpv().Describe()}" +
                        "\nEinen anderen Pfad gibt die Umgebungsvariable MEDIACENTER_MPV vor.", null),
                    PageParts.SettingRow("Werkzeuge", $"ffmpeg: {ToolLocator.Ffmpeg().Describe()}\nffprobe: {ToolLocator.Ffprobe().Describe()}" +
                        "\nOhne sie gibt es keine Scans, Cover, Vorschauen und keinen Mkv-Editor. mpv, ffmpeg und ffprobe werden auch neben der Anwendung und im Ordner tools gefunden; andere Pfade geben MEDIACENTER_MPV, MEDIACENTER_FFMPEG und MEDIACENTER_FFPROBE vor.", null),
                    PageParts.SettingRow("Datenquellen", "Dieses Produkt nutzt die TMDB-API, ist aber nicht von TMDB unterstützt oder zertifiziert (themoviedb.org). " +
                        "Weitere Quellen: TVmaze (tvmaze.com), OMDb (omdbapi.com), iTunes, MusicBrainz und Cover Art Archive, LRCLIB (lrclib.net), radio-browser.info.", null),
                    PageParts.SettingRow("Schriften", "Bebas Neue und Figtree, beide unter der SIL Open Font License (Lizenztexte im Ordner Fonts).", null)),
            },
        };
    }
}
