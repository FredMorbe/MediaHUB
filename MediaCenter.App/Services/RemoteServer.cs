using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MediaCenter.Core;

namespace MediaCenter.App.Services;

/// <summary>Was der Player gerade tut, für die Fernbedienung im Browser.</summary>
public sealed record NowPlayingInfo(bool Active, string? Title, string? Detail, double Position, double Duration, bool Paused, double Volume, bool Muted, bool Live, bool Casting, long? VideoId, bool CanNext);

/// <summary>Was der Heimnetz-Server vom Hauptfenster braucht. Alles, was die Oberfläche oder die Datenbank anfasst, läuft auf dem Oberflächen-Thread.</summary>
public interface IRemoteHost
{
    AppServices Services { get; }

    Task<T> OnUiAsync<T>(Func<T> work);

    NowPlayingInfo Now();

    Task PlayAsync(long videoId, bool resume);

    void Press(ControllerButton button);

    bool Act(PlayerAction action, bool large);

    void SeekTo(double seconds);

    void SetVolume(double volume);
}

/// <summary>Ein Befehl der Fernbedienung: eine Controller-Taste, eine Player-Aktion, eine Stelle oder eine Lautstärke.</summary>
public sealed record RemoteCommand(string? Press, string? Action, bool? Large, double? Seek, double? Volume);

/// <summary>
/// Der Medienserver im Heimnetz: eine kleine Weboberfläche fürs Handy mit Fernbedienung (Player und Bibliothek der App steuern), Bibliothek durchsuchen,
/// auf dem PC abspielen und im Browser anschauen (MP4 unverändert, sonst von ffmpeg umgewandelt). Standardmäßig aus; der Zugang läuft über eine PIN
/// (<see cref="RemoteAccess"/>). Die Verbindung ist unverschlüsselt (HTTP) und gehört ins eigene Heimnetz; der Server zeigt die Bibliothek des gerade
/// gewählten Profils, im Kinderprofil also nur dessen Titel.
/// </summary>
public sealed class RemoteServer(IRemoteHost host, RemoteAccess access) : IAsyncDisposable
{
    private const string CookieName = "mc";

    /// <summary>Gleichzeitig laufende ffmpeg-Umwandlungen für den Browser; weitere warten nicht, sie bekommen „zu viel los“.</summary>
    private readonly SemaphoreSlim _transcodes = new(2, 2);

    private WebApplication? _app;

    public bool Running => _app is not null;

    public int Port { get; private set; }

    public bool Lan { get; private set; }

