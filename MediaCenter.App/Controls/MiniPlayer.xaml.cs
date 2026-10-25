using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App.Controls;

/// <summary>Leiste unten im Fenster für Musik, Radio und Podcasts; erscheint, solange etwas in der Warteschlange ist.</summary>
public partial class MiniPlayer : UserControl
{
    private AudioController? _audio;
    private AppServices? _services;
    private Action? _openSoundSettings;
    private bool _seeking;
    private bool _updatingVolume;

    public MiniPlayer()
    {
        InitializeComponent();
        Visibility = Visibility.Collapsed;

        PrevButton.Click += async (_, _) => await Audio.PreviousAsync();
        NextButton.Click += async (_, _) => await Audio.NextAsync();
        BackSkipButton.Click += async (_, _) => await Audio.SeekRelativeAsync(-10);
        ForwardSkipButton.Click += async (_, _) => await Audio.SeekRelativeAsync(30);
        PlayPauseButton.Click += async (_, _) => await Audio.TogglePauseAsync();
        MuteButton.Click += async (_, _) => await Audio.ToggleMuteAsync();
        CloseButton.Click += async (_, _) => await Audio.StopAsync();
        SleepButton.Click += (_, _) => ContextPopup.Show(SleepButton, SleepItems(), "Einschlaf-Timer");
        SoundButton.Click += (_, _) => ContextPopup.Show(SoundButton, SoundItems(), "Klang");

        // handledEventsToo: Ein Klick auf die Linie wird vom Slider selbst behandelt (IsMoveToPointEnabled) und als erledigt
        // markiert; ohne diese Anmeldung bliebe _seeking aus, und die nächste Positionsmeldung setzte den Regler zurück.
        SeekSlider.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, _) => _seeking = true), handledEventsToo: true);
        SeekSlider.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) =>
            // Wert erst nach der eigenen Behandlung des Sliders lesen.
            Dispatcher.BeginInvoke(async () =>
            {
                await Audio.SeekAsync(SeekSlider.Value);
                _seeking = false;
            })), handledEventsToo: true);
        VolumeSlider.ValueChanged += async (_, e) =>
        {
            if (!_updatingVolume) await Audio.SetVolumeAsync(e.NewValue);
        };
    }

    private AudioController Audio => _audio ?? throw new InvalidOperationException("MiniPlayer ist noch nicht mit dem Audio-Controller verbunden.");

    /// <param name="openSoundSettings">Öffnet die Einstellungen beim Equalizer („Equalizer einstellen …“ im Klang-Menü).</param>
    public void Attach(AudioController audio, AppServices services, Action openSoundSettings)
    {
        _audio = audio;
        _services = services;
        _openSoundSettings = openSoundSettings;
        audio.Changed += Refresh;
        audio.Tick += UpdatePosition;
        services.Sleep.Changed += UpdateSleep;
        Refresh();
        UpdateSleep();
    }

    private AppServices Services => _services ?? throw new InvalidOperationException("MiniPlayer ist noch nicht mit den Diensten verbunden.");

    // --- Einschlaf-Timer und Klang --------------------------------------------------------------------------

    private void UpdateSleep()
    {
        var sleep = Services.Sleep;
        SleepText.Visibility = sleep.Mode == SleepMode.Off ? Visibility.Collapsed : Visibility.Visible;
        SleepText.Text = sleep.Label;
        SleepButton.ToolTip = sleep.Mode == SleepMode.Off ? "Einschlaf-Timer" : $"Einschlaf-Timer: {sleep.Label}";
    }

    private IReadOnlyList<(string Text, Action Click)> SleepItems()
    {
        var sleep = Services.Sleep;
        var items = new List<(string, Action)>();
        if (sleep.Mode != SleepMode.Off) items.Add(($"Timer abbrechen ({sleep.Label})", sleep.Cancel));
        foreach (var minutes in SleepTimer.Minutes)
        {
            var span = TimeSpan.FromMinutes(minutes);
            items.Add(($"In {minutes} Minuten anhalten", () => sleep.StartCountdown(span)));
        }
        // Radio hat kein Ende; dort gibt es nur den Countdown.
        if (Audio.Current is { Kind: not QueueKind.Radio } item)
        {
            items.Add((item.Kind == QueueKind.Podcast ? "Am Ende der Folge anhalten" : "Am Ende des Titels anhalten", sleep.StartEndOfItem));
        }
        return items;
    }

    private IReadOnlyList<(string Text, Action Click)> SoundItems()
    {
        var sound = Services.Sound;
        var items = new List<(string, Action)>();
        foreach (var preset in Equalizer.Presets)
        {
            var id = preset.Id;
            items.Add(((sound.Preset == id ? "✓ " : "") + preset.Name, () => sound.SetPreset(id)));
        }
        if (sound.Preset == "custom") items.Add(("✓ Eigene Einstellung", () => { }));
        items.Add(("Equalizer einstellen …", _openSoundSettings ?? throw new InvalidOperationException("MiniPlayer ist noch nicht mit den Diensten verbunden.")));
        return items;
    }

    private void Refresh()
    {
        if (_audio is not { IsActive: true, Current: { } item } audio)
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;

        var isRadio = item.Kind == QueueKind.Radio;
        var isTrack = item.Kind == QueueKind.Track;
        TitleText.Text = item.Title;
        SubText.Text = isRadio && !string.IsNullOrWhiteSpace(audio.StreamTitle) ? audio.StreamTitle : item.Subtitle;

        var cover = ImageCache.Load(item.CoverPath, 116);
        CoverBox.Background = cover is null ? (Brush)FindResource("CardBgBrush") : new ImageBrush(cover) { Stretch = Stretch.UniformToFill };
        CoverGlyph.Visibility = cover is null ? Visibility.Visible : Visibility.Collapsed;
        
        PrevButton.Visibility = NextButton.Visibility = isTrack ? Visibility.Visible : Visibility.Collapsed;
        BackSkipButton.Visibility = ForwardSkipButton.Visibility = item.Kind == QueueKind.Podcast ? Visibility.Visible : Visibility.Collapsed;
        SeekArea.Visibility = isRadio ? Visibility.Collapsed : Visibility.Visible;
        LiveBadge.Visibility = isRadio ? Visibility.Visible : Visibility.Collapsed;

        PlayPauseButton.Content = audio.Paused ? "" : "";
        MuteButton.Content = audio.Muted ? "" : "";
        _updatingVolume = true;
        VolumeSlider.Value = audio.Volume;
        _updatingVolume = false;
        UpdatePosition();
    }

    private void UpdatePosition()
    {
        if (_audio is not { IsActive: true } audio) return;
        PositionText.Text = Format(audio.Position);
        DurationText.Text = audio.Duration > 0 ? Format(audio.Duration) : "–";
        SeekSlider.Maximum = Math.Max(audio.Duration, 1);
        if (!_seeking) SeekSlider.Value = Math.Min(audio.Position, SeekSlider.Maximum);
    }

    private static string Format(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(seconds, 0));
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
    }
}
