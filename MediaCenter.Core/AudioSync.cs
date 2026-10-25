namespace MediaCenter.Core;

/// <summary>
/// Zeitversatz des Tons eines Videos, wenn Bild und Ton nicht zusammenpassen (mpv: audio-delay).
/// Positiv spielt den Ton später, negativ früher; 0 ändert nichts.
/// </summary>
public static class AudioSync
{
    public const double MaxDelaySeconds = 30;

    /// <summary>Wirft bei einem Wert außerhalb von ±<see cref="MaxDelaySeconds"/> (nie still klemmen).</summary>
    public static double Validated(double seconds)
    {
        if (!double.IsFinite(seconds) || Math.Abs(seconds) > MaxDelaySeconds)
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, $"Ton-Zeitversatz höchstens ±{MaxDelaySeconds:0} s.");
        return seconds;
    }
}
