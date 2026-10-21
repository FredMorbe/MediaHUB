# Plan: Google Cast (Chromecast) in der Desktop-App

Stand: Brainstorming, noch nichts umgesetzt.

## Ziel

Aus dem Player der Desktop-App ein Video per WLAN auf den Sony Android TV schicken (Google Cast / „Chromecast eingebaut“).

Anforderungen:
- Geräte im WLAN automatisch finden (mDNS).
- Wiedergabe mit der **aktuell gewählten Audio- und Untertitelspur** starten.
- Vom **aktuellen Resume-Punkt** weiterspielen.
- Steuerung aus der App: Pause, Spulen, Lautstärke, Spurwechsel, Stopp.
- Die Dateien sind meist MKV (H.264/HEVC, Ton DTS/EAC3). Der Standard-Receiver spielt das nicht immer, deshalb gehört ein **ffmpeg-Fallback** fest zum Plan.

## Funktionsweise

```
Desktop-App
 ├─ Cast-Sender (mDNS-Suche, Cast-Protokoll)  ──Steuerung──►  Sony TV (Default Media Receiver)
 └─ Lokaler HTTP-Server (Range, ffmpeg-Fallback)  ◄──Stream──┘
```

Der TV holt das Video selbst vom PC. Die App schickt ihm nur die URL und steuert die Wiedergabe. Der PC und der TV müssen im selben Netz sein.

## Bausteine

### 1. Cast-Sender
- NuGet-Bibliothek `GoogleCast` oder `Sharpcaster` (vor der Wahl kurz vergleichen: Wartung, .NET 8, Untertitel-/Spurwahl, Lizenz).
- Neue Klasse `CastService` in `MediaCenter.App/Services`: Geräte suchen, verbinden, Medien laden, Status (Position, Zustand, Lautstärke) als Ereignisse.
- Geräteliste mit Name und Modell (Sony-TV).

### 2. Lokaler Stream-Server
- Kleiner Kestrel-Host in der Desktop-App (`CastStreamServer`), nur im LAN erreichbar, zufälliger Token in der URL.
- Endpunkte: Video (HTTP-Range), Untertitel als WebVTT, Poster (für die Anzeige am TV).
- Firewall: Beim ersten Start die Windows-Firewall-Regel für das private Netz anlegen oder dem Nutzer erklären.
- Server startet nur beim Casten und stoppt danach.

### 3. Direct Play oder Transcoding (ffmpeg-Fallback)
Vor dem Start prüft ein Modul `CastCompatibility` per `FfprobeReader`, was der Receiver kann:

| Fall | Behandlung |
|---|---|
| MP4/MKV, H.264, AAC/AC3/EAC3 | Direct Play |
| Video ok, Ton nicht unterstützt (z. B. DTS, TrueHD) | Nur Ton nach AAC/AC3 umwandeln, Video kopieren (Remux, wenig CPU) |
| Container nicht ok | Remux nach MP4/fMP4, Video kopieren |
| Video nicht unterstützt (z. B. HEVC 10 Bit, falls das Modell es nicht kann) | Video-Transcode nach H.264 mit GPU (NVENC/QSV/AMF, wie in der bestehenden GPU-Automatik) |

- `FfmpegTool` um eine Funktion erweitern, die Remux/Transcode live als fragmentiertes MP4 an den Server liefert (`-movflags frag_keyframe+empty_moov`, Pipe statt Datei).
- Das Seeking beim Transcodieren: Bei einem Sprung ffmpeg mit `-ss` neu starten.
- Die Fähigkeiten des TVs (HEVC, Audio) einmal pro Gerät aus den Cast-Informationen lesen und speichern. Bis das geklärt ist, mit einer vorsichtigen Standardliste starten.

### 4. Untertitel
- Text-Untertitel (SRT/ASS) vor dem Senden nach WebVTT umwandeln. `Srt.cs` ist schon da, ASS-Stile gehen dabei verloren.
- Eingebettete Untertitel-Spuren mit `SubtitleExtractor` herausziehen. Die gespeicherten Übersetzungen aus `SubtitleStore` stehen ebenfalls zur Wahl.
- Bildbasierte Untertitel (PGS) lassen sich nicht als Text liefern. Sie nur per Transcode (einbrennen) oder gar nicht unterstützen. Das als bekannte Grenze dokumentieren.
- Der eingestellte Untertitel-Versatz (aus `LibraryDb.UserData`) fließt in die WebVTT-Zeiten ein.

