using System.Text.RegularExpressions;

namespace MediaCenter.Core;

/// <param name="Kids">Kinderprofil: sieht nur freigegebene Titel (<see cref="LibraryDb.SetKidsTitles"/>) und keine Verwaltung.</param>
public sealed record ProfileRow(long Id, string Name, string Color, bool Kids, bool HasPin);

// Profile: wer schaut, hat eigenen Fortschritt (playback), eigene Meine Liste (watchlist) und eigenen Podcast-Fortschritt. Alles andere
// (Titel, Cover, Sammlungen, Playlists, Untertitel- und Farbeinstellungen je Video) teilen sich die Profile. Die Abfragen dieser drei
// Tabellen gelten für das aktive Profil (ActiveProfileId).
public sealed partial class LibraryDb
{
    public const int MaxProfileNameLength = 30;

    /// <summary>Das Profil, das die Migration anlegt und dem alle bisherigen Daten gehören.</summary>
    public const long DefaultProfileId = 1;

    /// <summary>Farben zur Auswahl; jedes Profil hat eine als #RRGGBB.</summary>
    public static IReadOnlyList<string> ProfileColors { get; } = new[]
    {
        "#2EC4D6", "#F25C54", "#F7B801", "#7BC950", "#9D6BFF", "#FF7EB6", "#4F8EF7", "#FF9F45",
    };

