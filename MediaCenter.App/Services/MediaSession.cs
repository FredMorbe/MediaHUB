using System.IO;
using System.Windows.Threading;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;

namespace MediaCenter.App.Services;

public enum MediaKind
{
    Music,
    Video,
}

/// <summary>Was gerade läuft, so wie Windows es anzeigt (Lautstärke-Flyout, Sperrbildschirm, Kopfhörer- und Tastaturtasten).</summary>
/// <param name="Position">Sekunden; zusammen mit <paramref name="Duration"/> 0 bei Radio.</param>
public sealed record MediaInfo(
    MediaKind Kind, string Title, string? Artist, string? Album, string? CoverPath, bool Playing, bool CanNext, bool CanPrevious, double Position, double Duration);

/// <summary>Ein Player, den die Medientasten steuern können (Musik/Radio/Podcast oder Video).</summary>
public interface IMediaSource
{
    /// <summary>null, solange nichts läuft und keine Pause gehalten wird.</summary>
    MediaInfo? Current { get; }

    /// <summary>Titel, Pause oder Warteschlange haben sich geändert; kommt auf dem UI-Thread.</summary>
    event Action? Changed;

    Task SetPlayingAsync(bool playing);

    Task NextAsync();

    /// <summary>Wie die Taste „Zurück“ im Player: nach einigen Sekunden an den Anfang, sonst zum vorherigen.</summary>
    Task PreviousAsync();

    Task StopAsync();
}

/// <summary>
/// Meldet Windows, was läuft, und führt die Medientasten aus (Wiedergabe/Pause, Weiter, Zurück, Stopp), auch wenn das Fenster nicht im Vordergrund ist.
/// Die Quellen stehen nach Vorrang: die erste, die etwas spielt, bestimmt Anzeige und Ziel der Tasten (Video vor Musik).
/// </summary>
public sealed class MediaSession : IDisposable
{
    private readonly SystemMediaTransportControls _controls;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly IReadOnlyList<IMediaSource> _sources;
    private string? _shownKey;
    private int _coverRequest;

    /// <summary>Etwas ging schief (z. B. das Cover ließ sich nicht lesen); die Wiedergabe ist davon nicht betroffen.</summary>
    public event Action<string>? Failed;

    /// <param name="window">Fenster-Handle der Hauptfenster; Windows ordnet die Medientasten diesem Fenster zu.</param>
    public MediaSession(IntPtr window, IReadOnlyList<IMediaSource> sources)
    {
        _sources = sources;
        _controls = SystemMediaTransportControlsInterop.GetForWindow(window);
        _controls.ButtonPressed += (_, e) => _dispatcher.BeginInvoke(new Action(async () => await OnButtonAsync(e.Button)));
        foreach (var source in sources) source.Changed += Refresh;
        Refresh();
    }

    private IMediaSource? Active() => _sources.FirstOrDefault(s => s.Current is not null);

    private async Task OnButtonAsync(SystemMediaTransportControlsButton button)
    {
        if (Active() is not { } source) return;
        switch (button)
        {
            case SystemMediaTransportControlsButton.Play: await source.SetPlayingAsync(true); break;
            case SystemMediaTransportControlsButton.Pause: await source.SetPlayingAsync(false); break;
            case SystemMediaTransportControlsButton.Stop: await source.StopAsync(); break;
            case SystemMediaTransportControlsButton.Next: await source.NextAsync(); break;
            case SystemMediaTransportControlsButton.Previous: await source.PreviousAsync(); break;
        }
    }

