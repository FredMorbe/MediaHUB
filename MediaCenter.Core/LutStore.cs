namespace MediaCenter.Core;

/// <summary>
/// Die LUT-Sammlung der App: importierte <c>.cube</c>-Dateien im Datenordner (<c>luts\Name.cube</c>) und die daraus erzeugten
/// mpv-Shader (<c>luts\cache\Name.glsl</c>). Eine LUT wird beim Import geprüft; was nicht lesbar ist, kommt nicht in die Sammlung.
/// Der Name einer LUT ist der Dateiname ohne Endung.
/// </summary>
public sealed class LutStore(string root)
{
    private const int MaxBaseNameLength = 80;
    private const string Extension = ".cube";

    private readonly string _root = Path.GetFullPath(root);

    private string CachePath(string name) => Path.Combine(_root, "cache", name + ".glsl");

    public string FileOf(string name) => Path.Combine(_root, name + Extension);

    public bool Exists(string name) => File.Exists(FileOf(name));

    /// <summary>Namen aller importierten LUTs, alphabetisch.</summary>
    public IReadOnlyList<string> List()
    {
        if (!Directory.Exists(_root)) return Array.Empty<string>();

        return Directory.EnumerateFiles(_root, "*" + Extension)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Prüft die Datei und kopiert sie in die Sammlung; gibt den Namen der LUT zurück. Dieselbe Datei noch einmal zu importieren
    /// liefert die vorhandene LUT, eine andere Datei mit gleichem Namen bekommt „-2“, „-3“ … Fehler werfen mit deutscher Meldung.
    /// </summary>
    public string Add(string source)
    {
        if (!File.Exists(source)) throw new FileNotFoundException($"LUT-Datei nicht gefunden: {source}", source);
        if (!string.Equals(Path.GetExtension(source), Extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"„{Path.GetExtension(source)}“ ist kein unterstütztes LUT-Format (erlaubt: {Extension}).");

        var bytes = File.ReadAllBytes(source);
        CubeLut.Parse(File.ReadAllText(source)); // wirft bei Fehlern, bevor etwas angelegt wird
        Directory.CreateDirectory(_root);

        var baseName = CleanBaseName(Path.GetFileNameWithoutExtension(source));
        for (var attempt = 1; ; attempt++)
        {
            var name = attempt == 1 ? baseName : $"{baseName}-{attempt}";
            var target = FileOf(name);
            if (!File.Exists(target))
            {
                File.WriteAllBytes(target, bytes);
                return name;
            }
            if (File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) return name; // gleicher Inhalt: nichts doppelt ablegen
        }
    }

    /// <summary>Löscht die LUT samt Shader. Eine unbekannte LUT wirft.</summary>
    public void Remove(string name)
    {
        var file = FileOf(name);
        if (!File.Exists(file)) throw new FileNotFoundException($"LUT „{name}“ ist nicht in der Sammlung.", file);

        File.Delete(file);
        if (File.Exists(CachePath(name))) File.Delete(CachePath(name));
    }

    /// <summary>Pfad des mpv-Shaders dieser LUT; wird erzeugt, wenn er fehlt oder älter als die LUT ist. Eine fehlende LUT wirft.</summary>
    public string ShaderPath(string name)
    {
        var file = FileOf(name);
        if (!File.Exists(file)) throw new FileNotFoundException($"LUT „{name}“ ist nicht in der Sammlung.", file);

        var cache = CachePath(name);
        if (!File.Exists(cache) || File.GetLastWriteTimeUtc(cache) < File.GetLastWriteTimeUtc(file))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            var lut = CubeLut.Load(file);
            File.WriteAllText(cache, lut.ToMpvShader(), new System.Text.UTF8Encoding(false));
        }
        return cache;
    }

    /// <summary>Name ohne Zeichen, die Dateisysteme nicht mögen, und ohne Semikolon (Trenner der mpv-Shaderliste).</summary>
    private static string CleanBaseName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Replace(';', '_');
        name = name.Trim().TrimEnd('.');
        if (name.Length > MaxBaseNameLength) name = name[..MaxBaseNameLength].TrimEnd();
        return name.Length == 0 ? "LUT" : name;
    }
}
