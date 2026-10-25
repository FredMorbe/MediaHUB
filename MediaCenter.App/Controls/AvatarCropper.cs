using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App.Controls;

/// <summary>
/// Wählt den runden Ausschnitt eines Profilbilds: Das Bild lässt sich mit der Maus verschieben, mit Mausrad oder Regler vergrößern. Es bedeckt den Kreis immer
/// ganz. Die Rechnung steht in <see cref="AvatarCrop"/>.
/// </summary>
public sealed class AvatarCropper : UserControl
{
    private const double Area = 280;
    private const double Circle = 240;
    private const double CircleStart = (Area - Circle) / 2;

    private readonly BitmapSource _source;
    private readonly Image _image;
    private readonly Slider _zoom;
    private readonly double _minScale;
    private double _scale;
    private double _offsetX;
    private double _offsetY;
    private Point _dragStart;
    private double _dragOffsetX;
    private double _dragOffsetY;
    private bool _dragging;
    private bool _settingZoom;

    public AvatarCropper(BitmapSource source)
    {
        _source = source;
        _minScale = AvatarCrop.MinScale(source.PixelWidth, source.PixelHeight, Circle);
        _scale = _minScale;
        _offsetX = AvatarCrop.CenteredOffset(_scale, source.PixelWidth, CircleStart, Circle);
        _offsetY = AvatarCrop.CenteredOffset(_scale, source.PixelHeight, CircleStart, Circle);

        _image = new Image { Source = source, Stretch = Stretch.Fill, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);

        // Abdunkelung außerhalb des Kreises
        var mask = new System.Windows.Shapes.Path
        {
            Fill = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)),
            IsHitTestVisible = false,
            Data = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(0, 0, Area, Area)),
                new EllipseGeometry(new Point(Area / 2, Area / 2), Circle / 2, Circle / 2)),
        };
        var ring = new System.Windows.Shapes.Ellipse
        {
            Width = Circle,
            Height = Circle,
            Stroke = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1.5,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(ring, CircleStart);
        Canvas.SetTop(ring, CircleStart);

        var canvas = new Canvas { Width = Area, Height = Area, ClipToBounds = true, Background = Brushes.Black, Cursor = Cursors.SizeAll };
        canvas.Children.Add(_image);
        canvas.Children.Add(mask);
        canvas.Children.Add(ring);
        System.Windows.Automation.AutomationProperties.SetName(canvas, "Bildausschnitt");

        canvas.MouseLeftButtonDown += (_, e) =>
        {
            _dragging = true;
            _dragStart = e.GetPosition(canvas);
            _dragOffsetX = _offsetX;
            _dragOffsetY = _offsetY;
            canvas.CaptureMouse();
        };
        canvas.MouseMove += (_, e) =>
        {
            if (!_dragging) return;
            var position = e.GetPosition(canvas);
            Move(_dragOffsetX + position.X - _dragStart.X, _dragOffsetY + position.Y - _dragStart.Y);
        };
        canvas.MouseLeftButtonUp += (_, _) =>
        {
            _dragging = false;
            canvas.ReleaseMouseCapture();
        };
        canvas.MouseWheel += (_, e) =>
        {
            SetZoom(_scale / _minScale * Math.Pow(1.1, e.Delta / 120.0));
            e.Handled = true;
        };

        _zoom = new Slider { Minimum = 1, Maximum = AvatarCrop.MaxZoom, Value = 1, Width = Area, Margin = new Thickness(0, 12, 0, 0) };
        _zoom.SetResourceReference(StyleProperty, "SeekSlider");
        System.Windows.Automation.AutomationProperties.SetName(_zoom, "Zoom");
        _zoom.ValueChanged += (_, e) =>
        {
            if (!_settingZoom) SetZoom(e.NewValue);
        };

        Content = new StackPanel { Width = Area, Children = { canvas, _zoom } };
        Apply();
    }

    /// <summary>Der gewählte Ausschnitt als Profilbild (PNG).</summary>
    public byte[] ToPng() => AvatarImage.Encode(_source,
        AvatarCrop.SourceSquare(_source.PixelWidth, _source.PixelHeight, _scale, _offsetX, _offsetY, CircleStart, CircleStart, Circle));

    private void Move(double offsetX, double offsetY)
    {
        _offsetX = AvatarCrop.ClampOffset(offsetX, _scale, _source.PixelWidth, CircleStart, Circle);
        _offsetY = AvatarCrop.ClampOffset(offsetY, _scale, _source.PixelHeight, CircleStart, Circle);
        Apply();
    }

    /// <param name="zoom">1 = das Bild füllt den Kreis gerade, größer zoomt hinein (bis <see cref="AvatarCrop.MaxZoom"/>).</param>
    private void SetZoom(double zoom)
    {
        zoom = Math.Clamp(zoom, 1, AvatarCrop.MaxZoom);
        var scale = _minScale * zoom;
        _offsetX = AvatarCrop.ZoomedOffset(_offsetX, _scale, scale, CircleStart, Circle);
        _offsetY = AvatarCrop.ZoomedOffset(_offsetY, _scale, scale, CircleStart, Circle);
        _scale = scale;
        _settingZoom = true;
        _zoom.Value = zoom;
        _settingZoom = false;
        Move(_offsetX, _offsetY); // hält das Bild hinter dem Kreis
    }

    private void Apply()
    {
        _image.Width = _source.PixelWidth * _scale;
        _image.Height = _source.PixelHeight * _scale;
        Canvas.SetLeft(_image, _offsetX);
        Canvas.SetTop(_image, _offsetY);
    }
}
