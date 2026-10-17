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

        // Der Shader läuft nur, wenn er gebraucht wird: erst die Werte, dann (einmalig) die Datei.
        if (color.UsesShader && (before is null || before.ShaderOptions != color.ShaderOptions))
            await _mpv.CommandAsync("set", "glsl-shader-opts", color.ShaderOptions);
        if (before is null || before.UsesShader != color.UsesShader)
            await _mpv.CommandAsync("set", "glsl-shaders", color.UsesShader ? ColorShader.EnsureFile() : "");
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
        _appliedColor = null;
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

    private void FlushColor()
    {
        if (!_colorDirty || _playing is null) return;

        Services.Db.SaveColorSettings(_playing.VideoId, _color);
        _colorDirty = false;
    }
}
