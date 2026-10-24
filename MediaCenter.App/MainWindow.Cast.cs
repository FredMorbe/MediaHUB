using System.IO;
using System.Globalization;
using System.Windows;
using MediaCenter.App.Player;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App;

// Übertragen auf einen Fernseher (Google Cast): mpv ruht, der Fernseher holt das Video vom Webserver der App;
// Pause, Spulen, Lautstärke und Spurwahl des Overlays gehen an den Fernseher.
public partial class MainWindow
{
    /// <summary>Die Wiedergabe läuft auf dem Fernseher, nicht in mpv.</summary>
    private bool _casting;

    private CastTrackSet? _castTracks;
    private int? _castAudioStream;
    private IReadOnlyList<CastDevice> _castDevices = Array.Empty<CastDevice>();
    private bool _castSearching;
    private double _castVolume = 0.5;
    private bool _castMuted;

    private void InitCast()
    {
        _overlay.CastSearchRequested += () => _ = SearchCastDevicesAsync();
        _overlay.CastDeviceChosen += id => _ = StartCastAsync(id);
        _overlay.CastStopRequested += () => _ = StopCastAsync(returnToLocal: true, paused: false);

        var cast = Services.Cast;
        cast.Changed += state => Dispatcher.BeginInvoke(() => OnCastChanged(state));
        cast.Finished += () => Dispatcher.BeginInvoke(OnCastFinished);
        cast.VolumeChanged += (level, muted) => Dispatcher.BeginInvoke(() =>
        {
            _castVolume = level;
            _castMuted = muted;
            if (!_casting) return;
            _overlay.SetVolume(level * 100);
            _overlay.SetMuted(muted);
        });
        cast.Ended += reason => Dispatcher.BeginInvoke(() => _ = OnCastEndedAsync(reason));
        cast.Failed += message => Dispatcher.BeginInvoke(() =>
        {
            if (_casting) PlayerNotice(message, isError: true);
        });
    }

    private async Task SearchCastDevicesAsync()
    {
        if (_castSearching) return;

        _castSearching = true;
        _overlay.SetCastDevices(ChoicesOf(_castDevices), searching: true);
        try
        {
            _castDevices = await Services.Cast.FindDevicesAsync();
        }
        catch (Exception ex)
        {
            _castDevices = Array.Empty<CastDevice>();
            PlayerNotice($"Die Suche nach Geräten ist fehlgeschlagen: {ex.Message}", isError: true);
        }
        finally
        {
            _castSearching = false;
            _overlay.SetCastDevices(ChoicesOf(_castDevices), searching: false);
        }
    }

    private static IReadOnlyList<CastChoice> ChoicesOf(IEnumerable<CastDevice> devices) =>
        devices.Select(d => new CastChoice(d.Id, d.Name, d.Model)).ToList();

    /// <summary>Verbindet mit dem Fernseher, hält mpv an und startet das Video dort an der aktuellen Stelle.</summary>
    private async Task StartCastAsync(string deviceId)
    {
        if (_playing is not { } video || _mpv is null || _casting) return;
        if (_castDevices.FirstOrDefault(d => d.Id == deviceId) is not { } device) return;
        if (video.Disc != DiscKind.None)
        {
            PlayerNotice("DVDs und Blu-rays lassen sich nicht auf den Fernseher übertragen.", isError: true);
            return;
        }
        if (_embedding)
        {
            PlayerNotice("Das Einbetten läuft noch.", isError: false);
            return;
        }

        FlushAdjustment();
        FlushColor();
        SaveProgress();
        var start = _position;
        PlayerNotice($"Verbinde mit {device.Name} …", isError: false);
        try
        {
            await Services.Cast.ConnectAsync(device);
            await _mpv.CommandAsync("stop"); // erst nach erfolgreicher Verbindung: scheitert sie, läuft das Video hier einfach weiter
            _casting = true;
            _castAudioStream = null;
            _overlay.SetCastActive(device.Name);
            await LoadCastAsync(video, start);
        }
        catch (Exception ex)
        {
            PlayerNotice($"Übertragen auf {device.Name} nicht möglich: {ex.Message}", isError: true);
            await StopCastAsync(returnToLocal: true, paused: false, position: start);
        }
    }

    /// <summary>Prüft die Datei, wählt Direktwiedergabe oder ffmpeg, stellt die Spuren zusammen und lädt alles auf den Fernseher.</summary>
    private async Task LoadCastAsync(VideoRow video, double start)
    {
        var path = Services.Model.PathOf(video) ?? throw new FileNotFoundException($"Die Datei ist nicht erreichbar: {video.RelPath}");
        var probe = await Services.Probe.ProbeAsync(path);
        var preferences = Services.Settings.Playback;
        var audio = _castAudioStream ?? CastTracks.PreferredAudioStream(probe, preferences);
        var plan = CastCompatibility.Decide(probe, path, new CastCapabilities(Services.Settings.CastHevc), audio);
        var tracks = CastTracks.Build(probe, plan.AudioStreamIndex, Services.Subtitles.List(video.VideoId), CastTracks.FindSidecars(path), preferences);
        var duration = video.DurationSeconds ?? probe.DurationSeconds ?? 0;

        await Services.Cast.LoadAsync(new CastRequest(path, probe, plan, tracks, TitleOf(video), start, duration));

        _castTracks = tracks;
        _position = start;
        _duration = duration;
        _eof = false;
        _overlay.SetDuration(duration);
        _overlay.SetPosition(start);
        ShowCastTracks();
        PlayerNotice($"{Services.Cast.Device?.Name}: {plan.Description}", isError: false);
    }

    private void ShowCastTracks()
    {
        if (_castTracks is null) return;
        _overlay.SetTracks(_castTracks.Tracks.Select(t => new TrackInfo(t.Id, t.Type, t.Label, t.Selected)).ToList());
    }

