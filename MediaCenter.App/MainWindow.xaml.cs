using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
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

        GlassPanel.Backdrop = Backdrop;
        BuildNav();
        SettingsButton.Click += (_, _) => Navigate(Section.Settings);
        VersionButton.Content = "v" + AppInfo.Version;
        VersionButton.Click += (_, _) => Open(() => new ChangelogPage(this));

        Services.Theme.Changed += () => Services.Theme.StyleTitleBar(this);
        SourceInitialized += (_, _) => Services.Theme.StyleTitleBar(this);
        SizeChanged += (_, _) => BackdropArt.Height = ActualHeight * 0.66;
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) =>
        {
            Services.Model.Reload();
            UpdateInboxBadge();
            Navigate(Section.Home);
        };
        InitPlayer();

        Mini.Attach(Services.Audio);
        Mini.IsVisibleChanged += (_, _) => UpdateBottomInset();
        Services.Audio.Error += message => ShowMessage(message, isError: true);
    }

    /// <summary>Lässt unten Platz für die Audio-Leiste, damit sie keine Inhalte verdeckt.</summary>
    private void UpdateBottomInset()
    {
        var inset = Mini.IsVisible ? 104 : 0;
        PageHost.Margin = new Thickness(0, 0, 0, inset);
        Toasts.Margin = new Thickness(0, 0, 0, 26 + inset);
    }

    public AppServices Services { get; }

    // --- Navigation ----------------------------------------------------------------------------------------

    private void BuildNav()
    {
        foreach (var (section, label) in new[]
        {
            (Section.Home, "Start"), (Section.Movies, "Filme"), (Section.Series, "Serien"),
            (Section.Music, "Musik"), (Section.Radio, "Radio"), (Section.Podcasts, "Podcasts"),
            (Section.Inbox, "Inbox"), (Section.Sources, "Laufwerke"),
        })
        {
            var button = new RadioButton { Content = label, GroupName = "nav", Margin = new Thickness(0, 0, 4, 0) };
            button.SetResourceReference(StyleProperty, "NavToggle");
            // Checked statt Click: auch die Pfeiltasten in der Gruppe wählen eine Seite.
            button.Checked += (_, _) =>
            {
                if (!_syncingNav) Navigate(section);
            };
            _navButtons[section] = button;
            NavItems.Children.Add(button);
        }
    }

    private void UpdateInboxBadge()
    {
        var count = Services.Model.Inbox.Count;
        _navButtons[Section.Inbox].Content = count > 0 ? $"Inbox ({count})" : "Inbox";
    }

    public void Navigate(Section section)
    {
        _history.Clear();
        Show(new PageEntry(() => section switch
        {
            Section.Home => new HomePage(this),
            Section.Movies => new LibraryPage(this, TitleKind.Movie),
            Section.Series => new LibraryPage(this, TitleKind.Series),
            Section.Music => new MusicPage(this),
            Section.Radio => new RadioPage(this),
            Section.Podcasts => new PodcastsPage(this),
            Section.Inbox => new InboxPage(this),
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

    public void Open(Func<UserControl> page)
    {
        if (_current is not null) _history.Push(_current);
        Show(new PageEntry(page, null));
    }

    public void Back()
    {
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

    private Border CreateToast(string text, bool isError)
    {
        var label = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 640,
            FontSize = 14,
            FontFamily = (FontFamily)FindResource("BodySemiFont"),
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        var toast = new Border
        {
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(18, 12, 18, 12),
            Margin = new Thickness(0, 8, 0, 0),
            BorderThickness = new Thickness(1.5),
            Child = label,
            Cursor = Cursors.Hand,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.3, Color = Colors.Black },
        };
        toast.SetResourceReference(Border.BackgroundProperty, "BgSoftBrush");
        toast.SetResourceReference(Border.BorderBrushProperty, isError ? "Accent2Brush" : "GlassEdgeBrush");
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
        ClosePlayer();
        base.OnClosed(e);
        Services.Dispose();
    }
}
