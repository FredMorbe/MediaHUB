namespace MediaCenter.Core;

/// <summary>Was sich im Player per Taste oder Controller auslösen lässt.</summary>
public enum PlayerAction
{
    PlayPause,
    Fullscreen,
    PictureInPicture,
    Exit,
    SeekBack,
    SeekForward,
    VolumeUp,
    VolumeDown,
    Mute,
    Stats,
    NextVideo,
    SkipIntro,
    ChapterPrevious,
    ChapterNext,
    SubtitleCycle,
    AudioCycle,
    SubtitleEarlier,
    SubtitleLater,
    NextAspect,
    Rotate,
    ZoomIn,
    ZoomOut,
    ZoomReset,
    FrameBack,
    FrameForward,
}

/// <summary>
/// Die Tasten der Player-Aktionen: Standardbelegung plus die vom Nutzer geänderte. Tasten heißen wie die Werte von <c>System.Windows.Input.Key</c>
/// („Space“, „OemComma“, „Left“); der Core kennt WPF nicht, die App prüft die Namen. Mit Strg gedrückte Tasten (Einzelbild, Tempo, Ausschnitt)
/// sind fest; Umschalt macht aus Springen und Drehen die große Variante (60 s, gegen den Uhrzeigersinn).
/// </summary>
public static class PlayerKeys
{
    public static IReadOnlyDictionary<PlayerAction, string[]> Defaults { get; } = new Dictionary<PlayerAction, string[]>
    {
        [PlayerAction.PlayPause] = new[] { "Space", "K" },
        [PlayerAction.Fullscreen] = new[] { "F11", "F" },
        [PlayerAction.PictureInPicture] = new[] { "P" },
        [PlayerAction.Exit] = new[] { "Escape" },
        [PlayerAction.SeekBack] = new[] { "Left" },
        [PlayerAction.SeekForward] = new[] { "Right" },
        [PlayerAction.VolumeUp] = new[] { "Up" },
        [PlayerAction.VolumeDown] = new[] { "Down" },
        [PlayerAction.Mute] = new[] { "M" },
        [PlayerAction.Stats] = new[] { "I" },
        [PlayerAction.NextVideo] = new[] { "N" },
        [PlayerAction.SkipIntro] = new[] { "S" },
        [PlayerAction.ChapterPrevious] = new[] { "PageUp" },
        [PlayerAction.ChapterNext] = new[] { "PageDown" },
        [PlayerAction.SubtitleCycle] = new[] { "U" },
        [PlayerAction.AudioCycle] = new[] { "T" },
        [PlayerAction.SubtitleEarlier] = new[] { "Z" },
        [PlayerAction.SubtitleLater] = new[] { "X" },
        [PlayerAction.NextAspect] = new[] { "A" },
        [PlayerAction.Rotate] = new[] { "R" },
        [PlayerAction.ZoomIn] = new[] { "OemPlus", "Add" },
        [PlayerAction.ZoomOut] = new[] { "OemMinus", "Subtract" },
        [PlayerAction.ZoomReset] = new[] { "D0", "NumPad0" },
        [PlayerAction.FrameBack] = new[] { "OemComma" },
        [PlayerAction.FrameForward] = new[] { "OemPeriod" },
    };

