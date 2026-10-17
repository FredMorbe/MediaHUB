using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MediaCenter.Core;

namespace MediaCenter.App.Player;

/// <summary>Das Panel „Farbe“: Helligkeit, Kontrast, Sättigung, Farbton, Gamma, Weißabgleich, Schärfe und Invertieren.</summary>
public partial class PlayerOverlay
{
    private ColorSettings _color = ColorSettings.Default;
    private bool _refreshingColor;
    private readonly List<(Slider Slider, TextBlock Value, Func<ColorSettings, int> Get)> _colorRows = new();
    private readonly ToggleButton _invertToggle = new() { Content = "Farben invertieren", Foreground = Brushes.White, Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };

    /// <summary>Ein Farbregler oder „Invertieren“ wurde geändert.</summary>
    public event Action<ColorSettings>? ColorChanged;

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

        void Row(string name, string left, string right, Func<ColorSettings, int> get, Func<ColorSettings, int, ColorSettings> set)
        {
            var slider = new Slider { Minimum = ColorSettings.Min, Maximum = ColorSettings.Max, SmallChange = 1, LargeChange = 10, TickFrequency = 10, Value = 0, VerticalAlignment = VerticalAlignment.Center };
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
            reset.Click += (_, _) => ChangeColor(c => set(c, 0));

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
            _colorRows.Add((slider, value, get));
        }

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
            foreach (var (slider, value, get) in _colorRows)
            {
                slider.Value = get(_color);
                value.Text = get(_color).ToString("+0;−0;0", German);
            }
            _invertToggle.IsChecked = _color.Invert;
        }
        finally
        {
            _refreshingColor = false;
        }
    }
}