    private void Refresh()
    {
        if (Active()?.Current is not { } info)
        {
            // Nichts läuft: Windows soll die Tasten wieder anderen Programmen geben.
            _controls.PlaybackStatus = MediaPlaybackStatus.Closed;
            _controls.IsEnabled = false;
            _shownKey = null;
            _coverRequest++;
            return;
        }

        _controls.IsEnabled = true;
        _controls.IsPlayEnabled = true;
        _controls.IsPauseEnabled = true;
        _controls.IsStopEnabled = true;
        _controls.IsNextEnabled = info.CanNext;
        _controls.IsPreviousEnabled = info.CanPrevious;
        _controls.PlaybackStatus = info.Playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;

        var key = $"{info.Kind}|{info.Title}|{info.Artist}|{info.Album}|{info.CoverPath}";
        if (key != _shownKey)
        {
            _shownKey = key;
            ShowDetails(info);
        }

        if (info.Duration > 0 && double.IsFinite(info.Duration) && double.IsFinite(info.Position))
        {
            var end = TimeSpan.FromSeconds(info.Duration);
            _controls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                MinSeekTime = TimeSpan.Zero,
                Position = TimeSpan.FromSeconds(Math.Clamp(info.Position, 0, info.Duration)),
                MaxSeekTime = end,
                EndTime = end,
            });
        }
    }

    /// <summary>Titel, Künstler und Cover; das Cover kommt asynchron, ein inzwischen neuerer Titel verwirft ein spät eintreffendes.</summary>
    private async void ShowDetails(MediaInfo info)
    {
        var request = ++_coverRequest;
        var updater = _controls.DisplayUpdater;
        updater.ClearAll();
        if (info.Kind == MediaKind.Video)
        {
            updater.Type = MediaPlaybackType.Video;
            updater.VideoProperties.Title = info.Title;
        }
        else
        {
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = info.Title;
            updater.MusicProperties.Artist = info.Artist ?? "";
            updater.MusicProperties.AlbumTitle = info.Album ?? "";
        }
        updater.Update();

        if (info.CoverPath is null) return;
        try
        {
            if (!File.Exists(info.CoverPath)) return; // das Cover liegt im Cache und kann zwischendurch aufgeräumt worden sein
            var file = await StorageFile.GetFileFromPathAsync(info.CoverPath);
            if (request != _coverRequest) return;
            updater.Thumbnail = RandomAccessStreamReference.CreateFromFile(file);
            updater.Update();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Failed?.Invoke($"Das Cover für die Windows-Mediensteuerung ließ sich nicht lesen: {ex.Message}");
        }
    }

    public void Dispose()
    {
        foreach (var source in _sources) source.Changed -= Refresh;
        _controls.IsEnabled = false;
    }
}

/// <summary>Musik, Radio und Podcasts für die Medientasten.</summary>
public sealed class AudioMediaSource(AudioController audio) : IMediaSource
{
    public event Action? Changed
    {
        add => audio.Changed += value;
        remove => audio.Changed -= value;
    }

    public MediaInfo? Current
    {
        get
        {
            if (audio.Current is not { } item) return null;

            // Radio: die Titelzeile des Senders ist der Titel, der Sendername der „Künstler“. Titel: „Künstler · Album“ steht in Subtitle.
            string title, artist, album = "";
            if (item.Kind == QueueKind.Radio)
            {
                title = audio.StreamTitle ?? item.Title;
                artist = audio.StreamTitle is null ? item.Subtitle : item.Title;
            }
            else
            {
                title = item.Title;
                var parts = item.Subtitle.Split(" · ", 2);
                artist = parts[0];
                if (parts.Length > 1) album = parts[1];
            }

            var live = item.Kind == QueueKind.Radio;
            return new MediaInfo(MediaKind.Music, title, artist, album, item.CoverPath, !audio.Paused,
                CanNext: !live && audio.Index < audio.Queue.Count - 1, CanPrevious: !live, Position: audio.Position, Duration: live ? 0 : audio.Duration);
        }
    }

    public async Task SetPlayingAsync(bool playing)
    {
        if (playing == audio.Paused) await audio.TogglePauseAsync();
    }

    public Task NextAsync() => audio.NextAsync();

    public Task PreviousAsync() => audio.PreviousAsync();

    public Task StopAsync() => audio.StopAsync();
}
