using System.IO;
using System.Net;
using MediaCenter.Core;
using Sharpcaster;
using Sharpcaster.Models;
using Sharpcaster.Models.Media;

namespace MediaCenter.App.Services;

/// <summary>Ein Gerät im Netz, auf das sich übertragen lässt.</summary>
public sealed record CastDevice(string Id, string Name, string Model, ChromecastReceiver Receiver);

/// <param name="Position">Position im Video in Sekunden (nicht im Stream: bei einem neu gestarteten Stream schon eingerechnet).</param>
public sealed record CastState(double Position, bool Paused, bool Buffering);

/// <param name="Start">Ab hier abspielen.</param>
/// <param name="Duration">Gesamtlänge des Videos in Sekunden (für die Anzeige am Fernseher).</param>
public sealed record CastRequest(string Path, ProbeResult Probe, CastPlan Plan, CastTrackSet Tracks, string Title, double Start, double Duration);

/// <summary>
/// Überträgt ein Video per Google Cast: findet Geräte, startet den Standard-Empfänger, lädt die Adresse des eigenen Webservers
/// und steuert die Wiedergabe. Spulen in einem umgewandelten Stream startet ffmpeg an der neuen Stelle neu; <see cref="CastState.Position"/>
/// rechnet den Startpunkt des Streams schon ein.
/// </summary>
public sealed class CastService : IAsyncDisposable
{
    /// <summary>Kennung des Standard-Medienempfängers von Google, der jede Medienadresse abspielt.</summary>
    private const string DefaultMediaReceiver = "CC1AD845";

    private static readonly TimeSpan ResyncEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FirewallHintAfter = TimeSpan.FromSeconds(15);

    private readonly CastMediaServer _server;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _ticker;
    private ChromecastClient? _client;
    private CastDevice? _device;
    private IPAddress? _localAddress;
    private CastRequest? _request;
    private string? _token;
    private double _offset;
    private double _statusTime;
    private DateTime _statusAt;
    private DateTime _lastResync;
    private bool _playing;
    private bool _buffering;
    private bool _finishedReported;
    private int? _activeSubtitle;
    private int _generation;
    private bool _serverStarted;

