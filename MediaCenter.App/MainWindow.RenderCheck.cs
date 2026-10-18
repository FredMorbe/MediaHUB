using System.Windows;
using System.Windows.Interop;
using MediaCenter.App.Services;

namespace MediaCenter.App;

// Grafik-Selbsttest im Modus „Automatisch“ (siehe RenderSelfTest und AppSettings.GpuMode).
public partial class MainWindow
{
    /// <summary>Wartezeit nach dem Start, bis das erste Bild sicher gezeichnet ist.</summary>
    private static readonly TimeSpan RenderCheckDelay = TimeSpan.FromSeconds(2.5);

    private async void CheckRendering()
    {
        var settings = Services.Settings;
        if (App.StartedAfterGpuFallback) ShowMessage("Die Oberfläche wird jetzt per Software gezeichnet: Mit der Grafikkarte blieb das Fenster leer. Unter Einstellungen → Darstellung lässt sich das ändern.", isError: false);
        if (settings.GpuMode != AppSettings.GpuAuto || settings.GpuAutoResult is not null) return;

        await Task.Delay(RenderCheckDelay);
        // Ein verkleinertes Fenster liefert kein Bild; warten, bis es wieder zu sehen ist.
        while (WindowState == WindowState.Minimized || !IsVisible) await Task.Delay(1000);

        var window = new WindowInteropHelper(this).Handle;
        bool blank;
        try
        {
            blank = await RenderSelfTest.IsBlankAsync(window);
            if (blank)
            {
                // Zweite Aufnahme, damit ein einzelnes unglückliches Bild nicht zum Umschalten führt.
                await Task.Delay(1500);
                while (WindowState == WindowState.Minimized || !IsVisible) await Task.Delay(1000);
                blank = await RenderSelfTest.IsBlankAsync(window);
            }
        }
        catch (InvalidOperationException ex)
        {
            ShowMessage($"Der Grafiktest ließ sich nicht ausführen ({ex.Message}). Die App bleibt beim Hardware-Rendering; bei weißem Fenster unter Einstellungen → Darstellung „Aus“ wählen.", isError: true);
            return;
        }

        settings.GpuAutoResult = blank ? "failed" : "ok";
        settings.Save();
        if (blank) App.RestartAfterGpuFallback();
    }
}
