using System.Globalization;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App.Player;

/// <summary>Eine Folge im Folgenmenü des Players; bei Live-TV ein Sender (<paramref name="VideoId"/> ist dann die Sender-Id, <paramref name="Season"/> die Nummer der Gruppe).</summary>
/// <param name="GroupLabel">Überschrift der Gruppe im Menü; ohne sie heißt sie „Staffel n“.</param>
public sealed record EpisodeChoice(long VideoId, int? Season, string Label, bool Watched, bool Reachable, string? GroupLabel = null);

/// <summary>Ein Fernseher im Menü „Übertragen“.</summary>
public sealed record CastChoice(string Id, string Name, string Model);

/// <summary>Eine Ton- oder Untertitelspur aus der Spurliste von mpv.</summary>
/// <param name="Removable">Selbst hinzugefügter Untertitel: das Menü bietet „entfernen“ an.</param>
/// <param name="Translatable">Textuntertitel, aus dem sich eine Übersetzung machen lässt.</param>
public sealed record TrackInfo(int Id, string Type, string Label, bool Selected, bool Removable = false, bool Translatable = false);

/// <summary>
/// Bedienung des Players als durchsichtiges Fenster über dem Video (ein WPF-Element kann nicht über das native
/// mpv-Fenster gezeichnet werden). Die Steuerung blendet sich bei Ruhe aus; Klicks auf das Video gehen an dieses Fenster.
/// </summary>
public partial class PlayerOverlay : Window
{
    private bool _seeking;
    private bool _settingVolume;
    private bool _building;
    private bool _canEmbed;
    private SubtitleAdjustment _adjustment = SubtitleAdjustment.Default;
    private double _audioDelay;
    private readonly TextBlock _delayText = new();
    private readonly TextBlock _audioDelayText = new();
    private readonly TextBlock _sizeText = new();
    private readonly TextBlock _positionText = new();
    private static readonly CultureInfo German = new("de-DE");
    private string? _menuType;
    private IReadOnlyList<TrackInfo> _tracks = Array.Empty<TrackInfo>();
    private readonly System.Windows.Threading.DispatcherTimer _noticeTimer = new();
    private string _soundPreset = Equalizer.Flat;
    private SleepMode _sleepMode;
    private string _sleepLabel = "";
    private bool _compact;
    private Point? _compactDragPoint;

    /// <summary>Einschlaf-Timer gewählt: Minuten (&gt; 0), 0 = aus, -1 = am Ende der Folge.</summary>
    public event Action<int>? SleepChosen;

    /// <summary>Equalizer-Voreinstellung gewählt (Id aus <see cref="Equalizer.Presets"/>).</summary>
    public event Action<string>? SoundPresetChosen;

    /// <summary>Das Menü „Übertragen“ wurde geöffnet oder „Erneut suchen“ gedrückt: der Empfänger soll nach Geräten suchen.</summary>
    public event Action? CastSearchRequested;

    /// <summary>Ein Fernseher im Menü „Übertragen“ gewählt (Id aus <see cref="CastChoice"/>).</summary>
    public event Action<string>? CastDeviceChosen;

    /// <summary>„Übertragung beenden“: weiter auf diesem PC schauen.</summary>
    public event Action? CastStopRequested;

    /// <summary>Ein Kapitel im Kapitelmenü gewählt (Beginn in Sekunden).</summary>
    public event Action<double>? ChapterChosen;

    /// <summary>„Intro überspringen“ (Karte oder Knopf).</summary>
    public event Action? IntroSkipClicked;

    /// <summary>Eine Folge im Folgenmenü gewählt (Video-Id).</summary>
    public event Action<long>? EpisodeChosen;

    public event Action? BackClicked;
    public event Action? PlayPauseClicked;
    public event Action? FullscreenClicked;
    public event Action? PictureInPictureClicked;
    public event Action<Vector>? DragMoved;
    public event Action? MuteClicked;
    public event Action<double>? SeekRelative;
    public event Action<double>? SeekAbsolute;
    public event Action<double>? VolumeChanged;
    public event Action<string, int>? TrackChosen;
    public event Action<KeyEventArgs>? KeyPressed;

    /// <summary>„Untertitel-Datei laden …“ im Untertitelmenü.</summary>
    public event Action? SubtitleFileRequested;

    /// <summary>Eine Datei wurde auf das Fenster gezogen (Pfad der ersten Datei).</summary>
    public event Action<string>? SubtitleFileDropped;

    /// <summary>Zeitversatz, Größe oder Höhe der Untertitel wurden geändert (Schaltflächen oder Tasten Z/X).</summary>
    public event Action<SubtitleAdjustment>? AdjustmentChanged;

    /// <summary>Der Ton-Zeitversatz wurde geändert (Schaltflächen oder Tasten G/H); Sekunden, positiv = Ton später.</summary>
    public event Action<double>? AudioDelayChanged;

    /// <summary>Der Nutzer hat im Anpassen-Fenster ein anderes Aussehen der Untertitel gewählt (gilt für alle Videos).</summary>
    public event Action<SubtitleStyle>? SubtitleStyleChanged;

    /// <summary>Einbetten eines selbst hinzugefügten Untertitels in die MKV (Spur-Id).</summary>
    public event Action<int>? SubtitleEmbedRequested;

    /// <summary>„Abbrechen“ in der Fortschrittsanzeige.</summary>
    public event Action? BusyCancelled;

    /// <summary>Entfernen eines selbst hinzugefügten Untertitels (Spur-Id).</summary>
    public event Action<int>? SubtitleRemoveRequested;

    /// <summary>Nächste Folge sofort starten (Schaltfläche oder Countdown-Karte).</summary>
    public event Action? NextClicked;

    /// <summary>Live-TV: voriger Sender.</summary>
    public event Action? PreviousChannelClicked;

    /// <summary>Automatischer Start der nächsten Folge abgebrochen.</summary>
    public event Action? NextCancelled;

    /// <summary>Mausbewegung, Klick oder Taste im Fenster – die Steuerung soll sichtbar bleiben.</summary>
    public event Action? Activity;

