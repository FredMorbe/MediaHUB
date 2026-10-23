namespace MediaCenter.Core;

/// <param name="Tracks">Ton- und Untertitelspuren wie im Player (Id zählt je Art ab 1); <see cref="MediaTrack.Selected"/> zeigt die laufende.</param>
/// <param name="Subtitles">Die Untertitel, die der Fernseher als Spur bekommt; <see cref="CastSubtitle.Id"/> ist dieselbe Id wie in <paramref name="Tracks"/>.</param>
/// <param name="ActiveSubtitleId">Welche Untertitelspur anfangs läuft (nach den Einstellungen); null = keine.</param>
public sealed record CastTrackSet(IReadOnlyList<MediaTrack> Tracks, IReadOnlyList<CastSubtitle> Subtitles, int? ActiveSubtitleId);

/// <summary>Stellt aus einer Videodatei die Spuren zusammen, die beim Übertragen wählbar sind.</summary>
public static class CastTracks
{
    /// <summary>Formate, die sich als Text übertragen lassen. (.sub/.idx sind meist Bilduntertitel, .smi ist selten.)</summary>
    private static readonly HashSet<string> SidecarExtensions = new(StringComparer.OrdinalIgnoreCase) { ".srt", ".ass", ".ssa", ".vtt" };

    private static readonly string[] SidecarFolders = ["Subs", "Sub", "Subtitles", "Untertitel"];

    /// <summary>Untertitel-Dateien neben dem Video: gleicher Anfang des Namens, im Ordner des Videos oder in einem Unterordner „Subs“.</summary>
    public static IReadOnlyList<string> FindSidecars(string videoPath)
    {
        var directory = Path.GetDirectoryName(videoPath);
        if (directory is null || !Directory.Exists(directory)) return Array.Empty<string>();

        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        var folders = new[] { directory }.Concat(SidecarFolders.Select(f => Path.Combine(directory, f)).Where(Directory.Exists));
        return folders
            .SelectMany(folder => Directory.EnumerateFiles(folder))
            .Where(f => SidecarExtensions.Contains(Path.GetExtension(f)) && Path.GetFileName(f).StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Tonspur nach der bevorzugten Sprache (ffmpeg-Streamnummer); null, wenn keine Sprache bevorzugt ist oder keine Spur passt –
    /// dann gilt die Standardspur der Datei, wie bei mpv.
    /// </summary>
    public static int? PreferredAudioStream(ProbeResult probe, PlaybackPreferences preferences)
    {
        if (preferences.AudioLanguage is not { } key) return null;

        var wanted = Languages.Get(key);
        return probe.Streams.FirstOrDefault(s => s.Type == "audio" && (Languages.Find(s.Language) ?? Languages.FindInText(s.Title)) == wanted)?.Index;
    }

    /// <param name="audioStreamIndex">Gewählte Tonspur (ffmpeg-Streamnummer); null nimmt die Standardspur.</param>
    /// <param name="stored">Vom Nutzer hinzugefügte Untertitel (<see cref="SubtitleStore"/>); sie gehen bei der Wahl der Automatik vor.</param>
    /// <param name="sidecars">Dateien neben dem Video (<see cref="FindSidecars"/>).</param>
    public static CastTrackSet Build(ProbeResult probe, int? audioStreamIndex, IReadOnlyList<string> stored, IReadOnlyList<string> sidecars, PlaybackPreferences preferences)
    {
        var tracks = new List<MediaTrack>();
        var subtitles = new List<CastSubtitle>();

        var audioStreams = probe.Streams.Where(s => s.Type == "audio").ToList();
        var chosen = audioStreamIndex is { } wanted
            ? audioStreams.FirstOrDefault(s => s.Index == wanted) ?? throw new ArgumentException($"Die Datei hat keine Tonspur mit Streamnummer {wanted}.", nameof(audioStreamIndex))
            : audioStreams.FirstOrDefault(s => s.IsDefault) ?? audioStreams.FirstOrDefault();
        for (var i = 0; i < audioStreams.Count; i++)
        {
            var s = audioStreams[i];
            tracks.Add(new MediaTrack(i + 1, "audio", s.Language, s.Title, s.Codec, s.IsDefault, s.IsForced, External: false, Selected: ReferenceEquals(s, chosen), s.Channels, FfIndex: s.Index));
        }

        var nextSubtitle = 1;
        void Add(MediaTrack track, string? file, int? streamIndex)
        {
            var language = Languages.Find(track.Lang) ?? track.Language;
            tracks.Add(track);
            subtitles.Add(new CastSubtitle(track.Id, language?.Id ?? "und", track.Label, file, streamIndex));
        }

        foreach (var s in probe.Streams.Where(s => s.Type == "subtitle" && SubtitleExtractor.IsText(s.Codec)))
        {
            Add(new MediaTrack(nextSubtitle++, "sub", s.Language, s.Title, s.Codec, s.IsDefault, s.IsForced, External: false, Selected: false, FfIndex: s.Index), null, s.Index);
        }
        foreach (var file in stored.Concat(sidecars.Except(stored, StringComparer.OrdinalIgnoreCase)))
        {
            var isStored = stored.Contains(file, StringComparer.OrdinalIgnoreCase);
            var language = SubtitleStore.DetectLanguage(file);
            Add(new MediaTrack(nextSubtitle++, "sub", language?.Id, Path.GetFileName(file), Path.GetExtension(file).TrimStart('.').ToLowerInvariant() is "srt" ? "subrip" : Path.GetExtension(file).TrimStart('.').ToLowerInvariant(),
                Default: false, Forced: false, External: true, Selected: false, File: file, UserAdded: isStored), file, null);
        }

        int? active = null;
        if (TrackSelector.TryPickSubtitle(tracks, preferences, out var picked)) active = picked;
        if (active is { } id)
        {
            var index = tracks.FindIndex(t => t.Type == "sub" && t.Id == id);
            tracks[index] = tracks[index] with { Selected = true };
        }
        return new CastTrackSet(tracks, subtitles, active);
    }
}