    /// <summary>Die Adressen, unter denen das Handy den Server erreicht (IPv4 des Heimnetzes); bei „nur dieser PC“ nur localhost.</summary>
    public IReadOnlyList<string> Addresses()
    {
        if (!Running) return Array.Empty<string>();
        if (!Lan) return new[] { $"http://localhost:{Port}" };

        var found = new List<string>();
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up || network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var address in network.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address.Address)) continue;
                var bytes = address.Address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254) continue; // Link-Local: keine echte Verbindung
                found.Add($"http://{address.Address}:{Port}");
            }
        }
        return found;
    }

    /// <exception cref="IOException">Der Anschluss ist schon belegt oder der Zugriff verweigert.</exception>
    public async Task StartAsync(int port, bool lan)
    {
        if (_app is not null) await StopAsync();

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Listen(lan ? IPAddress.Any : IPAddress.Loopback, port);
        });
        builder.Logging.ClearProviders();
        builder.Services.Configure<JsonOptions>(o => o.SerializerOptions.PropertyNameCaseInsensitive = true);
        var app = builder.Build();
        MapRoutes(app);
        await app.StartAsync();
        _app = app;
        Port = port;
        Lan = lan;
    }

    public async Task StopAsync()
    {
        var app = _app;
        _app = null;
        if (app is null) return;
        await app.StopAsync();
        await app.DisposeAsync();
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    // --- Routen --------------------------------------------------------------------------------------------

    private static readonly HashSet<string> PublicPages = new(StringComparer.Ordinal) { "/", "/app.js", "/app.css", "/manifest.webmanifest", "/icon.png", "/api/info" };

    private void MapRoutes(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers["X-Frame-Options"] = "DENY";
            headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; media-src 'self' blob:; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

            var path = context.Request.Path.Value ?? "/";
            var isLogin = HttpMethods.IsPost(context.Request.Method) && path == "/api/login";
            var isPublic = HttpMethods.IsGet(context.Request.Method) && PublicPages.Contains(path);
            // Schreibende Aufrufe nur mit eigenem Kopf: eine fremde Webseite kann ihn ohne Vorabfrage nicht mitschicken.
            if (HttpMethods.IsPost(context.Request.Method) && context.Request.Headers["X-MC"] != "1")
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            if (!isPublic && !isLogin && !access.IsValidToken(TokenOf(context)))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "login" });
                return;
            }
            await next();
        });

        app.MapGet("/", () => Page("index.html", "text/html; charset=utf-8"));
        app.MapGet("/app.js", () => Page("app.js", "text/javascript; charset=utf-8"));
        app.MapGet("/app.css", () => Page("app.css", "text/css; charset=utf-8"));
        app.MapGet("/manifest.webmanifest", () => Page("manifest.webmanifest", "application/manifest+json"));
        app.MapGet("/icon.png", () => Page("icon.png", "image/png"));
        app.MapGet("/api/info", () => Results.Json(new { name = "Mediacenter", version = AppInfo.Version }));

        app.MapPost("/api/login", async (HttpContext context) =>
        {
            var body = await context.Request.ReadFromJsonAsync<LoginBody>();
            var client = context.Connection.RemoteIpAddress?.ToString() ?? "unbekannt";
            var login = access.TryLogin(client, body?.Pin);
            if (login.Ok)
            {
                context.Response.Cookies.Append(CookieName, login.Token!, new CookieOptions
                {
                    HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromDays(365), IsEssential = true,
                });
                return Results.Json(new { ok = true });
            }
            if (login.LockedFor is { } wait)
            {
                return Results.Json(new { ok = false, error = $"Zu viele falsche Eingaben. Bitte in {Math.Ceiling(wait.TotalMinutes)} Minuten noch einmal versuchen.", lockedSeconds = (int)wait.TotalSeconds }, statusCode: StatusCodes.Status429TooManyRequests);
            }
            return Results.Json(new { ok = false, error = "Die PIN stimmt nicht." }, statusCode: StatusCodes.Status401Unauthorized);
        });

        app.MapPost("/api/logout", (HttpContext context) =>
        {
            context.Response.Cookies.Delete(CookieName);
            return Results.Json(new { ok = true });
        });

        app.MapGet("/api/library", async (HttpContext context) =>
        {
            var kind = context.Request.Query["kind"].ToString();
            var query = PosterNames.MatchKey(context.Request.Query["q"].ToString());
            return Results.Json(await host.OnUiAsync(() =>
            {
                var model = host.Services.Model;
                var states = model.WatchStatesByTitle();
                return model.Titles
                    .Where(t => kind switch { "movie" => t.Kind == TitleKind.Movie, "series" => t.Kind == TitleKind.Series, _ => true })
                    .Where(t => query.Length == 0 || PosterNames.MatchKey(t.Name).Contains(query, StringComparison.Ordinal))
                    .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(t => new
                    {
                        id = t.Id, name = t.Name, year = t.Year, kind = t.Kind == TitleKind.Movie ? "movie" : "series", meta = model.MetaOf(t),
                        poster = model.BestCover(t.Id) is not null, watched = states.GetValueOrDefault(t.Id).ToString(), online = model.IsOnline(t.Id),
                    }).ToList();
            }));
        });

        app.MapGet("/api/continue", async () => Results.Json(await host.OnUiAsync(() =>
        {
            var model = host.Services.Model;
            return model.Continue.Where(c => model.FindTitle(c.TitleId) is not null).Select(c => new
            {
                titleId = c.TitleId, videoId = c.VideoId, name = model.FindTitle(c.TitleId)!.Name, season = c.Season, episode = c.Episode,
                position = c.PositionSeconds, duration = c.DurationSeconds, poster = model.BestCover(c.TitleId) is not null,
            }).ToList();
        })));

        app.MapGet("/api/title/{id:long}", async (long id) =>
        {
            var detail = await host.OnUiAsync(() => TitleDetail(id));
            return detail is null ? Results.NotFound(new { error = "Titel nicht gefunden." }) : Results.Json(detail);
        });

        app.MapGet("/api/poster/{id:long}", async (long id, HttpContext context) =>
        {
            var path = await host.OnUiAsync(() =>
            {
                var model = host.Services.Model;
                return model.FindTitle(id) is not null && model.BestCover(id) is { } cover ? cover.Path : null;
            });
            if (path is null || !File.Exists(path)) return Results.NotFound();
            context.Response.Headers.CacheControl = "private, max-age=3600";
            return Results.File(path, MimeOf(path));
        });

        app.MapGet("/api/now", async () => Results.Json(await host.OnUiAsync(host.Now)));

        app.MapPost("/api/play", async (HttpContext context) =>
        {
            var body = await context.Request.ReadFromJsonAsync<PlayBody>();
            if (body is null) return Results.BadRequest(new { error = "Anfrage ohne Inhalt." });
            try
            {
                var ok = await host.OnUiAsync(() => host.Services.Model.VideosByTitle.Values.SelectMany(v => v).Any(v => v.VideoId == body.VideoId));
                if (!ok) return Results.NotFound(new { error = "Video nicht gefunden." });
                await await host.OnUiAsync(() => host.PlayAsync(body.VideoId, body.Resume ?? true));
                return Results.Json(new { ok = true });
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or FileNotFoundException)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
            }
        });

        app.MapPost("/api/remote", async (HttpContext context) =>
        {
            var command = await context.Request.ReadFromJsonAsync<RemoteCommand>();
            if (command is null) return Results.BadRequest(new { error = "Anfrage ohne Inhalt." });

            if (command.Press is { } press)
            {
                if (!Enum.TryParse<ControllerButton>(press, ignoreCase: true, out var button) || !Enum.IsDefined(button)) return Results.BadRequest(new { error = $"Unbekannte Taste „{press}“." });
                await host.OnUiAsync(() => { host.Press(button); return 0; });
            }
            else if (command.Action is { } action)
            {
                if (!Enum.TryParse<PlayerAction>(action, ignoreCase: true, out var playerAction) || !Enum.IsDefined(playerAction)) return Results.BadRequest(new { error = $"Unbekannte Aktion „{action}“." });
                var done = await host.OnUiAsync(() => host.Act(playerAction, command.Large ?? false));
                return Results.Json(new { ok = done });
            }
            else if (command.Seek is { } seek && double.IsFinite(seek) && seek >= 0)
            {
                await host.OnUiAsync(() => { host.SeekTo(seek); return 0; });
            }
            else if (command.Volume is { } volume && double.IsFinite(volume))
            {
                await host.OnUiAsync(() => { host.SetVolume(Math.Clamp(volume, 0, 100)); return 0; });
            }
            else
            {
                return Results.BadRequest(new { error = "Kein Befehl angegeben." });
            }
            return Results.Json(new { ok = true });
        });

        app.MapPost("/api/progress", async (HttpContext context) =>
        {
            var body = await context.Request.ReadFromJsonAsync<ProgressBody>();
            if (body is null || !double.IsFinite(body.Position) || body.Position < 0) return Results.BadRequest(new { error = "Ungültige Stelle." });
            var saved = await host.OnUiAsync(() =>
            {
                var model = host.Services.Model;
                var video = model.VideosByTitle.Values.SelectMany(v => v).FirstOrDefault(v => v.VideoId == body.VideoId);
                if (video is null) return false;
                // Wie im Player: fast am Ende gilt als gesehen, ganz am Anfang wird nichts gemerkt.
                var watched = body.Watched || (video.DurationSeconds is > 0 && body.Position / video.DurationSeconds >= 0.92);
                if (watched) model.Db.SavePlayback(video.VideoId, 0, watched: true);
                else if (body.Position >= 30) model.Db.SavePlayback(video.VideoId, body.Position, watched: false);
                return true;
            });
            return saved ? Results.Json(new { ok = true }) : Results.NotFound(new { error = "Video nicht gefunden." });
        });

        app.MapGet("/api/stream/{id:long}", (long id, HttpContext context) => Stream(id, context));

        // Sagt der Seite vorab, ob das Video direkt läuft (dann hat der Browser Dauer und Spulen selbst) oder umgewandelt wird (dann bringt die Seite beides selbst mit).
        app.MapGet("/api/streaminfo/{id:long}", async (long id, HttpContext context) =>
        {
            var (path, video, preferences) = await host.OnUiAsync(() =>
            {
                var model = host.Services.Model;
                var found = model.VideosByTitle.Values.SelectMany(v => v).FirstOrDefault(v => v.VideoId == id);
                return (found is null ? null : model.PathOf(found), found, host.Services.Settings.Playback);
            });
            if (video is null || path is null) return Results.NotFound(new { error = "Video nicht gefunden oder nicht erreichbar." });
            if (video.Disc != DiscKind.None) return Results.Json(new { error = "DVDs und Blu-rays lassen sich nicht im Browser abspielen. Auf dem PC abspielen geht." }, statusCode: StatusCodes.Status415UnsupportedMediaType);
            try
            {
                var plan = WebStreamPlanner.Decide(await host.Services.Probe.ProbeAsync(path, context.RequestAborted), path, preferences);
                return Results.Json(new { direct = plan.Direct, description = plan.Description });
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or InvalidDataException or FileNotFoundException)
            {
                return Results.Json(new { error = $"Die Datei lässt sich nicht lesen: {ex.Message}" }, statusCode: StatusCodes.Status500InternalServerError);
            }
        });
    }

    private sealed record LoginBody(string? Pin);

    private sealed record PlayBody(long VideoId, bool? Resume);

    private sealed record ProgressBody(long VideoId, double Position, bool Watched);

    private static string? TokenOf(HttpContext context)
    {
        if (context.Request.Cookies.TryGetValue(CookieName, out var cookie)) return cookie;
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..] : null;
    }

    private static IResult Page(string name, string contentType)
    {
        using var stream = typeof(RemoteServer).Assembly.GetManifestResourceStream($"Web/{name}") ?? throw new InvalidOperationException($"Die Webseite „{name}“ fehlt in der Anwendung.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Results.Bytes(memory.ToArray(), contentType);
    }

    private static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/jpeg",
    };

    // --- Titel ---------------------------------------------------------------------------------------------

    /// <summary>Alles zu einem Titel für die Detailseite im Browser (Oberflächen-Thread).</summary>
    private object? TitleDetail(long id)
    {
        var services = host.Services;
        var model = services.Model;
        if (model.FindTitle(id) is not { } title) return null;

        var info = model.Infos.GetValueOrDefault(id);
        var playback = model.Db.GetAllPlayback();
        var episodeNames = model.Db.GetEpisodeInfos(id).ToDictionary(e => (e.Season, e.Episode), e => e.Name);
        var videos = model.VideosOf(id)
            .OrderBy(v => v.Season ?? 0).ThenBy(v => v.Episode ?? 0)
            .Select(v =>
            {
                var state = playback.GetValueOrDefault(v.VideoId);
                return new
                {
                    id = v.VideoId, season = v.Season, episode = v.Episode,
                    name = v.Season is { } s && v.Episode is { } e ? episodeNames.GetValueOrDefault((s, e)) : null,
                    watched = state?.Watched ?? false, position = state?.PositionSeconds ?? 0, duration = v.DurationSeconds,
                    online = model.PathOf(v) is not null, disc = v.Disc == DiscKind.None ? null : DiscDetector.Label(v.Disc),
                    height = v.Height, hdr = v.Hdr == HdrFormat.None ? null : v.Hdr.Label(),
                };
            }).ToList();

        return new
        {
            id = title.Id, name = title.Name, year = title.Year, kind = title.Kind == TitleKind.Movie ? "movie" : "series", meta = model.MetaOf(title),
            plot = info?.Plot, rating = info?.Rating, director = info?.Director, cast = info?.Cast, genres = model.GenresOf(id),
            poster = model.BestCover(id) is not null, videos,
        };
    }

    // --- Stream --------------------------------------------------------------------------------------------

    private async Task<IResult> Stream(long id, HttpContext context)
    {
        var (path, video, preferences) = await host.OnUiAsync(() =>
        {
            var model = host.Services.Model;
            var found = model.VideosByTitle.Values.SelectMany(v => v).FirstOrDefault(v => v.VideoId == id);
            return (found is null ? null : model.PathOf(found), found, host.Services.Settings.Playback);
        });
        if (video is null || path is null) return Results.NotFound(new { error = "Video nicht gefunden oder nicht erreichbar." });
        if (video.Disc != DiscKind.None) return Results.Json(new { error = "DVDs und Blu-rays lassen sich nicht im Browser abspielen. Auf dem PC abspielen geht." }, statusCode: StatusCodes.Status415UnsupportedMediaType);

        ProbeResult probe;
        try
        {
            probe = await host.Services.Probe.ProbeAsync(path, context.RequestAborted);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or FileNotFoundException)
        {
            return Results.Json(new { error = $"Die Datei lässt sich nicht lesen: {ex.Message}" }, statusCode: StatusCodes.Status500InternalServerError);
        }

        var plan = WebStreamPlanner.Decide(probe, path, preferences);
        if (plan.Direct) return Results.File(path, "video/mp4", enableRangeProcessing: true);

        var start = double.TryParse(context.Request.Query["start"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? seconds : 0;
        if (!await _transcodes.WaitAsync(0))
        {
            return Results.Json(new { error = "Es laufen schon zwei Umwandlungen. Bitte kurz warten." }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        Process? process = null;
        try
        {
            process = StartFfmpeg(WebStreamPlanner.FfmpegArguments(plan, path, start));
            var errors = new StringBuilder();
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null && errors.Length < 2000) errors.AppendLine(e.Data);
            };
            process.BeginErrorReadLine();

            // Erst das erste Stück abwarten: scheitert ffmpeg sofort, bekommt der Browser eine Fehlermeldung statt eines leeren Videos.
            var buffer = new byte[64 * 1024];
            using var firstTimeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            firstTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            var first = await process.StandardOutput.BaseStream.ReadAsync(buffer, firstTimeout.Token);
            if (first == 0)
            {
                await process.WaitForExitAsync(context.RequestAborted);
                return Results.Json(new { error = $"ffmpeg konnte das Video nicht umwandeln: {errors}".Trim() }, statusCode: StatusCodes.Status500InternalServerError);
            }

            context.Response.ContentType = "video/mp4";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Stream-Plan"] = Uri.EscapeDataString(plan.Description);
            await context.Response.Body.WriteAsync(buffer.AsMemory(0, first), context.RequestAborted);
            await process.StandardOutput.BaseStream.CopyToAsync(context.Response.Body, context.RequestAborted);
            return Results.Empty;
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return Results.Empty; // der Browser hat aufgehört (Seite verlassen, neu gesprungen)
        }
        catch (OperationCanceledException)
        {
            return Results.Json(new { error = "ffmpeg hat nicht rechtzeitig geantwortet." }, statusCode: StatusCodes.Status504GatewayTimeout);
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            _transcodes.Release();
        }
    }

    private static Process StartFfmpeg(IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo(ToolLocator.Ffmpeg().Path)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        return Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg konnte nicht gestartet werden.");
    }
}
