using System.Security.Cryptography;

namespace MediaCenter.Core;

/// <summary>
/// PIN eines Profils: vier bis acht Ziffern, gespeichert nur als salzgesicherter PBKDF2-Hash. Die PIN schützt vor dem Wechseln in ein Profil
/// (und davor, dass ein Kinderprofil zu einem Erwachsenenprofil wechselt); sie verschlüsselt keine Daten – wer Zugriff auf den Datenordner hat,
/// kommt auch ohne PIN an die Bibliothek.
/// </summary>
public static class ProfilePin
{
    public const int MinLength = 4;
    public const int MaxLength = 8;

    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Iterations = 100_000;

    /// <exception cref="ArgumentException">Keine vier bis acht Ziffern.</exception>
    public static string Validated(string? pin)
    {
        if (pin is null || pin.Length is < MinLength or > MaxLength || !pin.All(c => c is >= '0' and <= '9'))
            throw new ArgumentException($"Die PIN besteht aus {MinLength} bis {MaxLength} Ziffern.", nameof(pin));
        return pin;
    }

    /// <summary>Neues Salz und Hash zur PIN, beides als Base64.</summary>
    public static (string Salt, string Hash) Create(string pin)
    {
        Validated(pin);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        return (Convert.ToBase64String(salt), Convert.ToBase64String(Derive(pin, salt)));
    }

    public static bool Verify(string? pin, string salt, string hash)
    {
        if (pin is null || pin.Length is < MinLength or > MaxLength) return false;
        return CryptographicOperations.FixedTimeEquals(Derive(pin, Convert.FromBase64String(salt)), Convert.FromBase64String(hash));
    }

    private static byte[] Derive(string pin, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
}
