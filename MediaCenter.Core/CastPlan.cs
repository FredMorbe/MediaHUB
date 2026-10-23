using System.Globalization;

namespace MediaCenter.Core;

public enum CastMode
{
    /// <summary>Der Fernseher holt die Datei unverändert (mit Range-Anfragen, Spulen geht direkt).</summary>
    Direct,

    /// <summary>ffmpeg liefert fragmentiertes MP4: Spuren, die der Fernseher kann, werden kopiert, die anderen umgewandelt.</summary>
    Stream,
}

/// <param name="Hevc">Der Fernseher spielt HEVC (auch HDR) selbst ab; sonst wird HEVC nach H.264 umgewandelt.</param>
public sealed record CastCapabilities(bool Hevc = false);

/// <param name="AudioStreamIndex">ffmpeg-Streamnummer der Tonspur; null bei einem Video ohne Ton.</param>
/// <param name="ToneMap">HDR-Bild wird beim Umwandeln nach SDR umgerechnet.</param>
/// <param name="Description">Kurz, warum so entschieden wurde (für Meldungen und Logs).</param>
public sealed record CastPlan(
    CastMode Mode, bool CopyVideo, bool CopyAudio, bool ToneMap, int? AudioStreamIndex, string VideoCodec, string Description)
{
    public const string ContentType = "video/mp4";
}

/// <summary>Entscheidet, ob eine Datei direkt an den Fernseher gehen kann oder ffmpeg etwas umwandeln muss.</summary>
public static class CastCompatibility
{
    /// <summary>Tonformate, die jeder Cast-Empfänger im MP4 spielt.</summary>
    private static readonly HashSet<string> CopyableAudio = new(StringComparer.OrdinalIgnoreCase) { "aac", "mp3" };

    private static readonly HashSet<string> DirectExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov" };

    /// <param name="audioStreamIndex">Gewünschte Tonspur (ffmpeg-Streamnummer); null nimmt die Standardspur der Datei.</param>
    public static CastPlan Decide(ProbeResult probe, string path, CastCapabilities capabilities, int? audioStreamIndex = null)
    {
        var video = probe.Video ?? throw new InvalidOperationException("Die Datei hat keine Videospur.");
        var audioStreams = probe.Streams.Where(s => s.Type == "audio").ToList();
        var audio = audioStreamIndex is { } wanted
            ? audioStreams.FirstOrDefault(s => s.Index == wanted) ?? throw new ArgumentException($"Die Datei hat keine Tonspur mit Streamnummer {wanted}.", nameof(audioStreamIndex))
            : audioStreams.FirstOrDefault(s => s.IsDefault) ?? audioStreams.FirstOrDefault();

        var hdr = probe.IsHdr;
        var h264 = string.Equals(video.Codec, "h264", StringComparison.OrdinalIgnoreCase);
        var hevc = string.Equals(video.Codec, "hevc", StringComparison.OrdinalIgnoreCase);

        // H.264 mit mehr als 8 Bit (High 10, 4:2:2, 4:4:4) kann kein Cast-Gerät. HDR-H.264 gibt es praktisch nicht, würde aber umgerechnet.
        var copyVideo = (h264 && !IsHighBitDepth(video.Profile) && !hdr) || (hevc && capabilities.Hevc);
        var copyAudio = audio is null || (audio.Codec is not null && CopyableAudio.Contains(audio.Codec));
        var toneMap = hdr && !copyVideo;

        var firstAudio = audioStreams.FirstOrDefault();
        var direct = h264 && copyVideo && copyAudio
            && DirectExtensions.Contains(Path.GetExtension(path))
            // Der Empfänger spielt im MP4 die erste Tonspur; eine andere Wahl braucht ffmpeg.
            && (audio is null || ReferenceEquals(audio, firstAudio));

        var description = direct
            ? "Direkt: H.264 und Ton passen, MP4."
            : $"Stream: Bild {(copyVideo ? "kopiert" : toneMap ? "umgewandelt (HDR nach SDR)" : "umgewandelt")}, Ton {(audio is null ? "keiner" : copyAudio ? "kopiert" : $"umgewandelt ({audio.Codec})")}.";

        return new CastPlan(direct ? CastMode.Direct : CastMode.Stream, copyVideo, copyAudio, toneMap, audio?.Index, video.Codec ?? "?", description);
    }

