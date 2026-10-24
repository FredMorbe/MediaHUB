using System.Security.Cryptography;

namespace MediaCenter.Core;

/// <summary>
/// Fingerabdruck einer Datei aus Größe und drei Blöcken (Anfang, Mitte, Ende). Er überlebt Verschieben und Umbenennen; wird eine
/// Datei neu kodiert oder auch nur im Inneren verändert, ändert er sich meist mit. Damit findet der Scan eine verschobene Datei wieder
/// und hängt ihre Nutzerdaten (Fortschritt, Untertitel, Farbe, Merkliste) an den neuen Ort.
/// </summary>
public static class FileFingerprint
{
    private const int BlockSize = 64 * 1024;

    /// <exception cref="IOException">Die Datei ist nicht lesbar.</exception>
    public static string Compute(string path)
    {
        // Wird die Datei gerade abgespielt oder kopiert, soll das Lesen trotzdem klappen.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var size = stream.Length;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(BitConverter.GetBytes(size));

        var buffer = new byte[BlockSize];
        foreach (var offset in Offsets(size))
        {
            stream.Position = offset;
            var read = stream.ReadAtLeast(buffer, (int)Math.Min(BlockSize, size - offset), throwOnEndOfStream: false);
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset(), 0, 16).ToLowerInvariant();
    }

    /// <summary>Kleine Dateien werden ganz gelesen, große an drei Stellen.</summary>
    private static IEnumerable<long> Offsets(long size)
    {
        yield return 0;
        if (size <= BlockSize) yield break;
        if (size > 3L * BlockSize) yield return (size - BlockSize) / 2;
        yield return size - BlockSize;
    }
}
