using System.Windows;
using System.Windows.Threading;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App;

// Heimnetz-Server: Das Hauptfenster stellt dem Server (Services\RemoteServer) bereit, was die Fernbedienung im Browser braucht.
// Der Server läuft auf eigenen Threads; alles, was Oberfläche oder Datenbank anfasst, läuft hier über den Oberflächen-Thread.
public partial class MainWindow : IRemoteHost
{
    /// <summary>Lautstärke und Stummschaltung von mpv (die Ereignisse kommen aus OnMpvProperty).</summary>
    private double _mpvVolume = 100;

    private bool _mpvMuted;

    /// <summary>Startet den Heimnetz-Server, wenn er in den Einstellungen an ist; ein Fehler (Anschluss belegt) erscheint als Meldung.</summary>
    private async Task StartHomeServerAsync()
    {
        try
        {
            Services.RemoteHost = this;
            if (await Services.ApplyHomeServerAsync() is { } problem) ShowMessage(problem, isError: true);
        }
        catch (Exception ex)
        {
            ShowMessage($"Der Heimnetz-Server lässt sich nicht starten: {ex.Message}", isError: true);
        }
    }

    public Task<T> OnUiAsync<T>(Func<T> work) => Dispatcher.InvokeAsync(work, DispatcherPriority.Normal).Task;

    public NowPlayingInfo Now()
    {
        if (!PlayerActive) return new NowPlayingInfo(false, null, null, 0, 0, false, _mpvVolume, _mpvMuted, false, false, null, false);

        var volume = _casting ? _castVolume * 100 : _mpvVolume;
        var muted = _casting ? _castMuted : _mpvMuted;
        if (_live is { } channel)
            return new NowPlayingInfo(true, channel.Name, "Live", 0, 0, _paused, volume, muted, true, false, null, _liveList.Count > 1);

        return new NowPlayingInfo(
            true, _playing is { } video ? TitleOf(video) : null, _playing?.Hdr is { } hdr and not HdrFormat.None ? hdr.Label() : null,
            _position, _duration, _paused, volume, muted, false, _casting, _playing?.VideoId, _nextVideo is not null);
    }

    public Task PlayAsync(long videoId, bool resume)
    {
        var video = Services.Model.VideosByTitle.Values.SelectMany(v => v).FirstOrDefault(v => v.VideoId == videoId)
            ?? throw new InvalidOperationException("Das Video gibt es in dieser Bibliothek nicht.");
        return PlayAsync(video, resume);
    }

    public void Press(ControllerButton button)
    {
        // Ein Druck vom Handy holt das Fenster nach vorn: nur im Vordergrund bewegt sich der Fokus der App.
        if (!IsActive && !_overlay.IsActive)
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }
        Services.Controller.Inject(button);
    }

    public bool Act(PlayerAction action, bool large)
    {
        if (!PlayerActive) return false;

        var done = RunPlayerAction(action, large);
        if (done) _lastActivity = DateTime.UtcNow;
        return done;
    }

    public void SeekTo(double seconds)
    {
        if (PlayerActive && _live is null) _ = SeekAsync(seconds, relative: false);
    }

    public void SetVolume(double volume)
    {
        if (PlayerActive) _ = SetVolumeAsync(volume);
    }
}
