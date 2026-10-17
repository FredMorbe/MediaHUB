using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace MediaCenter.Spike;

public partial class MainWindow : Window
{
    private const string DefaultMpvPath = @"C:\Program Files\MPV Player\mpv.exe";

    private readonly OverlayWindow _overlay = new();
    private readonly Dictionary<string, string> _stats = new();
    private MpvPlayer? _player;
    private bool _paused;
    private bool _fullscreen;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += (_, _) => { _overlay.Close(); _player?.Dispose(); };

        LocationChanged += (_, _) => UpdateOverlay();
        SizeChanged += (_, _) => UpdateOverlay();
        StateChanged += (_, _) => UpdateOverlay();
        Host.SizeChanged += (_, _) => UpdateOverlay();

        PreviewKeyDown += OnKeyDown;
        _overlay.PreviewKeyDown += OnKeyDown;

        _overlay.OpenClicked += OpenFile;
        _overlay.PlayPauseClicked += () => _ = Player.CommandAsync("cycle", "pause");
        _overlay.AudioClicked += () => _ = Player.CommandAsync("cycle", "audio");
        _overlay.SubClicked += () => _ = Player.CommandAsync("cycle", "sub");
        _overlay.FullscreenClicked += ToggleFullscreen;
        _overlay.SeekRequested += seconds => _ = Player.CommandAsync("seek", seconds, "absolute");
    }

    private MpvPlayer Player => _player ?? throw new InvalidOperationException("mpv ist noch nicht gestartet.");

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Host.Handle == IntPtr.Zero) throw new InvalidOperationException("Video-Host hat kein Fenster-Handle.");

        _overlay.Owner = this;
        _overlay.Show();
        UpdateOverlay();

        var mpvPath = Environment.GetEnvironmentVariable("MEDIACENTER_MPV") ?? DefaultMpvPath;
        _player = await MpvPlayer.StartAsync(mpvPath, Host.Handle);
        _player.PropertyChanged += (name, data) => Dispatcher.Invoke(() => OnProperty(name, data));
        _player.CommandFailed += message => Dispatcher.Invoke(() => throw new InvalidOperationException($"mpv-Befehl fehlgeschlagen: {message}"));
        _player.Ended += reason => Dispatcher.Invoke(() => throw new InvalidOperationException($"mpv-Verbindung beendet: {reason}"));

        foreach (var property in new[]
        {
            "time-pos", "duration", "pause", "hwdec-current", "frame-drop-count",
            "decoder-frame-drop-count", "estimated-vf-fps",
            "current-tracks/audio/title", "current-tracks/sub/title",
        })
        {
            await _player.ObserveAsync(property);
        }

        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1]))
        {
            await _player.CommandAsync("loadfile", args[1]);
        }
    }

    private void OpenFile()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Video/Audio|*.mkv;*.mp4;*.avi;*.mov;*.ts;*.m2ts;*.webm;*.mp3;*.flac;*.m4a;*.ogg;*.opus|Alle Dateien|*.*",
        };
        if (dialog.ShowDialog(this) == true)
        {
            _ = Player.CommandAsync("loadfile", dialog.FileName);
        }
    }

    private void OnProperty(string name, JsonElement? data)
    {
        switch (name)
        {
            case "time-pos" when data is { ValueKind: JsonValueKind.Number } v:
                _overlay.SetPosition(v.GetDouble());
                break;
            case "duration" when data is { ValueKind: JsonValueKind.Number } v:
                _overlay.SetDuration(v.GetDouble());
                break;
            case "pause" when data is { } v:
                _paused = v.GetBoolean();
                _overlay.SetPaused(_paused);
                break;
            default:
                _stats[name] = data is { } v2 ? v2.ToString() : "–";
                _overlay.SetStats(
                    $"hw: {Stat("hwdec-current")} · Drops VO/Dec: {Stat("frame-drop-count")}/{Stat("decoder-frame-drop-count")}" +
                    $" · fps: {Stat("estimated-vf-fps")} · Audio: {Stat("current-tracks/audio/title")} · Sub: {Stat("current-tracks/sub/title")}");
                break;
        }
    }

    private string Stat(string key) => _stats.TryGetValue(key, out var value) ? value : "–";

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space: _ = Player.CommandAsync("cycle", "pause"); break;
            case Key.F11: ToggleFullscreen(); break;
            case Key.Escape when _fullscreen: ToggleFullscreen(); break;
            case Key.Left: _ = Player.CommandAsync("seek", -10, "relative"); break;
            case Key.Right: _ = Player.CommandAsync("seek", 10, "relative"); break;
            default: return;
        }
        e.Handled = true;
    }

    private void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;
        if (_fullscreen)
        {
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
        }
    }

    /// <summary>Legt das Overlay-Fenster exakt über den Video-Host.</summary>
    private void UpdateOverlay()
    {
        if (!IsLoaded || WindowState == WindowState.Minimized) return;

        // Layout muss durchgelaufen sein, bevor Host-Position und -Größe stimmen.
        Dispatcher.BeginInvoke(() =>
        {
            var source = PresentationSource.FromVisual(Host)
                ?? throw new InvalidOperationException("Video-Host hat keine PresentationSource.");
            var topLeft = source.CompositionTarget.TransformFromDevice.Transform(Host.PointToScreen(new Point(0, 0)));
            _overlay.Left = topLeft.X;
            _overlay.Top = topLeft.Y;
            _overlay.Width = Host.ActualWidth;
            _overlay.Height = Host.ActualHeight;
        }, System.Windows.Threading.DispatcherPriority.Render);
    }
}
