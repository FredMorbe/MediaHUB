using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace MediaCenter.Spike;

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

    /// <summary>Pipe geschlossen oder mpv beendet (Grund als Text).</summary>
    public event Action<string>? Ended;

    private MpvPlayer(Process process, NamedPipeClientStream pipe, string logFile)
    {
        _process = process;
        _pipe = pipe;
        _writer = new StreamWriter(pipe, new UTF8Encoding(false));
        LogFile = logFile;
    }

    public static async Task<MpvPlayer> StartAsync(string mpvPath, IntPtr wid)
    {
        if (!File.Exists(mpvPath))
        {
            throw new FileNotFoundException($"mpv.exe nicht gefunden: {mpvPath}. Pfad per Umgebungsvariable MEDIACENTER_MPV setzen.");
        }

        var pipeName = $"mediacenter-mpv-{Environment.ProcessId}";
        var logFile = Path.Combine(Path.GetTempPath(), "mediacenter-mpv.log");

        var psi = new ProcessStartInfo(mpvPath) { UseShellExecute = false };
        foreach (var arg in new[]
        {
            "--no-config",
            "--idle=yes",
            "--force-window=yes",
            "--keep-open=yes",
            "--no-osc",
            "--osd-level=0",
            "--input-default-bindings=no",
            "--input-vo-keyboard=no",
            "--vo=gpu-next",
            "--hwdec=auto-safe",
            $"--wid={wid.ToInt64()}",
            $@"--input-ipc-server=\\.\pipe\{pipeName}",
            $"--log-file={logFile}",
        })
        {
            psi.ArgumentList.Add(arg);
        }

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
