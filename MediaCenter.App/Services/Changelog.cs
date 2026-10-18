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
        new ChangelogEntry("0.4.1", "18.10.2026", new[]
        {
            "Musik kompakter: schmalere Panels, kleinere Cover und Köpfe, dichtere Zeilen in der Bibliothek, in „Jetzt läuft“ und in der Player-Leiste (72 statt 88 px). Titel stehen als Tabelle (Nummer, Titel, Künstler, Album, Dauer) mit einzeiligen Zeilen; der Schnellzugriff zeigt acht Kacheln.",
        }),
        new ChangelogEntry("0.4.0", "18.10.2026", new[]
        {
            "Musik nach dem Vorbild der Spotify-Desktop-App: links „Deine Bibliothek“ (Alben oder Künstler mit Suche, „Alle Titel“), in der Mitte Start mit Begrüßung, Schnellzugriff und Reihen („Zuletzt gehört“, „Neu hinzugefügt“, „Künstler“ mit „Alle anzeigen“), Album und Künstler mit eigenem Zurück, rechts „Jetzt läuft“ mit Cover und den nächsten Titeln der Warteschlange.",
            "Die Player-Leiste für Musik, Radio und Podcasts ist jetzt eine durchgehende Leiste über die ganze Breite: Titel links, Steuerung mit Zeitleiste in der Mitte, Lautstärke rechts.",
            "Album-Cover aus dem Internet: Alben ohne Bild im Ordner oder in den Titeln bekommen ihr Cover von iTunes (nur bei genauem Treffer von Künstler und Albumtitel; gesendet werden nur diese beiden Angaben). Abschaltbar mit „Cover aus dem Internet“ in den Einstellungen.",
        }),
        new ChangelogEntry("0.3.4", "18.10.2026", new[]
        {
            "Der große Block oben auf Start und auf den Detailseiten ist keine Glas-Karte mehr: Titel und Knöpfe stehen direkt auf dem Titelbild, das links dunkler wird und rechts sowie unten in den Hintergrund ausläuft.",
        }),
        new ChangelogEntry("0.3.3", "18.10.2026", new[]
        {
            "Logo: Wortmarke „MEDIACENTER.“ in der Navigationsleiste (Bebas Neue, der Punkt in der Akzentfarbe) und ein App-Symbol, ein K aus gefalteten Bändern in der Akzentfarbe (im Stil des Netflix-N), für Fenster, Taskleiste und die Programmdatei.",
        }),
        new ChangelogEntry("0.3.2", "18.10.2026", new[]
        {
            "Poster-Karten nach Vorbild von Disney+: größer, ohne Text darunter, mit weichem Schatten. Beim Überfahren erscheint ein heller Ring, Titel und Zusatzzeile blenden über dem Poster ein. „Offline“ ist jetzt eine kleine helle Marke.",
            "Reihentitel in Satzschrift und normaler Stärke statt in Versalien.",
            "Navigationsleiste nach Vorbild von Netflix: Logo links bündig mit dem Inhalt, größere Reiter als Pillen, rechts Symbole für Laufwerke, Inbox (mit Zahlenmarke an der Glocke) und Einstellungen.",
        }),
        new ChangelogEntry("0.3.1", "18.10.2026", new[]
        {
            "Navigation als schmaler Milchglas-Streifen oben: nur Text-Reiter, der aktive auf einer Pille, unten weich auslaufend, kein Logo. Einstellungen sind jetzt ein Reiter, die Versionsnummer steht klein rechts. Der Inhalt rückt entsprechend nach oben.",
            "Vorschau beim Überfahren: die Karte eines Films oder einer Serie wächst zur Seite (nach links, wenn rechts kein Platz ist) und zeigt einen stummen Ausschnitt. Er wird beim ersten Mal mit ffmpeg erzeugt und danach aus dem Ordner „previews“ geladen. Abschaltbar unter Einstellungen → Bibliothek.",
        }),
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
