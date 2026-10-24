namespace MediaCenter.Core;

// Sicherung: ein in sich stimmender Schnappschuss der Datenbank, während die App sie benutzt.
public sealed partial class LibraryDb
{
    /// <summary>Schema der Datenbank, wie diese Version der App sie schreibt (eine Sicherung mit höherem Schema stammt von einer neueren App).</summary>
    public static int CurrentSchema => SchemaVersion;

    /// <summary>
    /// Schreibt eine verdichtete Kopie der Datenbank nach <paramref name="path"/> (SQLite „VACUUM INTO“). Die Kopie ist auch dann stimmig, wenn
    /// die App gerade schreibt, und braucht keine Sperre der laufenden Datenbank.
    /// </summary>
    /// <exception cref="IOException">Die Zieldatei gibt es schon (VACUUM INTO überschreibt nie).</exception>
    public void BackupTo(string path)
    {
        if (File.Exists(path)) throw new IOException($"Die Datei gibt es schon: {path}");
        Run("VACUUM INTO $path", ("$path", Path.GetFullPath(path)));
    }
}