    private static bool IsHighBitDepth(string? profile) =>
        profile is not null && (profile.Contains("10", StringComparison.Ordinal) || profile.Contains("4:2:2", StringComparison.Ordinal)
            || profile.Contains("4:4:4", StringComparison.Ordinal) || profile.Contains("Predictive", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Baut die ffmpeg-Argumente für den Stream zum Fernseher.</summary>
public static class CastFfmpeg
{
    public const string SoftwareEncoder = "libx264";
    public const string NvidiaEncoder = "h264_nvenc";

    /// <summary>Höchste Bildhöhe beim Umwandeln; größere Bilder werden verkleinert, kleinere nie vergrößert.</summary>
    public const int MaxHeight = 1080;

    /// <param name="startSeconds">Ab hier ausliefern (die Zeitachse des Streams beginnt dann wieder bei 0).</param>
    /// <param name="encoder"><see cref="SoftwareEncoder"/> oder <see cref="NvidiaEncoder"/>; nur beim Umwandeln des Bildes benutzt.</param>
    public static IReadOnlyList<string> StreamArguments(string path, CastPlan plan, double startSeconds, string encoder)
    {
        if (plan.Mode != CastMode.Stream) throw new ArgumentException("Nur ein Stream-Plan geht durch ffmpeg.", nameof(plan));
        if (startSeconds < 0 || !double.IsFinite(startSeconds)) throw new ArgumentOutOfRangeException(nameof(startSeconds), startSeconds, "Die Startposition muss ≥ 0 sein.");
        if (encoder is not (SoftwareEncoder or NvidiaEncoder)) throw new ArgumentException($"Unbekannter Encoder '{encoder}'.", nameof(encoder));

        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error" };
        if (!plan.CopyVideo) args.AddRange(["-hwaccel", "auto"]);
        if (startSeconds > 0) args.AddRange(["-ss", startSeconds.ToString("F3", CultureInfo.InvariantCulture)]);
        args.AddRange(["-i", path, "-map", "0:v:0"]);
        if (plan.AudioStreamIndex is { } audio) args.AddRange(["-map", $"0:{audio}"]);
        args.AddRange(["-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1"]);

        if (plan.CopyVideo)
        {
            args.AddRange(["-c:v", "copy"]);
            // Chrome-basierte Empfänger erkennen HEVC im MP4 nur mit der Kennung hvc1.
            if (string.Equals(plan.VideoCodec, "hevc", StringComparison.OrdinalIgnoreCase)) args.AddRange(["-tag:v", "hvc1"]);
        }
        else
        {
            var scale = $"scale=w=-2:h='min({MaxHeight},ih)'";
            args.AddRange(["-vf", plan.ToneMap ? $"{scale},{FfmpegTool.HdrToSdr}" : $"{scale},format=yuv420p"]);
            if (encoder == NvidiaEncoder) args.AddRange(["-c:v", NvidiaEncoder, "-preset", "p4", "-rc", "vbr", "-cq", "23", "-b:v", "0", "-maxrate", "16M", "-bufsize", "32M"]);
            else args.AddRange(["-c:v", SoftwareEncoder, "-preset", "veryfast", "-crf", "22", "-maxrate", "16M", "-bufsize", "32M"]);
            args.AddRange(["-profile:v", "high", "-level", "4.1", "-g", "96", "-sc_threshold", "0"]);
        }

        if (plan.AudioStreamIndex is not null)
        {
            if (plan.CopyAudio) args.AddRange(["-c:a", "copy"]);
            else args.AddRange(["-c:a", "aac", "-b:a", "384k", "-ac", "6"]);
        }

        args.AddRange(["-avoid_negative_ts", "make_zero", "-movflags", "frag_keyframe+empty_moov+default_base_moof", "-f", "mp4", "pipe:1"]);
        return args;
    }
}

/// <summary>Untertitel für den Fernseher: WebVTT, bei Bedarf um den Startpunkt eines neu gestarteten Streams verschoben.</summary>
public static class WebVtt
{
    /// <summary>Schreibt die Einträge als WebVTT. Die Zeiten werden um <paramref name="shiftSeconds"/> nach vorn gerückt; was davor endet, entfällt.</summary>
    public static string Format(IEnumerable<SrtCue> cues, double shiftSeconds = 0)
    {
        var shift = TimeSpan.FromSeconds(shiftSeconds);
        var builder = new System.Text.StringBuilder("WEBVTT\n\n");
        foreach (var cue in cues)
        {
            var end = cue.End - shift;
            if (end <= TimeSpan.Zero) continue;
            var start = cue.Start - shift;
            if (start < TimeSpan.Zero) start = TimeSpan.Zero;
            builder.Append(Time(start)).Append(" --> ").Append(Time(end)).Append('\n');
            // "-->" im Text würde als Zeitangabe gelesen, "&" und "<" sind in WebVTT Markup; reiner Text bleibt reiner Text.
            builder.Append(Escape(cue.Text)).Append("\n\n");
        }
        return builder.ToString();
    }

    private static string Time(TimeSpan t) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}");

    /// <summary>Die einfachen HTML-Auszeichnungen aus SRT (<c>&lt;i&gt;</c>, <c>&lt;b&gt;</c>, <c>&lt;u&gt;</c>) gelten in WebVTT genauso und bleiben; alles andere wird entschärft.</summary>
    private static string Escape(string text)
    {
        // ASS-Positionsangaben wie {\an8} bleiben nach der Umwandlung als Text stehen und gehören nicht ins Bild.
        var safe = System.Text.RegularExpressions.Regex.Replace(text, @"\{\\[^}]*\}", "").Replace("&", "&amp;").Replace("-->", "--&gt;");
        // Nur erlaubte Tags behalten, jedes andere "<" wird zu Text.
        return System.Text.RegularExpressions.Regex.Replace(safe, @"<(?!/?[ibu]>)", "&lt;");
    }
}
