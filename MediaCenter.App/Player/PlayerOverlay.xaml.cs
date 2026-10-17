using System.Globalization;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MediaCenter.Core;

namespace MediaCenter.App.Player;

/// <summary>Eine Ton- oder Untertitelspur aus der Spurliste von mpv.</summary>
/// <param name="Removable">Selbst hinzugefügter Untertitel: das Menü bietet „entfernen“ an.</param>
public sealed record TrackInfo(int Id, string Type, string Label, bool Selected, bool Removable = false);

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
    private readonly TextBlock _delayText = new();
    private readonly TextBlock _sizeText = new();
    private readonly TextBlock _positionText = new();
    private static readonly CultureInfo German = new("de-DE");
    private string? _menuType;
    private IReadOnlyList<TrackInfo> _tracks = Array.Empty<TrackInfo>();
    private readonly System.Windows.Threading.DispatcherTimer _noticeTimer = new();

    public event Action? BackClicked;
    public event Action? PlayPauseClicked;
    public event Action? FullscreenClicked;
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

    /// <summary>Einbetten eines selbst hinzugefügten Untertitels in die MKV (Spur-Id).</summary>
    public event Action<int>? SubtitleEmbedRequested;

    /// <summary>„Abbrechen“ in der Fortschrittsanzeige.</summary>
    public event Action? BusyCancelled;

    /// <summary>Entfernen eines selbst hinzugefügten Untertitels (Spur-Id).</summary>
    public event Action<int>? SubtitleRemoveRequested;

    /// <summary>Nächste Folge sofort starten (Schaltfläche oder Countdown-Karte).</summary>
    public event Action? NextClicked;

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
        MuteButton.Click += (_, _) => MuteClicked?.Invoke();
        BackButton10.Click += (_, _) => SeekRelative?.Invoke(-10);
        ForwardButton.Click += (_, _) => SeekRelative?.Invoke(30);
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            Notice.Visibility = Visibility.Collapsed;
        };
        BuildAdjustPanel();
        AdjustButton.Click += (_, _) => ToggleAdjust();
        BusyCancelButton.Click += (_, _) => BusyCancelled?.Invoke();
        BusyBar.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
        BusyBar.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        NextButton.Click += (_, _) => NextClicked?.Invoke();
        NextNowButton.Click += (_, _) => NextClicked?.Invoke();
        NextCancelButton.Click += (_, _) => NextCancelled?.Invoke();
        AudioButton.Click += (_, _) => ToggleMenu("audio");
        SubButton.Click += (_, _) => ToggleMenu("sub");

        SeekSlider.PreviewMouseLeftButtonDown += (_, _) => _seeking = true;
        SeekSlider.PreviewMouseLeftButtonUp += (_, _) =>
            // Wert erst nach der eigenen Behandlung des Sliders lesen.
            Dispatcher.BeginInvoke(() =>
            {
                SeekAbsolute?.Invoke(SeekSlider.Value);
                _seeking = false;
            });
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
        Root.MouseLeftButtonUp += OnRootClick;
    }

    private void OnRootClick(object sender, MouseButtonEventArgs e)
    {
        Activity?.Invoke();
        if (IsInteractive(e.OriginalSource as DependencyObject))
        {
            return;
        }
        if (_menuType is not null)
        {
            CloseMenu();
            return;
        }
        // Einfacher Klick pausiert; ein Doppelklick wechselt den Vollbildmodus und macht die Pause des ersten Klicks wieder rückgängig.
        PlayPauseClicked?.Invoke();
        if (e.ClickCount == 2) FullscreenClicked?.Invoke();
    }

    private bool IsInteractive(DependencyObject? element)
    {
        for (var node = element; node is not null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is ButtonBase or RangeBase or Thumb or ScrollBar || ReferenceEquals(node, TrackMenu) || ReferenceEquals(node, NextCard) || ReferenceEquals(node, AdjustPanel) || ReferenceEquals(node, BusyLayer)) return true;
        }
        return false;
    }

    // --- Anzeige -------------------------------------------------------------------------------------------

    public void SetTitle(string text) => TitleText.Text = text;

    public void SetPosition(double seconds)
    {
        PositionText.Text = Format(seconds);
        if (!_seeking) SeekSlider.Value = seconds;
    }

    public void SetDuration(double seconds)
    {
        DurationText.Text = Format(seconds);
        SeekSlider.Maximum = Math.Max(seconds, 1);
    }

    public void SetPaused(bool paused) => PlayPauseButton.Content = paused ? "" : "";

    public void SetVolume(double volume)
    {
        _settingVolume = true;
        VolumeSlider.Value = volume;
        _settingVolume = false;
    }

    public void SetMuted(bool muted) => MuteButton.Content = muted ? "" : "";

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
        NextCard.Visibility = Visibility.Visible;
    }

    public void HideNextCountdown() => NextCard.Visibility = Visibility.Collapsed;

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

    public bool IsNextCountdownVisible => NextCard.Visibility == Visibility.Visible;

    /// <summary>Blendet Leisten und Menü ein oder aus; ausgeblendet verschwindet auch der Mauszeiger über dem Video.</summary>
    public void SetControlsVisible(bool visible)
    {
        if (!visible)
        {
            CloseMenu();
            AdjustPanel.Visibility = Visibility.Collapsed;
        }
        Controls.IsHitTestVisible = visible;
        Cursor = visible ? Cursors.Arrow : Cursors.None;
        Controls.BeginAnimation(OpacityProperty, new DoubleAnimation(visible ? 1 : 0, TimeSpan.FromMilliseconds(visible ? 120 : 350)));
    }

    public bool IsMenuOpen => _menuType is not null || AdjustPanel.Visibility == Visibility.Visible;

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

        var reset = Step("Alles zurücksetzen", "Anpassung zurücksetzen", () => Change(_ => SubtitleAdjustment.Default));
        reset.Margin = new Thickness(0, 14, 0, 0);
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        AdjustContent.Children.Add(reset);
        RefreshAdjustment();
    }

    // --- Fortschrittsanzeige -------------------------------------------------------------------------------

    public bool IsBusy => BusyLayer.Visibility == Visibility.Visible;

    public void ShowBusy(string title, string text)
    {
        CloseMenu();
        AdjustPanel.Visibility = Visibility.Collapsed;
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
        _menuType = type;
        BuildMenu(type);
        TrackMenu.Visibility = Visibility.Visible;
    }

    private void CloseMenu()
    {
        _menuType = null;
        TrackMenu.Visibility = Visibility.Collapsed;
    }

    private void BuildMenu(string type)
    {
        TrackList.Children.Clear();
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
        }
        _building = false;
    }

    private static string Format(double seconds) => TimeSpan.FromSeconds(Math.Max(seconds, 0)).ToString(@"h\:mm\:ss");
}