    private static readonly Regex ColorPattern = new("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

    /// <summary>Für wen die Fortschritts-, Merklisten- und Podcast-Abfragen gelten.</summary>
    public long ActiveProfileId { get; private set; } = DefaultProfileId;

    /// <exception cref="InvalidOperationException">Das Profil gibt es nicht.</exception>
    public void SetActiveProfile(long id)
    {
        _ = GetProfile(id);
        ActiveProfileId = id;
    }

    public List<ProfileRow> ListProfiles()
    {
        var rows = new List<ProfileRow>();
        using var cmd = Command("SELECT id, name, color, kids, pin_hash IS NOT NULL FROM profiles ORDER BY id");
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(new ProfileRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3) == 1, r.GetInt32(4) == 1));
        return rows;
    }

    /// <exception cref="InvalidOperationException">Das Profil gibt es nicht.</exception>
    public ProfileRow GetProfile(long id) =>
        ListProfiles().FirstOrDefault(p => p.Id == id) ?? throw new InvalidOperationException($"Profil {id} gibt es nicht.");

    private static string ValidName(string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("Der Name des Profils ist leer.", nameof(name));
        if (name.Length > MaxProfileNameLength) throw new ArgumentException($"Der Name darf höchstens {MaxProfileNameLength} Zeichen haben.", nameof(name));
        if (name.Any(char.IsControl)) throw new ArgumentException("Der Name enthält Steuerzeichen.", nameof(name));
        return name;
    }

    private static string ValidColor(string color) =>
        ColorPattern.IsMatch(color) ? color.ToUpperInvariant() : throw new ArgumentException($"Farbe „{color}“ ist keine Farbe der Form #RRGGBB.", nameof(color));

    private void RequireFreeName(string name, long exceptId)
    {
        if (Scalar<long>("SELECT COUNT(*) FROM profiles WHERE name = $name AND id <> $id", ("$name", name), ("$id", exceptId)) > 0)
            throw new InvalidOperationException($"Ein Profil „{name}“ gibt es schon.");
    }

    /// <summary>Wie viele Erwachsenen-Profile (nicht Kinder) eine PIN haben, ohne das angegebene.</summary>
    private int AdultsWithPin(long exceptId) =>
        Scalar<int>("SELECT COUNT(*) FROM profiles WHERE kids = 0 AND pin_hash IS NOT NULL AND id <> $id", ("$id", exceptId));

    private bool AnyKids(long exceptId) => Scalar<int>("SELECT COUNT(*) FROM profiles WHERE kids = 1 AND id <> $id", ("$id", exceptId)) > 0;

    /// <exception cref="ArgumentException">Ungültiger Name oder ungültige Farbe.</exception>
    /// <exception cref="InvalidOperationException">Name vergeben; oder ein Kinderprofil, ohne dass ein Erwachsenen-Profil eine PIN hat (sonst kann jedes Kind zurückwechseln).</exception>
    public long CreateProfile(string name, string color, bool kids) => InTransaction(() =>
    {
        name = ValidName(name);
        color = ValidColor(color);
        RequireFreeName(name, 0);
        if (kids && AdultsWithPin(0) == 0)
            throw new InvalidOperationException("Für ein Kinderprofil muss zuerst ein Erwachsenen-Profil eine PIN bekommen: Mit ihr wechselt man aus dem Kinderprofil zurück.");

        return Scalar<long>("INSERT INTO profiles (name, color, kids, created_utc) VALUES ($name, $color, $kids, $now) RETURNING id",
            ("$name", name), ("$color", color), ("$kids", kids ? 1 : 0), ("$now", Utc(DateTime.UtcNow)));
    });

    public void RenameProfile(long id, string name) => InTransaction(() =>
    {
        _ = GetProfile(id);
        name = ValidName(name);
        RequireFreeName(name, id);
        Run("UPDATE profiles SET name = $name WHERE id = $id", ("$name", name), ("$id", id));
    });

    public void SetProfileColor(long id, string color)
    {
        _ = GetProfile(id);
        Run("UPDATE profiles SET color = $color WHERE id = $id", ("$color", ValidColor(color)), ("$id", id));
    }

    /// <exception cref="InvalidOperationException">
    /// Es bliebe kein Erwachsenen-Profil mit PIN übrig, obwohl es Kinderprofile gibt – oder ein Profil mit PIN soll Kinderprofil werden (das braucht keine PIN).
    /// </exception>
    public void SetProfileKids(long id, bool kids) => InTransaction(() =>
    {
        var profile = GetProfile(id);
        if (profile.Kids == kids) return;

        if (kids)
        {
            if (Scalar<int>("SELECT COUNT(*) FROM profiles WHERE kids = 0 AND id <> $id", ("$id", id)) == 0)
                throw new InvalidOperationException("Mindestens ein Profil muss ein Erwachsenen-Profil bleiben.");
            if (AdultsWithPin(id) == 0)
                throw new InvalidOperationException("Für ein Kinderprofil muss ein anderes Erwachsenen-Profil eine PIN haben: Mit ihr wechselt man aus dem Kinderprofil zurück.");
            Run("UPDATE profiles SET kids = 1, pin_salt = NULL, pin_hash = NULL WHERE id = $id", ("$id", id)); // ein Kinderprofil braucht keine PIN
        }
        else
        {
            Run("UPDATE profiles SET kids = 0 WHERE id = $id", ("$id", id));
        }
    });

    /// <summary>
    /// Löscht ein Profil samt Fortschritt, Merkliste und Podcast-Fortschritt. Nicht das aktive Profil, nicht das letzte und nicht das letzte
    /// Erwachsenen-Profil (mit PIN, solange es Kinderprofile gibt).
    /// </summary>
    public void DeleteProfile(long id) => InTransaction(() =>
    {
        var profile = GetProfile(id);
        if (id == ActiveProfileId) throw new InvalidOperationException("Das aktive Profil lässt sich nicht löschen: erst zu einem anderen wechseln.");
        if (!profile.Kids && Scalar<int>("SELECT COUNT(*) FROM profiles WHERE kids = 0 AND id <> $id", ("$id", id)) == 0)
            throw new InvalidOperationException("Mindestens ein Erwachsenen-Profil muss bleiben.");
        if (!profile.Kids && profile.HasPin && AnyKids(id) && AdultsWithPin(id) == 0)
            throw new InvalidOperationException("Es gibt Kinderprofile, und dies ist das letzte Erwachsenen-Profil mit PIN. Erst einem anderen Erwachsenen-Profil eine PIN geben.");

        Run("DELETE FROM profiles WHERE id = $id", ("$id", id));
    });

    /// <summary>Setzt die PIN des Profils (null entfernt sie).</summary>
    /// <exception cref="ArgumentException">Keine vier bis acht Ziffern.</exception>
    /// <exception cref="InvalidOperationException">Ein Kinderprofil bekommt keine PIN; und die letzte PIN eines Erwachsenen-Profils lässt sich nicht entfernen, solange es Kinderprofile gibt.</exception>
    public void SetPin(long id, string? pin) => InTransaction(() =>
    {
        var profile = GetProfile(id);
        if (profile.Kids) throw new InvalidOperationException("Ein Kinderprofil braucht keine PIN: Mit der PIN eines Erwachsenen-Profils wechselt man zurück.");

        if (pin is null)
        {
            if (profile.HasPin && AnyKids(id) && AdultsWithPin(id) == 0)
                throw new InvalidOperationException("Es gibt Kinderprofile; die PIN dieses Erwachsenen-Profils ist die einzige. Erst die Kinderprofile löschen oder einem anderen Erwachsenen-Profil eine PIN geben.");
            Run("UPDATE profiles SET pin_salt = NULL, pin_hash = NULL WHERE id = $id", ("$id", id));
            return;
        }

        var (salt, hash) = ProfilePin.Create(pin);
        Run("UPDATE profiles SET pin_salt = $salt, pin_hash = $hash WHERE id = $id", ("$salt", salt), ("$hash", hash), ("$id", id));
    });

    /// <summary>Stimmt die PIN? Ein Profil ohne PIN gibt es nie als richtig an (false).</summary>
    public bool VerifyPin(long id, string? pin)
    {
        using var cmd = Command("SELECT pin_salt, pin_hash FROM profiles WHERE id = $id", ("$id", id));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException($"Profil {id} gibt es nicht.");
        if (r.IsDBNull(0) || r.IsDBNull(1)) return false;
        return ProfilePin.Verify(pin, r.GetString(0), r.GetString(1));
    }

    // --- Für Kinder freigegebene Titel ---------------------------------------------------------------------

    public HashSet<long> GetKidsTitleIds()
    {
        var ids = new HashSet<long>();
        using var cmd = Command("SELECT title_id FROM kids_titles");
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetInt64(0));
        return ids;
    }

    /// <summary>Gibt Titel für Kinderprofile frei oder nimmt sie zurück.</summary>
    /// <exception cref="InvalidOperationException">Ein Titel ist unbekannt; dann ändert sich nichts.</exception>
    public void SetKidsTitles(IEnumerable<long> titleIds, bool on) => InTransaction(() =>
    {
        foreach (var id in titleIds.Distinct())
        {
            if (Scalar<long>("SELECT COUNT(*) FROM titles WHERE id = $id", ("$id", id)) == 0) throw new InvalidOperationException($"Titel {id} ist nicht in der Bibliothek.");
            if (on) Run("INSERT OR IGNORE INTO kids_titles (title_id) VALUES ($id)", ("$id", id));
            else Run("DELETE FROM kids_titles WHERE title_id = $id", ("$id", id));
        }
    });
}
