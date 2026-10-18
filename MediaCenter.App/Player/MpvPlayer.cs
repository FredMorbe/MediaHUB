using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace MediaCenter.App.Player;

/// <summary>Startet mpv eingebettet (--wid) und steuert es über die JSON-IPC-Pipe.</summary>
public sealed class MpvPlayer : IDisposable
{
    private readonly Process _process;
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private int _observeId;
    private volatile bool _disposed;

    public string LogFile { get; }

    /// <summary>Property-Änderung; data ist null, wenn mpv die Property als nicht verfügbar meldet.</summary>
    public event Action<string, JsonElement?>? PropertyChanged;

    /// <summary>mpv meldet für einen Befehl einen Fehler.</summary>
    public event Action<string>? CommandFailed;

    /// <summary>Eine Datei oder ein Stream konnte nicht gespielt werden (Fehlertext von mpv).</summary>
    public event Action<string>? FileFailed;

    /// <summary>mpv beginnt eine Datei zu laden (davor eintreffende Ereignisse gehören zur vorigen Datei).</summary>
    public event Action? FileStarted;

    /// <summary>Datei geöffnet, Spurliste steht und mpv hat seine Vorauswahl der Spuren getroffen.</summary>
    public event Action? FileLoaded;

    /// <summary>Pipe geschlossen oder mpv beendet (Grund als Text).</summary>
    public event Action<string>? Ended;

    private MpvPlayer(Process process, NamedPipeClientStream pipe, string logFile)
    {
        _process = process;
        _pipe = pipe;
        _writer = new StreamWriter(pipe, new UTF8Encoding(false));
        LogFile = logFile;
    }

    /// <param name="wid">Fenster, in das mpv das Bild zeichnet; null startet eine reine Audio-Instanz ohne Fenster.</param>
    /// <param name="name">Unterscheidet Pipe und Logdatei, wenn mehrere Instanzen laufen (Video und Audio).</param>
    public static async Task<MpvPlayer> StartAsync(string mpvPath, IntPtr? wid, string name = "video")
    {
        if (!File.Exists(mpvPath))
        {
            throw new FileNotFoundException($"mpv.exe nicht gefunden: {mpvPath}. Pfad per Umgebungsvariable MEDIACENTER_MPV setzen.");
        }

        var pipeName = $"mediacenter-mpv-{Environment.ProcessId}-{name}";
        var logFile = Path.Combine(Path.GetTempPath(), $"mediacenter-mpv-{name}.log");

        var psi = new ProcessStartInfo(mpvPath) { UseShellExecute = false };
        var args = new List<string>
        {
            "--no-config",
            "--idle=yes",
            "--no-osc",
            "--osd-level=0",
            "--input-default-bindings=no",
            "--input-vo-keyboard=no",
            // mpv meldet sich sonst selbst bei der Windows-Mediensteuerung an und konkurriert mit der Sitzung der App um die Medientasten.
            "--input-media-keys=no",
            // Adressen kommen aus Feeds und Senderlisten: mpv soll sie nicht über yt-dlp auflösen.
            "--ytdl=no",
            $@"--input-ipc-server=\\.\pipe\{pipeName}",
            $"--log-file={logFile}",
        };
        if (wid is { } window)
        {
            args.AddRange(new[]
            {
                "--force-window=yes", "--keep-open=yes", "--vo=gpu-next", "--hwdec=auto-safe", $"--wid={window.ToInt64()}",
                // Externe Untertitel: alle Dateien, deren Name den des Videos enthält (Film.de.srt, Film.German.srt),
                // auch in den üblichen Unterordnern.
                "--sub-auto=fuzzy", @"--sub-file-paths=sub;subs;subtitles;untertitel",
            });
        }
        else
        {
            args.AddRange(new[] { "--no-video", "--force-window=no", "--audio-display=no", "--network-timeout=15", "--audio-client-name=Mediacenter" });
        }
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        var process = Process.Start(psi) ?? throw new InvalidOperationException("mpv-Prozess konnte nicht gestartet werden.");
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(10_000);
        }
        catch (TimeoutException)
        {
            var state = process.HasExited ? $"mpv ist bereits beendet (Exit-Code {process.ExitCode})" : "mpv läuft noch";
            if (!process.HasExited) process.Kill();
            throw new TimeoutException($"mpv-IPC-Pipe {pipeName} nach 10 s nicht erreichbar; {state}. Log: {logFile}");
        }

        var player = new MpvPlayer(process, pipe, logFile);
        _ = player.ReadLoopAsync();
        return player;
    }

    public Task ObserveAsync(string property) => CommandAsync("observe_property", ++_observeId, property);

    public async Task CommandAsync(params object[] args)
    {
        var json = JsonSerializer.Serialize(new { command = args });
        await _writeLock.WaitAsync();
        try
        {
            await _writer.WriteAsync(json + "\n");
            await _writer.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            using var reader = new StreamReader(_pipe, Encoding.UTF8);
            string? line;
            while ((line = await reader.ReadLineAsync(_cts.Token)) != null)
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (root.TryGetProperty("event", out var ev))
                {
                    if (ev.GetString() == "property-change")
                    {
                        var name = root.GetProperty("name").GetString()!;
                        JsonElement? data = root.TryGetProperty("data", out var d) ? d.Clone() : null;
                        PropertyChanged?.Invoke(name, data);
                    }
                    else if (ev.GetString() == "start-file")
                    {
                        FileStarted?.Invoke();
                    }
                    else if (ev.GetString() == "file-loaded")
                    {
                        FileLoaded?.Invoke();
                    }
                    else if (ev.GetString() == "end-file" && root.TryGetProperty("reason", out var reason) && reason.GetString() == "error")
                    {
                        FileFailed?.Invoke(root.TryGetProperty("file_error", out var fileError) ? fileError.GetString() ?? "unbekannter Fehler" : "unbekannter Fehler");
                    }
                    else if (ev.GetString() == "shutdown" && !_disposed)
                    {
                        Ended?.Invoke("mpv hat shutdown gemeldet");
                    }
                }
                else if (root.TryGetProperty("error", out var err) && err.GetString() != "success")
                {
                    CommandFailed?.Invoke($"{err.GetString()} (Antwort: {line})");
                }
            }
            if (!_disposed) Ended?.Invoke("IPC-Pipe wurde von mpv geschlossen");
        }
        catch (OperationCanceledException)
        {
            // Dispose() läuft – kein Fehler.
        }
        catch (Exception ex) when (!_disposed)
        {
            Ended?.Invoke($"Fehler beim Lesen der IPC-Pipe: {ex}");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try
        {
            if (!_process.HasExited)
            {
                _ = CommandAsync("quit");
                if (!_process.WaitForExit(2000)) _process.Kill();
            }
        }
        finally
        {
            _cts.Cancel();
            _pipe.Dispose();
            _process.Dispose();
        }
    }
}