    public PlayerOverlay()
    {
        InitializeComponent();

        BackButton.Click += (_, _) => BackClicked?.Invoke();
        PlayPauseButton.Click += (_, _) => PlayPauseClicked?.Invoke();
        FullscreenButton.Click += (_, _) => FullscreenClicked?.Invoke();
        PictureInPictureButton.Click += (_, _) => PictureInPictureClicked?.Invoke();
        RestorePlayerButton.Click += (_, _) => PictureInPictureClicked?.Invoke();
        CloseCompactButton.Click += (_, _) => BackClicked?.Invoke();
        CompactPlayPause.Click += (_, _) => PlayPauseClicked?.Invoke();
        CompactMute.Click += (_, _) => MuteClicked?.Invoke();
        CompactNextNow.Click += (_, _) => NextClicked?.Invoke();
        CompactNextCancel.Click += (_, _) => NextCancelled?.Invoke();
        CompactHeader.MouseLeftButtonDown += (_, e) =>
        {
            if (IsInteractive(e.OriginalSource as DependencyObject, includeHeader: false)) return;
            e.Handled = true;
            if (e.ClickCount == 2) PictureInPictureClicked?.Invoke();
            else
            {
                _compactDragPoint = PointToScreen(e.GetPosition(this));
                CompactHeader.CaptureMouse();
            }
        };
        CompactHeader.MouseMove += (_, e) =>
        {
            if (_compactDragPoint is not { } previous || e.LeftButton != MouseButtonState.Pressed) return;
            var current = PointToScreen(e.GetPosition(this));
            _compactDragPoint = current;
            DragMoved?.Invoke(current - previous);
            e.Handled = true;
        };
        CompactHeader.MouseLeftButtonUp += (_, e) =>
        {
            if (_compactDragPoint is null) return;
            _compactDragPoint = null;
            CompactHeader.ReleaseMouseCapture();
            e.Handled = true;
        };
        CompactHeader.LostMouseCapture += (_, _) => _compactDragPoint = null;
        MuteButton.Click += (_, _) => MuteClicked?.Invoke();
        BackButton10.Click += (_, _) => SeekRelative?.Invoke(-10);
        ForwardButton.Click += (_, _) => SeekRelative?.Invoke(30);
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            Notice.Visibility = Visibility.Collapsed;
        };
        BuildAdjustPanel();
        BuildPicturePanel();
        BuildColorPanel();
        AdjustButton.Click += (_, _) => ToggleAdjust();
        BusyCancelButton.Click += (_, _) => BusyCancelled?.Invoke();
        BusyBar.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
        BusyBar.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        NextButton.Click += (_, _) => NextClicked?.Invoke();
        ChannelPrevButton.Click += (_, _) => PreviousChannelClicked?.Invoke();
        NextNowButton.Click += (_, _) => NextClicked?.Invoke();
        NextCancelButton.Click += (_, _) => NextCancelled?.Invoke();
        EpisodesButton.Click += (_, _) => ToggleMenu("episodes");
        ChaptersButton.Click += (_, _) => ToggleMenu("chapters");
        IntroSkipButton.Click += (_, _) => IntroSkipClicked?.Invoke();
        IntroCardButton.Click += (_, _) => IntroSkipClicked?.Invoke();
        AudioButton.Click += (_, _) => ToggleMenu("audio");
        SubButton.Click += (_, _) => ToggleMenu("sub");
        CastButton.Click += (_, _) =>
        {
            ToggleMenu("cast");
            if (_menuType == "cast") CastSearchRequested?.Invoke();
        };
        SleepButton.Click += (_, _) => ToggleMenu("sleep");
        SoundButton.Click += (_, _) => ToggleMenu("sound");

