using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MediaCenter.App.Services;

namespace MediaCenter.App;

public partial class App : Application
{
    /// <summary>Startargument der Instanz, die nach einem leeren Fenster im Hardware-Rendering neu gestartet wurde.</summary>
    private const string GpuFallbackArgument = "--gpu-fallback";

    /// <summary>Beim Beenden eine neue Instanz starten (nach dem Selbsttest, der das Hardware-Rendering als unbrauchbar erkannt hat).</summary>
    public static bool RestartOnExit { get; set; }

    /// <summary>Diese Instanz läuft nach einem leeren Fenster im Hardware-Rendering jetzt per Software; der Nutzer bekommt einmal einen Hinweis.</summary>
    public static bool StartedAfterGpuFallback { get; private set; }

    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(e.Exception.ToString(), "Mediacenter – Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
            Shutdown(1);
        };
    }

    /// <summary>Wird die App mit dem Neustart-Flag beendet, startet sie sich neu: erst nach dem Schließen des Fensters und der Datenbank.</summary>
    public static void RestartAfterGpuFallback()
    {
        RestartOnExit = true;
        _restartArgument = GpuFallbackArgument;
        Current.Shutdown();
    }

    /// <summary>Beendet die App und startet sie neu (z. B. damit eine vorbereitete Wiederherstellung eingespielt wird).</summary>
    public static void Restart()
    {
        RestartOnExit = true;
        _restartArgument = "";
        Current.Shutdown();
    }

    private static string _restartArgument = GpuFallbackArgument;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        StartedAfterGpuFallback = e.Args.Contains(GpuFallbackArgument);
        try
        {
            var services = AppServices.Create();
            // Muss vor dem ersten Fenster stehen.
            if (services.Settings.SoftwareRendering) RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            services.Theme.Apply();
            MainWindow = new MainWindow(services);
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Mediacenter konnte nicht starten", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        if (!RestartOnExit) return;

        var path = Environment.ProcessPath ?? throw new InvalidOperationException("Der Pfad der laufenden Anwendung ist unbekannt; ein Neustart ist nicht möglich.");
        Process.Start(new ProcessStartInfo(path, _restartArgument) { UseShellExecute = false });
    }
}
