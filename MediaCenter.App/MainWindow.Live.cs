using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using MediaCenter.App.Player;
using MediaCenter.Core;

namespace MediaCenter.App;

// Live-TV: Ein Sender aus einer M3U-Liste läuft im selben Player wie ein Video, nur ohne Zeitleiste, Fortschritt und Übertragen auf den Fernseher.
// Bild auf/ab und die Knöpfe wechseln zwischen den Sendern der Ansicht, aus der der Sender gestartet wurde; das Sendermenü ersetzt das Folgenmenü.
public partial class MainWindow
{
    /// <summary>So oft versucht die App, einen abgebrochenen Stream neu zu öffnen, bevor sie aufgibt.</summary>
    private const int LiveMaxRetries = 3;

    private LiveChannel? _live;
    private IReadOnlyList<LiveChannel> _liveList = Array.Empty<LiveChannel>();
    private int _liveRetries;

    /// <summary>Hält „Jetzt“ und „Danach“ im Titel aktuell.</summary>
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    private void InitLive()
    {
        _overlay.PreviousChannelClicked += () => _ = ZapAsync(-1);
        _liveTimer.Tick += (_, _) => UpdateLiveTitle();
    }

    public async Task PlayLiveAsync(LiveChannel channel, IReadOnlyList<LiveChannel> zapList)
    {
        if (_casting)
        {
            ShowMessage("Live-TV läuft nicht während einer Übertragung auf den Fernseher. Beende zuerst die Übertragung.", isError: true);
            return;
        }

        if (_playing is not null)
        {
            // Wechsel aus einem Video heraus: das verlassene Video sichern
            SaveProgress();
            FlushAdjustment();
            FlushColor();
        }
        _playing = null;
        _nextVideo = null;
        CancelNextCountdown(declined: false);
        _nextDeclined = false;

        await Services.Audio.PauseAsync(); // Video und Musik nicht gleichzeitig
        if (!PlayerActive) _fullscreenBeforePlayer = _fullscreen;
        PlayerLayer.Visibility = Visibility.Visible;
        Focus();
        _overlay.Owner = this;
        _overlay.Show();
        UpdateOverlay();

        try
        {
            await EnsureMpvAsync();
            _live = channel;
            _liveList = zapList;
            _liveRetries = 0;
            _position = 0;
            _duration = 0;
            _eof = false;

            _overlay.SetNextEpisode(null);
            _overlay.SetLive(true); // nach SetNextEpisode: im Live-Modus ist der „Nächste“-Knopf der Senderwechsel
            _overlay.SetTracks(Array.Empty<TrackInfo>());
            _chapters = Array.Empty<Chapter>();
            _overlay.SetChapters(_chapters);
            _overlay.SetEpisodes(ChannelChoices(zapList), channel.Id);
            _overlay.SetPaused(false);
            _overlay.SetDuration(0);
            UpdateLiveTitle();

            // Wie bei einem Video neu setzen, was von der vorigen Datei übrig sein könnte; Untertitel und Ton wählt mpv selbst.
            var preferences = Services.Settings.Playback;
            _tracks = new List<MediaTrack>();
            _fileReady = false;
            _subtitlePicked = false;
            _switching = true;
            await _mpv!.CommandAsync("set", "alang", preferences.AudioMpvList);
            await _mpv.CommandAsync("set", "aid", "auto");
            await _mpv.CommandAsync("set", "sid", "no");
            await _mpv.CommandAsync("set", "sub-files", "");
            _adjustment = SubtitleAdjustment.Default;
            _adjustDirty = false;
            _overlay.SetAdjustment(_adjustment);
            _overlay.SetCanEmbed(false);
            await ApplyAdjustmentAsync(_adjustment);
            await ApplySubtitleStyleAsync(Services.Settings.SubtitleStyle);
            await ResetPictureAsync();
            _color = ColorSettings.Default;
            _colorDirty = false;
            _appliedColor = null;
            ResetHdrState();
            _overlay.SetColor(_color);
            await ApplyColorAsync(_color);

            // Ein Sender kann kurz abreißen: mpv soll die Verbindung selbst wieder aufnehmen, und eine tote Adresse soll nicht eine Minute hängen.
            await _mpv.CommandAsync("set", "stream-lavf-o", "reconnect=1,reconnect_streamed=1,reconnect_delay_max=5");
            await _mpv.CommandAsync("set", "network-timeout", "15");
            await _mpv.CommandAsync("loadfile", channel.Url, "replace");
            await _mpv.CommandAsync("set", "pause", "no");
            _lastActivity = DateTime.UtcNow;
            _idleTimer.Start();
            _liveTimer.Start();
            NotifyVideo();
        }
        catch
        {
            // Start fehlgeschlagen: Player-Ansicht wieder schließen, damit man nicht vor einem schwarzen Bild sitzt.
            await ExitPlayerAsync();
            throw;
        }
    }

