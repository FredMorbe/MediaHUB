using System.Security.Cryptography;
using System.Text;

namespace MediaCenter.Core;

/// <summary>Der gespeicherte Stand der Anmeldung am Heimnetz-Server: die PIN und von den angemeldeten Geräten nur die Prüfsummen ihrer Schlüssel.</summary>
public sealed record RemoteAccessState(string Pin, IReadOnlyList<string> TokenHashes);

/// <param name="Ok">Die PIN stimmte; <paramref name="Token"/> ist der Schlüssel für das Gerät (das Gerät bewahrt ihn im Cookie auf).</param>
/// <param name="LockedFor">Zu viele falsche Versuche von diesem Gerät: so lange wird nichts angenommen (auch nicht die richtige PIN).</param>
public sealed record RemoteLogin(bool Ok, string? Token, TimeSpan? LockedFor);

/// <summary>
/// Zugang zum Heimnetz-Server: Wer die PIN (aus den Einstellungen) kennt, bekommt einen Schlüssel für sein Gerät; ohne gültigen Schlüssel antwortet der
/// Server nur mit der Anmeldeseite. Schlüssel liegen nur als Prüfsumme auf der Platte. Nach fünf falschen PINs ist das Gerät zehn Minuten gesperrt.
/// Der Zugang ist für das eigene Heimnetz gedacht (HTTP, ohne Verschlüsselung).
/// </summary>
public sealed class RemoteAccess
{
    public const int PinLength = 8;
    public const int MaxFailures = 5;
    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(10);
    private const int MaxDevices = 25;

    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();
    private readonly List<string> _tokenHashes;
    private readonly Dictionary<string, List<DateTime>> _failures = new();

    public RemoteAccess(RemoteAccessState? state = null, Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
        Pin = state?.Pin is { Length: PinLength } pin && pin.All(char.IsAsciiDigit) ? pin : NewPin();
        _tokenHashes = state?.TokenHashes.ToList() ?? new();
    }

    /// <summary>Die PIN, die der Nutzer auf dem Handy eingibt (nur Ziffern).</summary>
    public string Pin { get; private set; }

    /// <summary>Wird aufgerufen, wenn sich der Stand ändert (zum Speichern).</summary>
    public event Action? Changed;

    public RemoteAccessState State
    {
        get
        {
            lock (_gate) return new RemoteAccessState(Pin, _tokenHashes.ToList());
        }
    }

    public int DeviceCount
    {
        get
        {
            lock (_gate) return _tokenHashes.Count;
        }
    }

    public static string NewPin()
    {
        var digits = new char[PinLength];
        for (var i = 0; i < digits.Length; i++) digits[i] = (char)('0' + RandomNumberGenerator.GetInt32(10));
        return new string(digits);
    }

    /// <summary>Prüft die PIN eines Geräts (<paramref name="client"/>: seine Adresse) und gibt bei Erfolg einen neuen Schlüssel aus.</summary>
    public RemoteLogin TryLogin(string client, string? pin)
    {
        lock (_gate)
        {
            var now = _clock();
            var recent = _failures.TryGetValue(client, out var list) ? list.Where(t => now - t < Lockout).ToList() : new List<DateTime>();
            _failures[client] = recent;
            if (recent.Count >= MaxFailures) return new RemoteLogin(false, null, Lockout - (now - recent.Min()));

            var given = Encoding.UTF8.GetBytes(pin ?? "");
            var expected = Encoding.UTF8.GetBytes(Pin);
            if (given.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(given, expected))
            {
                recent.Add(now);
                return new RemoteLogin(false, null, recent.Count >= MaxFailures ? Lockout : null);
            }

            _failures.Remove(client);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            _tokenHashes.Add(Hash(token));
            while (_tokenHashes.Count > MaxDevices) _tokenHashes.RemoveAt(0); // das älteste Gerät muss sich neu anmelden
            Raise();
            return new RemoteLogin(true, token, null);
        }
    }

    public bool IsValidToken(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length != 64) return false;

        var hash = Encoding.ASCII.GetBytes(Hash(token));
        lock (_gate)
        {
            var valid = false;
            foreach (var stored in _tokenHashes) valid |= CryptographicOperations.FixedTimeEquals(hash, Encoding.ASCII.GetBytes(stored)); // alle prüfen: gleich lang, gleich schnell
            return valid;
        }
    }

    /// <summary>Meldet alle Geräte ab (neue PIN, alte Schlüssel gelten nicht mehr).</summary>
    public void RevokeAll()
    {
        lock (_gate)
        {
            Pin = NewPin();
            _tokenHashes.Clear();
            _failures.Clear();
            Raise();
        }
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token))).ToLowerInvariant();

    private void Raise() => Changed?.Invoke();
}
