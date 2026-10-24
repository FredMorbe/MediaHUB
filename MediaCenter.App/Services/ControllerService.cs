using System.Runtime.InteropServices;
using System.Windows.Threading;
using MediaCenter.Core;
using Windows.Gaming.Input;

namespace MediaCenter.App.Services;

/// <param name="Supported">Die App kennt das Layout des Controllers; sonst steht der Grund in <paramref name="Note"/>.</param>
public sealed record ControllerInfo(string Name, ControllerFamily Family, ushort VendorId, ushort ProductId, bool Supported, string? Note);

/// <summary>
/// Fragt angeschlossene Controller ab (Xbox und kompatible über das Windows-Gamepad, PlayStation DualSense und DualShock 4 über das rohe HID-Gerät;
/// USB und Bluetooth) und meldet Tastendrücke. Es reagiert nur, wenn die App im Vordergrund liegt, damit ein Spiel nebenbei die App nicht steuert.
/// </summary>
public sealed class ControllerService : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly ControllerRepeater _repeater = new();
    private Func<bool> _isForeground = () => true;
    private IReadOnlyList<ControllerInfo> _controllers = Array.Empty<ControllerInfo>();
    private int _scanTick;

    /// <summary>Ein Tastendruck (Richtungstasten wiederholen sich bei gehaltener Taste).</summary>
    public event Action<ControllerButton>? Pressed;

    /// <summary>Controller wurden angeschlossen oder abgezogen.</summary>
    public event Action? ControllersChanged;

    /// <summary>Der Zustand des ersten unterstützten Controllers, für die Anzeige in den Einstellungen; null, wenn keiner angeschlossen ist.</summary>
    public ControllerSnapshot? Latest { get; private set; }

    /// <summary>Rohwerte des ersten Controllers, den die App nicht kennt, zum Nachsehen.</summary>
    public string? RawDump { get; private set; }

    public IReadOnlyList<ControllerInfo> Controllers => _controllers;

    /// <summary>Familie des Controllers, der zuletzt eine Taste gedrückt hat (für die Namen der Tasten in Hinweisen).</summary>
    public ControllerFamily LastFamily { get; private set; } = ControllerFamily.Xbox;

    public ControllerService()
    {
        _timer.Tick += (_, _) => Poll();
    }

    /// <summary>Löst einen Tastendruck aus, als käme er vom Controller (die Fernbedienung im Browser nutzt das). Nur auf dem Oberflächen-Thread aufrufen.</summary>
    public void Inject(ControllerButton button) => Pressed?.Invoke(button);

    public void Start(Func<bool> isForeground)
    {
        _isForeground = isForeground;
        Poll();
        _timer.Start();
    }

    private void Poll()
    {
        try
        {
            // Die Liste ändert sich selten und ihr Abruf ist teurer als das Lesen: alle halbe Sekunde.
            var list = RawGameController.RawGameControllers;
            if (_scanTick++ % 30 == 0 || list.Count != _controllers.Count) Rescan(list);

            var merged = new HashSet<ControllerButton>();
            ControllerSnapshot? first = null;
            string? dump = null;
            var family = LastFamily;
            foreach (var raw in list)
            {
                var snapshot = Read(raw, out var info, out var rawDump);
                dump ??= rawDump;
                if (snapshot is null) continue;
                first ??= snapshot;
                var withSticks = snapshot.WithStickAsDpad();
                if (withSticks.Buttons.Count > 0) family = info!.Family;
                merged.UnionWith(withSticks.Buttons);
            }
            Latest = first;
            RawDump = dump;
            LastFamily = family;

            // Im Hintergrund zählt der Zustand mit, ohne zu melden: eine beim Zurückkehren noch gehaltene Taste löst dann nichts aus.
            var presses = _repeater.Update(merged, DateTime.UtcNow);
            if (!_isForeground()) return;
            foreach (var button in presses) Pressed?.Invoke(button);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Windows kann die Liste kurz verweigern (Controller wird gerade abgezogen): der nächste Takt versucht es neu.
            Latest = null;
        }
    }

    private void Rescan(IReadOnlyList<RawGameController> list)
    {
        var infos = new List<ControllerInfo>();
        foreach (var raw in list)
        {
            Read(raw, out var info, out _, describeOnly: true);
            if (info is not null) infos.Add(info);
        }
        if (infos.Count == _controllers.Count && infos.Zip(_controllers).All(p => p.First == p.Second)) return;
        _controllers = infos;
        ControllersChanged?.Invoke();
    }

    private static ControllerSnapshot? Read(RawGameController raw, out ControllerInfo? info, out string? rawDump, bool describeOnly = false)
    {
        rawDump = null;
        var vendor = raw.HardwareVendorId;
        var product = raw.HardwareProductId;
        var name = string.IsNullOrWhiteSpace(raw.DisplayName) ? $"Controller {vendor:X4}:{product:X4}" : raw.DisplayName;

        var pad = Gamepad.FromGameController(raw);
        if (pad is not null)
        {
            info = new ControllerInfo(name, ControllerFamily.Xbox, vendor, product, true, null);
            return describeOnly ? null : FromGamepad(pad.GetCurrentReading());
        }

        var family = ControllerLayout.FamilyOf(vendor, product);
        var buttons = new bool[raw.ButtonCount];
        var switches = new GameControllerSwitchPosition[raw.SwitchCount];
        var axes = new double[raw.AxisCount];
        if (family == ControllerFamily.PlayStation &&
            raw.ButtonCount >= ControllerLayout.PlayStationButtons && raw.AxisCount >= ControllerLayout.PlayStationAxes)
        {
            info = new ControllerInfo(name, family, vendor, product, true, null);
            if (describeOnly) return null;
            raw.GetCurrentReading(buttons, switches, axes);
            return ControllerLayout.FromPlayStation(buttons, switches.Select(s => (int)s).ToArray(), axes);
        }

        var reason = family == ControllerFamily.PlayStation
            ? $"meldet {raw.ButtonCount} Tasten und {raw.AxisCount} Achsen, erwartet sind {ControllerLayout.PlayStationButtons} und {ControllerLayout.PlayStationAxes}"
            : "das Layout dieses Controllers ist der App nicht bekannt";
        info = new ControllerInfo(name, family, vendor, product, false, reason);
        if (!describeOnly)
        {
            raw.GetCurrentReading(buttons, switches, axes);
            rawDump = $"{name}: Tasten {string.Join(",", buttons.Select((b, i) => b ? i.ToString() : null).OfType<string>())}; " +
                      $"Kreuz {string.Join(",", switches.Select(s => (int)s))}; Achsen {string.Join(" ", axes.Select(a => a.ToString("0.00")))}";
        }
        return null;
    }

    private static ControllerSnapshot FromGamepad(GamepadReading reading)
    {
        var pressed = new HashSet<ControllerButton>();
        void Map(GamepadButtons flag, ControllerButton button)
        {
            if (reading.Buttons.HasFlag(flag)) pressed.Add(button);
        }
        Map(GamepadButtons.A, ControllerButton.A);
        Map(GamepadButtons.B, ControllerButton.B);
        Map(GamepadButtons.X, ControllerButton.X);
        Map(GamepadButtons.Y, ControllerButton.Y);
        Map(GamepadButtons.LeftShoulder, ControllerButton.LeftBumper);
        Map(GamepadButtons.RightShoulder, ControllerButton.RightBumper);
        Map(GamepadButtons.View, ControllerButton.View);
        Map(GamepadButtons.Menu, ControllerButton.Menu);
        Map(GamepadButtons.LeftThumbstick, ControllerButton.LeftStick);
        Map(GamepadButtons.RightThumbstick, ControllerButton.RightStick);
        Map(GamepadButtons.DPadUp, ControllerButton.DpadUp);
        Map(GamepadButtons.DPadDown, ControllerButton.DpadDown);
        Map(GamepadButtons.DPadLeft, ControllerButton.DpadLeft);
        Map(GamepadButtons.DPadRight, ControllerButton.DpadRight);
        if (reading.LeftTrigger > 0.6) pressed.Add(ControllerButton.LeftTrigger);
        if (reading.RightTrigger > 0.6) pressed.Add(ControllerButton.RightTrigger);

        // Windows meldet „oben“ positiv; die App rechnet mit „oben“ negativ.
        return new ControllerSnapshot(pressed, reading.LeftThumbstickX, -reading.LeftThumbstickY, reading.RightThumbstickX, -reading.RightThumbstickY);
    }

    public void Dispose() => _timer.Stop();

    /// <summary>Ob das Fenster dieses Prozesses im Vordergrund liegt (Hauptfenster oder das Overlay des Players).</summary>
    public static bool IsForegroundWindowOurs()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return false;
        GetWindowThreadProcessId(window, out var process);
        return process == (uint)Environment.ProcessId;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
