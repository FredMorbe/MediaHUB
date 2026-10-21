# Plan: Android-TV-Player + Raspberry-Pi-Medienserver

Stand: Brainstorming, noch nichts umgesetzt.

## Ziel

Filme und Serien aus MediaHUB auf dem Sony Android TV abspielen.
Die TV-App ist ein **reiner Player (nur lesen)**. Verwaltung (Scannen, Umbenennen, Poster, Ollama-Übersetzung, GPU-Automatik) bleibt ausschließlich in der Desktop-App.

## Architektur

```
Desktop (WPF)  --Snapshot-Export-->  Raspberry Pi 4  <--HTTP (LAN)--  Android-TV-App
 schreibt, verwaltet                 Nur-Lese-Server                   Player, nur lesen
                                     + externe Platten
```

- **Desktop** bleibt die einzige Schreib-Instanz. Er exportiert einen Snapshot: Kopie der `LibraryDb`, Poster und Untertitel.
- **Pi-Server** (.NET 8, ASP.NET Core, Docker, linux-arm64) nutzt `MediaCenter.Core`. Er scannt nichts und kennt kein Ollama. Er liest den Snapshot nur lesend und streamt die Videos per HTTP-Range.
- **TV-App** (Kotlin, Jetpack Compose for TV, Media3/ExoPlayer) zeigt Bibliothek und spielt ab. Sie schreibt nichts zurück. Einen Resume-Punkt speichert sie nur lokal auf dem TV.
- Die SQLite-Datei wird **nie live über SMB geteilt**, nur kopiert.

## Hardware

- Raspberry Pi 4, Gigabit-LAN.
- Systemlaufwerk: SSD oder USB-Stick statt SD-Karte.
- 5 externe Platten, aktiver USB-Hub oder Platten mit eigenem Netzteil (Brownout-Gefahr bei 2,5"-Platten).
- Platten auf **ext4** (NTFS nur im Notfall). Mount per UUID.
- Kein RAID, daher eine Sicherung der Medien an anderer Stelle.

## Energie-Strategie

- Pi läuft dauerhaft (ca. 3–4 W, etwa 12 € pro Jahr bei 0,35 €/kWh).
- **Stufe 1:** Spin-down der Platten (`hdparm -S`), Anlauf von selbst beim Zugriff (5–10 s Wartezeit).
- **Stufe 2 (optional):** Strom der Platten per Relais oder smarter Steckdose schalten. Endpunkte `/disks/on` und `/disks/off`, Auto-Aus nach z. B. 30 Minuten ohne Streaming. Die TV-App ruft `/disks/on` beim Druck auf „Abspielen“ auf und wartet auf die Bereitschaft.
- Bibliothek, Poster und Untertitel liegen auf dem Systemlaufwerk des Pi. Stöbern geht, ohne dass eine Platte läuft.

## Phasen

### Phase 0: Voraussetzungen klären
- Pi 4 und Platten prüfen (Größe, Dateisystem, Strom).
- Medienpfade in der DB ansehen (`D:\...` vs. NAS-Pfade). Entscheiden, wie sie auf den Mount abgebildet werden (Pfad-Präfix-Mapping in der Server-Konfiguration).
- Codec-Check des Sony-TVs (H.264, HEVC, AV1, Audio wie EAC3/DTS).

### Phase 1: Core Linux-tauglich
- Windows-Annahmen in `MediaCenter.Core` suchen (Pfade, Laufwerksbuchstaben, `VolumeInfo`/`VolumeMatcher`, ffmpeg-Aufruf).
- `MediaCenter.Core.Tests` unter Linux (arm64 oder x64) laufen lassen.
- Abhängigkeiten, die nur den Schreibbetrieb betreffen, vom Server fernhalten.

### Phase 2: Snapshot-Export im Desktop
- Funktion „Für TV veröffentlichen“ in der Desktop-App (Knopf in den Einstellungen).
- Exportiert: DB-Kopie (konsistent, z. B. über SQLite Backup API), Posterordner und Untertitel (inkl. Übersetzungen).
- Zielordner konfigurierbar (SMB-Freigabe des Pi oder `scp`/`rsync`).
- Der Server liest die Kopie nur lesend und lädt sie bei Änderung neu.

### Phase 3: Pi-Server (Nur-Lese-API)
- Neues Projekt `MediaCenter.Server` (ASP.NET Core, Minimal API).
- Endpunkte: Bibliothek (Filme, Serien, Staffeln, Episoden), Detail, Poster, Untertitel (SRT/VTT), Video-Stream mit Range-Support, Gesundheitscheck.
- Auth: einfacher API-Token, im LAN reicht das. Für unterwegs optional Tailscale.
- Dockerfile für linux-arm64, `docker-compose.yml` mit Volumes (Snapshot, Medien).
- Fehlende Platten sind kein Fehler: Eintrag wird als „nicht verfügbar“ geliefert.

### Phase 4: Spin-down und Energie
- `hdparm`-Einstellung als Skript oder Dienst.
- Endpunkt `/disks/on` (Stufe 2): Relais/Steckdose ansteuern, auf Mount warten.
- Idle-Timer für Auto-Aus.

### Phase 5: Android-TV-App
- Projekt mit Kotlin, Jetpack Compose for TV, Media3/ExoPlayer (Leanback-Launcher-Eintrag, D-Pad-Fokus).
- Screens: Startseite mit Reihen (Neu, Filme, Serien), Detailseite, Player.
- Player: Audio- und Untertitelspur wählen, Fortschritt lokal merken.
- Server-Adresse und Token einmalig eingeben.
- Installation per Sideload (ADB/APK).

### Phase 6: Feinschliff
- Fehlerbehandlung („Platten starten…“, Server nicht erreichbar).
- Optional: Transcoding-Fallback über ffmpeg, falls ein Format nicht abspielbar ist.
- Optional: Wake-Taste für die Platten in der TV-App.
- Sicherung/Backup-Konzept für die Medien.

## Risiken

| Risiko | Gegenmaßnahme |
|---|---|
| Plattenstrom am Pi (Brownout) | Aktiver Hub oder eigenes Netzteil |
| Dateien nicht TV-tauglich (Codec) | Phase 0 prüfen, sonst ffmpeg-Fallback |
| `LibraryDb`-Pfade passen nicht zum Mount | Pfad-Mapping in der Server-Konfiguration |
| Plattenausfall ohne RAID | Sicherung an zweiter Stelle |
| SD-Karten-Verschleiß | Systemlaufwerk SSD/USB-Stick |
| Core nicht ohne Weiteres unter Linux lauffähig | Phase 1 zuerst, früh testen |

## Offene Fragen

1. Wo liegen die Medien heute, und wie stehen die Pfade in der `LibraryDb`?
2. Gesamtgröße der 5 Platten, Dateisystem, 2,5" oder 3,5"?
3. Sony-Modell und welche Codecs es kann (HEVC, AV1, DTS)?
4. Reicht der lokale Resume-Punkt am TV, oder soll der Fortschritt später doch mit dem Desktop synchronisiert werden?
5. Stufe 2 (Strom schalten) gleich oder erst später?

## Empfohlene Reihenfolge

Phase 0 → 1 → 3 (Server läuft mit Test-DB) → 5 (TV-App früh benutzbar) → 2 (Export aus dem Desktop) → 4 → 6.
Phase 2 hängt von deinen lokalen Änderungen ab und kann warten, bis du wieder am PC bist. Phase 1 und 3 lassen sich schon von hier aus erledigen.
