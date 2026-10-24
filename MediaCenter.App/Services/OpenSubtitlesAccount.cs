using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaCenter.Core;

namespace MediaCenter.App.Services;

/// <summary>API-Schlüssel, Benutzername und Passwort für OpenSubtitles.</summary>
public sealed record OpenSubtitlesLogin(string Key, string User, string Password);

/// <summary>
/// Zugangsdaten für die Untertitel-Suche. Umgebungsvariablen haben Vorrang (<see cref="KeyVariable"/>, <see cref="UserVariable"/>, <see cref="PasswordVariable"/>);
/// was in den Einstellungen eingegeben wird, liegt mit dem Windows-Konto verschlüsselt (DPAPI) im Datenordner und ist auf anderen Konten oder
/// Rechnern unlesbar. Die Sicherung nimmt die Datei bewusst nicht mit.
/// </summary>
public static class OpenSubtitlesAccount
{
    public const string KeyVariable = "MEDIACENTER_OPENSUBTITLES_KEY";

    public const string UserVariable = "MEDIACENTER_OPENSUBTITLES_USER";

    public const string PasswordVariable = "MEDIACENTER_OPENSUBTITLES_PASSWORD";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MediaCenter.OpenSubtitles.v1");

    public static string FilePath => Path.Combine(AppSettings.DataDirectory, "opensubtitles.cred");

    private sealed record Stored(string? Key, string? User, string? Password);

    /// <summary>Die Zugangsdaten, nach Feldern aus der Umgebung oder der Datei; null, solange eines der drei fehlt.</summary>
    /// <exception cref="InvalidDataException">Die gespeicherte Datei ist unlesbar (z. B. von einem anderen Windows-Konto).</exception>
    public static OpenSubtitlesLogin? Load()
    {
        var stored = ReadFile();
        string? Pick(string variable, string? fromFile) => Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value.Trim() : fromFile;
        var key = Pick(KeyVariable, stored?.Key);
        var user = Pick(UserVariable, stored?.User);
        var password = Pick(PasswordVariable, stored?.Password);
        return string.IsNullOrEmpty(key) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password) ? null : new OpenSubtitlesLogin(key, user, password);
    }

    /// <summary>Kurze Beschreibung für die Einstellungen; nennt nie die Werte.</summary>
    public static string Describe()
    {
        try
        {
            var stored = ReadFile();
            var fromVariable = new[] { KeyVariable, UserVariable, PasswordVariable }.Where(v => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v))).ToList();
            if (Load() is null)
                return "Noch nicht eingerichtet. Für die Suche brauchst du einen API-Schlüssel, zum Herunterladen ein Konto auf opensubtitles.com.";
            if (fromVariable.Count > 0) return $"Eingerichtet; {fromVariable.Count} von 3 Angaben kommen aus Umgebungsvariablen (haben Vorrang).";
            return stored is null ? "Noch nicht eingerichtet." : $"Eingerichtet, verschlüsselt gespeichert in {FilePath}.";
        }
        catch (InvalidDataException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Speichert die Eingaben; ein leeres Feld behält den bisherigen Wert. Aus der Datei wird nie ein Wert zurück in die Oberfläche gegeben.</summary>
    public static void Save(string? key, string? user, string? password)
    {
        var old = ReadFile();
        var next = new Stored(Clean(key) ?? old?.Key, Clean(user) ?? old?.User, string.IsNullOrEmpty(password) ? old?.Password : password);
        if (next.Key is { } k && !k.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException("Ein API-Schlüssel von OpenSubtitles besteht nur aus Buchstaben und Ziffern.");

        Directory.CreateDirectory(AppSettings.DataDirectory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(next);
        File.WriteAllBytes(FilePath, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
    }

    public static void Clear()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Stored? ReadFile()
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Stored>(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            throw new InvalidDataException($"Die gespeicherten OpenSubtitles-Zugangsdaten sind unlesbar ({FilePath}); bitte neu eingeben oder die Datei löschen.", ex);
        }
    }
}
