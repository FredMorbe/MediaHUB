using System.IO;
using System.Net.Http;
using System.Text.Json;
using MediaCenter.Core;

namespace MediaCenter.App.Services;

/// <summary>
/// Die Trakt-Anbindung der App: Client-ID und Client-Secret der eigenen Trakt-App, die Anmeldung (Gerätecode) und der Abgleich „gesehen“.
/// Alles liegt in <c>trakt.json</c> im Datenordner (nicht in der Sicherung). Das Konto gehört zu dem Profil, das es verbunden hat; nur dessen Gesehen-Stände
/// werden abgeglichen. Kinderprofile gleichen nie ab.
/// </summary>
public sealed class TraktService(LibraryDb db, HttpClient http, Func<long> currentProfileId, Func<bool> currentIsKids)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string FilePath { get; } = Path.Combine(AppSettings.DataDirectory, "trakt.json");

    private TraktAccount? _account;
    private bool _loaded;
    private bool _syncing;

    /// <summary>Wann zuletzt automatisch abgeglichen wurde (der Abgleich nach dem Player läuft höchstens alle zehn Minuten).</summary>
    private DateTime _lastAuto = DateTime.MinValue;

    private sealed record Stored(string ClientId, string ClientSecret, long ProfileId, string? Username, string? AccessToken, string? RefreshToken, DateTime? ExpiresUtc);

    /// <summary>Die gespeicherten Zugangsdaten; null, solange nichts eingetragen ist. Eine kaputte Datei ist ein lauter Fehler.</summary>
    public TraktAccount? Account
    {
        get
        {
            if (_loaded) return _account;
            _loaded = true;
            if (!File.Exists(FilePath)) return _account = null;
            try
            {
                var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath)) ?? throw new InvalidDataException("Datei ist leer.");
                var tokens = stored.AccessToken is not null && stored.RefreshToken is not null && stored.ExpiresUtc is { } expires
                    ? new TraktTokens(stored.AccessToken, stored.RefreshToken, expires) : null;
                return _account = new TraktAccount(stored.ClientId, stored.ClientSecret, stored.ProfileId, stored.Username, tokens);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                throw new InvalidDataException($"Die Trakt-Daten sind fehlerhaft: {FilePath}. Datei löschen, um neu zu beginnen. ({ex.Message})", ex);
            }
        }
    }

    public bool IsConnected => Account?.Tokens is not null;

    /// <summary>Ob das aktuelle Profil abgleichen darf: es hat das Konto verbunden und ist kein Kinderprofil.</summary>
    public bool BelongsToCurrentProfile => Account is { } account && account.ProfileId == currentProfileId() && !currentIsKids();

    private void Save(TraktAccount account)
    {
        Directory.CreateDirectory(AppSettings.DataDirectory);
        var stored = new Stored(account.ClientId, account.ClientSecret, account.ProfileId, account.Username, account.Tokens?.AccessToken, account.Tokens?.RefreshToken, account.Tokens?.ExpiresUtc);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(stored, Json));
        File.Move(temp, FilePath, overwrite: true);
        _account = account;
        _loaded = true;
    }

    /// <summary>Speichert Client-ID und Client-Secret (eine frühere Anmeldung entfällt: sie gehörte zu anderen Zugangsdaten).</summary>
    public void SaveApp(string clientId, string clientSecret)
    {
        clientId = clientId.Trim();
        clientSecret = clientSecret.Trim();
        if (clientId.Length == 0 || clientSecret.Length == 0) throw new ArgumentException("Client-ID und Client-Secret werden beide gebraucht (trakt.tv/oauth/applications).");
        if (currentIsKids()) throw new InvalidOperationException("Im Kinderprofil lässt sich Trakt nicht verbinden.");

        Save(new TraktAccount(clientId, clientSecret, currentProfileId()));
        db.ClearTraktPushed();
    }

    /// <summary>Beginnt die Anmeldung; der Nutzer gibt den Code auf trakt.tv/activate ein.</summary>
    public Task<TraktDeviceCode> BeginConnectAsync(CancellationToken ct = default) =>
        TraktClient.StartDeviceAuthAsync(http, Account?.ClientId ?? throw new InvalidOperationException("Erst Client-ID und Client-Secret speichern."), ct);

    /// <summary>Wartet auf die Bestätigung und speichert die Anmeldung samt Benutzername.</summary>
    /// <exception cref="TraktAuthException">Code abgelaufen oder abgelehnt.</exception>
    public async Task<string?> CompleteConnectAsync(TraktDeviceCode code, CancellationToken ct = default)
    {
        var account = Account ?? throw new InvalidOperationException("Erst Client-ID und Client-Secret speichern.");
        var tokens = await TraktClient.WaitForDeviceTokenAsync(http, account, code, ct: ct);

        var connected = account with { Tokens = tokens, ProfileId = currentProfileId() };
        string? username = null;
        try
        {
            username = await new TraktClient(http, connected).GetUsernameAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TraktAuthException)
        {
            // Der Name dient nur der Anzeige: die Anmeldung gilt trotzdem.
        }
        Save(connected with { Username = username });
        db.ClearTraktPushed();
        return username;
    }

    public void Disconnect()
    {
        if (Account is { } account) Save(account with { Tokens = null, Username = null });
        db.ClearTraktPushed();
    }

    /// <summary>Gleicht ab, was schon eingerichtet ist; null, wenn nichts zu tun war (nicht verbunden, falsches Profil, läuft schon).</summary>
    public async Task<TraktSyncResult?> SyncAsync(IReadOnlyList<TitleRow> titles, IReadOnlyList<VideoRow> videos, IReadOnlyDictionary<long, PlaybackState> playback, bool automatic, CancellationToken ct = default)
    {
        if (!IsConnected || !BelongsToCurrentProfile || _syncing) return null;
        if (automatic && DateTime.UtcNow - _lastAuto < TimeSpan.FromMinutes(10)) return null;

        _syncing = true;
        try
        {
            if (automatic) _lastAuto = DateTime.UtcNow;
            var client = new TraktClient(http, Account!, tokens => Save(Account! with { Tokens = tokens }));
            return await new TraktSync(db).SyncAsync(client, titles, videos, playback, ct);
        }
        finally
        {
            _syncing = false;
        }
    }
}
