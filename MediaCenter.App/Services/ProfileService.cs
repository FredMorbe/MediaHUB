using MediaCenter.Core;

namespace MediaCenter.App.Services;

/// <summary>Der Wechsel in ein Profil ist nicht erlaubt (falsche PIN, Kinderprofil ohne Weg zurück …); der Text ist für den Nutzer.</summary>
public sealed class ProfileAccessException(string message) : Exception(message);

public enum ProfileAccessKind
{
    /// <summary>Der Wechsel geht ohne Eingabe.</summary>
    Allowed,

    /// <summary>Der Wechsel braucht die PIN des Zielprofils.</summary>
    NeedsPin,

    /// <summary>Der Wechsel ist von hier aus nicht möglich.</summary>
    Blocked,
}

public sealed record ProfileAccess(ProfileAccessKind Kind, string? Message = null);

/// <summary>
/// Das aktive Profil und der Wechsel zwischen Profilen. Regeln: Ein Profil mit PIN öffnet nur mit ihr; aus einem Kinderprofil kommt man nur in ein
/// Erwachsenen-Profil, wenn es eine PIN hat und sie stimmt (sonst könnte jedes Kind zurückwechseln). Die PIN hält Kinder und Mitbewohner
/// vom Wechseln ab, sie verschlüsselt die Daten nicht.
/// </summary>
public sealed class ProfileService(LibraryDb db, AppSettings settings)
{
    public ProfileRow Current { get; private set; } = db.GetProfile(LibraryDb.DefaultProfileId);

    /// <summary>Das aktive Profil hat sich geändert (Wechsel, Umbenennen, Kinder-Einstellung, PIN); Oberfläche und Bibliothek ziehen nach.</summary>
    public event Action? Changed;

    public IReadOnlyList<ProfileRow> All => db.ListProfiles();

    /// <summary>Mehr als ein Profil: dann lohnt „Wer schaut?“.</summary>
    public bool HasSeveral => db.ListProfiles().Count > 1;

    /// <summary>Beim Start das zuletzt benutzte Profil aktivieren; gibt es das nicht mehr, das erste.</summary>
    public void Initialize()
    {
        var profiles = db.ListProfiles();
        var profile = profiles.FirstOrDefault(p => p.Id == settings.LastProfileId) ?? profiles.First(p => p.Id == LibraryDb.DefaultProfileId);
        Activate(profile);
    }

    /// <summary>Ob beim Start „Wer schaut?“ erscheint: mit mehreren Profilen, wenn es der Nutzer will oder das zuletzt benutzte eine PIN hat.</summary>
    public bool ShouldAskAtStart => HasSeveral && (settings.AskProfileAtStart || Current.HasPin);

    public ProfileAccess Check(ProfileRow target)
    {
        if (target.Id == Current.Id) return new ProfileAccess(ProfileAccessKind.Allowed);
        if (Current.Kids && !target.Kids)
        {
            return target.HasPin
                ? new ProfileAccess(ProfileAccessKind.NeedsPin)
                : new ProfileAccess(ProfileAccessKind.Blocked, $"Aus einem Kinderprofil geht es nur in Profile mit PIN. „{target.Name}“ hat keine.");
        }
        return target.HasPin ? new ProfileAccess(ProfileAccessKind.NeedsPin) : new ProfileAccess(ProfileAccessKind.Allowed);
    }

    /// <exception cref="ProfileAccessException">Nicht erlaubt oder PIN falsch; nichts hat sich geändert.</exception>
    public void SwitchTo(long id, string? pin)
    {
        var target = db.ListProfiles().FirstOrDefault(p => p.Id == id) ?? throw new ProfileAccessException("Dieses Profil gibt es nicht mehr.");
        var access = Check(target);
        switch (access.Kind)
        {
            case ProfileAccessKind.Blocked:
                throw new ProfileAccessException(access.Message!);
            case ProfileAccessKind.NeedsPin when !db.VerifyPin(target.Id, pin):
                throw new ProfileAccessException("Die PIN stimmt nicht.");
        }

        Activate(target);
    }

    /// <summary>Ein Profil wurde angelegt, umbenannt, umgefärbt, gelöscht oder bekam eine PIN (auch das aktive); Leiste und Einstellungen aktualisieren sich.</summary>
    public event Action? Edited;

    /// <summary>Liest das aktive Profil neu (nach Bearbeiten, auch eines anderen Profils) und meldet es.</summary>
    public void Refresh()
    {
        Current = db.GetProfile(Current.Id);
        Edited?.Invoke();
    }

    private void Activate(ProfileRow profile)
    {
        var changed = profile.Id != Current.Id;
        db.SetActiveProfile(profile.Id);
        Current = profile;
        settings.LastProfileId = profile.Id;
        settings.Save();
        if (changed) Changed?.Invoke();
    }
}
