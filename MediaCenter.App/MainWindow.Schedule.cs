using System.Windows.Threading;

namespace MediaCenter.App;

// Sendepläne der Serien: kurz nach dem Start und danach alle sechs Stunden prüfen, ob etwas fällig ist (die Abrufe selbst sind zeitlich begrenzt).
public partial class MainWindow
{
    private readonly DispatcherTimer _scheduleTimer = new() { Interval = TimeSpan.FromHours(6) };

    private void InitSchedule()
    {
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        first.Tick += async (_, _) =>
        {
            first.Stop();
            await RefreshScheduleSafelyAsync();
        };
        first.Start();
        _scheduleTimer.Tick += async (_, _) => await RefreshScheduleSafelyAsync();
        _scheduleTimer.Start();

        // Trakt: eine Weile nach dem Start einmal abgleichen
        var trakt = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        trakt.Tick += async (_, _) =>
        {
            trakt.Stop();
            await SyncTraktSafelyAsync(automatic: true);
        };
        trakt.Start();
    }

    /// <summary>Gleicht mit Trakt ab (nur wenn verbunden); auch nach dem Player, damit das eben Gesehene dort ankommt.</summary>
    private async Task SyncTraktSafelyAsync(bool automatic)
    {
        try
        {
            await Services.SyncTraktAsync(this, automatic);
        }
        catch (Exception ex)
        {
            ShowMessage($"Der Trakt-Abgleich ist fehlgeschlagen: {ex.Message}", isError: true);
        }
    }

    private async Task RefreshScheduleSafelyAsync()
    {
        try
        {
            await Services.RefreshScheduleAsync(this);
        }
        catch (Exception ex)
        {
            // Aus einem Timer heraus: ohne Meldung ginge der Fehler verloren, mit einem Wurf stürzte die App ab.
            ShowMessage($"Die Sendepläne der Serien lassen sich nicht aktualisieren: {ex.Message}", isError: true);
        }
    }
}
