using System.Windows.Threading;

namespace MediaCenter.App.Services;

public enum SleepMode
{
    Off,

    /// <summary>Nach einer festen Zeit anhalten.</summary>
    Countdown,

    /// <summary>Anhalten, sobald der laufende Titel bzw. die laufende Folge zu Ende ist.</summary>
    EndOfItem,
}

/// <summary>
/// Einschlaf-Timer für Musik, Radio, Podcasts und Video. Beim Ablauf pausiert er die Musik selbst und meldet <see cref="Fired"/>;
/// der Videoplayer hört darauf und pausiert das Video. „Am Ende der Folge“ meldet ihm der Videoplayer über <see cref="ConsumeEndOfItem"/>.
/// Alle Ereignisse kommen auf dem UI-Thread.
/// </summary>
public sealed class SleepTimer : IDisposable
{
    /// <summary>Angebotene Zeiten in Minuten.</summary>
    public static IReadOnlyList<int> Minutes { get; } = new[] { 15, 30, 45, 60, 90 };

    /// <summary>Ein Titel gilt als zu Ende, wenn nur noch so viele Sekunden fehlen (mpv meldet die Position nicht auf die Millisekunde).</summary>
    private const double EndTolerance = 0.3;

    private readonly AudioController _audio;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _deadline;
    private int _armedIndex = -1;

    public SleepTimer(AudioController audio)
    {
        _audio = audio;
        _timer.Tick += (_, _) => OnTick();
        audio.Tick += OnAudioTick;
        audio.Changed += OnAudioChanged;
    }

    public SleepMode Mode { get; private set; }

    /// <summary>Restzeit des Countdowns; null in den anderen Zuständen.</summary>
    public TimeSpan? Remaining => Mode == SleepMode.Countdown ? Max(_deadline - DateTime.UtcNow, TimeSpan.Zero) : null;

    /// <summary>Kurzer Text für die Anzeige: „27 min“, „40 s“ oder „Ende“; leer, wenn der Timer aus ist.</summary>
    public string Label => Mode switch
    {
        SleepMode.Countdown when Remaining is { TotalMinutes: >= 1 } remaining => $"{Math.Ceiling(remaining.TotalMinutes):0} min",
        SleepMode.Countdown => $"{Remaining?.Seconds ?? 0} s",
        SleepMode.EndOfItem => "Ende",
        _ => "",
    };

    /// <summary>Zustand oder Restzeit haben sich geändert (der Countdown meldet sich jede Sekunde).</summary>
    public event Action? Changed;

    /// <summary>Der Timer hat abgelaufen und die Musik angehalten; das Argument ist die Meldung für den Nutzer.</summary>
    public event Action<string>? Fired;

    public void StartCountdown(TimeSpan span)
    {
        if (span <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(span), span, "Die Zeit muss größer als null sein.");
        _deadline = DateTime.UtcNow + span;
        Mode = SleepMode.Countdown;
        _timer.Start();
        Changed?.Invoke();
    }

    /// <summary>Hält am Ende des laufenden Titels bzw. der laufenden Folge an. Bei Radio gibt es kein Ende; dort greift nur der Countdown.</summary>
    public void StartEndOfItem()
    {
        _armedIndex = _audio.IsActive ? _audio.Index : -1;
        Mode = SleepMode.EndOfItem;
        _timer.Stop();
        Changed?.Invoke();
    }

    public void Cancel()
    {
        if (Mode == SleepMode.Off) return;
        Mode = SleepMode.Off;
        _timer.Stop();
        Changed?.Invoke();
    }

    /// <summary>Ein Player meldet das Ende eines Titels bzw. einer Folge.</summary>
    /// <returns>true, wenn der Timer es verbraucht hat: der Player soll hier anhalten und nicht zum nächsten gehen.</returns>
    public bool ConsumeEndOfItem()
    {
        if (Mode != SleepMode.EndOfItem) return false;
        Finish("Einschlaf-Timer: Das Ende ist erreicht, die Wiedergabe ist angehalten.");
        return true;
    }

    private void OnTick()
    {
        if (Mode != SleepMode.Countdown) return;
        if (DateTime.UtcNow >= _deadline) Finish("Einschlaf-Timer: Die Zeit ist um, die Wiedergabe ist angehalten.");
        else Changed?.Invoke();
    }

    private void OnAudioTick()
    {
        if (Mode != SleepMode.EndOfItem || _armedIndex < 0 || _audio.Paused) return;
        if (_audio.Current is { Kind: not QueueKind.Radio } && _audio.Duration > 0 && _audio.Duration - _audio.Position <= EndTolerance)
        {
            Finish("Einschlaf-Timer: Der Titel ist zu Ende, die Wiedergabe ist angehalten.");
        }
    }

    /// <summary>Springt die Musik trotzdem zum nächsten Titel (sehr kurzer Titel, ungenaue Position), hält der Timer dort an.</summary>
    private void OnAudioChanged()
    {
        if (Mode == SleepMode.EndOfItem && _armedIndex >= 0 && _audio.IsActive && _audio.Index != _armedIndex)
        {
            Finish("Einschlaf-Timer: Der Titel ist zu Ende, die Wiedergabe ist angehalten.");
        }
    }

    private async void Finish(string message)
    {
        Mode = SleepMode.Off;
        _timer.Stop();
        Changed?.Invoke();
        await _audio.PauseAsync();
        Fired?.Invoke(message);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    public void Dispose()
    {
        _timer.Stop();
        _audio.Tick -= OnAudioTick;
        _audio.Changed -= OnAudioChanged;
    }
}
