using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediaCenter.Core;

/// <param name="Id">Nummer der Untertitelspur im Cast (1, 2, …); bei der Wahl im Player gilt dieselbe Nummer.</param>
/// <param name="File">Externe Untertiteldatei; sonst steckt die Spur in der Videodatei unter <paramref name="StreamIndex"/>.</param>
public sealed record CastSubtitle(int Id, string Language, string Name, string? File, int? StreamIndex);

public sealed record CastItem(string Path, ProbeResult Probe, CastPlan Plan, IReadOnlyList<CastSubtitle> Subtitles);

/// <summary>
/// Kleiner Webserver im Heimnetz, von dem der Fernseher das Video holt. Es gibt nur, was vorher mit <see cref="Register"/>
/// angemeldet wurde, jeweils unter einer zufälligen Adresse; Pfade der Festplatte lassen sich nicht abfragen.
/// </summary>
public sealed class CastMediaServer(string ffmpegPath, SubtitleExtractor subtitleExtractor) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, CastItem> _items = new();
    private readonly ConcurrentDictionary<(string Token, int Id), Lazy<Task<IReadOnlyList<SrtCue>>>> _cues = new();
    private readonly ConcurrentDictionary<string, int> _requests = new();
    private WebApplication? _app;

    public int Port { get; private set; }

    /// <summary>"h264_nvenc", wenn die Grafikkarte das Umwandeln übernehmen kann, sonst "libx264".</summary>
    public string Encoder { get; private set; } = CastFfmpeg.SoftwareEncoder;

    /// <summary>ffmpeg ist beim Ausliefern eines Streams fehlgeschlagen (Meldung mit dem Fehlertext von ffmpeg).</summary>
    public event Action<string>? StreamFailed;

    /// <param name="bindTo">Nur an dieser Adresse lauschen (die des PCs im Netz des Fernsehers), nicht auf VPN- und anderen Adaptern.</param>
    public async Task StartAsync(IPAddress bindTo, CancellationToken ct = default)
    {
        if (_app is not null) throw new InvalidOperationException("Der Server läuft schon.");

        Encoder = await DetectEncoderAsync(ct);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(bindTo, 0));
        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            // Der Empfänger ist eine Webseite auf dem Fernseher und holt Untertitel per Skript: ohne diese Köpfe blockt er die Antwort.
            var headers = context.Response.Headers;
            headers["Access-Control-Allow-Origin"] = "*";
            headers["Access-Control-Allow-Headers"] = "*";
            headers["Access-Control-Allow-Methods"] = "GET, HEAD, OPTIONS";
            headers["Access-Control-Expose-Headers"] = "Content-Length, Content-Range, Accept-Ranges";
            if (HttpMethods.IsOptions(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }
            await next();
        });
        app.MapMethods("/m/{token}", ["GET", "HEAD"], ServeMediaAsync);
        app.MapMethods("/s/{token}/{id:int}.vtt", ["GET", "HEAD"], ServeSubtitleAsync);

        await app.StartAsync(ct);
        _app = app;
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Port = new Uri(address).Port;
    }

    /// <summary>Meldet ein Video an und liefert die Kennung für die Adressen.</summary>
    public string Register(CastItem item)
    {
        var token = Guid.NewGuid().ToString("N");
        _items[token] = item;
        return token;
    }

    public void Unregister(string token)
    {
        _items.TryRemove(token, out _);
        _requests.TryRemove(token, out _);
        foreach (var key in _cues.Keys.Where(k => k.Token == token)) _cues.TryRemove(key, out _);
    }

    /// <summary>Wie oft der Fernseher dieses Video schon abgefragt hat (0 heißt: Er erreicht den PC nicht, meist die Firewall).</summary>
    public int RequestCount(string token) => _requests.GetValueOrDefault(token);

    public static string MediaUrl(IPAddress host, int port, string token, double start) =>
        $"http://{host}:{port}/m/{token}?start={start.ToString("F3", CultureInfo.InvariantCulture)}";

    public static string SubtitleUrl(IPAddress host, int port, string token, int id, double start) =>
        $"http://{host}:{port}/s/{token}/{id}.vtt?start={start.ToString("F3", CultureInfo.InvariantCulture)}";

    /// <summary>Beginnt im Hintergrund, die Untertitel auszulesen (aus einer großen MKV dauert das einen Moment).</summary>
    public void PrepareSubtitles(string token)
    {
        if (!_items.TryGetValue(token, out var item)) return;
        foreach (var subtitle in item.Subtitles) _ = CuesAsync(token, item, subtitle);
    }

    private Task<IReadOnlyList<SrtCue>> CuesAsync(string token, CastItem item, CastSubtitle subtitle) =>
        _cues.GetOrAdd((token, subtitle.Id), _ => new Lazy<Task<IReadOnlyList<SrtCue>>>(() =>
            subtitle.File is { } file
                ? subtitleExtractor.FromFileAsync(file)
                : subtitleExtractor.FromStreamAsync(item.Path, subtitle.StreamIndex ?? throw new InvalidOperationException("Untertitelspur ohne Datei und ohne Streamnummer.")))).Value;

    /// <summary>Die Adresse dieses PCs im selben Netz wie der Fernseher (nicht die eines VPN oder virtuellen Adapters).</summary>
    public static IPAddress LocalAddressFor(IPAddress remote)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || remote.AddressFamily != AddressFamily.InterNetwork) continue;
                if (SameSubnet(unicast.Address, remote, unicast.IPv4Mask)) return unicast.Address;
            }
        }
        throw new InvalidOperationException($"Keine Netzwerkverbindung dieses PCs liegt im Netz des Fernsehers ({remote}).");
    }

    internal static bool SameSubnet(IPAddress a, IPAddress b, IPAddress mask)
    {
        var x = a.GetAddressBytes();
        var y = b.GetAddressBytes();
        var m = mask.GetAddressBytes();
        for (var i = 0; i < x.Length; i++)
        {
            if ((x[i] & m[i]) != (y[i] & m[i])) return false;
        }
        return true;
    }

    private async Task ServeMediaAsync(HttpContext context)
    {
        var token = (string?)context.Request.RouteValues["token"];
        if (token is null || !_items.TryGetValue(token, out var item))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        _requests.AddOrUpdate(token, 1, (_, n) => n + 1);

        if (item.Plan.Mode == CastMode.Direct)
        {
            await Results.File(item.Path, CastPlan.ContentType, enableRangeProcessing: true).ExecuteAsync(context);
            return;
        }

        context.Response.ContentType = CastPlan.ContentType;
        if (HttpMethods.IsHead(context.Request.Method)) return;

        var start = double.TryParse(context.Request.Query["start"], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s >= 0 ? s : 0;
        await StreamAsync(context, item, start);
    }

    private async Task StreamAsync(HttpContext context, CastItem item, double start)
    {
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8, CreateNoWindow = true,
        };
        foreach (var arg in CastFfmpeg.StreamArguments(item.Path, item.Plan, start, Encoder)) psi.ArgumentList.Add(arg);

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (Win32Exception ex)
        {
            StreamFailed?.Invoke($"ffmpeg konnte nicht gestartet werden ('{ffmpegPath}'). Im PATH bereitstellen oder MEDIACENTER_FFMPEG setzen. ({ex.Message})");
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        using (process)
        {
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.StandardOutput.BaseStream.CopyToAsync(context.Response.Body, 81920, context.RequestAborted);
                await process.WaitForExitAsync(context.RequestAborted);
                if (process.ExitCode != 0) StreamFailed?.Invoke($"ffmpeg beendete den Stream mit Fehler (Exit-Code {process.ExitCode}): {(await stderr).Trim()}");
            }
            catch (OperationCanceledException)
            {
                // Der Fernseher hat die Verbindung geschlossen (Spulen, Stopp): das ist der normale Abschied, kein Fehler.
            }
            catch (IOException) when (context.RequestAborted.IsCancellationRequested)
            {
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
    }

    private async Task ServeSubtitleAsync(HttpContext context)
    {
        var token = (string?)context.Request.RouteValues["token"];
        // Routenwerte sind Text, auch bei {id:int}.
        var id = int.TryParse(Convert.ToString(context.Request.RouteValues["id"], CultureInfo.InvariantCulture), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : (int?)null;
        if (token is null || id is null || !_items.TryGetValue(token, out var item) || item.Subtitles.FirstOrDefault(x => x.Id == id) is not { } subtitle)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var start = double.TryParse(context.Request.Query["start"], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s >= 0 ? s : 0;
        context.Response.ContentType = "text/vtt; charset=utf-8";
        try
        {
            var cues = await CuesAsync(token, item, subtitle);
            if (HttpMethods.IsHead(context.Request.Method)) return;
            await context.Response.WriteAsync(WebVtt.Format(cues, start), Encoding.UTF8, context.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Bilduntertitel, defekte Datei oder ffmpeg-Fehler: der Fernseher bekommt einen Fehlerstatus, der Grund geht an die App.
            _cues.TryRemove((token, subtitle.Id), out _);
            StreamFailed?.Invoke($"Untertitel „{subtitle.Name}“ nicht lesbar ({ex.GetType().Name}): {ex.Message}");
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        }
    }

    private async Task<string> DetectEncoderAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-hide_banner", "-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=c=black:s=320x240:d=0.1", "-frames:v", "1", "-c:v", CastFfmpeg.NvidiaEncoder, "-f", "null", "-" }) psi.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(psi)!;
            _ = process.StandardOutput.ReadToEndAsync(ct);
            _ = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0 ? CastFfmpeg.NvidiaEncoder : CastFfmpeg.SoftwareEncoder;
        }
        catch (Win32Exception ex)
        {
            throw new FileNotFoundException($"ffmpeg konnte nicht gestartet werden ('{ffmpegPath}'). Im PATH bereitstellen oder MEDIACENTER_FFMPEG setzen.", ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return CastFfmpeg.SoftwareEncoder; // der Test hing: ohne Grafikkarten-Encoder weitermachen
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null) return;
        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
    }
}
