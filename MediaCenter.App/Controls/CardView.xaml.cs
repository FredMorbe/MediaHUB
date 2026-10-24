using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MediaCenter.App.Services;

namespace MediaCenter.App.Controls;

/// <summary>Cover-Karte: Poster (oder Querformat-Bild, zugeschnitten, oder Platzhalter mit Titel), Titel und Zusatzzeile.</summary>
public partial class CardView : UserControl
{
    public CardView(CardItem item)
    {
        InitializeComponent();
        Item = item;

        if (item.Square)
        {
            // Alben, Podcasts und Sender: quadratisches Cover, Titel darunter.
            Width = item.SquareSize;
            Poster.Height = item.SquareSize;
            // Ohne Bild steht der Titel groß im Platzhalter; auf kleinen Karten entsprechend kleiner, damit Wörter nicht mitten umbrechen.
            if (item.SquareSize < 160) PlaceholderTitle.FontSize = 24;
            TitleText.Text = item.Title;
            MetaText.Text = item.Meta;
            InfoPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            // Poster-Karte: kein Text darunter; Titel und Zusatzzeile kommen beim Überfahren auf das Poster.
            TitleText.Visibility = Visibility.Collapsed;
            MetaText.Visibility = Visibility.Collapsed;
            OverlayTitle.Text = item.Title;
            OverlayMeta.Text = item.Meta;
            OverlayMeta.Visibility = item.Meta.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            // Ohne Poster steht der Titel schon im Platzhalter.
            if (item.Image is null) OverlayTitle.Visibility = Visibility.Collapsed;
        }
        PosterArt.Width = Width - 3;
        ToolTip = item.Title;

        // Der Rahmen schneidet seinen Inhalt nicht rund zu (anders als einen Hintergrund), also schneidet die Karte selbst.
        PosterContent.SizeChanged += (_, e) => PosterContent.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 18.5, 18.5);

        if (item.Image is { } image)
        {
            PosterArt.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        }
        else
        {
            PosterArt.Background = PlaceholderBrush(item.Title);
            PlaceholderTitle.Text = item.Title;
            PlaceholderTitle.Visibility = Visibility.Visible;
        }

        if (item.Offline)
        {
            OfflineChip.Visibility = Visibility.Visible;
            Poster.Opacity = 0.55;
        }
        if (item.Progress > 0)
        {
            ProgressTrack.Visibility = Visibility.Visible;
            InfoPanel.Padding = new Thickness(16, 44, 16, 28); // Platz für den Fortschrittsbalken
            OfflineChip.Margin = new Thickness(10, 0, 0, 26);
            ProgressTrack.SizeChanged += (_, _) => ProgressFill.Width = ProgressTrack.ActualWidth * item.Progress;
        }

