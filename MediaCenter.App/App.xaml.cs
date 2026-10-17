using System.Windows;
using MediaCenter.App.Services;

namespace MediaCenter.App;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(e.Exception.ToString(), "Mediacenter – Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
            Shutdown(1);
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var services = AppServices.Create();
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
}