    private void OnCastChanged(CastState state)
    {
        if (!_casting) return;

        _position = state.Position;
        _overlay.SetPosition(_position);
        if (state.Paused != _paused)
        {
            _paused = state.Paused;
            _overlay.SetPaused(_paused);
            NotifyVideo();
        }
    }

    /// <summary>Das Video ist am Fernseher zu Ende: wie das Ende in mpv (Gesehen merken, Countdown zur nächsten Folge).</summary>
    private void OnCastFinished()
    {
        if (!_casting) return;

        _eof = true;
        SaveProgress();
        if (!Services.Sleep.ConsumeEndOfItem()) BeginNextCountdown();
    }

    /// <summary>Der Fernseher oder das Netz hat die Übertragung beendet: hier angehalten an derselben Stelle weiterschauen.</summary>
    private async Task OnCastEndedAsync(string reason)
    {
        if (!_casting) return;

        await StopCastAsync(returnToLocal: true, paused: true);
        PlayerNotice($"{reason} Das Video steht hier, pausiert, an derselben Stelle.", isError: false);
    }

    /// <summary>Beendet die Übertragung; mit <paramref name="returnToLocal"/> läuft das Video in mpv ab der letzten Position weiter.</summary>
    private async Task StopCastAsync(bool returnToLocal, bool paused, double? position = null)
    {
        var video = _playing;
        var at = position ?? _position;
        var wasCasting = _casting;
        _casting = false;
        _castTracks = null;
        _overlay.SetCastActive(null);
        _overlay.SetTracks(Array.Empty<TrackInfo>());
        await Services.Cast.StopAsync();
        if (!wasCasting && !returnToLocal) return;

        if (returnToLocal && video is not null && PlayerActive)
        {
            try
            {
                await PlayAtAsync(video, resume: false, startAt: at);
                if (paused && _mpv is not null) await _mpv.CommandAsync("set", "pause", "yes");
            }
            catch (Exception ex)
            {
                PlayerNotice($"Die Wiedergabe auf diesem PC ließ sich nicht fortsetzen: {ex.Message}", isError: true);
            }
        }
    }

    // --- Steuerung: Fernseher oder mpv -----------------------------------------------------------------------

    private async Task SetPausedAsync(bool paused)
    {
        if (_casting)
        {
            if (paused) await Services.Cast.PauseAsync();
            else await Services.Cast.PlayAsync();
        }
        else if (_mpv is not null)
        {
            await _mpv.CommandAsync("set", "pause", paused ? "yes" : "no");
        }
    }

    private async Task SeekAsync(double seconds, bool relative)
    {
        if (_casting)
        {
            var target = Math.Max(relative ? _position + seconds : seconds, 0);
            if (_duration > 0) target = Math.Min(target, _duration);
            _position = target; // sofort zeigen; der Fernseher holt bei einem umgewandelten Stream erst das Bild nach
            _overlay.SetPosition(target);
            await Services.Cast.SeekAsync(target);
        }
        else if (_mpv is not null)
        {
            await _mpv.CommandAsync("seek", seconds, relative ? "relative" : "absolute");
        }
    }

    /// <param name="volume">0 bis 100, wie der Regler im Player.</param>
    private async Task SetVolumeAsync(double volume)
    {
        if (_casting)
        {
            _castVolume = Math.Clamp(volume, 0, 100) / 100;
            await Services.Cast.SetVolumeAsync(_castVolume);
        }
        else if (_mpv is not null)
        {
            await _mpv.CommandAsync("set", "volume", volume.ToString(CultureInfo.InvariantCulture));
        }
    }

    private async Task ChangeVolumeAsync(double delta)
    {
        if (_casting)
        {
            _castVolume = Math.Clamp(_castVolume + delta / 100, 0, 1);
            _overlay.SetVolume(_castVolume * 100);
            await Services.Cast.SetVolumeAsync(_castVolume);
        }
        else if (_mpv is not null)
        {
            await _mpv.CommandAsync("add", "volume", delta);
        }
    }

    private async Task ToggleMuteAsync()
    {
        if (_casting)
        {
            _castMuted = !_castMuted;
            _overlay.SetMuted(_castMuted);
            await Services.Cast.SetMutedAsync(_castMuted);
        }
        else if (_mpv is not null)
        {
            await _mpv.CommandAsync("cycle", "mute");
        }
    }

    /// <summary>Wahl einer Ton- oder Untertitelspur im Menü, solange übertragen wird.</summary>
    private async Task ChooseCastTrackAsync(string type, int id)
    {
        if (_castTracks is not { } set || _playing is not { } video) return;

        try
        {
            if (type == "sub")
            {
                await Services.Cast.SetSubtitleAsync(id < 0 ? null : id);
            }
            else if (set.Tracks.FirstOrDefault(t => t.Type == "audio" && t.Id == id)?.FfIndex is { } streamIndex)
            {
                // Der Fernseher spielt im MP4 nur die erste Tonspur: für eine andere startet der Stream neu, an der aktuellen Stelle.
                _castAudioStream = streamIndex;
                await LoadCastAsync(video, _position);
                return;
            }
            else
            {
                return;
            }

            var tracks = set.Tracks.Select(t => t.Type == type ? t with { Selected = t.Id == id } : t).ToList();
            _castTracks = set with { Tracks = tracks, ActiveSubtitleId = type == "sub" ? (id < 0 ? null : id) : set.ActiveSubtitleId };
            ShowCastTracks();
        }
        catch (Exception ex)
        {
            PlayerNotice($"Die Spur ließ sich am Fernseher nicht wählen: {ex.Message}", isError: true);
        }
    }
}
