namespace MediaCenter.Core;

/// <summary>Tasten eines Controllers, benannt nach dem Xbox-Layout; <see cref="ControllerLayout.Label"/> nennt sie für PlayStation um.</summary>
public enum ControllerButton
{
    A,
    B,
    X,
    Y,
    LeftBumper,
    RightBumper,
    LeftTrigger,
    RightTrigger,
    View,
    Menu,
    LeftStick,
    RightStick,
    Guide,
    Touchpad,
    DpadUp,
    DpadDown,
    DpadLeft,
    DpadRight,
}

/// <summary>Der Zustand eines Controllers in einem Augenblick. Sticks von −1 bis 1, nach oben und links negativ.</summary>
public sealed record ControllerSnapshot(IReadOnlySet<ControllerButton> Buttons, double LeftX = 0, double LeftY = 0, double RightX = 0, double RightY = 0)
{
    /// <summary>Wie weit ein Stick ausschlagen muss, damit er wie eine Richtungstaste zählt.</summary>
    public const double StickThreshold = 0.6;

    /// <summary>Der linke Stick als Richtungstasten zum Kreuz dazu (nach oben und links ist negativ).</summary>
    public ControllerSnapshot WithStickAsDpad()
    {
        var buttons = new HashSet<ControllerButton>(Buttons);
        if (LeftY <= -StickThreshold) buttons.Add(ControllerButton.DpadUp);
        if (LeftY >= StickThreshold) buttons.Add(ControllerButton.DpadDown);
        if (LeftX <= -StickThreshold) buttons.Add(ControllerButton.DpadLeft);
        if (LeftX >= StickThreshold) buttons.Add(ControllerButton.DpadRight);
        return this with { Buttons = buttons };
    }
}

public enum ControllerFamily
{
    /// <summary>Xbox-Layout (auch Controller, die Windows als Xbox-Controller kennt).</summary>
    Xbox,

    /// <summary>DualSense (PS5), DualSense Edge und DualShock 4 (PS4).</summary>
    PlayStation,

    /// <summary>Ein anderer Controller ohne bekanntes Layout.</summary>
    Unknown,
}

/// <summary>
/// Übersetzt die rohen Werte eines Controllers (Tasten, Kreuz, Achsen in der Reihenfolge der HID-Beschreibung) in Tasten.
/// Windows kennt nur Xbox-Controller als „Gamepad“; PlayStation-Controller kommen als allgemeines HID-Gerät, dessen Tasten die App selbst zuordnet.
/// </summary>
public static class ControllerLayout
{
    private const ushort Sony = 0x054C;

    /// <summary>DualShock 4 (zwei Versionen und der USB-Adapter), DualSense und DualSense Edge.</summary>
    private static readonly HashSet<ushort> PlayStationProducts = new() { 0x05C4, 0x09CC, 0x0BA0, 0x0CE6, 0x0DF2 };

    public static ControllerFamily FamilyOf(ushort vendorId, ushort productId) =>
        vendorId == Sony && PlayStationProducts.Contains(productId) ? ControllerFamily.PlayStation : ControllerFamily.Unknown;

    /// <summary>Anzahl der Tasten, Kreuze und Achsen, die das PlayStation-Layout braucht (HID-Beschreibung von DualSense und DualShock 4 über Windows).</summary>
    public const int PlayStationButtons = 14;

    public const int PlayStationAxes = 6;