        // handledEventsToo: Ein Klick auf die Linie wird vom Slider selbst behandelt (IsMoveToPointEnabled) und als erledigt
        // markiert; ohne diese Anmeldung bliebe _seeking aus, und die nächste Positionsmeldung setzte den Regler zurück.
        SeekSlider.MouseMove += (_, e) => ShowSeekTip(e.GetPosition(SeekSlider).X);
        SeekSlider.MouseLeave += (_, _) => SeekTip.Visibility = Visibility.Collapsed;
        SeekSlider.SizeChanged += (_, _) => BuildChapterMarks();
        SeekSlider.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, _) => _seeking = true), handledEventsToo: true);
        SeekSlider.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) =>
            // Wert erst nach der eigenen Behandlung des Sliders lesen.
            Dispatcher.BeginInvoke(() =>
            {
                SeekAbsolute?.Invoke(SeekSlider.Value);
                _seeking = false;
            })), handledEventsToo: true);
        VolumeSlider.ValueChanged += (_, e) =>
        {
            if (!_settingVolume) VolumeChanged?.Invoke(e.NewValue);
        };

        PreviewKeyDown += (_, e) =>
        {
            Activity?.Invoke();
            KeyPressed?.Invoke(e);
        };
        // Datei auf das Video ziehen: als Untertitel hinzufügen (ob die Endung passt, entscheidet der Empfänger mit klarer Meldung).
        AllowDrop = true;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) SubtitleFileDropped?.Invoke(files[0]);
            e.Handled = true;
        };
        PreviewMouseMove += (_, _) => Activity?.Invoke();
        // Doppelklick beim Drücken erkennen: Beim Loslassen meldet WPF keinen Klickzähler von 2 (der zweite Klick wäre sonst
        // nur eine zweite Pause-Umschaltung, ohne Vollbild).
        Root.PreviewMouseLeftButtonDown += (_, e) => _doubleClick = e.ClickCount == 2;
        Root.MouseLeftButtonUp += OnRootClick;
    }

    private bool _doubleClick;

    private void OnRootClick(object sender, MouseButtonEventArgs e)
    {
        Activity?.Invoke();
        // Der Titel ist ausschließlich zum Verschieben und Zurückwechseln da.
        if (_compact && e.GetPosition(Root).Y <= CompactHeader.ActualHeight) return;
        if (IsInteractive(e.OriginalSource as DependencyObject))
        {
            return;
        }
        // Ein Klick aufs Video schließt ein offenes Menü oder Einstellungsfenster und pausiert nicht.
        if (CloseOpenPanels())
        {
            _panelClosedByClick = true;
            return;
        }
        // Einfacher Klick pausiert; ein Doppelklick wechselt den Vollbildmodus und macht die Pause des ersten Klicks wieder rückgängig.
        // Schloss der erste Klick eines Doppelklicks ein Fenster, gab es keine Pause, die zurückzunehmen wäre.
        var doubleClick = _doubleClick;
        _doubleClick = false;
        var skipPause = doubleClick && _panelClosedByClick;
        _panelClosedByClick = false;
        if (!skipPause) PlayPauseClicked?.Invoke();
        if (doubleClick) FullscreenClicked?.Invoke();
    }

    private bool _panelClosedByClick;

    /// <summary>Schließt Spurmenü und alle Einstellungsfenster; true, wenn eins offen war.</summary>
    private bool CloseOpenPanels()
    {
        var open = IsMenuOpen;
        CloseMenu();
        AdjustPanel.Visibility = Visibility.Collapsed;
        TranslatePanel.Visibility = Visibility.Collapsed;
        SearchPanel.Visibility = Visibility.Collapsed;
        PicturePanel.Visibility = Visibility.Collapsed;
        ColorPanel.Visibility = Visibility.Collapsed;
        return open;
    }

    private bool IsInteractive(DependencyObject? element, bool includeHeader = true)
    {
        for (var node = element; node is not null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is ButtonBase or RangeBase or Thumb or ScrollBar || (includeHeader && ReferenceEquals(node, CompactHeader)) || ReferenceEquals(node, TrackMenu) || ReferenceEquals(node, NextCard) || ReferenceEquals(node, IntroCard) || ReferenceEquals(node, Dock) || ReferenceEquals(node, AdjustPanel) || ReferenceEquals(node, PicturePanel) || ReferenceEquals(node, ColorPanel) || ReferenceEquals(node, TranslatePanel) || ReferenceEquals(node, SearchPanel) || ReferenceEquals(node, BusyLayer)) return true;
        }
        return false;
    }

    // --- Anzeige -------------------------------------------------------------------------------------------

    /// <param name="kicker">Kleine Zeile über dem Titel (bei Folgen „Staffel 1 · Folge 2“); null blendet sie aus.</param>
    public void SetTitle(string text, string? kicker = null)
    {
        CompactTitle.Text = kicker is null ? text : $"{text}  ·  {kicker}";
        TitleText.Text = text;
        KickerText.Text = kicker?.ToUpperInvariant() ?? "";
        KickerText.Visibility = kicker is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetCompact(bool compact)
    {
        CloseOpenPanels();
        _compact = compact;
        Controls.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactControls.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        Topmost = compact;
        Notice.Margin = new Thickness(8, compact ? 48 : 70, 8, 0);
        Notice.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        NextCard.Visibility = !compact && CompactNext.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        SetControlsVisible(true);
    }

    public void SetPosition(double seconds)
    {
        PositionText.Text = Format(seconds);
        if (SeekRow.Visibility == Visibility.Visible) CompactTime.Text = $"{Format(seconds)} / {DurationText.Text}";
        if (!_seeking) SeekSlider.Value = seconds;
        _lastPosition = seconds;
        RefreshChapterText();
        RefreshIntroCard();
    }

    public void SetDuration(double seconds)
    {
        DurationText.Text = Format(seconds);
        SeekSlider.Maximum = Math.Max(seconds, 1);
        _duration = seconds;
        _introRange = ChapterList.FindIntro(_chapters, _duration);
        BuildChapterMarks();
        RefreshIntroCard();
    }

    // --- Kapitel und Intro ---------------------------------------------------------------------------------

    private IReadOnlyList<Chapter> _chapters = Array.Empty<Chapter>();
    private (double Start, double End)? _introRange;
    private double _duration;
    private double _lastPosition;

    /// <summary>Das Vorspann-Kapitel der Datei (Beginn, Ende); null, wenn keins so heißt.</summary>
    public (double Start, double End)? IntroRange => _introRange;

    /// <summary>Kapitel der laufenden Datei; weniger als zwei blenden den Knopf aus. Ein Fernseher kennt sie nicht (mpv ruht): dort ist der Knopf weg.</summary>
    public void SetChapters(IReadOnlyList<Chapter> chapters)
    {
        _chapters = chapters;
        _introRange = ChapterList.FindIntro(chapters, _duration);
        BuildChapterMarks();
        ChaptersButton.Visibility = chapters.Count > 1 && _castActiveName is null ? Visibility.Visible : Visibility.Collapsed;
        if (_menuType == "chapters")
        {
            if (chapters.Count > 1) BuildMenu("chapters");
            else CloseMenu();
        }
        RefreshChapterText();
        RefreshIntroCard();
    }

    /// <summary>Striche an den Kapitelanfängen. Der Regler läuft von Mitte Griff zu Mitte Griff, daher die halbe Griffbreite Rand.</summary>
    private void BuildChapterMarks()
    {
        ChapterMarks.Children.Clear();
        if (_chapters.Count < 2 || _duration <= 0 || SeekSlider.ActualWidth <= SeekThumbWidth) return;
        foreach (var chapter in _chapters.Where(c => c.Start > 0.5 && c.Start < _duration - 0.5))
        {
            var mark = new System.Windows.Shapes.Rectangle { Width = 2, Height = 10, RadiusX = 1, RadiusY = 1, Fill = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)) };
            Canvas.SetLeft(mark, SeekThumbWidth / 2 + chapter.Start / _duration * (SeekSlider.ActualWidth - SeekThumbWidth) - 1);
            Canvas.SetTop(mark, 6);
            ChapterMarks.Children.Add(mark);
        }
    }

    private const double SeekThumbWidth = 14;

    /// <summary>Zeigt über der Zeitleiste die Zeit (und das Kapitel), auf die ein Klick an dieser Stelle springt.</summary>
    private void ShowSeekTip(double x)
    {
        if (_duration <= 0 || SeekSlider.ActualWidth <= SeekThumbWidth) return;
        var fraction = Math.Clamp((x - SeekThumbWidth / 2) / (SeekSlider.ActualWidth - SeekThumbWidth), 0, 1);
        var seconds = fraction * _duration;
        var index = _chapters.Count > 1 ? ChapterList.IndexAt(_chapters, seconds) : -1;
        SeekTipText.Text = index < 0 ? Format(seconds) : $"{Format(seconds)}  ·  {_chapters[index].Label}";
        SeekTip.Visibility = Visibility.Visible;
        SeekTip.UpdateLayout();
        SeekTip.Margin = new Thickness(Math.Clamp(x - SeekTip.ActualWidth / 2, 0, Math.Max(0, SeekSlider.ActualWidth - SeekTip.ActualWidth)), -40, 0, 0);
    }

    /// <summary>Großes Zeichen in der Bildmitte, das nach einer Pause oder einem Start ausblendet.</summary>
    private void FlashState(bool paused)
    {
        StateFlashGlyph.Text = paused ? "" : "";
        StateFlash.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(750)) { BeginTime = TimeSpan.FromMilliseconds(120) });
    }

    private void RefreshChapterText()
    {
        var index = _chapters.Count > 1 && _castActiveName is null ? ChapterList.IndexAt(_chapters, _lastPosition) : -1;
        ChapterText.Text = index < 0 ? "" : $"Kapitel {index + 1} von {_chapters.Count}  ·  {_chapters[index].Label}";
        ChapterText.Visibility = index < 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshIntroCard()
    {
        var inside = _introRange is { } range && _lastPosition >= range.Start - 1 && _lastPosition < range.End - 1;
        IntroCard.Visibility = inside && !_compact && _castActiveName is null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Menü „Kapitel“: Name und Beginn, das laufende Kapitel ist markiert.</summary>
    private void BuildChapterMenu()
    {
        var current = ChapterList.IndexAt(_chapters, _lastPosition);
        _building = true; // das Setzen von IsChecked beim Aufbau ist keine Wahl des Nutzers
        foreach (var chapter in _chapters)
        {
            var chosen = chapter;
            var button = new RadioButton
            {
                Content = $"{chapter.Label}    {Format(chapter.Start)}",
                GroupName = "chapters",
                IsChecked = chapter.Index == current,
                Foreground = Brushes.White,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            button.SetResourceReference(StyleProperty, "Chip");
            button.Margin = new Thickness(0, 0, 0, 4);
            System.Windows.Automation.AutomationProperties.SetName(button, $"Kapitel {chapter.Index + 1} {chapter.Label}");
            button.Checked += (_, _) =>
            {
                if (_building) return;
                CloseMenu();
                ChapterChosen?.Invoke(chosen.Start);
            };
            TrackList.Children.Add(button);
        }
        _building = false;
    }

    private bool _pausedBefore;

    public void SetPaused(bool paused)
    {
        PlayPauseButton.Content = paused ? "" : "";
        CompactPlayPause.Content = PlayPauseButton.Content;
        if (_pausedBefore != paused && _lastPosition > 0.5 && !_compact) FlashState(paused);
        _pausedBefore = paused;
        // Einzelbild-Knöpfe nur bei Pause (die Tasten Strg+←/→ pausieren selbst).
        FramePrevButton.Visibility = FrameNextButton.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetVolume(double volume)
    {
        _settingVolume = true;
        VolumeSlider.Value = volume;
        _settingVolume = false;
    }

    public void SetMuted(bool muted) => CompactMute.Content = MuteButton.Content = muted ? "" : "";

    public void SetFullscreen(bool fullscreen) => FullscreenButton.Content = fullscreen ? "" : "";

    public void SetStats(string text) => StatsText.Text = text;

    public void ToggleStats() =>
        StatsText.Visibility = StatsText.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    public void SetTracks(IReadOnlyList<TrackInfo> tracks)
    {
        _tracks = tracks;
        AudioButton.IsEnabled = tracks.Any(t => t.Type == "audio");
        // Das Untertitelmenü ist immer da: auch ohne Untertitel kann man darüber eine Datei laden.
        if (_menuType is not null) BuildMenu(_menuType);
    }

    /// <summary>
    /// Live-TV: keine Zeitleiste, Sender statt Folgen und Staffeln, vorher/nachher als Senderwechsel; kein Übertragen auf den Fernseher. Das Sendermenü füllt <see cref="SetEpisodes"/>.
    /// </summary>
    public void SetLive(bool live)
    {
        SeekRow.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        BackButton10.Visibility = ForwardButton.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        CastButton.Visibility = IntroSkipButton.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        ChannelPrevButton.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        NextButton.ToolTip = live ? "Nächster Sender (Bild ab)" : null;
        System.Windows.Automation.AutomationProperties.SetName(NextButton, live ? "Nächster Sender" : "Nächste Folge");
        EpisodesButton.ToolTip = live ? "Sender wählen" : "Folge der Serie wählen";
        System.Windows.Automation.AutomationProperties.SetName(EpisodesButton, live ? "Sender" : "Folgen");
        CompactTime.Text = live ? "Live" : "";
        if (live) HideNextCountdown();
    }

    /// <summary>Zeigt die Schaltfläche für die nächste Folge; null blendet sie aus (letzte Folge, Film, Folge nicht erreichbar).</summary>
    public void SetNextEpisode(string? title)
    {
        NextButton.Visibility = title is null ? Visibility.Collapsed : Visibility.Visible;
        NextButton.ToolTip = title is null ? null : $"Nächste Folge: {title} (N)";
        if (title is null) HideNextCountdown();
    }

    public void ShowNextCountdown(string title, int secondsLeft)
    {
        NextHint.Text = $"Nächste Folge in {secondsLeft} s";
        NextTitle.Text = title;
        CompactNextHint.Text = NextHint.Text;
        CompactNextHint.ToolTip = title;
        CompactNext.Visibility = Visibility.Visible;
        NextCard.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
    }

    public void HideNextCountdown() => CompactNext.Visibility = NextCard.Visibility = Visibility.Collapsed;

    /// <summary>Zeigt einen Hinweis für ein paar Sekunden; Fehler bleiben länger stehen.</summary>
    public void ShowNotice(string text, bool isError)
    {
        NoticeText.Text = text;
        NoticeText.Foreground = isError ? new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80)) : Brushes.White;
        Notice.Visibility = Visibility.Visible;
        _noticeTimer.Stop();
        _noticeTimer.Interval = TimeSpan.FromSeconds(isError ? 8 : 4);
        _noticeTimer.Start();
    }

    public bool IsNextCountdownVisible => CompactNext.Visibility == Visibility.Visible;

    /// <summary>Blendet Leisten und Menü ein oder aus; ausgeblendet verschwindet auch der Mauszeiger über dem Video.</summary>
    public void SetControlsVisible(bool visible)
    {
        if (!visible)
        {
            CloseMenu();
            AdjustPanel.Visibility = Visibility.Collapsed;
            PicturePanel.Visibility = Visibility.Collapsed;
            ColorPanel.Visibility = Visibility.Collapsed;
        }
        var controls = _compact ? CompactControls : Controls;
        controls.IsHitTestVisible = visible;
        Cursor = visible ? Cursors.Arrow : Cursors.None;
        controls.BeginAnimation(OpacityProperty, new DoubleAnimation(visible ? 1 : 0, TimeSpan.FromMilliseconds(visible ? 120 : 350)));
    }

    public bool IsMenuOpen => _menuType is not null || AdjustPanel.Visibility == Visibility.Visible || PicturePanel.Visibility == Visibility.Visible || ColorPanel.Visibility == Visibility.Visible || TranslatePanel.Visibility == Visibility.Visible || SearchPanel.Visibility == Visibility.Visible;

    // --- Untertitel anpassen -------------------------------------------------------------------------------

    /// <summary>Zeigt die gespeicherten Werte des Videos an, ohne <see cref="AdjustmentChanged"/> auszulösen.</summary>
    public void SetAdjustment(SubtitleAdjustment adjustment)
    {
        _adjustment = adjustment;
        RefreshAdjustment();
    }

    /// <summary>Einbetten gibt es nur bei MKV-Dateien.</summary>
    public void SetCanEmbed(bool canEmbed) => _canEmbed = canEmbed;

    /// <summary>Taste Z / X: Zeitversatz um einen Schritt ändern und den neuen Wert kurz einblenden.</summary>
    public void NudgeDelay(double seconds)
    {
        Change(a => a with { DelaySeconds = ClampDelay(a.DelaySeconds + seconds) });
        ShowNotice($"Untertitel-Zeitversatz: {DelayText(_adjustment.DelaySeconds)}", isError: false);
    }

    /// <summary>Zeigt den gespeicherten Ton-Zeitversatz des Videos an, ohne <see cref="AudioDelayChanged"/> auszulösen.</summary>
    public void SetAudioDelay(double seconds)
    {
        _audioDelay = seconds;
        _audioDelayText.Text = AudioDelayText(_audioDelay);
    }

    /// <summary>Taste G / H: Ton-Zeitversatz um einen Schritt ändern und den neuen Wert kurz einblenden.</summary>
    public void NudgeAudioDelay(double seconds)
    {
        ChangeAudioDelay(_audioDelay + seconds);
        ShowNotice($"Ton-Zeitversatz: {AudioDelayText(_audioDelay)}", isError: false);
    }

    private void ChangeAudioDelay(double seconds)
    {
        var next = Math.Round(Math.Clamp(seconds, -AudioSync.MaxDelaySeconds, AudioSync.MaxDelaySeconds), 2);
        if (next == _audioDelay) return;
        SetAudioDelay(next);
        AudioDelayChanged?.Invoke(next);
    }

    private static string AudioDelayText(double seconds) => $"{Math.Round(seconds * 1000).ToString("+0;-0;0", German)} ms";

    private static double ClampDelay(double seconds) =>
        Math.Round(Math.Clamp(seconds, -SubtitleAdjustment.MaxDelaySeconds, SubtitleAdjustment.MaxDelaySeconds), 1);

    private static string DelayText(double seconds) => $"{seconds.ToString("+0.0;-0.0;0.0", German)} s";

    private void Change(Func<SubtitleAdjustment, SubtitleAdjustment> change)
    {
        var next = change(_adjustment);
        if (next == _adjustment) return;
        _adjustment = next;
        RefreshAdjustment();
        AdjustmentChanged?.Invoke(next);
    }

    private void RefreshAdjustment()
    {
        _delayText.Text = DelayText(_adjustment.DelaySeconds);
        _sizeText.Text = $"{Math.Round(_adjustment.Scale * 100):0} %";
        _positionText.Text = $"{_adjustment.Position} %";
    }

    private void ToggleAdjust()
    {
        var show = AdjustPanel.Visibility != Visibility.Visible;
        CloseMenu();
        TranslatePanel.Visibility = Visibility.Collapsed;
        SearchPanel.Visibility = Visibility.Collapsed;
        PicturePanel.Visibility = Visibility.Collapsed;
        ColorPanel.Visibility = Visibility.Collapsed;
        AdjustPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildAdjustPanel()
    {
        var heading = new TextBlock { Text = "Untertitel anpassen", FontSize = 22, Margin = new Thickness(0, 0, 0, 4) };
        heading.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");

        TextBlock Label(string text) => new() { Text = text, FontSize = 13, Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 12, 0, 6) };

        Button Step(string text, string name, Action action)
        {
            var button = new Button { Content = text, Foreground = Brushes.White, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 6, 0) };
            button.SetResourceReference(StyleProperty, "GhostButton");
            System.Windows.Automation.AutomationProperties.SetName(button, name);
            button.Click += (_, _) => action();
            return button;
        }

        StackPanel Row(params UIElement[] items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var item in items) row.Children.Add(item);
            return row;
        }

        TextBlock Value(TextBlock block, double width)
        {
            block.MinWidth = width;
            block.TextAlignment = TextAlignment.Center;
            block.VerticalAlignment = VerticalAlignment.Center;
            block.FontSize = 15;
            block.Margin = new Thickness(0, 0, 6, 0);
            return block;
        }

        AdjustContent.Children.Add(heading);
        AdjustContent.Children.Add(Label("Zeitversatz (positiv = Untertitel später)"));
        AdjustContent.Children.Add(Row(
            Step("−1 s", "Zeitversatz minus 1 Sekunde", () => Change(a => a with { DelaySeconds = ClampDelay(a.DelaySeconds - 1) })),
            Step("−0,1", "Zeitversatz minus 0,1 Sekunden", () => Change(a => a with { DelaySeconds = ClampDelay(a.DelaySeconds - 0.1) })),
            Value(_delayText, 72),
            Step("+0,1", "Zeitversatz plus 0,1 Sekunden", () => Change(a => a with { DelaySeconds = ClampDelay(a.DelaySeconds + 0.1) })),
            Step("+1 s", "Zeitversatz plus 1 Sekunde", () => Change(a => a with { DelaySeconds = ClampDelay(a.DelaySeconds + 1) })),
            Step("0", "Zeitversatz zurücksetzen", () => Change(a => a with { DelaySeconds = 0 }))));

        AdjustContent.Children.Add(Label("Größe"));
        AdjustContent.Children.Add(Row(
            Step("−", "Untertitel kleiner", () => Change(a => a with { Scale = Math.Round(Math.Clamp(a.Scale - 0.1, SubtitleAdjustment.MinScale, SubtitleAdjustment.MaxScale), 1) })),
            Value(_sizeText, 72),
            Step("+", "Untertitel größer", () => Change(a => a with { Scale = Math.Round(Math.Clamp(a.Scale + 0.1, SubtitleAdjustment.MinScale, SubtitleAdjustment.MaxScale), 1) }))));

        AdjustContent.Children.Add(Label("Höhe (100 % = ganz unten)"));
        AdjustContent.Children.Add(Row(
            Step("Höher", "Untertitel höher", () => Change(a => a with { Position = Math.Clamp(a.Position - 5, 0, 100) })),
            Value(_positionText, 72),
            Step("Tiefer", "Untertitel tiefer", () => Change(a => a with { Position = Math.Clamp(a.Position + 5, 0, 100) }))));

        var audioHeading = new TextBlock { Text = "Ton synchronisieren", FontSize = 22, Margin = new Thickness(0, 22, 0, 4) };
        audioHeading.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        AdjustContent.Children.Add(audioHeading);
        AdjustContent.Children.Add(Label("Ton-Zeitversatz (positiv = Ton später; wird für dieses Video gemerkt)"));
        AdjustContent.Children.Add(Row(
            Step("−100", "Ton-Zeitversatz minus 100 Millisekunden", () => ChangeAudioDelay(_audioDelay - 0.1)),
            Step("−10", "Ton-Zeitversatz minus 10 Millisekunden", () => ChangeAudioDelay(_audioDelay - 0.01)),
            Value(_audioDelayText, 84),
            Step("+10", "Ton-Zeitversatz plus 10 Millisekunden", () => ChangeAudioDelay(_audioDelay + 0.01)),
            Step("+100", "Ton-Zeitversatz plus 100 Millisekunden", () => ChangeAudioDelay(_audioDelay + 0.1)),
            Step("0", "Ton-Zeitversatz zurücksetzen", () => ChangeAudioDelay(0))));

        AdjustContent.Children.Add(Label("Aussehen (für alle Videos; mehr unter Einstellungen → Wiedergabe)"));
        var looks = new WrapPanel { MaxWidth = 380 };
        foreach (var (name, style) in SubtitleStyle.Presets)
        {
            var preset = style;
            var button = Step(name, $"Untertitel-Aussehen {name}", () => SubtitleStyleChanged?.Invoke(preset));
            button.Margin = new Thickness(0, 0, 6, 6);
            looks.Children.Add(button);
        }
        AdjustContent.Children.Add(looks);

        var reset = Step("Zeit, Größe und Höhe zurücksetzen", "Anpassung zurücksetzen", () => Change(_ => SubtitleAdjustment.Default));
        reset.Margin = new Thickness(0, 14, 0, 0);
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        AdjustContent.Children.Add(reset);
        RefreshAdjustment();
        SetAudioDelay(_audioDelay);
    }

    // --- Fortschrittsanzeige -------------------------------------------------------------------------------

    public bool IsBusy => BusyLayer.Visibility == Visibility.Visible;

    public void ShowBusy(string title, string text)
    {
        CloseMenu();
        AdjustPanel.Visibility = Visibility.Collapsed;
        PicturePanel.Visibility = Visibility.Collapsed;
        ColorPanel.Visibility = Visibility.Collapsed;
        BusyTitle.Text = title;
        BusyText.Text = text;
        BusyBar.Value = 0;
        BusyCancelButton.IsEnabled = true;
        BusyLayer.Visibility = Visibility.Visible;
    }

    public void SetBusyProgress(double fraction, string? text = null)
    {
        BusyBar.Value = Math.Clamp(fraction, 0, 1);
        if (text is not null) BusyText.Text = text;
    }

    /// <summary>Während des Abbrechens ist die Schaltfläche gesperrt (der Abbruch dauert einen Moment).</summary>
    public void SetBusyCancelling() => BusyCancelButton.IsEnabled = false;

    public void HideBusy() => BusyLayer.Visibility = Visibility.Collapsed;

    // --- Spurmenü ------------------------------------------------------------------------------------------

    private void ToggleMenu(string type)
    {
        if (_menuType == type)
        {
            CloseMenu();
            return;
        }
        AdjustPanel.Visibility = Visibility.Collapsed;
        TranslatePanel.Visibility = Visibility.Collapsed;
        SearchPanel.Visibility = Visibility.Collapsed;
        PicturePanel.Visibility = Visibility.Collapsed;
        ColorPanel.Visibility = Visibility.Collapsed;
        _menuType = type;
        // Die Folgenliste ist lang und die Namen sind breit; die Spurlisten bleiben schmal.
        TrackMenu.MaxHeight = type is "episodes" or "chapters" ? 480 : 320;
        TrackMenu.MinWidth = type is "episodes" or "chapters" ? 320 : 220;
        BuildMenu(type);
        TrackMenu.Visibility = Visibility.Visible;
    }

    private void CloseMenu()
    {
        _menuType = null;
        TrackMenu.Visibility = Visibility.Collapsed;
    }

    /// <summary>Stand des Equalizers; das Klang-Menü markiert die gewählte Voreinstellung.</summary>
    public void SetSoundPreset(string presetId)
    {
        _soundPreset = presetId;
        SoundButton.Tag = presetId == Equalizer.Flat ? null : "on";
        if (_menuType == "sound") BuildMenu("sound");
    }

    /// <summary>Stand des Einschlaf-Timers; der Knopf zeigt die Restzeit.</summary>
    public void SetSleep(SleepMode mode, string label)
    {
        _sleepMode = mode;
        _sleepLabel = label;
        SleepButton.Tag = mode == SleepMode.Off ? null : "on";
        SleepButton.ToolTip = mode == SleepMode.Off ? "Einschlaf-Timer" : $"Einschlaf-Timer: {label}";
        if (_menuType == "sleep") BuildMenu("sleep");
    }

    /// <summary>Folgen der laufenden Serie für das Folgenmenü; weniger als zwei blenden den Knopf aus (Film, einzelne Folge).</summary>
    public void SetEpisodes(IReadOnlyList<EpisodeChoice> episodes, long currentVideoId)
    {
        _episodes = episodes;
        _currentVideoId = currentVideoId;
        EpisodesButton.Visibility = episodes.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (_menuType == "episodes")
        {
            if (episodes.Count > 1) BuildMenu("episodes");
            else CloseMenu();
        }
    }

    private IReadOnlyList<EpisodeChoice> _episodes = Array.Empty<EpisodeChoice>();
    private long _currentVideoId;

    /// <summary>Menü „Folgen“: die Folgen der Serie nach Staffeln, die laufende ist markiert.</summary>
    private void BuildEpisodeMenu()
    {
        _building = true; // das Setzen von IsChecked beim Aufbau ist keine Wahl des Nutzers
        foreach (var season in _episodes.GroupBy(e => e.Season).OrderBy(g => g.Key ?? int.MaxValue))
        {
            var header = new TextBlock
            {
                Text = season.First().GroupLabel ?? (season.Key is { } number ? $"Staffel {number}" : "Folgen"),
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(8, 8, 0, 4),
            };
            TrackList.Children.Add(header);

            foreach (var episode in season)
            {
                var chosen = episode;
                var button = new RadioButton
                {
                    Content = episode.Label + (episode.Watched ? "  ✓" : "") + (episode.Reachable ? "" : "  (offline)"),
                    GroupName = "episodes",
                    IsChecked = episode.VideoId == _currentVideoId,
                    IsEnabled = episode.Reachable,
                    Foreground = Brushes.White,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                };
                button.SetResourceReference(StyleProperty, "Chip");
                button.Margin = new Thickness(0, 0, 0, 4);
                System.Windows.Automation.AutomationProperties.SetName(button, $"{(episode.GroupLabel is null && episode.Season is { } s ? $"Staffel {s} " : "")}{episode.Label}");
                button.Checked += (_, _) =>
                {
                    if (_building) return;
                    CloseMenu();
                    EpisodeChosen?.Invoke(chosen.VideoId);
                };
                TrackList.Children.Add(button);
            }
        }
        _building = false;
    }

    // --- Übertragen auf einen Fernseher ----------------------------------------------------------------------

    private IReadOnlyList<CastChoice> _castDevices = Array.Empty<CastChoice>();
    private bool _castSearching;
    private string? _castActiveName;

    /// <summary>Gefundene Geräte für das Menü „Übertragen“; <paramref name="searching"/> zeigt, dass die Suche noch läuft.</summary>
    public void SetCastDevices(IReadOnlyList<CastChoice> devices, bool searching)
    {
        _castDevices = devices;
        _castSearching = searching;
        if (_menuType == "cast") BuildMenu("cast");
    }

    /// <summary>Name des Fernsehers, auf dem gerade übertragen wird; null = die Wiedergabe läuft auf diesem PC.</summary>
    public void SetCastActive(string? deviceName)
    {
        _castActiveName = deviceName;
        var casting = deviceName is not null;
        CastButton.Tag = casting ? "on" : null;
        CastButton.ToolTip = casting ? $"Wird auf {deviceName} übertragen" : "Auf einen Fernseher übertragen (Google Cast)";
        CastBanner.Visibility = casting ? Visibility.Visible : Visibility.Collapsed;
        CastBannerTitle.Text = deviceName ?? "";
        // Bildoptionen und Untertitel-Anpassung wirken nur im lokalen Player.
        var local = casting ? Visibility.Collapsed : Visibility.Visible;
        PictureButton.Visibility = ColorButton.Visibility = AdjustButton.Visibility = PictureInPictureButton.Visibility = local;
        ChaptersButton.Visibility = _chapters.Count > 1 && !casting ? Visibility.Visible : Visibility.Collapsed;
        RefreshChapterText();
        RefreshIntroCard();
        if (_menuType == "cast") BuildMenu("cast");
    }

    private void BuildCastMenu()
    {
        TextBlock Note(string text) => new()
        {
            Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 300,
            Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(8, 6, 8, 6),
        };

        if (_castActiveName is not null)
        {
            var stop = new Button { Content = $"Übertragung beenden ({_castActiveName})", Foreground = Brushes.White, Padding = new Thickness(18, 7, 18, 7), Margin = new Thickness(0, 0, 0, 8) };
            stop.SetResourceReference(StyleProperty, "GhostButton");
            System.Windows.Automation.AutomationProperties.SetName(stop, "Übertragung beenden");
            stop.Click += (_, _) =>
            {
                CloseMenu();
                CastStopRequested?.Invoke();
            };
            TrackList.Children.Add(stop);
        }

        _building = true; // das Setzen von IsChecked beim Aufbau ist keine Wahl des Nutzers
        foreach (var device in _castDevices)
        {
            var chosen = device;
            var button = new RadioButton
            {
                Content = string.IsNullOrWhiteSpace(device.Model) ? device.Name : $"{device.Name}  ·  {device.Model}",
                GroupName = "cast",
                IsChecked = device.Name == _castActiveName,
                Foreground = Brushes.White,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            button.SetResourceReference(StyleProperty, "Chip");
            button.Margin = new Thickness(0, 0, 0, 4);
            System.Windows.Automation.AutomationProperties.SetName(button, $"Übertragen auf {device.Name}");
            button.Checked += (_, _) =>
            {
                if (_building) return;
                CloseMenu();
                CastDeviceChosen?.Invoke(chosen.Id);
            };
            TrackList.Children.Add(button);
        }
        _building = false;

        if (_castSearching) TrackList.Children.Add(Note("Suche nach Geräten …"));
        else
        {
            if (_castDevices.Count == 0)
            {
                TrackList.Children.Add(Note("Kein Gerät gefunden. Ist der Fernseher an und im selben WLAN? Das Windows-Netzwerk sollte als „Privat“ eingestellt sein."));
            }
            var again = new Button { Content = "Erneut suchen", Foreground = Brushes.White, Padding = new Thickness(18, 7, 18, 7), Margin = new Thickness(0, 6, 0, 0) };
            again.SetResourceReference(StyleProperty, "GhostButton");
            System.Windows.Automation.AutomationProperties.SetName(again, "Nach Geräten suchen");
            again.Click += (_, _) => CastSearchRequested?.Invoke();
            TrackList.Children.Add(again);
        }
    }

    /// <summary>Menü „Timer“ und „Klang“: eine Liste zum Anklicken statt der Spurauswahl.</summary>
    private void BuildChoiceMenu(string type)
    {
        void Add(string text, Action click, bool selected = false)
        {
            var button = new RadioButton { Content = text, GroupName = $"choice-{type}", IsChecked = selected, Foreground = Brushes.White, HorizontalContentAlignment = HorizontalAlignment.Left };
            button.SetResourceReference(StyleProperty, "Chip");
            button.Margin = new Thickness(0, 0, 0, 4);
            System.Windows.Automation.AutomationProperties.SetName(button, $"{(type == "sleep" ? "Einschlaf-Timer" : "Klang")} {text}");
            button.Checked += (_, _) =>
            {
                if (_building) return;
                CloseMenu();
                click();
            };
            TrackList.Children.Add(button);
        }

        _building = true; // das Setzen von IsChecked beim Aufbau ist keine Wahl des Nutzers
        if (type == "sound")
        {
            foreach (var preset in Equalizer.Presets)
            {
                var id = preset.Id;
                Add(preset.Name, () => SoundPresetChosen?.Invoke(id), selected: _soundPreset == id);
            }
            if (_soundPreset == "custom") Add("Eigene Einstellung", () => { }, selected: true);
        }
        else
        {
            if (_sleepMode != SleepMode.Off) Add($"Timer abbrechen ({_sleepLabel})", () => SleepChosen?.Invoke(0));
            foreach (var minutes in SleepTimer.Minutes)
            {
                var chosen = minutes;
                Add($"In {minutes} Minuten", () => SleepChosen?.Invoke(chosen));
            }
            Add("Am Ende der Folge", () => SleepChosen?.Invoke(-1), selected: _sleepMode == SleepMode.EndOfItem);
        }
        _building = false;
    }

    private void BuildMenu(string type)
    {
        TrackList.Children.Clear();
        if (type is "sleep" or "sound")
        {
            BuildChoiceMenu(type);
            return;
        }
        if (type == "episodes")
        {
            BuildEpisodeMenu();
            return;
        }
        if (type == "chapters")
        {
            BuildChapterMenu();
            return;
        }
        if (type == "cast")
        {
            BuildCastMenu();
            return;
        }
        var items = _tracks.Where(t => t.Type == type).ToList();
        if (type == "sub") items.Insert(0, new TrackInfo(-1, "sub", "Aus", !items.Any(t => t.Selected)));
        _building = true; // das Setzen von IsChecked beim Aufbau ist keine Wahl des Nutzers
        foreach (var track in items)
        {
            var button = new RadioButton
            {
                Content = track.Label,
                GroupName = $"tracks-{type}",
                IsChecked = track.Selected,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = Brushes.White,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            button.SetResourceReference(StyleProperty, "Chip");
            button.Margin = new Thickness(0, 0, 0, 4);
            button.Checked += (_, _) =>
            {
                if (_building) return;
                TrackChosen?.Invoke(type, track.Id);
                CloseMenu();
            };

            if (!track.Removable)
            {
                TrackList.Children.Add(button);
                continue;
            }

            var remove = new Button { Content = "", ToolTip = "Gespeicherten Untertitel entfernen", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
            remove.SetResourceReference(StyleProperty, "IconButton");
            System.Windows.Automation.AutomationProperties.SetName(remove, $"Untertitel entfernen: {track.Label}");
            remove.Click += (_, _) =>
            {
                SubtitleRemoveRequested?.Invoke(track.Id);
                CloseMenu();
            };
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(remove, 2);
            row.Children.Add(button);
            row.Children.Add(remove);

            if (_canEmbed)
            {
                var embed = new Button { Content = "\uE74E", ToolTip = "In die MKV-Datei einbetten", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
                embed.SetResourceReference(StyleProperty, "IconButton");
                System.Windows.Automation.AutomationProperties.SetName(embed, $"Untertitel einbetten: {track.Label}");
                embed.Click += (_, _) =>
                {
                    CloseMenu();
                    SubtitleEmbedRequested?.Invoke(track.Id);
                };
                Grid.SetColumn(embed, 1);
                row.Children.Add(embed);
            }
            TrackList.Children.Add(row);
        }

        if (type == "sub")
        {
            var load = new Button { Content = "Untertitel-Datei laden …", Foreground = Brushes.White, Padding = new Thickness(18, 7, 18, 7), Margin = new Thickness(0, 6, 0, 0) };
            load.SetResourceReference(StyleProperty, "GhostButton");
            System.Windows.Automation.AutomationProperties.SetName(load, "Untertitel-Datei laden");
            load.Click += (_, _) =>
            {
                CloseMenu();
                SubtitleFileRequested?.Invoke();
            };
            TrackList.Children.Add(load);

            if (_castActiveName is null)
            {
                var search = new Button { Content = "Online suchen …", Foreground = Brushes.White, Padding = new Thickness(18, 7, 18, 7), Margin = new Thickness(0, 6, 0, 0) };
                search.SetResourceReference(StyleProperty, "GhostButton");
                System.Windows.Automation.AutomationProperties.SetName(search, "Untertitel online suchen");
                search.Click += (_, _) =>
                {
                    CloseMenu();
                    SubtitleSearchRequested?.Invoke();
                };
                TrackList.Children.Add(search);
            }

            var translate = new Button { Content = "Untertitel übersetzen …", Foreground = Brushes.White, Padding = new Thickness(18, 7, 18, 7), Margin = new Thickness(0, 6, 0, 0) };
            translate.SetResourceReference(StyleProperty, "GhostButton");
            System.Windows.Automation.AutomationProperties.SetName(translate, "Untertitel übersetzen");
            translate.Click += (_, _) => ShowTranslatePanel();
            TrackList.Children.Add(translate);
        }
        _building = false;
    }

    private static string Format(double seconds) => TimeSpan.FromSeconds(Math.Max(seconds, 0)).ToString(@"h\:mm\:ss");
}
