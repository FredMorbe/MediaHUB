using MediaCenter.App.Controls;
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
    Live,
    Podcasts,
    History,
}

/// <summary>Namen der Reiter in der Navigation (Leiste und Einstellungen).</summary>
public static class SectionLabels
{
    /// <summary>Alle Reiter, die sich ausblenden lassen, in der Reihenfolge der Leiste; „Einstellungen“ fehlt absichtlich (sonst käme man nicht zurück).</summary>
    public static IReadOnlyList<Section> Hideable { get; } = new[]
    {
        Section.Home, Section.Movies, Section.Series, Section.Music, Section.Radio, Section.Live, Section.Podcasts,
        Section.Sources, Section.Inbox, Section.History,
    };

    public static string Of(Section section) => section switch
    {
        Section.Home => "Start",
        Section.Movies => "Filme",
        Section.Series => "Serien",
        Section.Music => "Musik",
        Section.Radio => "Radio",
        Section.Live => "Live-TV",
        Section.Podcasts => "Podcasts",
        Section.Sources => "Laufwerke",
        Section.Inbox => "Inbox",
        Section.History => "Verlauf und Statistik",
        Section.Settings => "Einstellungen",
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, null),
    };
}

/// <summary>Was die Seiten vom Hauptfenster brauchen.</summary>
public interface IShell
{
    AppServices Services { get; }

    void Navigate(Section section);

    /// <summary>Zeigt die Reiter der Leiste neu an, nachdem sich in den Einstellungen geändert hat, welche ausgeblendet sind.</summary>
    void RefreshNavigation();

    void OpenTitle(long titleId);

    /// <summary>Öffnet die Suche (Strg+F); mit <paramref name="query"/> sucht sie gleich danach (z. B. nach einem Schauspieler), ohne zeigt sie die letzte Suche.</summary>
    void OpenSearch(string? query = null);

    /// <summary>Öffnet die Musik direkt auf diesem Album; „Zurück“ führt zur vorherigen Seite.</summary>
    void OpenAlbum(string albumKey);

    /// <summary>Öffnet eine Unterseite (Album, Podcast …); "Zurück" führt zur aktuellen Seite.</summary>
    void Open(Func<System.Windows.Controls.UserControl> page);

    /// <summary>
    /// Zeigt ein Fenster über der Seite (Browser, Umbenennen, Editor …). Ein offenes Fenster wird ersetzt. Der Inhalt bringt keine eigene
    /// Überschrift und keinen Zurück-Knopf mit; Titel und Schließen-Knopf liefert das Fenster.
    /// </summary>
    void OpenModal(string kicker, string title, System.Windows.Controls.UserControl content, ModalSize size = ModalSize.Medium, bool scroll = true, bool dismissOnScrim = false);

    /// <summary>Schließt das offene Fenster ohne Rückfrage.</summary>
    void CloseModal();

    /// <summary>Rückfrage in der App; kehrt erst nach der Antwort zurück. true = bestätigt.</summary>
    bool Confirm(string title, string message, string confirmText = "OK", bool danger = false);

    /// <summary>Zurück zur vorherigen Seite (z. B. aus der Detailseite); ist ein Fenster offen, schließt das stattdessen das Fenster.</summary>
    void Back();

    Task PlayAsync(VideoRow video, bool resume);

    /// <summary>Spielt einen Live-Sender im Player. <paramref name="zapList"/> sind die Sender, zwischen denen dort Bild auf/ab und das Sendermenü wechseln.</summary>
    Task PlayLiveAsync(LiveChannel channel, IReadOnlyList<LiveChannel> zapList);

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