    /// <summary>
    /// Zustand eines PlayStation-Controllers aus den rohen Werten. Tasten: Quadrat, Kreuz, Kreis, Dreieck, L1, R1, L2, R2, Create/Share, Options,
    /// L3, R3, PS, Touchpad. Kreuz: 0 Mitte, 1 oben, dann im Uhrzeigersinn (2 oben rechts … 8 oben links). Achsen von 0 bis 1 mit 0,5 in der Mitte:
    /// linker Stick X und Y, rechter Stick X, L2, R2, rechter Stick Y.
    /// </summary>
    public static ControllerSnapshot FromPlayStation(IReadOnlyList<bool> buttons, IReadOnlyList<int> switches, IReadOnlyList<double> axes)
    {
        if (buttons.Count < PlayStationButtons) throw new InvalidDataException($"Der Controller meldet {buttons.Count} Tasten, erwartet sind {PlayStationButtons}.");
        if (axes.Count < PlayStationAxes) throw new InvalidDataException($"Der Controller meldet {axes.Count} Achsen, erwartet sind {PlayStationAxes}.");

        var pressed = new HashSet<ControllerButton>();
        void Map(int index, ControllerButton button)
        {
            if (buttons[index]) pressed.Add(button);
        }
        Map(0, ControllerButton.X);          // Quadrat
        Map(1, ControllerButton.A);          // Kreuz
        Map(2, ControllerButton.B);          // Kreis
        Map(3, ControllerButton.Y);          // Dreieck
        Map(4, ControllerButton.LeftBumper); // L1
        Map(5, ControllerButton.RightBumper);
        Map(6, ControllerButton.LeftTrigger);
        Map(7, ControllerButton.RightTrigger);
        Map(8, ControllerButton.View);       // Create / Share
        Map(9, ControllerButton.Menu);       // Options
        Map(10, ControllerButton.LeftStick);
        Map(11, ControllerButton.RightStick);
        Map(12, ControllerButton.Guide);     // PS
        Map(13, ControllerButton.Touchpad);

        // die Triggerachsen gelten auch als Taste, falls die Taste nicht meldet
        if (axes[3] > 0.6) pressed.Add(ControllerButton.LeftTrigger);
        if (axes[4] > 0.6) pressed.Add(ControllerButton.RightTrigger);

        var hat = switches.Count > 0 ? switches[0] : 0;
        if (hat is 1 or 2 or 8) pressed.Add(ControllerButton.DpadUp);
        if (hat is 3 or 2 or 4) pressed.Add(ControllerButton.DpadRight);
        if (hat is 5 or 4 or 6) pressed.Add(ControllerButton.DpadDown);
        if (hat is 7 or 6 or 8) pressed.Add(ControllerButton.DpadLeft);

        double Centered(double value) => Math.Abs(value - 0.5) < 0.08 ? 0 : (value - 0.5) * 2; // kleine Abweichung der Mitte ist kein Ausschlag
        return new ControllerSnapshot(pressed, Centered(axes[0]), Centered(axes[1]), Centered(axes[2]), Centered(axes[5]));
    }

    /// <summary>Name einer Taste auf dem Controller dieser Familie („Kreuz“, „A“).</summary>
    public static string Label(ControllerButton button, ControllerFamily family)
    {
        if (family == ControllerFamily.PlayStation)
        {
            return button switch
            {
                ControllerButton.A => "Kreuz",
                ControllerButton.B => "Kreis",
                ControllerButton.X => "Quadrat",
                ControllerButton.Y => "Dreieck",
                ControllerButton.LeftBumper => "L1",
                ControllerButton.RightBumper => "R1",
                ControllerButton.LeftTrigger => "L2",
                ControllerButton.RightTrigger => "R2",
                ControllerButton.View => "Create",
                ControllerButton.Menu => "Options",
                ControllerButton.LeftStick => "L3",
                ControllerButton.RightStick => "R3",
                ControllerButton.Guide => "PS-Taste",
                ControllerButton.Touchpad => "Touchpad",
                _ => DpadLabel(button),
            };
        }
        return button switch
        {
            ControllerButton.A => "A",
            ControllerButton.B => "B",
            ControllerButton.X => "X",
            ControllerButton.Y => "Y",
            ControllerButton.LeftBumper => "LB",
            ControllerButton.RightBumper => "RB",
            ControllerButton.LeftTrigger => "LT",
            ControllerButton.RightTrigger => "RT",
            ControllerButton.View => "View",
            ControllerButton.Menu => "Menü",
            ControllerButton.LeftStick => "Linker Stick drücken",
            ControllerButton.RightStick => "Rechter Stick drücken",
            ControllerButton.Guide => "Guide",
            ControllerButton.Touchpad => "Touchpad",
            _ => DpadLabel(button),
        };
    }

    private static string DpadLabel(ControllerButton button) => button switch
    {
        ControllerButton.DpadUp => "Kreuz hoch",
        ControllerButton.DpadDown => "Kreuz runter",
        ControllerButton.DpadLeft => "Kreuz links",
        ControllerButton.DpadRight => "Kreuz rechts",
        _ => throw new ArgumentOutOfRangeException(nameof(button), button, null),
    };
}