    /// <summary>Name der Aktion für Einstellungen und Hinweise.</summary>
    public static string Label(PlayerAction action) => action switch
    {
        PlayerAction.PlayPause => "Wiedergabe / Pause",
        PlayerAction.Fullscreen => "Vollbild",
        PlayerAction.PictureInPicture => "Bild-in-Bild",
        PlayerAction.Exit => "Verlassen (Bild-in-Bild, Vollbild, Player)",
        PlayerAction.SeekBack => "Zurückspringen (Umschalt: 60 s)",
        PlayerAction.SeekForward => "Vorspringen (Umschalt: 60 s)",
        PlayerAction.VolumeUp => "Lauter",
        PlayerAction.VolumeDown => "Leiser",
        PlayerAction.Mute => "Ton aus / an",
        PlayerAction.Stats => "Statistik",
        PlayerAction.NextVideo => "Nächste Folge / nächster Sender",
        PlayerAction.SkipIntro => "Intro überspringen",
        PlayerAction.ChapterPrevious => "Voriges Kapitel / voriger Sender",
        PlayerAction.ChapterNext => "Nächstes Kapitel / nächster Sender",
        PlayerAction.SubtitleCycle => "Untertitel wechseln",
        PlayerAction.AudioCycle => "Tonspur wechseln",
        PlayerAction.SubtitleEarlier => "Untertitel früher",
        PlayerAction.SubtitleLater => "Untertitel später",
        PlayerAction.NextAspect => "Nächstes Seitenverhältnis",
        PlayerAction.Rotate => "Drehen (Umschalt: andersherum)",
        PlayerAction.ZoomIn => "Zoom größer",
        PlayerAction.ZoomOut => "Zoom kleiner",
        PlayerAction.ZoomReset => "Zoom zurücksetzen",
        PlayerAction.FrameBack => "Ein Bild zurück",
        PlayerAction.FrameForward => "Ein Bild vor",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    /// <summary>Die Tasten einer Aktion: die geänderten, sonst die Standardtasten.</summary>
    public static IReadOnlyList<string> KeysOf(PlayerAction action, IReadOnlyDictionary<string, string[]>? overrides) =>
        overrides is not null && overrides.TryGetValue(action.ToString(), out var keys) ? keys : Defaults[action];

    /// <summary>
    /// Taste → Aktion. Wirft bei einer unbekannten Aktion, einem leeren oder doppelten Tastennamen oder einer Taste, die zwei Aktionen auslösen
    /// soll (ein Eintrag in der Einstellungsdatei von Hand): sonst täte eine Taste etwas anderes als angezeigt.
    /// </summary>
    public static Dictionary<string, PlayerAction> Resolve(IReadOnlyDictionary<string, string[]>? overrides)
    {
        if (overrides is not null)
        {
            foreach (var name in overrides.Keys)
            {
                if (!Enum.TryParse<PlayerAction>(name, out var parsed) || !Enum.IsDefined(parsed))
                    throw new InvalidDataException($"Unbekannte Player-Aktion „{name}“ in den Tastenbelegungen.");
            }
        }

        var map = new Dictionary<string, PlayerAction>();
        foreach (var action in Enum.GetValues<PlayerAction>())
        {
            foreach (var key in KeysOf(action, overrides))
            {
                if (string.IsNullOrWhiteSpace(key)) throw new InvalidDataException($"Leere Taste bei „{Label(action)}“.");
                if (map.TryGetValue(key, out var other))
                    throw new InvalidDataException($"Die Taste „{key}“ ist doppelt belegt: „{Label(other)}“ und „{Label(action)}“.");
                map[key] = action;
            }
        }
        return map;
    }

    /// <summary>
    /// Die Belegungen, nachdem <paramref name="key"/> der Aktion zugewiesen wurde (eine neue Taste je Aktion; die Standard-Zweittasten entfallen).
    /// Gehört die Taste schon einer anderen Aktion, kommt <paramref name="conflict"/> mit dieser zurück und nichts ändert sich.
    /// </summary>
    public static Dictionary<string, string[]> Assign(IReadOnlyDictionary<string, string[]>? current, PlayerAction action, string key, out PlayerAction? conflict)
    {
        var result = current is null ? new Dictionary<string, string[]>() : new Dictionary<string, string[]>(current);
        conflict = null;
        foreach (var other in Enum.GetValues<PlayerAction>())
        {
            if (other != action && KeysOf(other, current).Contains(key))
            {
                conflict = other;
                return result;
            }
        }
        result[action.ToString()] = new[] { key };
        return result;
    }

    /// <summary>
    /// Die Standardtasten einer Aktion wiederherstellen. Hat eine andere Aktion eine dieser Tasten inzwischen bekommen, kommt
    /// <paramref name="conflict"/> mit ihr zurück und nichts ändert sich.
    /// </summary>
    public static Dictionary<string, string[]> Reset(IReadOnlyDictionary<string, string[]>? current, PlayerAction action, out PlayerAction? conflict)
    {
        var result = current is null ? new Dictionary<string, string[]>() : new Dictionary<string, string[]>(current);
        conflict = null;
        foreach (var other in Enum.GetValues<PlayerAction>())
        {
            if (other != action && KeysOf(other, current).Intersect(Defaults[action]).Any())
            {
                conflict = other;
                return result;
            }
        }
        result.Remove(action.ToString());
        return result;
    }
}
