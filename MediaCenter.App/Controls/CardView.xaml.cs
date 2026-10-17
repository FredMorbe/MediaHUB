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

        if (item.Square) Poster.Height = 176;
        TitleText.Text = item.Title;
        MetaText.Text = item.Meta;
        ToolTip = item.Title;

        if (item.Image is { } image)
        {
            Poster.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        }
        else
        {
            Poster.Background = PlaceholderBrush(item.Title);
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
            ProgressTrack.SizeChanged += (_, _) => ProgressFill.Width = ProgressTrack.ActualWidth * item.Progress;
        }

        MouseEnter += (_, _) => Highlight(true);
        MouseLeave += (_, _) => Highlight(IsKeyboardFocused);
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
        Poster.BorderBrush = on ? (Brush)FindResource("AccentBrush") : Brushes.Transparent;
        var scale = on ? 1.04 : 1.0;
        var animation = new DoubleAnimation(scale, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase() };
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        Panel.SetZIndex(this, on ? 1 : 0);
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
