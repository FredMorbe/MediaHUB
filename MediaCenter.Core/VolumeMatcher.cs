namespace MediaCenter.Core;

public enum VolumeMatchKind
{
    /// <summary>Kein bekanntes Laufwerk hat diese Seriennummer.</summary>
    New,

    /// <summary>Eindeutig ein bekanntes Laufwerk.</summary>
    Matched,

    /// <summary>Mehrdeutig oder verdächtig – der Nutzer muss entscheiden.</summary>
    NeedsConfirmation,
}

public sealed record VolumeMatch(
    VolumeMatchKind Kind, long? VolumeId, string? Reason, IReadOnlyList<KnownVolume> Candidates);

/// <summary>Regeln, nach denen ein angeschlossenes Laufwerk einem bekannten zugeordnet wird.</summary>
public static class VolumeMatcher
{
    public static VolumeMatch Match(VolumeInfo mounted, IReadOnlyList<KnownVolume> known, IReadOnlyList<VolumeInfo> allMounted)
    {
        var candidates = known.Where(k => string.Equals(k.Serial, mounted.Serial, StringComparison.OrdinalIgnoreCase)).ToList();

        if (allMounted.Count(m => string.Equals(m.Serial, mounted.Serial, StringComparison.OrdinalIgnoreCase)) > 1)
        {
            return new VolumeMatch(VolumeMatchKind.NeedsConfirmation, null,
                $"Mehrere angeschlossene Laufwerke haben dieselbe Seriennummer {mounted.Serial}.", candidates);
        }

        if (candidates.Count == 0) return new VolumeMatch(VolumeMatchKind.New, null, null, candidates);

        var exact = candidates.Where(k => k.Capacity == mounted.Capacity &&
            string.Equals(k.FileSystem, mounted.FileSystem, StringComparison.OrdinalIgnoreCase)).ToList();

        return exact.Count switch
        {
            1 => new VolumeMatch(VolumeMatchKind.Matched, exact[0].Id, null, candidates),
            0 => new VolumeMatch(VolumeMatchKind.NeedsConfirmation, null,
                $"Seriennummer {mounted.Serial} ist bekannt, Dateisystem oder Kapazität weichen aber ab " +
                "(neu formatiert, geklont oder ein anderes Laufwerk?).", candidates),
            _ => new VolumeMatch(VolumeMatchKind.NeedsConfirmation, null,
                $"Mehrere bekannte Laufwerke passen auf Seriennummer {mounted.Serial}.", candidates),
        };
    }
}
