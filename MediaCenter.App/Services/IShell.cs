using MediaCenter.Core;

namespace MediaCenter.App.Services;

public enum Section
{
    Home,
    Movies,
    Series,
    Inbox,
    Sources,
    Settings,
    Music,
    Radio,
    Podcasts,
}

/// <summary>Was die Seiten vom Hauptfenster brauchen.</summary>
public interface IShell
{
    AppServices Services { get; }

    void Navigate(Section section);

    void OpenTitle(long titleId);

    /// <summary>Öffnet eine Unterseite (Album, Podcast …); "Zurück" führt zur aktuellen Seite.</summary>
    void Open(Func<System.Windows.Controls.UserControl> page);

    /// <summary>Zurück zur vorherigen Seite (z. B. aus der Detailseite).</summary>
    void Back();

    Task PlayAsync(VideoRow video, bool resume);

    /// <summary>Großes Titelbild hinter der Seite; null blendet es aus.</summary>
    void SetBackdrop(string? imagePath);

    void ShowMessage(string text, bool isError = false);

    /// <summary>Zeigt während der Arbeit eine Meldung. Erwartbare Fehler (Pfad fehlt, Laufwerk unklar) werden angezeigt, alle anderen laufen weiter nach oben.</summary>
    Task RunBusyAsync(string text, Func<Task> work);

    /// <summary>Liest die Bibliothek neu und baut die aktuelle Seite neu auf.</summary>
    void ReloadView();

    /// <summary>Erzeugt fehlende Cover und lädt die Ansicht neu.</summary>
    Task RefreshArtworkAsync(bool force = false);
}
