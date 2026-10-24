using System.Text.Json;
using System.Windows.Threading;
using MediaCenter.Core;

namespace MediaCenter.App;

// HDR: mpv gibt ein HDR-Video als HDR aus, wenn Windows-HDR für den Bildschirm an ist (target-colorspace-hint=auto), und rechnet es sonst nach SDR um.
// Die App legt nur fest, wann die Umrechnung erzwungen wird (Einstellung „Immer SDR“, LUT oder Farbfilter) und sagt beim Start, was passiert.
public partial class MainWindow
{
    /// <summary>Was mpv zuletzt als target-colorspace-hint bekam; null = noch nichts geschickt (neue mpv-Instanz).</summary>
    private string? _appliedHdrHint;

    /// <summary>Übertragungsfunktion des Videos (video-params) und des Ausgabebildes (video-target-params): „pq“ und „hlg“ sind HDR.</summary>
    private string? _sourceGamma;

    private string? _targetGamma;

    /// <summary>Der Hinweis zu dieser Datei ist noch nicht gezeigt.</summary>
    private bool _hdrNoticePending;

    /// <summary>
    /// mpv meldet das Ausgabebild beim Start zuerst als SDR und stellt es erst danach auf HDR um: der Hinweis wartet nach der ersten vollständigen Meldung kurz und nimmt dann den aktuellen Wert.
    /// </summary>
    private readonly DispatcherTimer _hdrNoticeTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };

    private static bool IsHdrGamma(string? gamma) => gamma is "pq" or "hlg";

    /// <summary>Neue Datei oder neuer Sender: die Werte der vorigen gelten nicht mehr, der Hinweis kommt neu.</summary>
    private void ResetHdrState()
    {
        _hdrNoticeTimer.Stop();
        _sourceGamma = null;
        _targetGamma = null;
        _hdrNoticePending = true;
    }

    /// <summary>Schickt mpv, ob HDR durchgereicht werden darf; mpv behält den Wert über Dateien hinweg, deshalb nur bei Änderung.</summary>
    private async Task ApplyHdrPolicyAsync(ColorSettings color)
    {
        if (_mpv is null) return;

        var hint = HdrOutputs.ColorspaceHint(Services.Settings.Hdr, color);
        if (hint == _appliedHdrHint) return;

        _appliedHdrHint = hint;
        _targetGamma = null; // das Ausgabebild wird neu eingerichtet; der Hinweis kommt mit dem neuen Wert
        _hdrNoticePending = true;
        await _mpv.CommandAsync("set", "target-colorspace-hint", hint);
    }

    private void InitHdr() => _hdrNoticeTimer.Tick += (_, _) => ShowHdrNotice();

    private void OnVideoParams(JsonElement? data)
    {
        _sourceGamma = GammaOf(data);
        UpdateHdrNotice();
    }

    private void OnVideoTargetParams(JsonElement? data)
    {
        _targetGamma = GammaOf(data);
        UpdateHdrNotice();
    }

    private static string? GammaOf(JsonElement? data) =>
        data is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty("gamma", out var gamma) && gamma.ValueKind == JsonValueKind.String ? gamma.GetString() : null;

    /// <summary>Sobald Quelle und Ausgabe bekannt sind und das Video HDR ist: einmal sagen, ob es als HDR läuft oder umgerechnet wird.</summary>
    private void UpdateHdrNotice()
    {
        if (!_hdrNoticePending || !IsHdrGamma(_sourceGamma) || _targetGamma is null) return;

        if (!_hdrNoticeTimer.IsEnabled) _hdrNoticeTimer.Start(); // einmal warten, nicht bei jeder Meldung neu: bei HDR-Ausgabe kommen laufend neue Werte
    }

    private void ShowHdrNotice()
    {
        _hdrNoticeTimer.Stop();
        if (!_hdrNoticePending || !IsHdrGamma(_sourceGamma) || _targetGamma is null) return;

        _hdrNoticePending = false;
        var name = _playing?.Hdr is { } format and not HdrFormat.None ? format.Label() : _sourceGamma == "hlg" ? "HLG" : "HDR";
        var mode = Services.Settings.Hdr;
        var text = IsHdrGamma(_targetGamma) ? $"{name}: HDR-Ausgabe"
            : HdrOutputs.FilterForcesSdr(mode, _color) ? $"{name}: wegen LUT oder Farbfilter in SDR umgerechnet"
            : mode == HdrOutput.Sdr ? $"{name}: in SDR umgerechnet (Einstellung)"
            : $"{name}: für diesen Bildschirm in SDR umgerechnet";
        _overlay.ShowNotice(text, isError: false);
    }
}
