using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MediaCenter.App.Services;
using Microsoft.Win32;

namespace MediaCenter.App.Theming;

public enum ThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Schreibt die Farben als Ressourcen (je Name eine <c>…Color</c> und eine <c>…Brush</c>) in die Anwendung;
/// die Oberfläche bindet sie per DynamicResource und folgt damit jedem Wechsel sofort.
/// </summary>
public sealed class ThemeService
{
    private readonly AppSettings _settings;

    public ThemeService(AppSettings settings)
    {
        _settings = settings;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && Mode == ThemeMode.System)
            {
                Application.Current.Dispatcher.Invoke(Apply);
            }
        };
    }

    public ThemeMode Mode => _settings.Mode switch
    {
        "system" => ThemeMode.System,
        "light" => ThemeMode.Light,
        "dark" => ThemeMode.Dark,
        var other => throw new InvalidDataException($"Unbekannter Modus '{other}' in den Einstellungen (system, light, dark)."),
    };

    public AccentPalette Palette => Palettes.Get(_settings.Palette);

    public bool IsDark { get; private set; }

    /// <summary>Wird nach jedem Anwenden ausgelöst (z. B. für die Titelleiste).</summary>
    public event Action? Changed;

    public void SetMode(ThemeMode mode)
    {
        _settings.Mode = mode.ToString().ToLowerInvariant();
        _settings.Save();
        Apply();
    }

    public void SetPalette(string id)
    {
        _settings.Palette = Palettes.Get(id).Id;
        _settings.Save();
        Apply();
    }

    public void Apply()
    {
        IsDark = Mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => !SystemUsesLightApps(),
        };
        var palette = Palette;
        var accent = IsDark ? palette.DarkAccent : palette.LightAccent;
        var accent2 = IsDark ? palette.DarkAccent2 : palette.LightAccent2;

        var res = Application.Current.Resources;
        void Set(string name, Color color)
        {
            res[name + "Color"] = color;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            res[name + "Brush"] = brush;
        }

        if (IsDark)
        {
            Set("Bg", Hex("#070d16"));
            Set("BgSoft", Hex("#0d1626"));
            Set("CardBg", Hex("#101b2d"));
            Set("Ink", Hex("#eef2f7"));
            Set("Muted", Hex("#94a3b8"));
            Set("Faint", Hex("#64748b"));
            Set("Line", Hex("#14e2e8f0"));
            Set("GlassTint", Hex("#701e293b"));
            Set("GlassEdge", Hex("#26e2e8f0"));
            Set("Scrim", Hex("#e6070d16"));
            Set("Track", Hex("#33e2e8f0"));
            Set("Surface", Hex("#0b1423"));
            Set("ModalScrim", Hex("#b3050a12"));
        }
        else
        {
            Set("Bg", Hex("#f8fafc"));
            Set("BgSoft", Hex("#eef2f7"));
            Set("CardBg", Hex("#ffffff"));
            Set("Ink", Hex("#0f172a"));
            Set("Muted", Hex("#475569"));
            Set("Faint", Hex("#64748b"));
            Set("Line", Hex("#1a0f172a"));
            Set("GlassTint", Hex("#b8ffffff"));
            Set("GlassEdge", Hex("#2a0f172a"));
            Set("Scrim", Hex("#e6f8fafc"));
            Set("Track", Hex("#330f172a"));
            Set("Surface", Hex("#f5f7fa"));
            Set("ModalScrim", Hex("#8c0f172a"));
        }
        Set("Accent", accent);
        Set("Accent2", accent2);
        Set("AccentSoft", Color.FromArgb(IsDark ? (byte)0x22 : (byte)0x1c, accent.R, accent.G, accent.B));
        Set("AccentLine", Color.FromArgb(0x66, accent.R, accent.G, accent.B));
        Set("Accent2Soft", Color.FromArgb(IsDark ? (byte)0x22 : (byte)0x1a, accent2.R, accent2.G, accent2.B));
        // Durchsichtige Gegenstücke für Verläufe: "Transparent" ist weiß, und ein Verlauf dorthin würde über Grau laufen.
        Set("AccentClear", Color.FromArgb(0, accent.R, accent.G, accent.B));
        Set("Accent2Clear", Color.FromArgb(0, accent2.R, accent2.G, accent2.B));
        Set("BgClear", Color.FromArgb(0, ((Color)res["BgColor"]).R, ((Color)res["BgColor"]).G, ((Color)res["BgColor"]).B));
        Set("OnAccent", Luminance(accent) > 0.5 ? Hex("#07131f") : Colors.White);
        Set("OnAccent2", Luminance(accent2) > 0.5 ? Hex("#07131f") : Colors.White);

        Changed?.Invoke();
    }

    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>Relative Helligkeit nach WCAG, 0 (schwarz) bis 1 (weiß).</summary>
    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static bool SystemUsesLightApps()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")
            ?? throw new InvalidOperationException("Windows-Designeinstellung nicht lesbar (Registry-Schlüssel Personalize fehlt).");
        return key.GetValue("AppsUseLightTheme") is int value && value == 1;
    }

    /// <summary>Färbt die Titelleiste des Fensters passend zum Modus (Windows 10 20H1 und neuer).</summary>
    public void StyleTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var dark = IsDark ? 1 : 0;
        var result = DwmSetWindowAttribute(handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        if (result != 0) throw new InvalidOperationException($"DwmSetWindowAttribute ist fehlgeschlagen (HRESULT 0x{result:X}).");
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