### 5. Audiospur und Resume
- Beim Start übernimmt `CastService` die gewählte Audio- und Untertitelspur aus dem Player. Bei Transcode/Remux wird die gewählte Tonspur direkt gemappt (`-map`), bei Direct Play per Cast-Spurwahl.
- Resume-Position aus der Bibliothek holen und als Startposition mitgeben. Beim Beenden den Stand zurückschreiben (wie beim lokalen Abspielen).
- Fortschritt während des Castens laufend in die Datenbank schreiben, damit „Weiterschauen“ stimmt.

### 6. Oberfläche
- **Cast-Knopf im Player-Overlay** (`PlayerOverlay.xaml`) mit Geräteauswahl.
- **Cast-Zustand:** Wenn verbunden, stoppt der lokale mpv-Player oder wird pausiert, und das Overlay zeigt die Steuerung für den TV (Pause, Spulen, Lautstärke, Spuren, „Auf diesem PC weiterspielen“).
- Auch an Detailseite und Kontextmenü: „Auf TV abspielen“.
- Medientasten (existierende Windows-Mediensteuerung) steuern im Cast-Modus den TV.
- Hinweis bei Transcode („Wird umgewandelt…“) und bei Fehlern (Gerät weg, Format nicht möglich).

### 7. Einstellungen
- Letztes Gerät merken und optional automatisch vorschlagen.
- Transcode-Qualität (Bitrate-Obergrenze), GPU-Encoder-Wahl (Standard: Automatik).
- Eintrag in `AppSettings` und `SettingsPage`.

## Phasen

1. **Spike:** Gerät finden, MP4 mit H.264/AAC per URL abspielen. Zeigt, ob Bibliothek und Netz funktionieren. (Projekt `MediaCenter.Spike` ist dafür vorhanden.)
2. **Stream-Server:** Kestrel mit Range, Token, Firewall-Hinweis.
3. **Steuerung:** Pause, Spulen, Lautstärke, Statusanzeige, Resume.
4. **Direct-Play-Prüfung** (`CastCompatibility`) und Remux (Ton umwandeln, Container ändern).
5. **Untertitel:** WebVTT, Auswahl, Versatz.
6. **Video-Transcode** mit GPU, Seeking.
7. **Oberfläche:** Cast-Knopf, Overlay-Modus, Kontextmenü, Medientasten.
8. **Feinschliff:** Fehlerfälle, Einstellungen, Changelog-Eintrag, Tests.

## Tests

- `MediaCenter.Core.Tests`: Kompatibilitätsentscheidung (Codec-Tabelle), WebVTT-Umwandlung, Range-Parsing, ffmpeg-Argumente.
- Manuell am Sony-TV: MKV mit H.264+DTS, HEVC 10 Bit, 4K, mehrere Tonspuren, SRT und eingebettete Untertitel, Spulen, Resume, Gerät fällt aus dem WLAN.

## Risiken

| Risiko | Gegenmaßnahme |
|---|---|
| Der TV unterstützt ein Format nicht wie angenommen | Fähigkeiten pro Gerät testen, Fallback-Kette Remux → Transcode |
| Transcoding ist langsam oder ruckelt | GPU-Encoder nutzen, Bitrate begrenzen, PC darf nicht schlafen |
| Seeking im Transcode ist träge | ffmpeg mit `-ss` neu starten, kurze Wartezeit anzeigen |
| Firewall blockiert den Stream-Server | Regel anlegen, klare Fehlermeldung |
| mDNS im WLAN gesperrt (Router, VLAN) | Manuelle IP-Eingabe als Notlösung |
| PGS-/ASS-Untertitel gehen verloren | Dokumentieren, bei Bedarf einbrennen |
| Cast-Bibliothek schlecht gepflegt | Im Spike prüfen, sonst Protokoll selbst schlank nachbauen |

## Offene Fragen

1. Welches Sony-Modell, und kann es HEVC 10 Bit sowie 4K über Cast?
2. Sind Untertitel bei dir meist Text (SRT) oder Bild (PGS)?
3. Soll die Cast-Funktion später auch gegen den Pi-Server arbeiten (Quelle = Pi-URL statt PC)? Das wäre mit dem Stream-Server vereinbar.

## Empfohlener Start

Phase 1 (Spike) mit einer einzelnen MP4-Datei. Danach steht fest, ob die Bibliothek taugt, und die restlichen Phasen bauen darauf auf.
