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
        new ChangelogEntry("0.8.2", "23.10.2026", new[]
        {
            "Infotexte aus dem Internet: die Seite eines Films oder einer Serie zeigt jetzt Handlung, Wertung (IMDb bei Filmen, TVmaze bei Serien), Regie und Besetzung; bei Serien haben die Folgen in der Liste ihren Namen und eine kurze Zusammenfassung. Die Texte sind englisch, darunter steht die Quelle.",
            "Sie kommen von denselben Diensten und mit denselben Treffer-Regeln wie die Poster (Filme OMDb, Serien TVmaze), über den Schalter „Cover aus dem Internet“ in den Einstellungen. „Fehlende Cover suchen“ holt sie für Titel ohne Infotexte, „Alle Cover neu erzeugen“ lädt sie neu. Gesendet werden nur Titel und Jahr.",
            "Detailseite von Film und Serie: das Poster steht links neben Titel und Beschreibung. Die Reihe der Knöpfe und die nicht gewählten Genres sind in den dezenten Knopf „Bearbeiten“ (Fenster) gewandert: Genres, Poster/Hintergrund, Umbenennen, MKV bearbeiten, Entfernen. Gewählte Genres bleiben sichtbar. In der Navigation steht oben links das App-Symbol (das K) statt der Wortmarke „MEDIACENTER“.",
        }),
        new ChangelogEntry("0.8.1", "23.10.2026", new[]
        {
            "LUTs für den Look: im Player unter „Farbe“ lassen sich 3D-LUTs (.cube) importieren und in Echtzeit auf das Bild legen, z. B. für einen Film-Look. Die Stärke ist von 0 bis 100 % regelbar; die LUT gilt wie die anderen Farbregler pro Video und wird dafür gespeichert. Untertitel bleiben unverfärbt.",
            "Die LUT liegt nach dem Import in der Sammlung der App (Datenordner, Ordner „luts“) und steht für jedes Video zur Wahl; „Gewählte LUT löschen“ entfernt sie nach Rückfrage für alle Videos. Unterstützt werden 3D-LUTs bis Größe 65; 1D-LUTs und andere Formate lehnt die App mit einer Meldung ab.",
        }),
        new ChangelogEntry("0.8.0", "23.10.2026", new[]
        {
            "Auf den Fernseher übertragen (Google Cast): im Player der neue Knopf „Übertragen“ sucht Chromecast-Geräte und Fernseher mit Cast im Netz. Das Video läuft dann dort an der aktuellen Stelle weiter; Pause, Spulen, Lautstärke, Ton- und Untertitelwahl und die nächste Folge steuert der Player wie gewohnt. „Übertragung beenden“ setzt es auf diesem PC fort.",
            "Passt das Video, geht es unverändert an den Fernseher (H.264/AAC im MP4); sonst wandelt ffmpeg nur um, was der Fernseher nicht kann (z. B. DTS-Ton oder HEVC → H.264, HDR → SDR, mit der Grafikkarte, wenn möglich). Untertitel (eingebettet, daneben liegend oder hinzugefügt) gehen als Text mit. Unter Einstellungen → Wiedergabe lässt sich HEVC direkt senden, wenn der Fernseher es kann.",
            "Der Fernseher holt das Video von einem kleinen Webserver der App (nur an der Adresse im Netz des Fernsehers, nur für das gerade übertragene Video). Beim ersten Mal fragt die Windows-Firewall; für ein als „Öffentlich“ eingestuftes WLAN muss der Zugriff dafür auch dort erlaubt sein.",
        }),
        new ChangelogEntry("0.7.2", "20.10.2026", new[]
        {
            "Bibliothek aktualisieren: neuer Knopf auf Start, bei Filmen und Serien sowie neben dem Plus in „Deine Bibliothek“ bei Musik. Erkennt nachträglich angeschlossene Laufwerke, liest die bekannten Quellen neu ein und aktualisiert Dateien, Erreichbarkeit und Cover. Nicht angeschlossene Quellen bleiben erhalten; gleichzeitige Aktualisierungen werden verhindert.",
        }),
        new ChangelogEntry("0.7.1", "20.10.2026", new[]
        {
            "Bild-in-Bild für Videos: Knopf oben im Player oder Taste P. Das kleine Fenster bleibt über anderen Programmen, lässt sich am Titel verschieben und am Rand skalieren. Pause, Ton und Rückkehr zum großen Player sind direkt erreichbar; P oder Esc stellt die vorherige Fenster- bzw. Vollbildansicht wieder her. Die Wiedergabe läuft beim Umschalten ohne Neuladen weiter.",
        }),
        new ChangelogEntry("0.7.0", "19.10.2026", new[]
        {
            "Songs und Alben: Im Fenster „Bearbeiten …“ (vorher „Umbenennen …“) lässt sich jetzt auch der Künstler ändern. Der Künstler eines Songs ändert nichts an seinem Album; der Künstler eines Albums bestimmt, unter welchem Künstler es steht, das Cover wandert mit. Leer lassen heißt: Künstler aus den Tags. „Angaben aus den Tags wiederherstellen“ nimmt Name und Künstler zurück.",
            "Vollbild für die ganze App: Knopf rechts in der Navigation und F11; unter Einstellungen → Darstellung „Immer im Vollbild starten“. Ein vor dem Player eingeschaltetes Vollbild bleibt nach dem Player erhalten.",
            "Folgen wählen: im Player der neue Knopf „Folgen“ (Staffeln und Folgen der Serie, die laufende ist markiert), auf der Startseite bei „Weiterschauen“ und im Hero der Knopf „Folge wählen“ bzw. Rechtsklick auf die Serienkarte.",
            "Player: Ein Klick aufs Video schließt ein offenes Menü oder Einstellungsfenster (Untertitel anpassen, Bild, Farbe, Übersetzen, Folgen …), ohne das Video zu pausieren. Ein Doppelklick auf das Video schaltet das Vollbild um.",
        }),
        new ChangelogEntry("0.6.1", "19.10.2026", new[]
        {
            "Songs und Alben umbenennen: Rechtsklick auf einen Titel (Album, „Alle Titel“, Playlist) oder auf eine Albumkarte, bei einem Album auch der Stift-Knopf im Kopf. Es ändert nur den Namen in der Bibliothek; Dateien und Tags bleiben, der Name übersteht einen neuen Scan und gilt für später hinzukommende Titel des Albums. Das Cover wandert mit, ein Album ohne Cover wird mit dem neuen Namen neu gesucht.",
        }),
        new ChangelogEntry("0.6.0", "18.10.2026", new[]
        {
            "Einschlaf-Timer für Musik und Video: in 15 bis 90 Minuten oder am Ende des Titels bzw. der Folge anhalten (Mond-Knopf in der Musikleiste, „Timer“ im Videoplayer). Am Ende der Folge startet die nächste dann nicht von selbst.",
            "Equalizer mit zehn Bändern für Musik, Radio, Podcasts und Video: Voreinstellungen (Bass, Höhen, Stimme, Loudness, Rock) über den Knopf „Klang“ im Player, Regler unter Einstellungen → Wiedergabe. Änderungen sind sofort zu hören.",
            "Songtexte im Panel „Jetzt läuft“ (Reiter „Songtext“): zeitgesteuerte Texte laufen mit, die gesungene Zeile ist hervorgehoben. Quellen: .lrc-Datei neben dem Titel, Text im Tag der Datei, sonst LRCLIB (abschaltbar unter Einstellungen → Bibliothek).",
            "Album-Cover aus einer zweiten Quelle: Findet iTunes ein Album nicht (z. B. Pink Floyd – The Dark Side of the Moon), fragt die App MusicBrainz und das Cover Art Archive.",
            "Ruckeln auf den Filmseiten: Die Vorschau-Erzeugung nimmt nur noch den zuletzt überfahrenen Film in die Warteschlange, läuft mit niedriger Priorität und zwei Threads. Vorher stand für jede kurz überfahrene Karte ein ffmpeg-Lauf an.",
            "GPU-Beschleunigung automatisch: Die App startet mit der Grafikkarte und prüft einmal, ob das Fenster wirklich etwas zeigt. Bleibt es leer (Intel Arc mit NVIDIA), startet sie sich von selbst neu und zeichnet per Software. Unter Einstellungen → Darstellung wählbar: Automatisch, An, Aus.",
        }),
        new ChangelogEntry("0.5.0", "18.10.2026", new[]
        {
            "Playlists für Musik: in der Bibliothek links unter „Playlists“ anlegen, umbenennen (Namen anklicken) und löschen. Titel kommen per Rechtsklick „Zu Playlist hinzufügen …“ oder mit dem Plus-Symbol eines Albums hinein; in der Playlist lassen sie sich nach oben und unten schieben oder entfernen.",
            "„Meine Liste“ für Filme und Serien: Rechtsklick auf eine Karte oder der Knopf auf der Detailseite. Die Liste steht als Reihe auf der Startseite und als Filter bei Filme und Serien.",
            "Medientasten und Windows-Mediensteuerung: Wiedergabe/Pause, Weiter, Zurück und Stopp gelten für Video, Musik, Radio und Podcasts, auch bei minimiertem Fenster. Windows zeigt Titel, Cover und Fortschritt an. mpv meldet sich nicht mehr selbst bei der Mediensteuerung an.",
            "Weißes Fenster behoben: Auf manchen Rechnern (Intel Arc mit NVIDIA) bleibt jedes WPF-Fenster im Hardware-Modus weiß. Die Oberfläche rendert deshalb standardmäßig per Software; Videos laufen weiter über die GPU. Unter Einstellungen → Darstellung lässt sich die GPU-Beschleunigung wieder einschalten.",
            "Filter-Knöpfe (Genres, „Meine Liste“) lassen sich auch per Bedienhilfe umschalten.",
        }),
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
