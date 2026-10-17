using System.Globalization;
using MediaCenter.Core;

namespace MediaCenter.App;

// Bildeinstellungen der Wiedergabe: Seitenverhältnis, Tempo, Zoom und Ausschnitt, Drehung, Einzelbild.
public partial class MainWindow
{
    /// <summary>Was mpv zuletzt bekommen hat; null = mpv kennt noch nichts davon (neues Video), dann geht alles raus.</summary>
    private PictureSettings? _appliedPicture;

    private static string Num(double value) => value.ToString("0.#####", CultureInfo.InvariantCulture);

    /// <summary>
    /// Schickt nur, was sich gegenüber dem letzten Stand geändert hat. Die Befehle laufen der Reihe nach über dieselbe Pipe;
    /// zwei schnell aufeinanderfolgende Änderungen (Regler ziehen) überholen sich deshalb nicht.
    /// </summary>
    private async Task ApplyPictureAsync(PictureSettings picture)
    {
        if (_mpv is null) return;

        picture.Validated();
        var before = _appliedPicture;
        _appliedPicture = picture;

        if (before is null || before.Speed != picture.Speed)
            await _mpv.CommandAsync("set", "speed", Num(picture.Speed));
        if (before is null || before.MpvAspectOverride != picture.MpvAspectOverride)
            await _mpv.CommandAsync("set", "video-aspect-override", picture.MpvAspectOverride);
        if (before is null || before.MpvPanscan != picture.MpvPanscan)
            await _mpv.CommandAsync("set", "panscan", Num(picture.MpvPanscan));
        if (before is null || before.MpvKeepAspect != picture.MpvKeepAspect)
            await _mpv.CommandAsync("set", "keepaspect", picture.MpvKeepAspect ? "yes" : "no");
        if (before is null || before.ZoomPercent != picture.ZoomPercent)
            await _mpv.CommandAsync("set", "video-zoom", Num(picture.MpvZoom));
        // Der Ausschnitt hängt am Zoom: ändert sich der Zoom, ändert sich auch der Wert für mpv.
        if (before is null || before.MpvPanX != picture.MpvPanX)
            await _mpv.CommandAsync("set", "video-pan-x", Num(picture.MpvPanX));
        if (before is null || before.MpvPanY != picture.MpvPanY)
            await _mpv.CommandAsync("set", "video-pan-y", Num(picture.MpvPanY));
        if (before is null || before.Rotation != picture.Rotation)
            await _mpv.CommandAsync("set", "video-rotate", picture.Rotation.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Neues Video: alles zurück auf Standard (mpv behielte die Werte sonst von der vorigen Datei).</summary>
    private Task ResetPictureAsync()
    {
        _appliedPicture = null;
        _overlay.SetVideoAspect(null); // das Format der neuen Datei meldet mpv nach dem Laden (video-params)
        _overlay.SetPicture(PictureSettings.Default);
        return ApplyPictureAsync(PictureSettings.Default);
    }

    /// <summary>Ein Bild vor oder zurück; mpv pausiert dabei selbst. Ein Fehler von mpv erscheint als Meldung (CommandFailed).</summary>
    private async Task StepFrameAsync(int direction)
    {
        if (_mpv is null || !_fileReady) return;
        await _mpv.CommandAsync(direction > 0 ? "frame-step" : "frame-back-step");
    }
}
