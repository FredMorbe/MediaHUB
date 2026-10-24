namespace MediaCenter.Core;

/// <summary>
/// Überblenden zwischen zwei Musiktiteln: Der nächste Titel beginnt, solange der laufende noch ausklingt; beide ändern ihre Lautstärke
/// gegenläufig mit gleicher Gesamtenergie (Sinus/Kosinus), damit die Mitte nicht leiser klingt.
/// </summary>
public static class Crossfade
{
    /// <summary>Wählbare Längen in Sekunden; 0 = aus (lückenlose Übergänge ohne Überblendung).</summary>
    public static IReadOnlyList<int> Options { get; } = new[] { 0, 2, 4, 6, 8, 12 };

    /// <summary>
    /// So viele Sekunden vor Beginn der Überblendung wird der zweite Player vorbereitet (ein mpv-Start dauert einen Moment);
    /// der Titel selbst wird erst bei <see cref="ShouldStart"/> geladen.
    /// </summary>
    public const double PrepareLeadSeconds = 5;

    /// <exception cref="ArgumentOutOfRangeException">Die Länge gehört nicht zu <see cref="Options"/>.</exception>
    public static int Validated(int seconds) =>
        Options.Contains(seconds) ? seconds : throw new ArgumentOutOfRangeException(nameof(seconds), seconds, $"Überblendung: {string.Join(", ", Options)} Sekunden.");

    /// <summary>
    /// Ob der laufende Titel überhaupt überblendet werden kann: Kurze Titel (unter dem Dreifachen der Überblendung) laufen lückenlos weiter,
    /// damit Anfang und Ende nicht ineinander verschwimmen.
    /// </summary>
    public static bool Applies(double durationSeconds, int fadeSeconds) =>
        fadeSeconds > 0 && double.IsFinite(durationSeconds) && durationSeconds >= fadeSeconds * 3;

    /// <summary>Der zweite Player soll jetzt bereitstehen.</summary>
    public static bool ShouldPrepare(double positionSeconds, double durationSeconds, int fadeSeconds) =>
        Applies(durationSeconds, fadeSeconds) && durationSeconds - positionSeconds <= fadeSeconds + PrepareLeadSeconds;

    /// <summary>Der nächste Titel soll jetzt einsetzen, damit die Überblendung genau am Ende des laufenden endet.</summary>
    public static bool ShouldStart(double positionSeconds, double durationSeconds, int fadeSeconds) =>
        Applies(durationSeconds, fadeSeconds) && durationSeconds - positionSeconds <= fadeSeconds;

    /// <summary>Lautstärkefaktoren (0 bis 1) für den ausklingenden und den einsetzenden Titel; <paramref name="progress"/> 0 bis 1 (außerhalb wird begrenzt).</summary>
    public static (double Outgoing, double Incoming) Gains(double progress)
    {
        var p = double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 0;
        return (Math.Cos(p * Math.PI / 2), Math.Sin(p * Math.PI / 2));
    }
}
