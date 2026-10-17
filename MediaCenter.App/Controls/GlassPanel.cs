using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MediaCenter.App.Controls;

/// <summary>
/// Milchglas-Fläche. WPF kann nichts live hinter einem Element weichzeichnen; stattdessen zeichnet die Fläche
/// den passenden Ausschnitt des Hintergrund-Elements (<see cref="Backdrop"/>) unscharf nach und legt eine Tönung darüber.
/// Inhalte, die über den Hintergrund scrollen, erscheinen daher nicht im Glas.
/// </summary>
public sealed class GlassPanel : ContentControl
{
    /// <summary>Rand um die Fläche, den der Weichzeichner zusätzlich liest, damit die Kanten nicht ausbleichen.</summary>
    private const double Pad = 48;

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(GlassPanel), new PropertyMetadata(new CornerRadius(22), (d, _) => ((GlassPanel)d).UpdateClip()));

    private static readonly DependencyPropertyKey EffectiveCornerRadiusKey = DependencyProperty.RegisterReadOnly(
        nameof(EffectiveCornerRadius), typeof(CornerRadius), typeof(GlassPanel), new PropertyMetadata(new CornerRadius(0)));

    public static readonly DependencyProperty EffectiveCornerRadiusProperty = EffectiveCornerRadiusKey.DependencyProperty;

    /// <summary>Das Element, dessen Bild als Glas-Untergrund dient; steht im selben Fenster wie die Flächen.</summary>
    public static FrameworkElement? Backdrop { get; set; }

    private Grid? _clip;
    private Rectangle? _blur;
    private Rect _lastBounds;

    static GlassPanel()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(GlassPanel), new FrameworkPropertyMetadata(typeof(GlassPanel)));
    }

    /// <summary>Der eingestellte Radius, begrenzt auf die halbe kurze Seite (sonst werden Pillen zu Linsen).</summary>
    public CornerRadius EffectiveCornerRadius => (CornerRadius)GetValue(EffectiveCornerRadiusProperty);

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _clip = GetTemplateChild("PART_Clip") as Grid ?? throw new InvalidOperationException("GlassPanel: PART_Clip fehlt im Template.");
        _blur = GetTemplateChild("PART_Blur") as Rectangle ?? throw new InvalidOperationException("GlassPanel: PART_Blur fehlt im Template.");
        _clip.SizeChanged += (_, _) => UpdateClip();
        UpdateClip();
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Loaded += (_, _) =>
        {
            LayoutUpdated += OnLayoutUpdated;
            // LayoutUpdated kommt nur bei Änderungen; beim ersten Anzeigen muss der Ausschnitt einmal gesetzt werden.
            Dispatcher.BeginInvoke(() => OnLayoutUpdated(this, EventArgs.Empty), System.Windows.Threading.DispatcherPriority.Loaded);
        };
        Unloaded += (_, _) => LayoutUpdated -= OnLayoutUpdated;
    }

    private void UpdateClip()
    {
        if (_clip is null) return;
        // Der Radius darf nicht größer als die halbe kurze Seite sein, sonst wird aus der Pille eine Linse.
        var radius = Math.Min(CornerRadius.TopLeft, Math.Min(_clip.ActualWidth, _clip.ActualHeight) / 2);
        SetValue(EffectiveCornerRadiusKey, new CornerRadius(radius));
        _clip.Clip = new RectangleGeometry(new Rect(0, 0, _clip.ActualWidth, _clip.ActualHeight), radius, radius);
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_blur is null || Backdrop is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        if (Window.GetWindow(this) is not { } window || Window.GetWindow(Backdrop) != window) return;

        var bounds = TransformToVisual(Backdrop).TransformBounds(new Rect(0, 0, ActualWidth, ActualHeight));
        if (bounds == _lastBounds) return;
        _lastBounds = bounds;

        _blur.Fill = new VisualBrush(Backdrop)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(bounds.X - Pad, bounds.Y - Pad, bounds.Width + 2 * Pad, bounds.Height + 2 * Pad),
            ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewport = new Rect(0, 0, 1, 1),
            Stretch = Stretch.Fill,
        };
    }
}
