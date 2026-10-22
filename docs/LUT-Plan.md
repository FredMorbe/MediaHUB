# Plan: Live-Colorgrading mit LUTs und Looks

Stand: Brainstorming, noch nichts umgesetzt.

## Ziel

Filme mit starkem Farbstich (z. B. das Grün in *Matrix*, Türkis/Orange bei *John Wick*) beim Abspielen **live** umfärben, ohne Rendering und ohne die Datei zu ändern. Zum Beispiel:
- Den Grünstich herausnehmen und eine natürliche, realistische Farbgebung erzeugen.
- Einem Film mit einem anderen „Look“ eine neue Atmosphäre geben (warm, kühl, entsättigt, Kino-Look).
- Die Auswahl pro Film speichern und mit einem Tastendruck vorher/nachher vergleichen.

## Ausgangslage

- `MediaCenter.App/Player/ColorShader.cs` hängt per `glsl-shaders` einen GLSL-Shader im mpv-Hook `OUTPUT` ein (Weißabgleich, Schärfe, Invertieren). Die Werte werden zur Laufzeit über `glsl-shader-opts` gesetzt.
- `MediaCenter.Core/ColorSettings.cs` hält die Werte, `MainWindow.Color.cs` und `PlayerOverlay.Color.cs` setzen sie und zeigen die Regler.
- Die Pipeline ist also vorhanden. Wir erweitern sie und bauen keine neue.

## Konzept: zwei Ebenen

### Ebene 1: Eigene Looks ohne externe Dateien (löst das Matrix-Problem)
Ein Grünstich in Matrix liegt vor allem in den **Mitten und Schatten**. Ein globaler Weißabgleich (`gain_g`) färbt dann auch die Lichter und Hauttöne falsch. Besser sind:

- **Lift / Gamma / Gain pro Farbkanal** (Schatten, Mitten, Lichter getrennt): den Grünanteil in den Schatten und Mitten senken.
- **Selektive Farbtonregler:** Sättigung und Farbton für einen Bereich (z. B. Grüntöne) einzeln ändern, damit Hauttöne unberührt bleiben.
- **Kühl/Warm- und Grün/Magenta-Schieber** (Temperatur und Tint) als einfache Bedienung.
- **Sättigung, Kontrast, Schwarzpunkt** als Ergänzung.
- **Auto-Neutralisieren („Stich erkennen“):** Aus einigen Frames des Films (wie bei den `PreviewClips`) die durchschnittliche Farbabweichung bestimmen und eine Korrektur vorschlagen, die der Nutzer per Schieber nachjustiert. Für *Matrix* ein guter Startpunkt.

### Ebene 2: Importierte 3D-LUTs (`.cube`)
- Für fertige Looks, etwa aus DaVinci Resolve, Lightroom oder Online-Sammlungen.
- Die LUT wird als 3D-Textur in den Shader geladen und mit trilinearer Interpolation abgefragt. Die GPU-Kosten sind vernachlässigbar.
- Dazu ein Regler **Intensität** (0–100 %, Mischung zwischen Original und LUT).

Beide Ebenen sind kombinierbar: erst LUT, dann Feinkorrektur.

## Bausteine

### 1. `.cube`-Parser (Core)
- Neue Klasse `CubeLut` in `MediaCenter.Core`: liest `LUT_3D_SIZE`, `DOMAIN_MIN/MAX`, `TITLE` und die RGB-Tripel. Prüft Größe (17, 33, 65), lehnt kaputte oder zu große Dateien ab (Größenlimit wie bei Untertiteln).
- 1D-LUTs (`LUT_1D_SIZE`) zunächst ablehnen oder in 3D umrechnen, später optional.
- Unit-Tests in `MediaCenter.Core.Tests`: gültige Datei, falsche Größe, Domain ungleich 0–1, fehlende Zeilen, Kommentare.

### 2. Shader erweitern (`ColorShader`)
- Neue `//!TEXTURE`-Definition für die LUT (3D, RGB-Float) und `//!PARAM lut_strength`.
- Reihenfolge im Hook: LUT → Lift/Gamma/Gain → Farbtonregler → Weißabgleich → Schärfe → Invertieren. Der Hook bleibt `OUTPUT`: Untertitel bleiben wie bisher unberührt.
- Die Textur wird zur Laufzeit per `glsl-shaders-append` mit einer erzeugten Shader-Datei in den Cache gelegt. Pro Auswahl wird die Datei neu geschrieben und `glsl-shaders` aktualisiert (wie in `MainWindow.Color.cs`).
- Der Shader läuft nur, wenn eine LUT oder ein Look aktiv ist, wie `UsesShader` heute.