    /// <summary>Beendet den Live-Modus des Players (Sender verlassen: Esc, oder ein Video startet); stellt die Optionen für Videodateien wieder her.</summary>
    private async Task EndLiveAsync()
    {
        if (_live is null) return;

        _live = null;
        _liveList = Array.Empty<LiveChannel>();
        _liveRetries = 0;
        _liveTimer.Stop();
        _overlay.SetLive(false);
        _overlay.SetEpisodes(Array.Empty<EpisodeChoice>(), 0);
        if (_mpv is not null) await _mpv.CommandAsync("set", "stream-lavf-o", "");
    }

    /// <summary>Wechselt zum Sender davor (<paramref name="direction"/> −1) oder danach (+1) in der Liste der Ansicht; am Ende geht es von vorn los.</summary>
    private async Task ZapAsync(int direction)
    {
        if (_live is not { } current || _liveList.Count < 2) return;

        var index = _liveList.ToList().FindIndex(c => c.Id == current.Id);
        var next = _liveList[(Math.Max(index, 0) + direction + _liveList.Count) % _liveList.Count];
        await PlayChannelAsync(next);
    }

    /// <summary>Sender aus dem Sendermenü des Players.</summary>
    private async Task PlayChannelAsync(long channelId)
    {
        if (_live is null || _live.Id == channelId) return;
        if (_liveList.FirstOrDefault(c => c.Id == channelId) is not { } channel) return;
        await PlayChannelAsync(channel);
    }

    private async Task PlayChannelAsync(LiveChannel channel)
    {
        try
        {
            await PlayLiveAsync(channel, _liveList);
        }
        catch (Exception ex)
        {
            // Der Aufruf kommt aus Ereignissen (Taste, Knopf, Menü): ohne Meldung ginge der Fehler verloren.
            PlayerNotice($"Der Sender „{channel.Name}“ lässt sich nicht starten: {ex.Message}", isError: true);
        }
    }

    /// <summary>Der Stream ist zu Ende (Anbieter hat getrennt): ein paar Mal neu öffnen, dann aufgeben.</summary>
    private async Task ReconnectLiveAsync()
    {
        if (_live is not { } channel || _mpv is null) return;
        if (++_liveRetries > LiveMaxRetries)
        {
            PlayerNotice($"Der Sender „{channel.Name}“ sendet nicht mehr.", isError: true);
            return;
        }

        PlayerNotice($"Verbindung zu „{channel.Name}“ verloren – neuer Versuch {_liveRetries} von {LiveMaxRetries} …", isError: false);
        await Task.Delay(TimeSpan.FromSeconds(2));
        if (_live?.Id != channel.Id || _mpv is null) return; // inzwischen gewechselt oder beendet
        await _mpv.CommandAsync("loadfile", channel.Url, "replace");
    }

    /// <summary>Titel mit dem Sendernamen und, wo der Programmführer es kennt, „Jetzt“ und „Danach“.</summary>
    private void UpdateLiveTitle()
    {
        if (_live is not { } channel) return;

        var kicker = "Live";
        if (channel.EpgId is { } epgId)
        {
            var (now, next) = Services.Db.GetLiveNowNext(channel.SourceId, epgId, DateTime.UtcNow);
            if (now is not null) kicker += $" · Jetzt: {now.Title} (bis {now.StopUtc.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture)})";
            if (next is not null) kicker += $" · Danach: {next.Title}";
        }
        _overlay.SetTitle(channel.Name, kicker);
    }

    /// <summary>Die Sender der Ansicht als Auswahl, nach Gruppen geordnet (die Reihenfolge der Gruppen ist die der Liste).</summary>
    private static IReadOnlyList<EpisodeChoice> ChannelChoices(IReadOnlyList<LiveChannel> channels)
    {
        var groups = new List<string>();
        var choices = new List<EpisodeChoice>(channels.Count);
        foreach (var channel in channels)
        {
            var group = channel.Group ?? "";
            var index = groups.IndexOf(group);
            if (index < 0)
            {
                groups.Add(group);
                index = groups.Count - 1;
            }
            choices.Add(new EpisodeChoice(channel.Id, index, channel.Number is { } number ? $"{number}  {channel.Name}" : channel.Name,
                Watched: false, Reachable: true, GroupLabel: group.Length == 0 ? "Sender" : group));
        }
        return choices;
    }
}
