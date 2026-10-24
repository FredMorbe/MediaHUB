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
        new ChangelogEntry("0.12.0", "24.10.2026", new[]
        {
            "Verschobene Dateien behalten ihre Daten: Der Scan merkt sich je Datei einen Fingerabdruck (Größe plus Anfang, Mitte und Ende des Inhalts). Verschiebst oder benennst du eine Datei um, erkennt der nächste Scan sie am Inhalt wieder und hängt Fortschritt, gesehen-Markierung, Untertitel, Farbeinstellungen, Poster und Meine Liste an den neuen Ort, statt einen neuen Eintrag anzulegen („… verschoben“ in der Scan-Meldung). Kopien, bei denen das Original noch liegt, bleiben eigene Einträge; liegt das Laufwerk der alten Datei nicht an, wird nichts übernommen. Schema 18; vorhandene Dateien bekommen ihren Fingerabdruck beim nächsten Scan. Eine Verschiebung, die schon vor diesem Update gescannt wurde, ist nicht mehr zu retten.",
            "Profile: Mehrere Personen am selben Mediacenter. Jedes Profil hat eigenen Fortschritt, eigene „Meine Liste“ und eigenen Podcast-Fortschritt; Titel, Cover, Sammlungen, Playlists und Einstellungen gelten für alle. Der Farbkreis rechts in der Navigation wechselt („Wer schaut?“), beim Start fragt die App, sobald es mehrere Profile gibt (abschaltbar). Verwaltung unter Einstellungen → Profile: anlegen, umbenennen, Farbe, löschen. Alle bisherigen Daten gehören dem Profil „Ich“. Schema 19.",
            "Kindersicherung: Erwachsenen-Profile können eine PIN (4 bis 8 Ziffern) bekommen, ohne PIN wechselt niemand hinein. Ein Kinderprofil sieht nur freigegebene Titel (Rechtsklick → „Für Kinderprofile freigeben“, im Fenster „Bearbeiten“ oder gesammelt unter Einstellungen → Profile → Freigaben) und hat nur Start, Filme und Serien: keine Verwaltung, Musik, Radio, Podcasts, Einstellungen, Poster- oder Namensänderung. Aus einem Kinderprofil kommt man nur in ein Profil mit PIN zurück; ein Kinderprofil lässt sich erst anlegen, wenn ein Erwachsenen-Profil eine PIN hat. Die PIN hält vom Wechseln ab, sie verschlüsselt nichts: wer an den Datenordner kommt, sieht die Bibliothek trotzdem.",
            "Untertitel-Aussehen: Einstellungen → Wiedergabe → „Untertitel-Aussehen“ mit Vorschau auf heller und dunkler Szene: Schrift, Größe, Fettdruck, Schrift- und Randfarbe, Randstärke, Schatten, Hintergrund (Rand oder halbtransparenter Kasten) und, ob auch Untertitel mit eigenem Stil (ASS, oft Anime) so aussehen sollen. Fünf Vorlagen; im Player im Fenster „Anpassen“ sind sie auch wählbar und gelten sofort. Zeit, Maßstab und Höhe bleiben je Video.",
            "OpenSubtitles: Der API-Schlüssel und das Konto stehen jetzt gleich unter den Untertitel-Einstellungen (Einstellungen → Wiedergabe → „OpenSubtitles: API-Schlüssel und Konto“).",
            "Musik: Die Warteschlange lässt sich bearbeiten. Rechtsklick auf Titel oder Album → „Als Nächstes spielen“ und „Zur Warteschlange hinzufügen“ (auf der Albumseite der Knopf mit der Liste); im Panel „Jetzt läuft“ je Titel nach oben, nach unten, entfernen und „Leeren“.",
            "Musik: Übergänge sind lückenlos (mpv öffnet den nächsten Titel vorab). Neu: Überblenden (Einstellungen → Wiedergabe → „Musik: Überblenden“, 2 bis 12 Sekunden, Standard aus). Kurz vor dem Ende startet ein zweiter Player stumm, der nächste Titel setzt ein, während der laufende ausklingt (gleichbleibende Lautstärke). Titel unter der dreifachen Überblendzeit, Radio und Podcasts werden nicht überblendet.",
            "Musik: Tags ändern. Rechtsklick auf einen Titel → „Tags in der Datei ändern …“ (Titel, Künstler, Album, Album-Künstler, Titelnummer, Disc, Jahr, Genre) oder auf der Albumseite der Tag-Knopf (Album, Künstler, Album-Künstler, Jahr, Genre für alle Titel). Anders als „Bearbeiten“ schreibt das in die Dateien selbst (ffmpeg ohne Neukodierung, Cover bleiben; MP3, FLAC, M4A, Ogg, Opus geprüft). Nach Rückfrage; die Datei wird erst ersetzt, wenn ffmpeg fehlerfrei fertig ist, läuft sie gerade in der Warteschlange, wird die Wiedergabe beendet.",
        }),
        new ChangelogEntry("0.11.0", "24.10.2026", new[]
        {
            "Neuer Stil „Liquid Glass“: Glasflächen haben jetzt einen Glanz, der oben links am hellsten ist, und eine Lichtkante an zwei gegenüberliegenden Ecken statt eines gleichmäßigen Rands. Das gilt für die Navigationsleiste, Fenster, Menüs, Knöpfe, Pillen, Eingabefelder und Poster; alles ist stärker gerundet.",
            "Die Navigationsleiste oben läuft nicht mehr nach unten aus, sondern hat eine helle Unterkante. Der aktive Reiter liegt auf einer glänzenden Pille mit Lichtkante. Hauptknöpfe tragen einen Glanz auf der oberen Hälfte.",
            "Das Titelbild auf der Startseite und auf der Detailseite ist jetzt eine abgerundete Karte mit heller Kante unterhalb der Navigationsleiste statt eines Hintergrunds über die ganze Breite. Text und Knöpfe darauf sind in Hell und Dunkel hell und lesbar.",
            "Die Audio-Leiste ist jetzt eine schwebende Glas-Insel mit Abstand zum Rand statt einer durchgehenden Leiste. Der Hintergrund hat einen kräftigeren Farbschein in den Akzentfarben und einen dritten Fleck unten rechts.",
        }),
        new ChangelogEntry("0.10.0", "24.10.2026", new[]
        {
            "Neuer Player: Die Steuerung ist eine schwebende Glas-Leiste. Oben links steht Zurück mit Titel (bei Folgen Serienname und „Staffel · Folge“), oben rechts Bild-in-Bild und Vollbild. In der Leiste: Zeitleiste mit Kapitelstrichen und Zeitanzeige unter dem Mauszeiger, große Wiedergabe-Taste, Lautstärke, links die Sprünge, rechts „Ton“ und „Untertitel“ als Pillen und die übrigen Werkzeuge (Kapitel, Folgen, Untertitel anpassen, Bild, Farbe, Übertragen, Timer, Klang) als Symbole mit Hinweistext. Timer, Klang und Übertragung zeigen einen Akzentpunkt, wenn sie aktiv sind. Pause und Start erscheinen kurz groß in der Bildmitte.",
            "Kapitel: Dateien mit Kapiteln bekommen den Knopf „Kapitel“ (Liste mit Sprung, das laufende ist markiert), Striche auf der Zeitleiste und den Kapitelnamen über der Leiste. Bild auf / Bild ab springt zum vorigen oder nächsten Kapitel.",
            "Intro überspringen: Heißt ein Kapitel „Intro“, „Opening“, „OP“, „Vorspann“ o. ä., erscheint während dessen eine Karte „Intro überspringen“. Die Taste S und der Knopf „Intro“ springen ans Ende dieses Kapitels, sonst 85 Sekunden vor.",
            "Sicherung: Einstellungen → Bibliothek → „Sichern …“ schreibt Datenbank (Fortschritt, Playlists, „Meine Liste“, Sammlungen, eigene Namen, Infotexte), Einstellungen, eigene Poster, gespeicherte Untertitel und LUTs in eine ZIP-Datei, ohne Schlüssel und Zwischenspeicher. „Wiederherstellen …“ prüft die Datei, startet die App neu und tauscht beim Start aus; der bisherige Stand bleibt im Ordner „vor-wiederherstellung-…“ im Datenordner.",
            "Verlauf und Statistik: neues Uhr-Symbol in der Navigation. „Verlauf“ zeigt, was zuletzt gesehen oder angefangen wurde, nach Tagen; „Statistik“ gesehene Zeit, die letzten 30 Tage, Größe der Bibliothek, Stunden pro Monat, Genres und Meistgesehenes. Gezählt wird der gemerkte Stand je Video, nicht jede einzelne Wiedergabe.",
            "Sammlungen: Filme → „Sammlungen“. Filmreihen anlegen, umbenennen, löschen; Rechtsklick auf einen Film → „Zu Sammlung hinzufügen …“ (auch im Fenster „Bearbeiten“). Die App schlägt Reihen vor, deren Filme nach demselben Grundnamen heißen („The Matrix“, „Iron Man 2“, „Die Hard: …“); angelegt wird erst auf Wunsch. Auf der Seite des Films steht die Sammlung als Link. Schema 17.",
            "Duplikate: Einstellungen → Bibliothek → „Duplikate suchen“ zeigt Filme und Folgen in mehreren Dateien (mit dem Platz, den man gewänne) und gleichnamige Einträge. Je Datei „Aus Mediacenter“ (die Datei bleibt) oder „In den Papierkorb“, gleichnamige Einträge lassen sich zusammenführen; alles nach Rückfrage.",
            "Untertitel online suchen: im Untertitelmenü „Online suchen …“ (OpenSubtitles). Gesucht wird in der Untertitelsprache der Einstellungen nach Titel, Jahr bzw. Staffel und Folge und einer Prüfsumme der Datei; Treffer, die nachweislich zu genau dieser Datei gehören, stehen oben. „Laden“ speichert den Untertitel wie eine selbst geladene Datei. Dafür braucht es einen kostenlosen API-Schlüssel und ein Konto (Einstellungen → Wiedergabe → „Untertitel aus dem Internet“; verschlüsselt gespeichert oder per Umgebungsvariable). Noch nicht gegen den echten Dienst geprüft.",
        }),
        new ChangelogEntry("0.9.0", "24.10.2026", new[]
        {
            "Suche: die Lupe in der Navigation oder Strg+F. Sie findet Filme und Serien nach Name oder Schauspieler (Treffer zeigen „mit …“), außerdem Alben nach Titel, Künstler oder Song. Passende Schauspieler stehen oben als Knöpfe, ein Klick zeigt alle Titel mit ihnen. Auf der Detailseite sind die Namen der Besetzung Links in die Suche.",
            "Schauspieler kennt die Suche nur bei Titeln mit Infotexten. TVmaze liefert bis zu 20 Namen je Serie, OMDb höchstens vier je Film. Bereits geholte Serien haben noch die alten sechs: „Alle Cover neu erzeugen“ in den Einstellungen holt die längere Besetzung.",
            "Filme und Serien: „Sortierung“ nach Name, Jahr, zuletzt hinzugefügt, Wertung oder Auflösung und „Ansicht“ Alle, Ungesehen, Angefangen oder Gesehen; beides gilt zusätzlich zu Genre und „Meine Liste“ und bleibt bis zum Beenden der App erhalten.",
            "Gesehen: auf der Detailseite „Als gesehen markieren“ für den ganzen Film oder die ganze Serie und je Staffel ein eigener Knopf; im Rechtsklick-Menü einer Karte ebenso. Das setzt auch die gemerkte Stelle zurück, die Titel verschwinden aus „Weiterschauen“.",
            "Infotext bearbeiten: im Fenster „Bearbeiten“ lassen sich Handlung, Regie und Besetzung selbst schreiben; solche Texte überschreibt kein automatischer Durchlauf. „Online neu holen“ holt die Texte dieses einen Titels neu (nach Rückfrage auch über selbst geschriebene). Die Datenbank bekommt dafür Schema 16.",
        }),
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
