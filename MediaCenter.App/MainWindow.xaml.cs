using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using MediaCenter.App.Controls;
using MediaCenter.App.Pages;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App;

public partial class MainWindow : Window, IShell
{
    private sealed record PageEntry(Func<UserControl> Factory, Section? Section);

    private readonly Stack<PageEntry> _history = new();
    private readonly Dictionary<Section, RadioButton> _navButtons = new();
    private PageEntry? _current;
    private bool _syncingNav;

    public MainWindow(AppServices services)
    {
        Services = services;
        InitializeComponent();

        // Die Vorgabegröße aus dem XAML ist größer als manche Bildschirme (Skalierung 125 %); an den Arbeitsbereich anpassen.
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width * 0.92);
        Height = Math.Min(Height, area.Height * 0.92);

        GlassPanel.Backdrop = Scene;
        GlassPanel.Overlay = Stage;
        CardView.Previews = Services.Previews;
        // Ein defektes ffmpeg oder eine kaputte Datei würde bei jedem Überfahren dieselbe Meldung auslösen; jede nur einmal zeigen.
        var reported = new HashSet<string>();
        Services.Previews.Failed += message =>
        {
            if (reported.Add(message)) ShowMessage(message, isError: true);
        };
        Modal.Blocked = reason => ShowMessage(reason, isError: true);
        BuildNav();
        VersionButton.Content = "v" + AppInfo.Version;
        VersionButton.Click += (_, _) => ChangelogPage.Show(this);

