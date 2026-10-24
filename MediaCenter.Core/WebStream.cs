using System.Globalization;

namespace MediaCenter.Core;

/// <summary>Wie ein Video an den Browser eines Handys geht: unverändert (Direkt) oder von ffmpeg umgewandelt.</summary>
/// <param name="Direct">Die Datei läuft so im Browser (MP4 mit H.264 und AAC oder MP3, Standardtonspur): sie geht mit Bereichsabrufen hinaus.</param>
/// <param name="CopyVideo">Beim Umwandeln bleibt das Bild, wie es ist (H.264 ohne HDR): schont den PC.</param>
/// <param name="AudioStreamIndex">Gewählte Tonspur (ffmpeg-Streamnummer); null nimmt die Standardspur.</param>
public sealed record WebStreamPlan(bool Direct, bool CopyVideo, bool ToneMap, int? VideoStreamIndex, int? AudioStreamIndex, string Description);

public static class WebStreamPlanner
{
    /// <summary>Höhe, über die ein umgewandeltes Bild nicht hinausgeht (Handy-Bildschirm und Netzwerk).</summary>
    public const int MaxHeight = 1080;

    public static WebStreamPlan Decide(ProbeResult probe, string path, PlaybackPreferences preferences)
    {
        var video = probe.Video ?? throw new InvalidDataException("Die Datei enthält kein Bild.");
        var audio = ChooseAudio(probe, preferences);
        var h264 = video.Codec == "h264";
        var plainVideo = h264 && !IsHighBitDepth(video.Profile) && !probe.IsHdr;
        var extension = Path.GetExtension(path);
        var mp4 = extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase);
        var browserAudio = audio is null || audio.Codec is "aac" or "mp3";

        // Die Standardspur spielt der Browser selbst; will der Nutzer eine andere Sprache, muss ffmpeg die Spur auswählen.
        var defaultAudio = probe.Streams.Where(s => s.Type == "audio").FirstOrDefault(s => s.IsDefault) ?? probe.Streams.FirstOrDefault(s => s.Type == "audio");
        var wantsOtherTrack = audio is not null && defaultAudio is not null && audio.Index != defaultAudio.Index;

        if (mp4 && plainVideo && browserAudio && !wantsOtherTrack)
            return new WebStreamPlan(true, true, false, video.Index, audio?.Index, "Direkt: die Datei läuft unverändert im Browser.");

        var toneMap = probe.IsHdr;
        var description = $"Umgewandelt: Bild {(plainVideo ? "kopiert" : toneMap ? "umgerechnet (HDR nach SDR)" : "neu kodiert")}, Ton {(audio is null ? "keiner" : "AAC")}.";
        return new WebStreamPlan(false, plainVideo, toneMap, video.Index, audio?.Index, description);
    }

    private static StreamInfo? ChooseAudio(ProbeResult probe, PlaybackPreferences preferences)
    {
        var index = CastTracks.PreferredAudioStream(probe, preferences);
        var streams = probe.Streams.Where(s => s.Type == "audio").ToList();
        return (index is { } wanted ? streams.FirstOrDefault(s => s.Index == wanted) : null) ?? streams.FirstOrDefault(s => s.IsDefault) ?? streams.FirstOrDefault();
    }

    private static bool IsHighBitDepth(string? profile) =>
        profile is not null && (profile.Contains("10", StringComparison.Ordinal) || profile.Contains("4:2:2", StringComparison.Ordinal)
            || profile.Contains("4:4:4", StringComparison.Ordinal) || profile.Contains("Predictive", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// ffmpeg-Argumente für einen Fragment-MP4-Strom auf der Standardausgabe, der ab <paramref name="startSeconds"/> beginnt. Ein Fragment-MP4 lässt sich
    /// beim Senden abspielen, ohne dass die Datei fertig sein muss; das Springen im Browser startet einen neuen Strom ab der neuen Stelle.
    /// </summary>
    public static IReadOnlyList<string> FfmpegArguments(WebStreamPlan plan, string input, double startSeconds)
    {
        if (plan.Direct) throw new InvalidOperationException("Ein Direktstream braucht kein ffmpeg.");

        var args = new List<string> { "-v", "error", "-nostdin" };
        if (startSeconds > 0) args.AddRange(new[] { "-ss", startSeconds.ToString("F3", CultureInfo.InvariantCulture) });
        args.AddRange(new[] { "-i", input, "-map", $"0:{plan.VideoStreamIndex ?? 0}" });
        if (plan.AudioStreamIndex is { } audio) args.AddRange(new[] { "-map", $"0:{audio}" });

        if (plan.CopyVideo)
        {
            args.AddRange(new[] { "-c:v", "copy" });
        }
        else
        {
            var scale = $"scale=-2:'min(ih,{MaxHeight})'";
            args.AddRange(new[]
            {
                "-vf", plan.ToneMap ? $"{FfmpegTool.HdrToSdr},{scale}" : $"{scale},format=yuv420p",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-profile:v", "high", "-pix_fmt", "yuv420p",
            });
        }
        if (plan.AudioStreamIndex is not null) args.AddRange(new[] { "-c:a", "aac", "-ac", "2", "-b:a", "192k" });
        args.AddRange(new[] { "-sn", "-dn", "-movflags", "frag_keyframe+empty_moov+default_base_moof", "-f", "mp4", "pipe:1" });
        return args;
    }
}
