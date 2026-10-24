using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MediaCenter.App.Controls;

/// <summary>Welche Ebene des Hauptfensters eine Glas-Fläche weichzeichnet.</summary>
public enum GlassLayer
{
    /// <summary>Hintergrund und Seite: für Flächen, die selbst darüber liegen (Navigation, Leiste, Modale, Menüs über der Seite).</summary>
    Scene,

    /// <summary>Alles bis auf Rückfragen und Meldungen: für Flächen, die auch über Modalen und der Navigation liegen können.</summary>
    Stage,
}

/// <summary>
/// Milchglas-Fläche. WPF kann nichts live hinter einem Element weichzeichnen; stattdessen zeichnet die Fläche
/// den passenden Ausschnitt einer Ebene des Hauptfensters (<see cref="Backdrop"/> oder <see cref="Overlay"/>) als lebendes Bild
/// unscharf nach und legt Tönung, Glanz und einen Lichtrand darüber. Was hinter der Fläche scrollt, scheint also weichgezeichnet durch.
/// Funktioniert auch in Popups (eigene Fenster): dort wird die Lage über Bildschirmkoordinaten bestimmt.
/// </summary>
public sealed class GlassPanel : ContentControl
{
    /// <summary>Rand um die Fläche, den der Weichzeichner zusätzlich liest, damit die Kanten nicht ausbleichen.</summary>
    private const double Pad = 48;

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(GlassPanel), new PropertyMetadata(new CornerRadius(22), (d, _) => ((GlassPanel)d).UpdateClip()));

    public static readonly DependencyProperty LayerProperty = DependencyProperty.Register(
        nameof(Layer), typeof(GlassLayer), typeof(GlassPanel), new PropertyMetadata(GlassLayer.Scene, (d, _) => ((GlassPanel)d).Refresh()));

    /// <summary>Farbe zwischen Weichzeichnung und Tönung, z. B. die Abdunkelung eines Modals, die im Hintergrund des Glases fehlt.</summary>
    public static readonly DependencyProperty UnderlayProperty = DependencyProperty.Register(
        nameof(Underlay), typeof(Brush), typeof(GlassPanel), new PropertyMetadata(null));

    private static readonly DependencyPropertyKey EffectiveCornerRadiusKey = DependencyProperty.RegisterReadOnly(
        nameof(EffectiveCornerRadius), typeof(CornerRadius), typeof(GlassPanel), new PropertyMetadata(new CornerRadius(0)));

    public static readonly DependencyProperty EffectiveCornerRadiusProperty = EffectiveCornerRadiusKey.DependencyProperty;

    /// <summary>Hintergrund und Seite des Hauptfensters (<see cref="GlassLayer.Scene"/>).</summary>
    public static FrameworkElement? Backdrop { get; set; }

    /// <summary>Alle Ebenen bis auf Rückfragen und Meldungen (<see cref="GlassLayer.Stage"/>).</summary>
    public static FrameworkElement? Overlay { get; set; }

    private Grid? _clip;
    private Rectangle? _blur;
    private Rect _lastBounds;
    private FrameworkElement? _lastSource;

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

    public GlassLayer Layer
    {
        get => (GlassLayer)GetValue(LayerProperty);
        set => SetValue(LayerProperty, value);
    }

    public Brush? Underlay
    {
        get => (Brush?)GetValue(UnderlayProperty);
        set => SetValue(UnderlayProperty, value);
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
            // In einem Popup steht das Fenster erst danach an seiner Stelle, darum zusätzlich noch einmal im Leerlauf.
            Dispatcher.BeginInvoke(Refresh, System.Windows.Threading.DispatcherPriority.Loaded);
            Dispatcher.BeginInvoke(Refresh, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
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

    private void Refresh()
    {
        _lastBounds = Rect.Empty;
        OnLayoutUpdated(this, EventArgs.Empty);
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_blur is null || !IsLoaded || ActualWidth <= 0 || ActualHeight <= 0) return;
        var source = Layer == GlassLayer.Stage ? Overlay ?? Backdrop : Backdrop;
        if (source is null || PresentationSource.FromVisual(source) is not { } sourceRoot || PresentationSource.FromVisual(this) is not { } ownRoot) return;

        Rect bounds;
        if (ReferenceEquals(sourceRoot, ownRoot))
        {
            bounds = TransformToVisual(source).TransformBounds(new Rect(0, 0, ActualWidth, ActualHeight));
        }
        else
        {
            // Popup: eigenes Fenster, kein gemeinsamer Elternknoten; die Lage kommt über den Bildschirm.
            var topLeft = source.PointFromScreen(PointToScreen(new Point(0, 0)));
            var bottomRight = source.PointFromScreen(PointToScreen(new Point(ActualWidth, ActualHeight)));
            bounds = new Rect(topLeft, bottomRight);
        }
        if (bounds == _lastBounds && ReferenceEquals(source, _lastSource)) return;
        _lastBounds = bounds;
        _lastSource = source;

        _blur.Fill = new VisualBrush(source)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(bounds.X - Pad, bounds.Y - Pad, bounds.Width + 2 * Pad, bounds.Height + 2 * Pad),
            ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewport = new Rect(0, 0, 1, 1),
            Stretch = Stretch.Fill,
        };
    }
}
