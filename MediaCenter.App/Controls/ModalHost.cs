using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace MediaCenter.App.Controls;

public enum ModalSize
{
    /// <summary>Rückfragen und kurze Hinweise.</summary>
    Small,

    /// <summary>Formulare mit wenigen Feldern.</summary>
    Medium,

    /// <summary>Listen und Editoren.</summary>
    Large,

    /// <summary>Fast das ganze Fenster, für den eingebetteten Browser. Ohne Bewegung beim Öffnen, weil ein WebView2 nicht mitwandert.</summary>
    Browser,
}

/// <summary>Inhalt eines Modals, der das Schließen verhindern kann, solange eine Arbeit läuft.</summary>
public interface IModalContent
{
    /// <summary>Grund, warum das Modal gerade nicht schließt, oder null, wenn es schließen darf.</summary>
    string? BlockClose { get; }

    /// <summary>
    /// Breite, unter die der Inhalt nicht schrumpft (z. B. eine Tabelle mit festen Spalten). In schmaleren Fenstern scrollt das Modal waagerecht;
    /// 0 heißt: der Inhalt passt sich der Breite an.
    /// </summary>
    double MinContentWidth => 0;
}

/// <summary>
/// Schicht über der Seite: abgedunkelter Hintergrund und ein Panel mit Titelzeile, Schließen-Knopf und Inhalt.
/// Eine Schicht zeigt immer nur ein Modal; ein neues ersetzt das offene.
/// </summary>
public sealed class ModalHost : Grid
{
    private readonly Border _scrim = new();
    private readonly Border _panel = new();
    private readonly TextBlock _kicker = new();
    private readonly TextBlock _title = new();
    private readonly ContentControl _body = new() { IsTabStop = false };
    private readonly TranslateTransform _lift = new();
    private ModalSize _size = ModalSize.Medium;
    private bool _dismissOnScrim;
    private IInputElement? _previousFocus;
    private bool _closing;

    public ModalHost()
    {
        Visibility = Visibility.Collapsed;
        SnapsToDevicePixels = true;

        _scrim.SetResourceReference(Border.BackgroundProperty, "ModalScrimBrush");
        _scrim.MouseLeftButtonDown += (_, e) =>
        {
            if (_dismissOnScrim) TryClose();
            e.Handled = true;
        };
        Children.Add(_scrim);

        _panel.CornerRadius = new CornerRadius(28);
        _panel.BorderThickness = new Thickness(1);
        _panel.HorizontalAlignment = HorizontalAlignment.Center;
        _panel.VerticalAlignment = VerticalAlignment.Center;
        _panel.Focusable = true;
        _panel.FocusVisualStyle = null;
        _panel.RenderTransform = _lift;
        _panel.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        _panel.SetResourceReference(Border.BorderBrushProperty, "GlassEdgeBrush");
        _panel.Effect = new DropShadowEffect { BlurRadius = 56, ShadowDepth = 14, Direction = 270, Opacity = 0.42, Color = Colors.Black };
        KeyboardNavigation.SetTabNavigation(_panel, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(_panel, KeyboardNavigationMode.Contained);
        // Klicks im Panel sollen nicht zur Abdunkelung durchfallen.
        _panel.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is not TextBox) _panel.Focus();
        };

        _kicker.SetResourceReference(StyleProperty, "KickerText");
        _kicker.Margin = new Thickness(0, 0, 0, 2);
        _title.SetResourceReference(StyleProperty, "DisplayText");
        _title.FontSize = 36;
        _title.TextTrimming = TextTrimming.CharacterEllipsis;

        var close = new Button { Content = "\uE711", ToolTip = "Schließen (Esc)", VerticalAlignment = VerticalAlignment.Top, Width = 36, Height = 36, FontSize = 14 };
        close.SetResourceReference(StyleProperty, "IconButton");
        AutomationProperties.SetName(close, "Schließen");
        close.Click += (_, _) => TryClose();

        var heading = new StackPanel { Children = { _kicker, _title }, VerticalAlignment = VerticalAlignment.Center };
        var header = new Grid { Margin = new Thickness(30, 22, 18, 14) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SetColumn(close, 1);
        header.Children.Add(heading);
        header.Children.Add(close);

        var rule = new Border { Height = 1 };
        rule.SetResourceReference(Border.BackgroundProperty, "LineBrush");

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        SetRow(rule, 1);
        SetRow(_body, 2);
        layout.Children.Add(header);
        layout.Children.Add(rule);
        layout.Children.Add(_body);
        // Der Inhalt endet an den runden Ecken des Panels (ein Rahmen beschneidet seinen Inhalt nicht selbst).
        layout.SizeChanged += (_, _) => layout.Clip = new RectangleGeometry(new Rect(0, 0, layout.ActualWidth, layout.ActualHeight), 27, 27);
        _panel.Child = layout;
        Children.Add(_panel);

        SizeChanged += (_, _) => ApplySize();
    }