/// <summary>Was die Tasten eines Controllers im Player tun. Das Kreuz springt und regelt die Lautstärke, die Schultertasten wechseln Kapitel oder Sender.</summary>
public static class ControllerPlayerMap
{
    /// <summary>Die Aktion für eine Taste und ob sie die große Variante ist (60 Sekunden statt 10); null, wenn die Taste im Player nichts tut.</summary>
    public static (PlayerAction Action, bool Large)? For(ControllerButton button) => button switch
    {
        ControllerButton.A => (PlayerAction.PlayPause, false),
        ControllerButton.B => (PlayerAction.Exit, false),
        ControllerButton.X => (PlayerAction.SubtitleCycle, false),
        ControllerButton.Y => (PlayerAction.AudioCycle, false),
        ControllerButton.DpadLeft => (PlayerAction.SeekBack, false),
        ControllerButton.DpadRight => (PlayerAction.SeekForward, false),
        ControllerButton.DpadUp => (PlayerAction.VolumeUp, false),
        ControllerButton.DpadDown => (PlayerAction.VolumeDown, false),
        ControllerButton.LeftBumper => (PlayerAction.ChapterPrevious, false),
        ControllerButton.RightBumper => (PlayerAction.ChapterNext, false),
        ControllerButton.LeftTrigger => (PlayerAction.SeekBack, true),
        ControllerButton.RightTrigger => (PlayerAction.SeekForward, true),
        ControllerButton.Menu => (PlayerAction.Fullscreen, false),
        ControllerButton.View => (PlayerAction.SkipIntro, false),
        ControllerButton.LeftStick => (PlayerAction.Mute, false),
        ControllerButton.RightStick => (PlayerAction.NextVideo, false),
        ControllerButton.Touchpad => (PlayerAction.Stats, false),
        _ => null,
    };

    /// <summary>Die Belegung mit Wirkung in Worten, für die Einstellungen.</summary>
    public static IReadOnlyList<(ControllerButton Button, string Effect)> Overview { get; } = new[]
    {
        (ControllerButton.A, "Wiedergabe / Pause"),
        (ControllerButton.B, "Zurück (Vollbild oder Bild-in-Bild verlassen, Player schließen)"),
        (ControllerButton.X, "Untertitel wechseln"),
        (ControllerButton.Y, "Tonspur wechseln"),
        (ControllerButton.DpadLeft, "10 Sekunden zurück"),
        (ControllerButton.DpadRight, "10 Sekunden vor"),
        (ControllerButton.DpadUp, "Lauter"),
        (ControllerButton.DpadDown, "Leiser"),
        (ControllerButton.LeftBumper, "Voriges Kapitel, bei Live-TV voriger Sender"),
        (ControllerButton.RightBumper, "Nächstes Kapitel, bei Live-TV nächster Sender"),
        (ControllerButton.LeftTrigger, "60 Sekunden zurück"),
        (ControllerButton.RightTrigger, "60 Sekunden vor"),
        (ControllerButton.Menu, "Vollbild"),
        (ControllerButton.View, "Intro überspringen"),
        (ControllerButton.LeftStick, "Ton aus / an"),
        (ControllerButton.RightStick, "Nächste Folge"),
        (ControllerButton.Touchpad, "Statistik (PlayStation)"),
    };
}

/// <summary>
/// Macht aus dem Zustand eines Controllers im Takt der Abfrage einzelne Tastendrücke: jede neue Taste einmal, Richtungstasten bei gehaltener
/// Taste nach einer Pause immer wieder (wie bei einer Tastatur).
/// </summary>
public sealed class ControllerRepeater(TimeSpan firstDelay, TimeSpan interval)
{
    private readonly Dictionary<ControllerButton, DateTime> _nextRepeat = new();

    public ControllerRepeater() : this(TimeSpan.FromMilliseconds(420), TimeSpan.FromMilliseconds(110))
    {
    }

    /// <summary>Tasten, die sich bei gehaltener Taste wiederholen.</summary>
    public static bool Repeats(ControllerButton button) =>
        button is ControllerButton.DpadUp or ControllerButton.DpadDown or ControllerButton.DpadLeft or ControllerButton.DpadRight;

    /// <summary>Die Drücke, die zu diesem Zustand neu hinzukommen (in der Reihenfolge der Tasten).</summary>
    public IReadOnlyList<ControllerButton> Update(IReadOnlySet<ControllerButton> pressed, DateTime now)
    {
        foreach (var gone in _nextRepeat.Keys.Where(b => !pressed.Contains(b)).ToList()) _nextRepeat.Remove(gone);

        var presses = new List<ControllerButton>();
        foreach (var button in Enum.GetValues<ControllerButton>().Where(pressed.Contains))
        {
            if (!_nextRepeat.TryGetValue(button, out var next))
            {
                presses.Add(button);
                _nextRepeat[button] = Repeats(button) ? now + firstDelay : DateTime.MaxValue;
            }
            else if (now >= next)
            {
                presses.Add(button);
                _nextRepeat[button] = now + interval;
            }
        }
        return presses;
    }

    /// <summary>Vergisst, was gedrückt war (Controller abgezogen, Fenster ohne Fokus): die nächste Taste zählt neu.</summary>
    public void Reset() => _nextRepeat.Clear();
}
