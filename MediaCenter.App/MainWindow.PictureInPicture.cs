using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;

namespace MediaCenter.App;

public partial class MainWindow
{
    // Dasselbe Fenster und derselbe native Video-Host bleiben bestehen: mpv spielt ohne Neuladen weiter.
    private PictureInPictureRestore? _pictureInPictureRestore;
    private bool PictureInPictureActive => _pictureInPictureRestore is not null;

    private sealed record PictureInPictureRestore(Rect Bounds, WindowState State, bool Fullscreen,
        WindowStyle Style, ResizeMode Resize, bool Topmost, double MinWidth, double MinHeight,
        Thickness PlayerMargin, WindowChrome? Chrome);

    private void TogglePictureInPicture()
    {
        if (PictureInPictureActive) RestorePictureInPicture();
        else EnterPictureInPicture();
    }

    private void EnterPictureInPicture()
    {
        if (!PlayerActive || PictureInPictureActive) return;
        if (_embedding)
        {
            _overlay.ShowNotice("Bild-in-Bild ist nach dem Einbetten wieder verfügbar.", isError: false);
            return;
        }

        var fullscreen = _fullscreen;
        SetFullscreen(false);
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _pictureInPictureRestore = new(bounds, WindowState, fullscreen, WindowStyle, ResizeMode, Topmost,
            MinWidth, MinHeight, PlayerLayer.Margin, WindowChrome.GetWindowChrome(this));

        WindowState = WindowState.Normal;
        MinWidth = 320;
        MinHeight = 200;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        // Das Overlay fängt Klicks ab. Ein freier Rand lässt Windows weiterhin die Größe ändern.
        PlayerLayer.Margin = new Thickness(6);
        PictureInPictureFrame.Visibility = Visibility.Visible;
        Topmost = true;
        _overlay.SetCompact(true);

        var area = CurrentMonitorWorkArea();
        Width = Math.Min(480, area.Width - 32);
        Height = Math.Min(282, area.Height - 32);
        Left = area.Right - Width - 16;
        Top = area.Bottom - Height - 16;
        _lastActivity = DateTime.UtcNow;
        UpdateOverlay();
    }

    private void RestorePictureInPicture()
    {
        if (_pictureInPictureRestore is not { } restore) return;
        _pictureInPictureRestore = null;
        _overlay.SetCompact(false);
        WindowState = WindowState.Normal;
        WindowChrome.SetWindowChrome(this, restore.Chrome);
        WindowStyle = restore.Style;
        ResizeMode = restore.Resize;
        Topmost = restore.Topmost;
        MinWidth = restore.MinWidth;
        MinHeight = restore.MinHeight;
        PlayerLayer.Margin = restore.PlayerMargin;
        PictureInPictureFrame.Visibility = Visibility.Collapsed;
        Left = restore.Bounds.Left;
        Top = restore.Bounds.Top;
        Width = restore.Bounds.Width;
        Height = restore.Bounds.Height;
        WindowState = restore.State;
        SetFullscreen(restore.Fullscreen);
        _lastActivity = DateTime.UtcNow;
        UpdateOverlay();
    }

    private void DragPictureInPicture(Vector deviceDelta)
    {
        if (!PictureInPictureActive) return;
        // Die Maus gehört zum Overlay-Fenster; dessen Capture bleibt auch beim Verschieben erhalten.
        var delta = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice.Transform(deviceDelta) ?? deviceDelta;
        Left += delta.X;
        Top += delta.Y;
        _lastActivity = DateTime.UtcNow;
        UpdateOverlay();
    }

    private Rect CurrentMonitorWorkArea()
    {
        var monitor = MonitorFromWindow(new WindowInteropHelper(this).Handle, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return SystemParameters.WorkArea;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
        var area = new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        if (transform is { } matrix) area.Transform(matrix);
        return area;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
