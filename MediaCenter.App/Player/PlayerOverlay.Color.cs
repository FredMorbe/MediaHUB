using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MediaCenter.Core;

namespace MediaCenter.App.Player;

/// <summary>Das Panel „Farbe“: LUT, Helligkeit, Kontrast, Sättigung, Farbton, Gamma, Weißabgleich, Schärfe und Invertieren.</summary>
public partial class PlayerOverlay
{
    private ColorSettings _color = ColorSettings.Default;
    private bool _refreshingColor;
    private readonly List<(Slider Slider, TextBlock Value, Func<ColorSettings, int> Get, string Format)> _colorRows = new();
    private IReadOnlyList<string> _luts = Array.Empty<string>();
    private readonly WrapPanel _lutList = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly Button _lutRemoveButton = new() { Content = "Gewählte LUT löschen", Foreground = Brushes.White, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 8, 0, 0) };
    private Slider? _lutStrengthSlider;
    private bool _lutRemoveArmed;
    private readonly ToggleButton _invertToggle = new() { Content = "Farben invertieren", Foreground = Brushes.White, Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };

    /// <summary>Ein Farbregler, eine LUT oder „Invertieren“ wurde geändert.</summary>
    public event Action<ColorSettings>? ColorChanged;

    /// <summary>„LUT importieren …“ wurde gedrückt (die Dateiauswahl macht das Hauptfenster).</summary>
    public event Action? LutImportRequested;

    /// <summary>Die LUT mit diesem Namen soll aus der Sammlung gelöscht werden.</summary>
    public event Action<string>? LutRemoveRequested;

    /// <summary>Die LUTs der Sammlung (alphabetisch); baut die Auswahl neu auf.</summary>
    public void SetLuts(IReadOnlyList<string> names)
    {
        _luts = names;
        RebuildLutList();
    }

    /// <summary>Setzt die Farbeinstellungen (neues Video), ohne <see cref="ColorChanged"/> auszulösen.</summary>
    public void SetColor(ColorSettings color)
    {
        _color = color;
        RefreshColor();
    }

    private void ChangeColor(Func<ColorSettings, ColorSettings> change)
    {
        var next = change(_color);
        if (next == _color) return;
        _color = next.Validated();
        RefreshColor();
        ColorChanged?.Invoke(_color);
    }

    private void ToggleColor()
    {
        var show = ColorPanel.Visibility != Visibility.Visible;
        CloseMenu();
        AdjustPanel.Visibility = Visibility.Collapsed;
        TranslatePanel.Visibility = Visibility.Collapsed;
        PicturePanel.Visibility = Visibility.Collapsed;
        ColorPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) ColorScroll.MaxHeight = Math.Max(160, ActualHeight - 160);
    }

    private void BuildColorPanel()
    {
        ColorButton.Click += (_, _) => ToggleColor();
        SizeChanged += (_, _) => ColorScroll.MaxHeight = Math.Max(160, ActualHeight - 160);

        var heading = new TextBlock { Text = "Farbe", FontSize = 22, Margin = new Thickness(0, 0, 0, 4) };
        heading.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        ColorContent.Children.Add(heading);
        ColorContent.Children.Add(new TextBlock { Text = "Gilt für dieses Video und wird dafür gespeichert.", FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)) });

        TextBlock Label(string text) => new() { Text = text, FontSize = 13, Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 12, 0, 2) };

        void Row(string name, string left, string right, Func<ColorSettings, int> get, Func<ColorSettings, int, ColorSettings> set, int min = ColorSettings.Min, int max = ColorSettings.Max, int resetValue = 0, string format = "+0;−0;0")
        {
            var slider = new Slider { Minimum = min, Maximum = max, SmallChange = 1, LargeChange = 10, TickFrequency = 10, Value = 0, VerticalAlignment = VerticalAlignment.Center };
            slider.SetResourceReference(StyleProperty, "SeekSlider");
            slider.Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            AutomationProperties.SetName(slider, name);
            var value = new TextBlock { FontSize = 13, MinWidth = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            var reset = new Button { Content = "↺", Foreground = Brushes.White, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(8, 0, 0, 0), ToolTip = $"{name} zurücksetzen" };
            reset.SetResourceReference(StyleProperty, "GhostButton");
            AutomationProperties.SetName(reset, $"{name} zurücksetzen");
            slider.ValueChanged += (_, e) =>
            {
                if (!_refreshingColor) ChangeColor(c => set(c, (int)Math.Round(e.NewValue)));
            };
            reset.Click += (_, _) => ChangeColor(c => set(c, resetValue));

            var title = Label(right is "" ? $"{name}  ({left})" : $"{name}  ({left} ↔ {right})");
            ColorContent.Children.Add(title);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(value, 1);
            Grid.SetColumn(reset, 2);
            grid.Children.Add(slider);
            grid.Children.Add(value);
            grid.Children.Add(reset);
            ColorContent.Children.Add(grid);
            _colorRows.Add((slider, value, get, format));
        }

        var lutHeading = Label("LUT (Look)");
        lutHeading.Margin = new Thickness(0, 14, 0, 0);
        ColorContent.Children.Add(lutHeading);
        ColorContent.Children.Add(_lutList);
        var lutButtons = new WrapPanel();
        var import = new Button { Content = "LUT importieren …", Foreground = Brushes.White, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 8, 0, 0) };
        import.SetResourceReference(StyleProperty, "GhostButton");
        AutomationProperties.SetName(import, "LUT importieren");
        import.Click += (_, _) => LutImportRequested?.Invoke();
        _lutRemoveButton.SetResourceReference(StyleProperty, "GhostButton");
        AutomationProperties.SetName(_lutRemoveButton, "Gewählte LUT löschen");
        _lutRemoveButton.Click += (_, _) => OnLutRemoveClicked();
        lutButtons.Children.Add(import);
        lutButtons.Children.Add(_lutRemoveButton);
        ColorContent.Children.Add(lutButtons);
        Row("LUT-Stärke", "aus", "voll", c => c.LutStrength, (c, v) => c with { LutStrength = v }, min: 0, max: ColorSettings.LutStrengthMax, resetValue: ColorSettings.LutStrengthMax, format: "0 '%'");
        _lutStrengthSlider = _colorRows[^1].Slider;
        RebuildLutList();

        Row("Helligkeit", "dunkler", "heller", c => c.Brightness, (c, v) => c with { Brightness = v });
        Row("Kontrast", "weniger", "mehr", c => c.Contrast, (c, v) => c with { Contrast = v });
        Row("Sättigung", "blasser", "kräftiger", c => c.Saturation, (c, v) => c with { Saturation = v });
        Row("Farbton", "Farbkreis drehen", "", c => c.Hue, (c, v) => c with { Hue = v });
        Row("Gamma", "dunkler", "heller", c => c.Gamma, (c, v) => c with { Gamma = v });
        Row("Farbtemperatur", "kühl", "warm", c => c.Temperature, (c, v) => c with { Temperature = v });
        Row("Tönung", "grün", "magenta", c => c.Tint, (c, v) => c with { Tint = v });
        Row("Schärfe", "weicher", "schärfer", c => c.Sharpness, (c, v) => c with { Sharpness = v });

        _invertToggle.SetResourceReference(StyleProperty, "Chip");
        AutomationProperties.SetName(_invertToggle, "Farben invertieren");
        _invertToggle.Margin = new Thickness(0, 16, 0, 0);
        // Checked/Unchecked statt Click: so löst auch die UI-Automation (Toggle) die Änderung aus.
        _invertToggle.Checked += (_, _) => OnInvertToggled(true);
        _invertToggle.Unchecked += (_, _) => OnInvertToggled(false);
        ColorContent.Children.Add(_invertToggle);

        var reset = new Button { Content = "Alles zurücksetzen", Foreground = Brushes.White, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        reset.SetResourceReference(StyleProperty, "GhostButton");
        AutomationProperties.SetName(reset, "Farbeinstellungen zurücksetzen");
        reset.Click += (_, _) => ChangeColor(_ => ColorSettings.Default);
        ColorContent.Children.Add(reset);
        RefreshColor();
    }

    /// <summary>Die Auswahl „Keine“ plus eine Schaltfläche je LUT; die gewählte ist eingeschaltet.</summary>
    private void RebuildLutList()
    {
        _lutList.Children.Clear();

        void Add(string label, string automationName, string? lut)
        {
            var chip = new ToggleButton { Content = label, Foreground = Brushes.White, Margin = new Thickness(0, 0, 8, 8), IsChecked = _color.Lut == lut, MaxWidth = 300 };
            chip.SetResourceReference(StyleProperty, "Chip");
            AutomationProperties.SetName(chip, automationName);
            // Checked statt Click: so löst auch die UI-Automation (Toggle) die Änderung aus; bei schon gewählter LUT bleibt sie gewählt.
            chip.Checked += (_, _) => ChangeColor(c => lut is null
                ? c with { Lut = null, LutStrength = ColorSettings.LutStrengthMax }
                : c with { Lut = lut });
            chip.Unchecked += (_, _) => { if (_color.Lut == lut && !_refreshingColor) chip.IsChecked = true; };
            _lutList.Children.Add(chip);
        }

        Add("Keine", "LUT Keine", null);
        foreach (var name in _luts) Add(name, $"LUT {name}", name);

        _lutRemoveButton.Visibility = _color.UsesLut ? Visibility.Visible : Visibility.Collapsed;
        _lutRemoveButton.Content = _lutRemoveArmed ? $"„{_color.Lut}“ wirklich löschen?" : "Gewählte LUT löschen";
    }

    /// <summary>Löschen braucht zwei Klicks: der erste fragt nach, der zweite löscht (die Datei lässt sich nur neu importieren).</summary>
    private void OnLutRemoveClicked()
    {
        if (_color.Lut is not { } name) return;

        if (!_lutRemoveArmed)
        {
            _lutRemoveArmed = true;
            RebuildLutList();
            return;
        }
        _lutRemoveArmed = false;
        LutRemoveRequested?.Invoke(name);
    }

    private void OnInvertToggled(bool invert)
    {
        if (!_refreshingColor) ChangeColor(c => c with { Invert = invert });
    }

    /// <summary>Überträgt <see cref="_color"/> auf Regler und Werte, ohne Ereignisse auszulösen.</summary>
    private void RefreshColor()
    {
        _refreshingColor = true;
        try
        {
            foreach (var (slider, value, get, format) in _colorRows)
            {
                slider.Value = get(_color);
                value.Text = get(_color).ToString(format, German);
            }
            _invertToggle.IsChecked = _color.Invert;
            if (_lutStrengthSlider is not null) _lutStrengthSlider.IsEnabled = _color.UsesLut;
            _lutRemoveArmed = false;
            RebuildLutList();
        }
        finally
        {
            _refreshingColor = false;
        }
    }
}
