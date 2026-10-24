using System.IO;
using System.Globalization;
using MediaCenter.App.Player;
using MediaCenter.Core;

namespace MediaCenter.App;

// Farbeinstellungen der Wiedergabe: Helligkeit bis Gamma setzt mpv, Weißabgleich, Schärfe und Invertieren ein Shader.
public partial class MainWindow
{
    /// <summary>Was mpv zuletzt bekommen hat; null = mpv kennt noch nichts davon (neues Video), dann geht alles raus.</summary>
    private ColorSettings? _appliedColor;

    /// <summary>Schickt nur, was sich gegenüber dem letzten Stand geändert hat (Reihenfolge je Eigenschaft bleibt erhalten).</summary>
    private async Task ApplyColorAsync(ColorSettings color)
    {
        if (_mpv is null) return;

        color.Validated();
        var before = _appliedColor;
        _appliedColor = color;

        async Task Set(string property, int value) => await _mpv.CommandAsync("set", property, value.ToString(CultureInfo.InvariantCulture));

        if (before is null || before.Brightness != color.Brightness) await Set("brightness", color.Brightness);
        if (before is null || before.Contrast != color.Contrast) await Set("contrast", color.Contrast);
        if (before is null || before.Saturation != color.Saturation) await Set("saturation", color.Saturation);
        if (before is null || before.Hue != color.Hue) await Set("hue", color.Hue);
        if (before is null || before.Gamma != color.Gamma) await Set("gamma", color.Gamma);

        // Die Shader laufen nur, wenn sie gebraucht werden: erst die Werte, dann (bei Änderung der Auswahl) die Dateien.
        var options = color.AllShaderOptions;
        if (options.Length > 0 && (before is null || before.AllShaderOptions != options))
            await _mpv.CommandAsync("set", "glsl-shader-opts", options);
        if (before is null || before.UsesShader != color.UsesShader || before.Lut != color.Lut)
        {
            var shaders = new List<string>();
            if (color.UsesShader) shaders.Add(ColorShader.EnsureFile());
            if (color.Lut is { } lut)
            {
                try
                {
                    shaders.Add(Services.Luts.ShaderPath(lut)); // erst Weißabgleich und Schärfe, dann der Look
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    PlayerNotice($"LUT „{lut}“ nicht angewendet: {ex.Message}", isError: true);
                }
            }
            await _mpv.CommandAsync("set", "glsl-shaders", string.Join(';', shaders));
        }

        await ApplyHdrPolicyAsync(color); // LUT und Filter sind für SDR gebaut: mit ihnen wird HDR nicht durchgereicht
    }

    private ColorSettings _color = ColorSettings.Default;
    private bool _colorDirty;

    /// <summary>
    /// Neues Video: die für dieses Video gespeicherten Farben (sonst Standard) laden und an mpv schicken; mpv behielte sonst die
    /// Werte der vorigen Datei.
    /// </summary>
    private Task LoadColorAsync(VideoRow video)
    {
        _color = Services.Db.GetColorSettings(video.VideoId);
        _colorDirty = false;
        if (_color.Lut is { } lut && !Services.Luts.Exists(lut))
        {
            // Die LUT wurde gelöscht: ohne sie spielen, die Meldung zeigt es, und der Verweis fliegt beim Speichern raus.
            PlayerNotice($"LUT „{lut}“ gibt es nicht mehr und wird nicht angewendet.", isError: true);
            _color = _color with { Lut = null, LutStrength = ColorSettings.LutStrengthMax };
            _colorDirty = true;
            _colorSaveTimer.Start();
        }
        _appliedColor = null;
        ResetHdrState();
        _overlay.SetColor(_color);
        return ApplyColorAsync(_color);
    }

    private void OnColorChanged(ColorSettings color)
    {
        _color = color;
        _colorDirty = true;
        _ = ApplyColorAsync(color);
        _colorSaveTimer.Stop();
        _colorSaveTimer.Start(); // gespeichert wird, wenn 0,6 s nichts mehr geändert wurde
    }

    /// <summary>Dateiauswahl für „LUT importieren“; das Dialogfenster gehört dem Overlay, damit es über dem Video liegt. Importierte LUTs gelten sofort für dieses Video (die zuletzt gewählte).</summary>
    private void PickLutFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "LUT wählen",
            Filter = "3D-LUT (*.cube)|*.cube|Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true,
        };
        if (dialog.ShowDialog(_overlay) != true) return;

        string? last = null;
        var failures = new List<string>();
        foreach (var file in dialog.FileNames)
        {
            try
            {
                last = Services.Luts.Add(file);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                failures.Add($"{System.IO.Path.GetFileName(file)}: {ex.Message}");
            }
        }

        _overlay.SetLuts(Services.Luts.List());
        if (last is not null) OnColorChanged(_color with { Lut = last });
        _overlay.SetColor(_color);
        if (failures.Count > 0) PlayerNotice($"LUT nicht importiert – {string.Join(" · ", failures)}", isError: true);
        else if (last is not null) PlayerNotice($"LUT „{last}“ importiert und aktiv.", isError: false);
    }

    /// <summary>Löscht die LUT aus der Sammlung; sie verschwindet damit für alle Videos (dort fällt sie beim nächsten Abspielen mit Meldung weg).</summary>
    private void RemoveLut(string name)
    {
        try
        {
            Services.Luts.Remove(name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PlayerNotice($"LUT nicht gelöscht: {ex.Message}", isError: true);
            return;
        }

        _overlay.SetLuts(Services.Luts.List());
        if (_color.Lut == name) OnColorChanged(_color with { Lut = null, LutStrength = ColorSettings.LutStrengthMax });
        _overlay.SetColor(_color);
        PlayerNotice($"LUT „{name}“ gelöscht.", isError: false);
    }

    private void FlushColor()
    {
        if (!_colorDirty || _playing is null) return;

        Services.Db.SaveColorSettings(_playing.VideoId, _color);
        _colorDirty = false;
    }
}
