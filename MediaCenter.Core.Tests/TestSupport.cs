using MediaCenter.Core;

namespace MediaCenter.Core.Tests;

/// <summary>Gemeinsame Fakes für Tests, die weder echte Laufwerke noch ffmpeg/ffprobe brauchen.</summary>
internal static class TestSupport
{
    public sealed class FakeVolumes : IVolumeProvider
    {
        public List<VolumeInfo> Mounted { get; } = new();

        public IReadOnlyList<VolumeInfo> GetMountedVolumes() => Mounted.ToList();
    }

    public sealed class FakeProbe : IMediaProbe
    {
        public static ProbeResult VideoOnly { get; } = new(
            100, "matroska", new[] { new StreamInfo(0, "video", "h264", "High", 1920, 1080, null, null, null, true, false, "bt709") });

        /// <summary>Antwort je Dateipfad; Standard: nur ein Videostream.</summary>
        public Func<string, ProbeResult> Respond { get; set; } = _ => VideoOnly;

        public Task<ProbeResult> ProbeAsync(string file, CancellationToken ct = default) => Task.FromResult(Respond(file));
    }

    /// <summary>Schreibt statt eines Bildes eine Textdatei und merkt sich jeden Aufruf.</summary>
    public sealed class FakeImageTool : IImageTool
    {
        public List<string> Calls { get; } = new();

        /// <summary>Wirft für Aufrufe, deren Beschreibung diesen Text enthält.</summary>
        public string? FailWhenCallContains { get; set; }

        public Task ScaleImageAsync(string input, string output, int maxWidth, int maxHeight, CancellationToken ct = default) =>
            Write(output, $"scale:{Path.GetFileName(input)}:{maxWidth}x{maxHeight}");

        public Task GrabFrameAsync(string video, double seconds, string output, int maxWidth, int maxHeight, CancellationToken ct = default) =>
            Write(output, $"frame:{Path.GetFileName(video)}@{seconds:0.#}:{maxWidth}x{maxHeight}");

        public Task ExtractStreamImageAsync(string video, int streamIndex, string output, int maxWidth, int maxHeight, CancellationToken ct = default) =>
            Write(output, $"extract:{Path.GetFileName(video)}#{streamIndex}:{maxWidth}x{maxHeight}");

        private Task Write(string output, string call)
        {
            Calls.Add(call);
            if (FailWhenCallContains is { } text && call.Contains(text)) throw new InvalidOperationException($"Absichtlicher Fehler bei {call}");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, call);
            return Task.CompletedTask;
        }
    }

    public static VolumeInfo Vol(string serial, string root, long capacity = 1000, string fs = "NTFS") =>
        new(serial, "Data", fs, capacity, root);

    public static void Touch(string root, string rel)
    {
        var path = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }
}