        Services.Theme.Changed += () => Services.Theme.StyleTitleBar(this);
        SourceInitialized += (_, _) => Services.Theme.StyleTitleBar(this);
        SizeChanged += (_, _) => BackdropArt.Height = ActualHeight * 0.66;
        PreviewKeyDown += OnPreviewKeyDown;
        FullscreenNavButton.Click += (_, _) => ToggleFullscreen();
        SearchNavButton.Click += (_, _) => OpenSearch();
        ProfileNavButton.Click += (_, _) => ProfilePickerPage.Show(this, startup: false);
        Services.Profiles.Changed += OnProfileChanged;
        Services.Profiles.Edited += ApplyProfile;
        Loaded += (_, _) =>
        {
            Services.Model.Reload();
            ApplyProfile();
            UpdateInboxBadge();
            Navigate(Section.Home);
            if (Services.Profiles.ShouldAskAtStart) ProfilePickerPage.Show(this, startup: true);
            if (Services.RestoredPreviousState is { } previous)
                ShowMessage($"Sicherung wiederhergestellt. Der bisherige Stand liegt in {previous}.");
            if (Services.Settings.StartFullscreen) SetFullscreen(true);
            CheckRendering();
            _watch = new LibraryWatch(this, () => PlayerActive || Modal.IsOpen || Dialog.IsOpen); // nicht unter einem Fenster oder Video die Seite neu bauen
            _watch.Start();
            InitController();
            InitSchedule();
            _ = StartHomeServerAsync();
        };
        InitPlayer();
        SourceInitialized += (_, _) => StartMediaSession();
        Mini.Attach(Services.Audio, Services, () =>
        {
            SettingsPage.SelectTab("Wiedergabe");
            Navigate(Section.Settings);
        });
        Mini.IsVisibleChanged += (_, _) => UpdateBottomInset();
        Services.Audio.Error += message => ShowMessage(message, isError: true);
    }

    /// <summary>Lässt unten Platz für die Audio-Leiste, damit sie keine Inhalte verdeckt.</summary>
    private void UpdateBottomInset()
    {
        var inset = Mini.IsVisible ? 72 : 0;
        PageHost.Margin = new Thickness(0, 0, 0, inset);
        Toasts.Margin = new Thickness(0, 0, 0, 26 + inset);
    }

    public AppServices Services { get; }

    private LibraryWatch? _watch;

    // --- Navigation ----------------------------------------------------------------------------------------

    // Symbolzeichen der Segoe-Symbolschrift: Laufwerk, Glocke, Zahnrad
    private const string DriveGlyph = "\uEDA2";
    private const string InboxGlyph = "\uEA8F";
    private const string SettingsGlyph = "\uE713";
    private const string HistoryGlyph = "";

    private void BuildNav()
    {
        var items = new (Section Section, string Label, Panel Host, string? Glyph)[]
        {
            (Section.Home, "Start", NavItems, null), (Section.Movies, "Filme", NavItems, null), (Section.Series, "Serien", NavItems, null),
            (Section.Music, "Musik", NavItems, null), (Section.Radio, "Radio", NavItems, null), (Section.Live, "Live-TV", NavItems, null), (Section.Podcasts, "Podcasts", NavItems, null),
            (Section.Sources, "Laufwerke", UtilItems, DriveGlyph), (Section.Inbox, "Inbox", UtilItems, InboxGlyph), (Section.History, "Verlauf und Statistik", UtilItems, HistoryGlyph), (Section.Settings, "Einstellungen", UtilItems, SettingsGlyph),
        };
        foreach (var (section, label, host, glyph) in items)
        {
            var button = new RadioButton { Content = glyph ?? label, GroupName = "nav", Height = 60 };
            button.SetResourceReference(StyleProperty, glyph is null ? "StripToggle" : "StripIcon");
            if (glyph is not null) button.ToolTip = label;
            System.Windows.Automation.AutomationProperties.SetName(button, label);
            // Checked statt Click: auch die Pfeiltasten in der Gruppe wählen eine Seite.
            button.Checked += (_, _) =>
            {
                if (!_syncingNav) Navigate(section);
            };
            _navButtons[section] = button;
            host.Children.Add(button);
        }
        Logo.MouseLeftButtonUp += (_, _) => Navigate(Section.Home);
    }

    /// <summary>Die Inbox zeigt ihre Anzahl als kleine Marke an der Glocke, nicht als Klammer im Text.</summary>
    private void UpdateInboxBadge()
    {
        var count = Services.Model.Inbox.Count;
        if (count == 0)
        {
            _navButtons[Section.Inbox].Content = InboxGlyph;
            return;
        }
        var number = new TextBlock { Text = count > 99 ? "99+" : count.ToString(), FontSize = 10, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        number.SetResourceReference(TextBlock.FontFamilyProperty, "BodySemiFont");
        number.SetResourceReference(TextBlock.ForegroundProperty, "OnAccentBrush");
        var badge = new Border
        {
            CornerRadius = new CornerRadius(8),
            MinWidth = 16,
            Height = 16,
            Padding = new Thickness(4, 0, 4, 0),
            Margin = new Thickness(0, -9, -10, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Child = number,
        };
        badge.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        _navButtons[Section.Inbox].Content = new Grid { Children = { new TextBlock { Text = InboxGlyph }, badge } };
    }

    /// <summary>Was ein Kinderprofil öffnen darf: Start, Filme und Serien.</summary>
    private static bool KidsAllowed(Section section) => section is Section.Home or Section.Movies or Section.Series;

    private void OnProfileChanged()
    {
        _ = Services.Audio.StopAsync(); // die Musik des einen Profils läuft nicht im anderen weiter
        Services.Model.Reload();
        ApplyProfile();
        Navigate(Section.Home);
    }

    /// <summary>Profil-Knopf (Farbkreis mit Anfangsbuchstaben) und, im Kinderprofil, nur die erlaubten Reiter.</summary>
    private void ApplyProfile()
    {
        var profile = Services.Profiles.Current;
        ProfileNavButton.Content = ProfileAvatar.Create(profile, 28);
        ProfileNavButton.ToolTip = $"Profil: {profile.Name} (Klick zum Wechseln)";
        System.Windows.Automation.AutomationProperties.SetName(ProfileNavButton, "Profil wechseln");
        foreach (var (section, button) in _navButtons) button.Visibility = profile.Kids && !KidsAllowed(section) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Navigate(Section section)
    {
        if (Services.Profiles.Current.Kids && !KidsAllowed(section))
        {
            ShowMessage("Im Kinderprofil nicht verfügbar.", isError: true);
            return;
        }
        if (Modal.IsOpen) Modal.Close();
        _history.Clear();
        Show(new PageEntry(() => section switch
        {
            Section.Home => new HomePage(this),
            Section.Movies => new LibraryPage(this, TitleKind.Movie),
            Section.Series => new LibraryPage(this, TitleKind.Series),
            Section.Music => new MusicPage(this),
            Section.Radio => new RadioPage(this),
            Section.Live => new LiveTvPage(this),
            Section.Podcasts => new PodcastsPage(this),
            Section.Inbox => new InboxPage(this),
            Section.History => new HistoryPage(this),
            Section.Sources => new SourcesPage(this),
            Section.Settings => new SettingsPage(this),
            _ => throw new ArgumentOutOfRangeException(nameof(section), section, null),
        }, section));
    }

    public void OpenTitle(long titleId)
    {
        if (_current is not null) _history.Push(_current);
        Show(new PageEntry(() => new DetailPage(this, titleId), null));
    }

    public void OpenSearch(string? query = null)
    {
        if (Modal.IsOpen) Modal.Close();
        // Auf der Suche selbst bleibt die Seite stehen (sonst stapelte jedes Strg+F eine weitere Suche auf den Zurück-Verlauf).
        if (PageHost.Content is SearchPage current)
        {
            current.Focus(query);
            return;
        }
        Open(() => new SearchPage(this, query));
    }

    public void OpenAlbum(string albumKey) => Open(() => new MusicPage(this, albumKey));

    public void Open(Func<UserControl> page)
    {
        if (_current is not null) _history.Push(_current);
        Show(new PageEntry(page, null));
    }

    public void OpenModal(string kicker, string title, UserControl content, ModalSize size = ModalSize.Medium, bool scroll = true, bool dismissOnScrim = false) =>
        Modal.Show(kicker, title, content, size, scroll, dismissOnScrim);

    public void CloseModal() => Modal.Close();

    /// <summary>Rückfrage in der App statt eines Windows-Meldungsfensters. Läuft wie ShowDialog in einer eigenen Nachrichtenschleife und kehrt erst nach der Antwort zurück.</summary>
    public bool Confirm(string title, string message, string confirmText = "OK", bool danger = false)
    {
        var result = false;
        var frame = new DispatcherFrame();
        void OnClosed() => frame.Continue = false;

        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 21 };
        text.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        var cancel = PageParts.Button("Abbrechen", "GhostButton", (_, _) => Dialog.Close(), new Thickness(0, 0, 10, 0));
        var accept = PageParts.Button(confirmText, danger ? "DangerButton" : "PrimaryButton", (_, _) =>
        {
            result = true;
            Dialog.Close();
        }, new Thickness(0));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 26, 0, 0), Children = { cancel, accept } };

        Dialog.Closed += OnClosed;
        Dialog.Show("Rückfrage", title, new StackPanel { Children = { text, buttons } }, ModalSize.Small);
        Dispatcher.PushFrame(frame);
        Dialog.Closed -= OnClosed;
        return result;
    }

    public void Back()
    {
        if (Modal.IsOpen)
        {
            Modal.TryClose();
            return;
        }
        if (_history.Count == 0)
        {
            Navigate(Section.Home);
            return;
        }
        Show(_history.Pop());
    }

    public void ReloadView()
    {
        Services.Model.Reload();
        UpdateInboxBadge();
        if (_current is not null) Show(_current);
    }

    private void Show(PageEntry entry)
    {
        _current = entry;
        SetBackdrop(null); // die Seite setzt ihr Bild selbst, wenn sie eins hat
        var page = entry.Factory();
        PageHost.Content = page;
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        _syncingNav = true;
        foreach (var (section, button) in _navButtons) button.IsChecked = section == entry.Section;
        _syncingNav = false;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (PlayerActive)
        {
            OnPlayerKey(e);
            return;
        }
        if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && !Dialog.IsOpen)
        {
            OpenSearch();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && (Dialog.IsOpen || Modal.IsOpen))
        {
            var layer = Dialog.IsOpen ? Dialog : Modal;
            // Aus einem Eingabefeld führt das erste Esc nur heraus, erst das zweite schließt (sonst gehen Eingaben ungewollt verloren).
            if (Keyboard.FocusedElement is TextBox box && layer.IsAncestorOf(box)) layer.FocusPanel();
            else layer.TryClose();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && PlayerLayer.Visibility != Visibility.Visible && _history.Count > 0 && Keyboard.FocusedElement is not TextBox)
        {
            Back();
            e.Handled = true;
        }
    }

    // --- Hintergrundbild -----------------------------------------------------------------------------------

    public void SetBackdrop(string? imagePath)
    {
        if (imagePath is null)
        {
            BackdropArt.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)));
            return;
        }
        BackdropImage.Source = ImageCache.Load(imagePath, 1600);
        BackdropArt.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(320)));
    }

    // --- Meldungen -----------------------------------------------------------------------------------------

    public void ShowMessage(string text, bool isError = false)
    {
        var toast = CreateToast(text, isError);
        Toasts.Children.Add(toast);
        toast.MouseLeftButtonUp += (_, _) => Toasts.Children.Remove(toast);
        if (isError) return;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Toasts.Children.Remove(toast);
        };
        timer.Start();
    }

    public async Task RunBusyAsync(string text, Func<Task> work)
    {
        var toast = CreateToast(text, isError: false);
        toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.55, TimeSpan.FromMilliseconds(700))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        });
        Toasts.Children.Add(toast);
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is NotSupportedException or DirectoryNotFoundException or FileNotFoundException or VolumeConfirmationRequiredException
            or HttpRequestException or InvalidDataException or ArgumentException or TimeoutException or TaskCanceledException)
        {
            ShowMessage(ex.Message, isError: true);
        }
        finally
        {
            Toasts.Children.Remove(toast);
        }
    }

    private GlassPanel CreateToast(string text, bool isError)
    {
        var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 6, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        dot.SetResourceReference(Shape.FillProperty, isError ? "Accent2Brush" : "AccentBrush");
        var label = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 640,
            FontSize = 14,
            FontFamily = (FontFamily)FindResource("BodySemiFont"),
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        var lift = new TranslateTransform();
        var toast = new GlassPanel
        {
            Layer = GlassLayer.Stage,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 12, 20, 12),
            Margin = new Thickness(0, 8, 0, 0),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, label } },
            Cursor = Cursors.Hand,
            RenderTransform = lift,
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Direction = 270, Opacity = 0.32, Color = Colors.Black },
        };
        toast.SetResourceReference(Control.BackgroundProperty, "SheetTintBrush");
        if (isError) toast.SetResourceReference(Control.BorderBrushProperty, "Accent2Brush");
        toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        return toast;
    }

    // --- Cover ---------------------------------------------------------------------------------------------

    public async Task RefreshArtworkAsync(bool force = false)
    {
        var model = Services.Model;
        var failures = new List<ArtworkFailure>();
        await RunBusyAsync("Cover werden erzeugt …", async () =>
        {
            failures.AddRange((await model.Artwork.RefreshAsync(model.Volumes, force)).Failures);
            failures.AddRange((await model.Artwork.RefreshAlbumsAsync(model.Volumes, force)).Failures);
        });
        ReloadView();
        if (failures.Count > 0)
        {
            ShowMessage($"Für {failures.Count} Einträge konnte kein Cover erzeugt werden: " +
                string.Join("; ", failures.Take(3).Select(f => $"{f.Subject} ({f.Error})")), isError: true);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _watch?.Dispose();
        ClosePlayer();
        base.OnClosed(e);
        Services.Dispose();
    }
}
