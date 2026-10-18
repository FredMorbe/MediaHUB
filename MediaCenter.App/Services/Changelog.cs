namespace MediaCenter.App.Services;

public sealed record ChangelogEntry(string Version, string Date, IReadOnlyList<string> Changes);

/// <summary>
/// Was sich in welcher Version geändert hat, neueste zuerst. Bei jeder neuen Version hier oben einen Eintrag ergänzen und
/// <c>&lt;Version&gt;</c> in MediaCenter.App.csproj anheben; stimmen beide nicht überein, sagt das die Changelog-Seite.
/// </summary>
public static class Changelog
{
    public static IReadOnlyList<ChangelogEntry> Entries { get; } = new[]
    {
        new ChangelogEntry("0.3.0", "18.10.2026", new[]
        {
            "Neue Navigation: schmale Leiste über die ganze Breite, Reiter nur als Text mit feiner Akzentlinie. Inbox, Laufwerke und Einstellungen stehen rechts, die Zahl der Inbox als kleine Marke.",
            "Einstellungen in Reitern (Darstellung, Bibliothek, Wiedergabe, Übersetzung, Tastenkürzel, Über), mit übersichtlichen Zeilen statt langer Textblöcke.",
            "Eingebetteter Browser, „Filme umbenennen“, Umbenennen von Hand, MKV-Editor und Changelog öffnen als Fenster über der Seite. Esc oder × schließt sie, solange keine Arbeit läuft.",
            "Rückfragen erscheinen in der App statt als Windows-Meldungsfenster.",
            "Eingabefelder überarbeitet: mehr Platz und Beschriftung im Feld (z. B. Staffel und Folge in der Inbox). Dazu einheitliche Kästchen, Fortschrittsbalken, Kurzinfos und Bildlaufleisten.",
            "Einheitlicher Seitenkopf, gestaltete Leerzustände, ruhigere Karten und ein Fokusring, der nur bei Bedienung mit der Tastatur erscheint.",
        }),
        new ChangelogEntry("0.2.0", "18.10.2026", new[]
        {
            "Poster aus dem Internet: Serien von TVmaze, Filme von OMDb. Schalter und „Fehlende Cover suchen“ in den Einstellungen.",
            "OMDb-Schlüssel direkt in den Einstellungen eintragen (gilt sofort, liegt im Datenordner).",
            "Poster per IMDb-Suche im eingebauten Browser wählen (Rechtsklick auf eine Karte → „Poster auf IMDb suchen“).",
            "Filme automatisch umbenennen: „Titel_(Jahr)_[Auflösung].ext“ mit Vorschau, Begleitdateien (Untertitel u. a.) ziehen mit, die letzte Umbenennung lässt sich zurücknehmen.",
            "Filme und Serien von Hand umbenennen (Rechtsklick → „Umbenennen …“): Anzeigename oder Dateiname, mit Rückgängig.",
            "„Aus Mediacenter entfernen …“ (Rechtsklick auf Film, Serie, Folge oder Inbox-Datei): nimmt Einträge aus der Bibliothek, die Dateien auf der Festplatte bleiben. Unter Laufwerke lassen sie sich wieder aufnehmen.",
            "Ollama wird nur noch beim Übersetzen von Untertiteln angesprochen, nicht mehr beim Öffnen der Einstellungen.",
            "Versionsnummer im Kopf und in den Einstellungen; ein Klick darauf zeigt diesen Changelog.",
        }),
        new ChangelogEntry("0.1.0", "bis 17.10.2026", new[]
        {
            "Bibliothek aus Ordnern und Dateien; Laufwerke werden an der Seriennummer erkannt, Dateien ohne sichere Zuordnung landen in der Inbox.",
            "Player auf Basis von mpv: Vorgaben für Tonsprache und Untertitel, nächste Folge automatisch, Untertitel-Dateien laden (auch per Drag & Drop).",
            "Bildfunktionen im Player: Seitenverhältnis, Tempo, Zoom und Ausschnitt, Drehen, Einzelbild; Farbeinstellungen mit Weißabgleich, Schärfe und Invertieren.",
            "Untertitel mit einem lokalen KI-Modell (Ollama) ins Deutsche und in traditionelles Chinesisch übersetzen.",
            "MKV-Editor: Spuren entfernen, hinzufügen, benennen, verschieben, Versatz und Standard/Erzwungen setzen.",
            "Musik, Radio (radio-browser.info) und Podcasts.",
            "Design „Ink & Signal“ mit mehreren Farbstilen und Hell/Dunkel/System.",
        }),
    };
}