### 3. Einstellungen und Speicherung
- `ColorSettings` bekommt: LUT-Datei (relativer Name), Intensität, Lift/Gamma/Gain (RGB), Temperatur, Tint, Sättigung je Bereich.
- Speicherung **pro Film** in der `LibraryDb` (wie `PictureSettings`/`ColorSettings` heute), dazu ein globaler Standard.
- LUT-Ordner `Luts/` im Datenverzeichnis der App, Import per Knopf („LUT hinzufügen“) oder Ziehen und Ablegen.

### 4. Oberfläche (Player-Overlay, Farbseite)
- Bereich **Look**: Dropdown (Aus, Neutralisieren, eigene Presets, importierte LUTs) und Regler Intensität.
- Presets mit sprechenden Namen: *Neutral*, *Warm*, *Kühl*, *Entsättigt*, *Kino-Natürlich*, und Film-spezifische Vorschläge wie *Grünstich entfernen*.
- **Vorher/Nachher:** Taste halten (zeigt das Original) und optional geteiltes Bild (links Original, rechts Look).
- Vorschaubild: Aktuelles Frame mit dem Look in einer kleinen Vorschau, bevor man ihn bestätigt.
- „Als Standard für diesen Film speichern“ und „Zurücksetzen“.
- Look-Presets lassen sich als Datei exportieren und teilen.

### 5. HDR und Farbraum
- Die meisten LUTs erwarten **SDR / Rec.709**. Bei HDR-Material mappt mpv zuerst nach SDR (Tone Mapping), erst dann greift der `OUTPUT`-Hook. Bei aktivem HDR-Durchreichen den Look deaktivieren und einen Hinweis zeigen.
- Log-LUTs (z. B. S-Log, LogC) sind für normales Videomaterial nicht gedacht und werden mit Warnung angezeigt.

## Phasen

1. **Spike:** Einen festen Lift/Gamma/Gain-Block im bestehenden Shader testen und die Matrix-Szene damit korrigieren. Zeigt, wie weit Ebene 1 allein trägt.
2. **Ebene 1 ausbauen:** Temperatur, Tint, Schatten/Mitten/Lichter, Bereichssättigung. Regler im Overlay, pro Film gespeichert.
3. **`.cube`-Parser** mit Tests.
4. **LUT im Shader:** 3D-Textur, Intensität, Import und Verwaltung.
5. **Vorher/Nachher und Presets** (Taste halten, geteiltes Bild, Vorschau).
6. **Auto-Neutralisieren:** Frames auswerten, Korrektur vorschlagen.
7. **Feinschliff:** HDR-Hinweis, Export/Import von Looks, Changelog-Eintrag.

## Tests

- Core: `CubeLut`-Parser, Looks als reine Mathematik (Eingabefarbe → Ausgabefarbe) gegen bekannte Werte.
- `RenderSelfTest` erweitern: Testbild mit bekannter Farbe, nach dem Look prüfen, ob der erwartete Wert herauskommt.
- Manuell: *Matrix*, *John Wick*, ein SDR-Film ohne Stich (darf sich nicht verschlechtern), ein HDR-Film.

## Risiken

| Risiko | Gegenmaßnahme |
|---|---|
| Globale Korrektur färbt auch Hauttöne falsch | Getrennte Regler für Schatten/Mitten/Lichter, Bereichsregler für Grüntöne |
| LUT passt nicht zum Farbraum | Hinweise, HDR-Warnung, Log-LUTs kennzeichnen |
| Große LUT-Dateien oder kaputte Dateien | Größen- und Formatprüfung beim Import |
| Look wirkt auf Szenen unterschiedlich (Matrix-Realwelt ist nicht grün) | Intensität-Regler, schnelles Ausschalten, Look pro Film speichern |
| Shader-Kosten bei sehr hoher Auflösung | LUT-Abfrage ist günstig; Messung mit 4K im Selbsttest |
| Konflikte mit laufenden lokalen Änderungen an `ColorShader.cs` | Plan erst umsetzen, wenn die lokalen Änderungen gepusht sind |

## Offene Fragen

1. Sollen Looks nur pro Film gelten, oder auch pro Serie/Sammlung (z. B. alle *Matrix*-Teile)?
2. Reicht ein Look pro Film, oder sollen Szenen unterschiedlich behandelt werden können (Matrix-Welt vs. Realwelt)? Das wäre aufwendiger (Zeitmarken).
3. Eigene LUTs importieren oder zunächst nur eingebaute Looks?

## Empfohlener Start

Phase 1 (Spike): Lift/Gamma/Gain im vorhandenen Shader testen. Wenn sich der Matrix-Look damit natürlich anfühlt, ist die LUT-Unterstützung eine Ergänzung und kein Muss.