    public CastService(string ffmpegPath, SubtitleExtractor subtitles)
    {
        _server = new CastMediaServer(ffmpegPath, subtitles);
        _server.StreamFailed += message => Failed?.Invoke(message);
        _ticker = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Verbunden und ein Video geladen (auch pausiert).</summary>
    public bool IsActive => _client is not null && _request is not null;

    /// <summary>Mit einem Gerät verbunden (auch wenn gerade kein Video läuft).</summary>
    public bool IsConnected => _client is not null;

    public CastDevice? Device => _device;

    public CastRequest? Current => _request;

    /// <summary>Position, Pause oder Puffern haben sich geändert (alle halbe Sekunde, solange ein Video läuft).</summary>
    public event Action<CastState>? Changed;

    /// <summary>Das Video ist am Fernseher zu Ende gelaufen.</summary>
    public event Action? Finished;

    /// <summary>Lautstärke (0 bis 1) und Stummschaltung des Fernsehers.</summary>
    public event Action<double, bool>? VolumeChanged;

    /// <summary>Die Übertragung ist vorbei, ohne dass die App sie beendet hat (Fernseher aus, anderer Absender, Verbindung weg); mit dem Grund.</summary>
    public event Action<string>? Ended;

    /// <summary>Ein Fehler, der die Übertragung nicht beendet, aber gemeldet werden soll (Untertitel nicht lesbar, ffmpeg-Fehler, Firewall).</summary>
    public event Action<string>? Failed;

    public CastMediaServer Server => _server;

    public double Position => _request is null ? 0 : _offset + CurrentStreamTime();

    // --- Suchen und Verbinden ---------------------------------------------------------------------------

    /// <summary>Sucht Geräte im Netz (dauert einige Sekunden). Geräte ohne Bildausgabe (Lautsprecher) fehlen.</summary>
    public async Task<IReadOnlyList<CastDevice>> FindDevicesAsync(CancellationToken ct = default)
    {
        using var locator = new ChromecastLocator();
        var found = await locator.FindReceiversAsync().WaitAsync(ct);
        return found
            .Where(CanShowVideo)
            .Select(r => new CastDevice($"{r.Name}@{r.DeviceUri.Host}:{r.Port}", r.Name, r.Model ?? "", r))
            .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Das Feld „ca“ der Geräte-Ankündigung nennt die Fähigkeiten; Bit 1 ist die Bildausgabe. Fehlt es, bleibt das Gerät in der Liste.</summary>
    private static bool CanShowVideo(ChromecastReceiver receiver) =>
        receiver.ExtraInformation is null
        || !receiver.ExtraInformation.TryGetValue("ca", out var caps)
        || !int.TryParse(caps?.ToString(), out var flags)
        || (flags & 1) != 0;

    public async Task ConnectAsync(CastDevice device, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await DisconnectCoreAsync(stopApplication: false);

            var local = CastMediaServer.LocalAddressFor(IPAddress.Parse(device.Receiver.DeviceUri.Host));
            if (_serverStarted && !local.Equals(_localAddress))
            {
                await _server.DisposeAsync(); // anderes Netz als beim letzten Gerät: der Server muss an der neuen Adresse lauschen
                _serverStarted = false;
            }
            if (!_serverStarted)
            {
                await _server.StartAsync(local, ct);
                _serverStarted = true;
            }
            _localAddress = local;

            var client = new ChromecastClient();
            await client.ConnectChromecast(device.Receiver).WaitAsync(ct);
            client.Disconnected += OnDisconnected;
            client.MediaChannel.StatusChanged += OnMediaStatus;
            client.MediaChannel.ErrorHappened += (_, e) => Failed?.Invoke($"Der Fernseher meldet einen Fehler: {e}");
            client.MediaChannel.LoadFailed += (_, _) => Failed?.Invoke("Der Fernseher konnte das Video nicht laden.");
            client.ReceiverChannel.ReceiverStatusChanged += OnReceiverStatus;
            _client = client;
            _device = device;

            await client.LaunchApplicationAsync(DefaultMediaReceiver).WaitAsync(ct);
        }
        catch
        {
            await DisconnectCoreAsync(stopApplication: false);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    // --- Laden und Steuern ------------------------------------------------------------------------------

    /// <summary>Lädt das Video auf den Fernseher und spielt es ab <see cref="CastRequest.Start"/>.</summary>
    public async Task LoadAsync(CastRequest request, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await LoadCoreAsync(request, request.Start, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task LoadCoreAsync(CastRequest request, double start, CancellationToken ct)
    {
        var client = _client ?? throw new InvalidOperationException("Es besteht keine Verbindung zu einem Fernseher.");
        var local = _localAddress ?? throw new InvalidOperationException("Die Netzwerkadresse dieses PCs ist unbekannt.");

        if (_token is not null) _server.Unregister(_token);
        var token = _server.Register(new CastItem(request.Path, request.Probe, request.Plan, request.Tracks.Subtitles));
        _server.PrepareSubtitles(token);

        // Ein direkt gespieltes MP4 spult der Fernseher selbst; ein Stream beginnt bei der Startposition neu.
        var streamStart = request.Plan.Mode == CastMode.Stream ? start : 0;
        var port = _server.Port;
        var media = new Media
        {
            ContentUrl = CastMediaServer.MediaUrl(local, port, token, streamStart),
            ContentId = CastMediaServer.MediaUrl(local, port, token, streamStart),
            ContentType = CastPlan.ContentType,
            StreamType = StreamType.Buffered,
            Duration = Math.Max(request.Duration - streamStart, 0) is var duration and > 0 ? duration : null,
            Metadata = new MovieMetadata { MetadataType = MetadataType.Movie, Title = request.Title },
            Tracks = request.Tracks.Subtitles.Select(s => new Track
            {
                TrackId = s.Id,
                Type = TrackType.TEXT,
                Subtype = TextTrackType.SUBTITLES,
                TrackContentId = CastMediaServer.SubtitleUrl(local, port, token, s.Id, streamStart),
                TrackContentType = "text/vtt",
                Language = s.Language,
                Name = s.Name,
            }).ToArray(),
        };

        var generation = Interlocked.Increment(ref _generation);
        _token = token;
        _request = request;
        _offset = streamStart;
        _statusTime = 0;
        _statusAt = DateTime.UtcNow;
        _finishedReported = false;
        _playing = false;
        _buffering = true;
        _activeSubtitle = request.Tracks.ActiveSubtitleId;

        int[]? active = _activeSubtitle is { } id ? [id] : null;
        await client.MediaChannel.LoadAsync(media, autoPlay: true, activeTrackIds: active).WaitAsync(ct);
        if (request.Plan.Mode == CastMode.Direct && start > 0) await client.MediaChannel.SeekAsync(start).WaitAsync(ct);

        _lastResync = DateTime.UtcNow;
        _ticker.Change(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
        _ = WatchFirstRequestAsync(token, generation);
    }

    /// <summary>Holt der Fernseher nach einer Weile nichts ab, erreicht er den PC nicht – meist blockt die Windows-Firewall.</summary>
    private async Task WatchFirstRequestAsync(string token, int generation)
    {
        await Task.Delay(FirewallHintAfter);
        if (generation == _generation && _token == token && _server.RequestCount(token) == 0)
        {
            Failed?.Invoke("Der Fernseher erreicht diesen PC nicht. Meist blockt die Windows-Firewall: Zugriff für „MediaCenter“ im privaten Netzwerk erlauben (und prüfen, dass das Netzwerk als „privat“ gilt).");
        }
    }

    public async Task PlayAsync()
    {
        if (_client is { } client && _request is not null) await client.MediaChannel.PlayAsync();
    }

    public async Task PauseAsync()
    {
        if (_client is { } client && _request is not null) await client.MediaChannel.PauseAsync();
    }

    /// <summary>Springt an die Stelle (Sekunden im Video). Bei einem umgewandelten Stream startet ffmpeg dort neu.</summary>
    public async Task SeekAsync(double seconds)
    {
        await _gate.WaitAsync();
        try
        {
            if (_request is not { } request || _client is not { } client) return;
            seconds = Math.Max(seconds, 0);
            if (request.Duration > 0) seconds = Math.Min(seconds, Math.Max(request.Duration - 1, 0));

            if (request.Plan.Mode == CastMode.Direct)
            {
                await client.MediaChannel.SeekAsync(seconds);
                _statusTime = seconds;
                _statusAt = DateTime.UtcNow;
            }
            else
            {
                await LoadCoreAsync(request, seconds, CancellationToken.None);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Wählt die Untertitelspur (Id aus <see cref="CastTrackSet"/>); null schaltet sie ab.</summary>
    public async Task SetSubtitleAsync(int? id)
    {
        if (_client is not { } client || _request is null) return;
        _activeSubtitle = id;
        await client.MediaChannel.EditTracksAsync(id is { } value ? [value] : []);
    }

    /// <summary>Lautstärke des Fernsehers, 0 bis 1.</summary>
    public async Task SetVolumeAsync(double level)
    {
        if (_client is { } client) await client.ReceiverChannel.SetVolume(Math.Clamp(level, 0, 1));
    }

    public async Task SetMutedAsync(bool muted)
    {
        if (_client is { } client) await client.ReceiverChannel.SetMute(muted);
    }

    /// <summary>Beendet die Übertragung und lässt den Fernseher in seinen Ruhezustand zurückgehen.</summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await DisconnectCoreAsync(stopApplication: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DisconnectCoreAsync(bool stopApplication)
    {
        _ticker.Change(Timeout.Infinite, Timeout.Infinite);
        Interlocked.Increment(ref _generation);
        var client = _client;
        _client = null;
        _device = null;
        _request = null;
        if (_token is not null) _server.Unregister(_token);
        _token = null;
        if (client is null) return;

        client.Disconnected -= OnDisconnected;
        client.MediaChannel.StatusChanged -= OnMediaStatus;
        client.ReceiverChannel.ReceiverStatusChanged -= OnReceiverStatus;
        try
        {
            if (stopApplication) await client.ReceiverChannel.StopApplication().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or IOException or System.Net.Sockets.SocketException)
        {
            // Der Fernseher ist schon weg; die Verbindung wird ohnehin geschlossen.
        }
        try
        {
            await client.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            // Siehe oben: eine tote Verbindung braucht keinen sauberen Abschied.
        }
    }

    // --- Rückmeldungen vom Fernseher -----------------------------------------------------------------------

    private double CurrentStreamTime() =>
        _statusTime + (_playing ? (DateTime.UtcNow - _statusAt).TotalSeconds : 0);

    private void Tick()
    {
        if (_request is null || _client is null) return;

        Changed?.Invoke(new CastState(Position, !_playing, _buffering));
        if (DateTime.UtcNow - _lastResync > ResyncEvery)
        {
            _lastResync = DateTime.UtcNow;
            // Der Empfänger meldet die Zeit nur bei Änderungen; die Nachfrage hält die Anzeige nach Pufferpausen und Ruckeln genau.
            _ = _client.MediaChannel.GetMediaStatusAsync().ContinueWith(
                t => Failed?.Invoke($"Der Fernseher antwortet nicht: {t.Exception!.GetBaseException().Message}"),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }

    private void OnMediaStatus(object? sender, MediaStatus status)
    {
        if (_request is null) return;

        _statusTime = status.CurrentTime;
        _statusAt = DateTime.UtcNow;
        _playing = status.PlayerState == PlayerStateType.Playing;
        _buffering = status.PlayerState is PlayerStateType.Buffering or PlayerStateType.Loading;

        if (status.PlayerState == PlayerStateType.Idle && !_finishedReported)
        {
            switch (status.IdleReason)
            {
                case "FINISHED":
                    _finishedReported = true;
                    _ticker.Change(Timeout.Infinite, Timeout.Infinite);
                    Finished?.Invoke();
                    break;
                case "ERROR":
                    _finishedReported = true;
                    Failed?.Invoke("Der Fernseher kann dieses Video nicht abspielen (Format nicht unterstützt?).");
                    break;
                // CANCELLED und INTERRUPTED entstehen beim Neuladen (Spulen im Stream) und sind kein Ende.
            }
        }
        Changed?.Invoke(new CastState(Position, !_playing, _buffering));
    }

    private void OnReceiverStatus(object? sender, Sharpcaster.Models.ChromecastStatus.ChromecastStatus status)
    {
        if (status.Volume is { } volume && volume.Level is { } level) VolumeChanged?.Invoke(level, volume.Muted ?? false);

        // Anderes Gerät oder die Fernbedienung hat die App beendet: ohne unseren Empfänger gibt es nichts mehr zu steuern.
        if (_request is not null && status.Application?.AppId != DefaultMediaReceiver)
        {
            EndSession("Die Übertragung wurde am Fernseher beendet.");
        }
    }

    private void OnDisconnected(object? sender, EventArgs e) => EndSession("Die Verbindung zum Fernseher ist abgebrochen.");

    private void EndSession(string reason)
    {
        if (_request is null) return;

        _ticker.Change(Timeout.Infinite, Timeout.Infinite);
        _request = null;
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try { await DisconnectCoreAsync(stopApplication: false); }
            finally { _gate.Release(); }
            Ended?.Invoke(reason);
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _ticker.DisposeAsync();
        try
        {
            await StopAsync();
        }
        finally
        {
            await _server.DisposeAsync();
        }
    }
}