        MouseEnter += (_, _) =>
        {
            Highlight(true);
            BeginPreview();
        };
        MouseLeave += (_, _) =>
        {
            Highlight(IsKeyboardFocused);
            EndPreview();
        };
        GotKeyboardFocus += (_, _) => Highlight(true);
        LostKeyboardFocus += (_, _) => Highlight(IsMouseOver);
        MouseLeftButtonUp += (_, e) =>
        {
            Activate();
            e.Handled = true;
        };
        MouseRightButtonUp += (_, e) => e.Handled = ShowMenu();
        KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                Activate();
                e.Handled = true;
            }
            else if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
            {
                e.Handled = ShowMenu();
            }
        };
    }

    public CardItem Item { get; }

    public event Action<CardItem>? Activated;

    /// <summary>Einträge des Kontextmenüs (Rechtsklick, Menütaste); ohne Angabe oder ohne Einträge gibt es kein Menü.</summary>
    public Func<CardItem, IReadOnlyList<(string Text, Action Click)>>? Menu { get; set; }

    internal void Activate() => Activated?.Invoke(Item);

    private bool HasMenu => Menu?.Invoke(Item).Count > 0;

    private bool ShowMenu()
    {
        if (Menu?.Invoke(Item) is not { Count: > 0 } items) return false;
        ContextPopup.Show(this, items, Item.Title);
        return true;
    }

    /// <summary>Die Karte ist für Screenreader und Automation ein Knopf mit dem Titel als Namen.</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new CardPeer(this);

    private sealed class CardPeer(CardView owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider, IExpandCollapseProvider
    {
        protected override string GetClassNameCore() => nameof(CardView);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

        protected override string GetNameCore() => owner.Item.Title;

        // „Aufklappen“ öffnet das Kontextmenü (für Bedienhilfen und Automation, die nicht rechtsklicken können).
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface switch
        {
            PatternInterface.Invoke => this,
            PatternInterface.ExpandCollapse when owner.HasMenu => this,
            _ => base.GetPattern(patternInterface),
        };

        public ExpandCollapseState ExpandCollapseState => ExpandCollapseState.Collapsed;

        public void Expand() => owner.Dispatcher.BeginInvoke(() => owner.ShowMenu());

        public void Collapse()
        {
        }

        // Asynchron, damit der Aufrufer nicht blockiert, wenn die Aktion ein Fenster öffnet.
        public void Invoke() => owner.Dispatcher.BeginInvoke(owner.Activate);
    }

    private void Highlight(bool on)
    {
        // Heller Ring wie bei Disney+; die Info auf dem Poster blendet ein, der flache Schatten darunter aus.
        Poster.BorderBrush = (Brush)FindResource(on ? "InkBrush" : "GlassEdgeBrush");
        InfoPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(on ? 180 : 120)));
        Shade.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 0 : 1, TimeSpan.FromMilliseconds(120)));
        // Der Schatten kostet Zeit und gehört darum nur zur Karte unter dem Zeiger.
        Poster.Effect = on ? new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 26, ShadowDepth = 8, Direction = 270, Opacity = 0.45, Color = Colors.Black } : null;
        var scale = on ? 1.035 : 1.0;
        var animation = new DoubleAnimation(scale, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase() };
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        // Eine noch zurückgleitende Karte bleibt über ihrer Nachbarin.
        Panel.SetZIndex(this, on || _previewVisible ? 1 : 0);
    }

    // --- Vorschau beim Überfahren ---------------------------------------------------------------------------

    /// <summary>Gesetzt von der Hauptseite; ohne Dienst gibt es keine Vorschau.</summary>
    public static PreviewClips? Previews { get; set; }

    private static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(450);
    private static readonly CubicEase EaseOut = new() { EasingMode = EasingMode.EaseOut };

    private int _hover;            // zählt bei jedem Betreten und Verlassen hoch; alte, noch wartende Abläufe erkennen sich daran
    private MediaElement? _video;
    private bool _previewVisible;  // Video ist eingeblendet oder gleitet noch zurück

    /// <summary>Nur Filme und Serien mit erreichbarer Datei; Alben, Podcasts und Sender haben keine Vorschau.</summary>
    private bool CanPreview => Previews is { Enabled: true } && Item is { TitleId: > 0, Square: false, Offline: false };

    private async void BeginPreview()
    {
        if (!CanPreview) return;
        var hover = ++_hover;
        await Task.Delay(HoverDelay);
        if (hover != _hover) return;
        var clip = await Previews!.GetAsync(Item.TitleId);
        if (hover != _hover || clip is null) return;
        OpenVideo(clip, hover);
    }

    private void EndPreview()
    {
        _hover++;
        Previews?.Release(Item.TitleId);
        CloseVideo();
    }

    /// <summary>Das Video öffnet unsichtbar; erst wenn es bereit ist, wächst die Karte (sonst gäbe es eine leere Fläche).</summary>
    private void OpenVideo(string clip, int hover)
    {
        var video = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            IsMuted = true,
            Volume = 0,
            Stretch = Stretch.UniformToFill,
            Opacity = 0,
            IsHitTestVisible = false,
            Source = new Uri(clip),
        };
        video.MediaOpened += (_, _) =>
        {
            if (hover == _hover && _video == video) Expand(video);
        };
        video.MediaEnded += (_, _) =>
        {
            video.Position = TimeSpan.Zero;
            video.Play();
        };
        video.MediaFailed += (_, e) =>
        {
            if (_video == video) CloseVideo();
            Previews!.ReportUnplayable(clip, e.ErrorException.Message);
        };
        PosterContent.Children.Insert(1, video); // über dem Poster, unter Offline-Marke und Fortschritt
        _video = video;
        video.Play();
    }

    private void Expand(MediaElement video)
    {
        _previewVisible = true;
        var extra = Math.Round(Poster.Height * 16 / 9) - ActualWidth; // Höhe bleibt, die Breite wird 16:9
        var toRight = FitsRight(extra);
        PosterArt.HorizontalAlignment = toRight ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        Poster.BeginAnimation(MarginProperty, new ThicknessAnimation(toRight ? new Thickness(0, 0, -extra, 0) : new Thickness(-extra, 0, 0, 0), TimeSpan.FromMilliseconds(280)) { EasingFunction = EaseOut });
        video.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(320)) { BeginTime = TimeSpan.FromMilliseconds(80) });
        var flat = new DoubleAnimation(1, TimeSpan.FromMilliseconds(200));
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, flat);
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, flat);
    }

    private void CloseVideo()
    {
        var video = _video;
        _video = null;
        if (video is null) return;

        var back = new ThicknessAnimation(new Thickness(0), TimeSpan.FromMilliseconds(220)) { EasingFunction = EaseOut };
        back.Completed += (_, _) =>
        {
            Poster.BeginAnimation(MarginProperty, null);
            _previewVisible = false;
            if (!IsMouseOver && !IsKeyboardFocused) Panel.SetZIndex(this, 0);
        };
        Poster.BeginAnimation(MarginProperty, back);

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) =>
        {
            video.Stop();
            video.Close();
            PosterContent.Children.Remove(video);
        };
        video.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Nach rechts wächst die Karte, wenn dort Platz ist (in der Reihe bis zum Rand des Bildlaufbereichs, sonst bis zum Fensterrand); sonst nach links.</summary>
    private bool FitsRight(double extra)
    {
        FrameworkElement? area = null;
        for (var parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ScrollViewer { HorizontalScrollBarVisibility: not ScrollBarVisibility.Disabled } scroller)
            {
                area = scroller;
                break;
            }
        }
        area ??= Window.GetWindow(this) ?? throw new InvalidOperationException("Die Karte hängt in keinem Fenster.");

        var left = TranslatePoint(new Point(0, 0), area).X;
        var right = area.ActualWidth - (left + ActualWidth);
        return right >= extra + 12 || right >= left;
    }

    /// <summary>Ruhiger Verlauf, dessen Farbton sich aus dem Titel ergibt (stabil über Starts, anders als string.GetHashCode).</summary>
    private static Brush PlaceholderBrush(string title)
    {
        var hash = 17;
        foreach (var ch in title) hash = unchecked(hash * 31 + ch);
        var hue = Math.Abs(hash) % 360;
        var brush = new LinearGradientBrush(FromHsl(hue, 0.45, 0.30), FromHsl((hue + 40) % 360, 0.50, 0.12), 60);
        brush.Freeze();
        return brush;
    }

    public static Color FromHsl(double hue, double saturation, double lightness)
    {
        var c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = lightness - c / 2;
        var (r, g, b) = (int)(hue / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
