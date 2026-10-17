using System.Windows;
using System.Windows.Input;

namespace MediaCenter.Spike;

public partial class OverlayWindow : Window
{
    private bool _seeking;

    public event Action? OpenClicked;
    public event Action? PlayPauseClicked;
    public event Action? AudioClicked;
    public event Action? SubClicked;
    public event Action? FullscreenClicked;
    public event Action<double>? SeekRequested;

    public OverlayWindow()
    {
        InitializeComponent();

        OpenButton.Click += (_, _) => OpenClicked?.Invoke();
        PlayPauseButton.Click += (_, _) => PlayPauseClicked?.Invoke();
        AudioButton.Click += (_, _) => AudioClicked?.Invoke();
        SubButton.Click += (_, _) => SubClicked?.Invoke();
        FullscreenButton.Click += (_, _) => FullscreenClicked?.Invoke();

        SeekSlider.PreviewMouseLeftButtonDown += (_, _) => _seeking = true;
        SeekSlider.PreviewMouseLeftButtonUp += (_, _) =>
            // Wert erst nach dem Slider-eigenen Mouse-Up-Handling lesen.
            Dispatcher.BeginInvoke(() =>
            {
                SeekRequested?.Invoke(SeekSlider.Value);
                _seeking = false;
            });
    }

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

    public void SetPaused(bool paused) => PlayPauseButton.Content = paused ? "Play" : "Pause";

    public void SetStats(string text) => StatsText.Text = text;

    private static string Format(double seconds) => TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss");
}
