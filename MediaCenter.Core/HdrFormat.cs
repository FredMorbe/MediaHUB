namespace MediaCenter.Core;

/// <summary>
/// Die HDR-Art eines Videos. Die Zahl steht in <c>files.is_hdr</c> (0 = SDR); ältere Scans haben dort 1 geschrieben, als es nur „HDR ja/nein“ gab.
/// </summary>
public enum HdrFormat
{
    None = 0,

    /// <summary>HDR aus einem älteren Scan: PQ oder HLG, welche Art genau ist nicht bekannt (neu gelesen, sobald die Datei sich ändert).</summary>
    Unspecified = 1,

    /// <summary>PQ (SMPTE 2084) mit festen Metadaten.</summary>
    Hdr10 = 2,

    /// <summary>Hybrid Log-Gamma (Fernsehen, manche Streams).</summary>
    Hlg = 3,

    /// <summary>PQ mit dynamischen Metadaten je Szene (SMPTE 2094-40).</summary>
    Hdr10Plus = 4,

    /// <summary>Dolby Vision mit PQ- oder HLG-Grundbild (Profil 7, 8): Ohne Dolby-Vision-Auswertung bleibt ein HDR10-Bild.</summary>
    DolbyVision = 5,

    /// <summary>Dolby Vision ohne HDR10-Grundbild (Profil 5): ffprobe meldet keinen PQ/HLG-Wert, das Bild braucht die Dolby-Daten (mpv wertet sie aus).</summary>
    DolbyVisionOnly = 6,
}

public static class HdrFormats
{
    private const string PqTransfer = "smpte2084";
    private const string HlgTransfer = "arib-std-b67";

    /// <summary>Liest die HDR-Art aus dem Videostream; null (kein Video) gilt als SDR.</summary>
    public static HdrFormat Detect(StreamInfo? video)
    {
        if (video is null) return HdrFormat.None;

        var pq = video.ColorTransfer == PqTransfer;
        var hlg = video.ColorTransfer == HlgTransfer;
        var dolby = video.SideData?.Any(s => s.Contains("DOVI", StringComparison.OrdinalIgnoreCase) || s.Contains("Dolby Vision", StringComparison.OrdinalIgnoreCase)) == true;
        var plus = video.SideData?.Any(s => s.Contains("2094-40", StringComparison.Ordinal) || s.Contains("HDR10+", StringComparison.Ordinal)) == true;

        if (dolby) return pq || hlg ? HdrFormat.DolbyVision : HdrFormat.DolbyVisionOnly;
        if (pq) return plus ? HdrFormat.Hdr10Plus : HdrFormat.Hdr10;
        return hlg ? HdrFormat.Hlg : HdrFormat.None;
    }

    /// <summary>Kurzname für Anzeigen; leer bei SDR.</summary>
    public static string Label(this HdrFormat format) => format switch
    {
        HdrFormat.None => "",
        HdrFormat.Unspecified => "HDR",
        HdrFormat.Hdr10 => "HDR10",
        HdrFormat.Hlg => "HLG",
        HdrFormat.Hdr10Plus => "HDR10+",
        HdrFormat.DolbyVision or HdrFormat.DolbyVisionOnly => "Dolby Vision",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unbekannte HDR-Art."),
    };

    /// <summary>
    /// Ob das Bild PQ oder HLG trägt, sodass ffmpeg es nach SDR umrechnen kann (Vorschau, Übertragen); Dolby Vision ohne Grundbild
    /// kann ffmpeg nicht umrechnen.
    /// </summary>
    public static bool IsToneMappable(this HdrFormat format) => format is not (HdrFormat.None or HdrFormat.DolbyVisionOnly);

    /// <summary>Liest den gespeicherten Wert; eine unbekannte Zahl ist ein lauter Fehler (Datenbank von einer neueren Version).</summary>
    public static HdrFormat FromStored(int value) =>
        Enum.IsDefined(typeof(HdrFormat), value) ? (HdrFormat)value : throw new InvalidDataException($"Unbekannte HDR-Art {value} in der Datenbank.");
}

/// <summary>Ob HDR-Videos als HDR an den Bildschirm gehen.</summary>
public enum HdrOutput
{
    /// <summary>mpv entscheidet: HDR-Ausgabe, wenn Windows-HDR für den Bildschirm an ist, sonst Umrechnung nach SDR.</summary>
    Auto,

    /// <summary>Immer nach SDR umrechnen.</summary>
    Sdr,
}

public static class HdrOutputs
{
    /// <summary>
    /// Wert für mpvs <c>target-colorspace-hint</c>: „auto“ lässt HDR an den Bildschirm durch, „no“ erzwingt die Umrechnung nach SDR.
    /// LUTs und Farbfilter (Weißabgleich, Schärfe, Invertieren) sind für SDR-Bilder gebaut und arbeiten sonst auf den PQ-Werten; mit ihnen bleibt das Bild SDR.
    /// </summary>
    public static string ColorspaceHint(HdrOutput mode, ColorSettings color) =>
        mode == HdrOutput.Sdr || color.UsesShader || color.UsesLut ? "no" : "auto";

    /// <summary>Ob die Umrechnung nur wegen Farbfiltern oder LUT erzwungen ist (der Nutzer hat HDR-Ausgabe an, bekommt sie aber nicht).</summary>
    public static bool FilterForcesSdr(HdrOutput mode, ColorSettings color) => mode == HdrOutput.Auto && ColorspaceHint(mode, color) == "no";
}