    public bool IsOpen => Visibility == Visibility.Visible && !_closing;

    /// <summary>Wird mit dem Grund aufgerufen, wenn ein Schließen abgelehnt wurde.</summary>
    public Action<string>? Blocked { get; set; }

    /// <summary>Wird ausgelöst, wenn das Modal geschlossen ist.</summary>
    public event Action? Closed;

    public void Show(string kicker, string title, FrameworkElement content, ModalSize size = ModalSize.Medium, bool scroll = true, bool dismissOnScrim = false)
    {
        var wasOpen = IsOpen;
        if (wasOpen) _body.Content = null;
        else _previousFocus = Keyboard.FocusedElement;

        _size = size;
        _dismissOnScrim = dismissOnScrim;
        _kicker.Text = kicker.ToUpperInvariant();
        _kicker.Visibility = kicker.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _title.Text = title;
        AutomationProperties.SetName(_panel, title);

        if (scroll)
        {
            content.Margin = new Thickness(30, 22, 30, 30);
            var minWidth = (content as IModalContent)?.MinContentWidth ?? 0;
            var viewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = minWidth > 0 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
                Focusable = false,
                Content = content,
            };
            if (minWidth > 0)
            {
                // Im waagerecht scrollenden Viewer bekäme der Inhalt unbegrenzte Breite: er wird darum selbst auf die Breite des Fensters (mindestens minWidth) gesetzt.
                viewer.SizeChanged += (_, _) => content.Width = Math.Max(viewer.ViewportWidth - 60, minWidth);
            }
            _body.Content = viewer;
        }
        else
        {
            _body.Content = content;
        }
        _current = content;

        _closing = false;
        Visibility = Visibility.Visible;
        ApplySize();
        UpdateLayout();
        if (size != ModalSize.Browser) Dispatcher.BeginInvoke(() => _panel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)), System.Windows.Threading.DispatcherPriority.Loaded);
        else Dispatcher.BeginInvoke(() => _panel.Focus(), System.Windows.Threading.DispatcherPriority.Loaded);
        if (wasOpen) return;

        _scrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        _panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        if (size != ModalSize.Browser)
        {
            _lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        else
        {
            _lift.BeginAnimation(TranslateTransform.YProperty, null);
            _lift.Y = 0;
        }
    }

    private FrameworkElement? _current;

    /// <summary>Der Inhalt, den <see cref="Show"/> bekommen hat (ohne den Bildlauf darum).</summary>
    public FrameworkElement? Content => _current;

    /// <summary>Setzt den Fokus auf das Panel (z. B. um ein Eingabefeld zu verlassen, ohne zu schließen).</summary>
    public void FocusPanel() => _panel.Focus();

    /// <summary>Schließt, sofern der Inhalt es erlaubt; sonst nennt <see cref="Blocked"/> den Grund.</summary>
    public bool TryClose()
    {
        if (!IsOpen) return true;
        if ((_current as IModalContent)?.BlockClose is { } reason)
        {
            Blocked?.Invoke(reason);
            return false;
        }
        Close();
        return true;
    }

    /// <summary>Schließt ohne Rückfrage.</summary>
    public void Close()
    {
        if (!IsOpen) return;
        _closing = true;

        void Finish()
        {
            _body.Content = null;
            _current = null;
            Visibility = Visibility.Collapsed;
            _closing = false;
            if (_previousFocus is UIElement previous && previous.IsVisible && previous.Focusable) previous.Focus();
            _previousFocus = null;
            Closed?.Invoke();
        }

        if (_size == ModalSize.Browser)
        {
            Finish();
            return;
        }
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
        fade.Completed += (_, _) =>
        {
            if (_closing) Finish();
        };
        _scrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));
        _panel.BeginAnimation(OpacityProperty, fade);
    }

    private void ApplySize()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var width = _size switch
        {
            ModalSize.Small => 520,
            ModalSize.Medium => 780,
            ModalSize.Large => 1180,
            _ => 1320,
        };
        _panel.Width = Math.Min(width, ActualWidth - 64);
        var available = ActualHeight - 64;
        if (_size is ModalSize.Large or ModalSize.Browser)
        {
            _panel.Height = Math.Min(available, _size == ModalSize.Browser ? ActualHeight * 0.9 : ActualHeight * 0.88);
            _panel.MaxHeight = double.PositiveInfinity;
        }
        else
        {
            _panel.ClearValue(HeightProperty);
            _panel.MaxHeight = available;
        }
    }
}
