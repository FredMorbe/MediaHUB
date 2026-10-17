using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using MediaCenter.Core;

namespace MediaCenter.App.Player;

/// <summary>Das Panel „Bild“: Seitenverhältnis, Tempo, Zoom samt Ausschnitt und Drehung; dazu die Tasten dafür.</summary>
public partial class PlayerOverlay
{
    private PictureSettings _picture = PictureSettings.Default;
    private bool _refreshingPicture;
    private double? _videoAspect;
    private readonly Dictionary<AspectOption, RadioButton> _aspectButtons = new();
    private readonly Dictionary<double, RadioButton> _speedButtons = new();
    private readonly TextBlock _zoomText = new();
    private readonly TextBlock _rotationText = new();
    private readonly Slider _panXSlider = new() { Minimum = -100, Maximum = 100, SmallChange = 1, LargeChange = PictureSettings.PanStep, TickFrequency = 10 };
    private readonly Slider _panYSlider = new() { Minimum = -100, Maximum = 100, SmallChange = 1, LargeChange = PictureSettings.PanStep, TickFrequency = 10 };
    private readonly TextBlock _panXText = new();
    private readonly TextBlock _panYText = new();
    private readonly TextBlock _panHint = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 6, 0, 0) };

    /// <summary>Seitenverhältnis, Tempo, Zoom, Ausschnitt oder Drehung wurden geändert (Panel oder Tasten).</summary>
    public event Action<PictureSettings>? PictureChanged;

    /// <summary>Ein Bild vor (+1) oder zurück (−1), nur sinnvoll bei Pause.</summary>
    public event Action<int>? FrameStepRequested;

    /// <summary>Setzt die Bildeinstellungen (neues Video), ohne <see cref="PictureChanged"/> auszulösen.</summary>
    public void SetPicture(PictureSettings picture)
    {
        _picture = picture;
        RefreshPicture();
    }

    /// <summary>Seitenverhältnis der geladenen Datei (null: noch keine Datei); daraus ergibt sich, wie weit sich der Ausschnitt verschieben lässt.</summary>
    public void SetVideoAspect(double? aspect)
    {
        _videoAspect = aspect is > 0 ? aspect : null;
        UpdateFit();
    }

    /// <summary>Formate neu eintragen (Datei geladen, Fenstergröße geändert); mpv bekommt die dadurch geänderte Verschiebung.</summary>
    private void UpdateFit()
    {
        PictureFit? fit = _videoAspect is { } aspect && ActualWidth > 0 && ActualHeight > 0 ? new PictureFit(aspect, ActualWidth / ActualHeight) : null;
        if (fit == _picture.Fit) return;
        _picture = _picture with { Fit = fit };
        RefreshPicture();
        PictureChanged?.Invoke(_picture);
    }

    private static string SpeedText(double speed) => $"{speed.ToString("0.##", German)}×";

    private void ChangePicture(Func<PictureSettings, PictureSettings> change, string? notice = null)
    {
        var next = change(_picture);
        if (next == _picture) return;
        _picture = next.Validated();
        RefreshPicture();
        PictureChanged?.Invoke(_picture);
        if (notice is not null) ShowNotice(notice, isError: false);
    }

    // --- Tasten (MPC-BE: Strg+↑/↓ Tempo, Strg+R Tempo zurück) --------------------------------------------------

    /// <summary>Strg+↑ / Strg+↓: nächste Tempo-Stufe; am Ende der Skala bleibt es stehen und der Hinweis sagt das.</summary>
    public void StepSpeed(int direction)
    {
        if (PictureSettings.StepSpeed(_picture.Speed, direction) is not { } speed)
        {
            ShowNotice($"Tempo: {SpeedText(_picture.Speed)} ist die {(direction > 0 ? "höchste" : "niedrigste")} Stufe.", isError: false);
            return;
        }
        ChangePicture(p => p with { Speed = speed }, $"Tempo: {SpeedText(speed)}");
    }

    public void ResetSpeed()
    {
        if (_picture.Speed == 1)
        {
            ShowNotice("Tempo: 1×", isError: false);
            return;
        }
        ChangePicture(p => p with { Speed = 1 }, "Tempo: 1×");
    }

    /// <summary>Taste + / −: Zoom in 10-Prozent-Schritten.</summary>
    public void StepZoom(int direction)
    {
        var zoom = _picture.ZoomPercent + direction * PictureSettings.ZoomStep;
        if (zoom < PictureSettings.MinZoomPercent || zoom > PictureSettings.MaxZoomPercent)
        {
            ShowNotice($"Zoom: {_picture.ZoomPercent} % ist die {(direction > 0 ? "höchste" : "niedrigste")} Stufe.", isError: false);
            return;
        }
        // Beim Verkleinern bis 100 % rückt der Ausschnitt mit zurück in die Mitte, sonst bliebe ein unsichtbarer Versatz.
        ChangePicture(p => p with { ZoomPercent = zoom, PanX = zoom <= 100 ? 0 : p.PanX, PanY = zoom <= 100 ? 0 : p.PanY }, $"Zoom: {zoom} %");
    }

    /// <summary>Taste 0: Zoom und Ausschnitt zurück.</summary>
    public void ResetZoom() =>
        ChangePicture(p => p with { ZoomPercent = 100, PanX = 0, PanY = 0 }, "Zoom: 100 %");

    /// <summary>Strg+Umschalt+Pfeile: Ausschnitt verschieben. Ohne Zoom gibt es keinen Ausschnitt – das sagt der Hinweis.</summary>
    public void Pan(int dx, int dy)
    {
        if ((dx != 0 && _picture.PanRangeX == 0) || (dy != 0 && _picture.PanRangeY == 0))
        {
            ShowNotice("In dieser Richtung ragt das Bild nicht über das Fenster hinaus. Mit der Taste + lässt sich weiter zoomen.", isError: false);
            return;
        }
        ChangePicture(p => p with { PanX = Math.Clamp(p.PanX + dx, -100, 100), PanY = Math.Clamp(p.PanY + dy, -100, 100) });
        ShowNotice($"Ausschnitt: waagerecht {_picture.PanX} %, senkrecht {_picture.PanY} %", isError: false);
    }

    /// <summary>Taste R / Umschalt+R: um 90° drehen.</summary>
    public void Rotate(int degrees)
    {
        var rotation = PictureSettings.Rotated(_picture.Rotation, degrees);
        ChangePicture(p => p with { Rotation = rotation }, $"Drehung: {rotation}°");
    }

    /// <summary>Taste A: nächstes Seitenverhältnis.</summary>
    public void NextAspect()
    {
        var next = _picture.Aspect.Next();
        ChangePicture(p => p with { Aspect = next }, $"Seitenverhältnis: {next.Label}");
    }

    // --- Panel -----------------------------------------------------------------------------------------------

    private void TogglePicture()
    {
        var show = PicturePanel.Visibility != Visibility.Visible;
        CloseMenu();
        AdjustPanel.Visibility = Visibility.Collapsed;
        TranslatePanel.Visibility = Visibility.Collapsed;
        ColorPanel.Visibility = Visibility.Collapsed;
        PicturePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) FitPictureScroll();
    }

    /// <summary>Das Panel darf nicht höher werden als das Fenster; darüber scrollt es.</summary>
    private void FitPictureScroll() => PictureScroll.MaxHeight = Math.Max(160, ActualHeight - 160);

    private void BuildPicturePanel()
    {
        PictureButton.Click += (_, _) => TogglePicture();
        FramePrevButton.Click += (_, _) => FrameStepRequested?.Invoke(-1);
        FrameNextButton.Click += (_, _) => FrameStepRequested?.Invoke(1);
        SpeedButton.Click += (_, _) => ResetSpeed();
        SizeChanged += (_, _) =>
        {
            FitPictureScroll();
            UpdateFit();
        };

        var heading = new TextBlock { Text = "Bild", FontSize = 22, Margin = new Thickness(0, 0, 0, 4) };
        heading.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");

        TextBlock Label(string text) => new() { Text = text, FontSize = 13, Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 12, 0, 6) };

        Button Step(string text, string name, Action action)
        {
            var button = new Button { Content = text, Foreground = Brushes.White, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 6, 0) };
            button.SetResourceReference(StyleProperty, "GhostButton");
            AutomationProperties.SetName(button, name);
            button.Click += (_, _) => action();
            return button;
        }

        StackPanel Row(params UIElement[] items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var item in items) row.Children.Add(item);
            return row;
        }

        TextBlock Value(TextBlock block, double width)
        {
            block.MinWidth = width;
            block.TextAlignment = TextAlignment.Center;
            block.VerticalAlignment = VerticalAlignment.Center;
            block.FontSize = 15;
            block.Margin = new Thickness(0, 0, 6, 0);
            return block;
        }

        RadioButton Chip(string text, string group, string name, Action onChecked)
        {
            var chip = new RadioButton { Content = text, GroupName = group, Foreground = Brushes.White };
            chip.SetResourceReference(StyleProperty, "Chip");
            AutomationProperties.SetName(chip, name);
            // Checked statt Click: so löst auch die UI-Automation (Toggle) die Wahl aus.
            chip.Checked += (_, _) =>
            {
                if (!_refreshingPicture) onChecked();
            };
            return chip;
        }

        // Seitenverhältnis
        var aspects = new WrapPanel();
        foreach (var option in AspectOption.All)
        {
            var chip = Chip(option.Label, "picture-aspect", $"Seitenverhältnis {option.Label}", () => ChangePicture(p => p with { Aspect = option }, $"Seitenverhältnis: {option.Label}"));
            _aspectButtons[option] = chip;
            aspects.Children.Add(chip);
        }

        // Tempo
        var speeds = new WrapPanel();
        foreach (var speed in PictureSettings.SpeedSteps)
        {
            var chip = Chip(SpeedText(speed), "picture-speed", $"Tempo {SpeedText(speed)}", () => ChangePicture(p => p with { Speed = speed }, $"Tempo: {SpeedText(speed)}"));
            _speedButtons[speed] = chip;
            speeds.Children.Add(chip);
        }

        // Zoom und Ausschnitt
        _panXSlider.Style = (Style)FindResource("SeekSlider");
        _panYSlider.Style = (Style)FindResource("SeekSlider");
        foreach (var slider in new[] { _panXSlider, _panYSlider })
        {
            slider.Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            slider.VerticalAlignment = VerticalAlignment.Center;
        }
        AutomationProperties.SetName(_panXSlider, "Ausschnitt waagerecht");
        AutomationProperties.SetName(_panYSlider, "Ausschnitt senkrecht");
        _panXSlider.ValueChanged += (_, e) =>
        {
            if (!_refreshingPicture) ChangePicture(p => p with { PanX = (int)Math.Round(e.NewValue) });
        };
        _panYSlider.ValueChanged += (_, e) =>
        {
            if (!_refreshingPicture) ChangePicture(p => p with { PanY = (int)Math.Round(e.NewValue) });
        };

        Grid SliderRow(string left, Slider slider, TextBlock value)
        {
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock { Text = left, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(slider, 1);
            Grid.SetColumn(value, 2);
            value.MinWidth = 56;
            value.TextAlignment = TextAlignment.Right;
            value.VerticalAlignment = VerticalAlignment.Center;
            value.FontSize = 13;
            grid.Children.Add(name);
            grid.Children.Add(slider);
            grid.Children.Add(value);
            return grid;
        }

        // Drehung
        var rotateLeft = Step("↺ 90°", "Gegen den Uhrzeigersinn drehen", () => Rotate(-90));
        var rotateRight = Step("↻ 90°", "Im Uhrzeigersinn drehen", () => Rotate(90));

        PictureContent.Children.Add(heading);
        PictureContent.Children.Add(Label("Seitenverhältnis (A = nächstes)"));
        PictureContent.Children.Add(aspects);
        PictureContent.Children.Add(Label("Tempo (Strg+↑ / Strg+↓, Strg+R = normal)"));
        PictureContent.Children.Add(speeds);
        PictureContent.Children.Add(Label("Zoom (+ / −, 0 = zurück)"));
        PictureContent.Children.Add(Row(
            Step("−", "Zoom kleiner", () => StepZoom(-1)),
            Value(_zoomText, 64),
            Step("+", "Zoom größer", () => StepZoom(1)),
            Step("100 %", "Zoom und Ausschnitt zurücksetzen", ResetZoom)));
        PictureContent.Children.Add(Label("Ausschnitt (Strg+Umschalt+Pfeile)"));
        PictureContent.Children.Add(SliderRow("Links ↔ Rechts", _panXSlider, _panXText));
        PictureContent.Children.Add(SliderRow("Oben ↔ Unten", _panYSlider, _panYText));
        var center = Row(Step("Mitte", "Ausschnitt in die Mitte", () => ChangePicture(p => p with { PanX = 0, PanY = 0 }, "Ausschnitt: Mitte")));
        center.Margin = new Thickness(0, 8, 0, 0);
        PictureContent.Children.Add(center);
        PictureContent.Children.Add(_panHint);
        PictureContent.Children.Add(Label("Drehen (R / Umschalt+R)"));
        PictureContent.Children.Add(Row(rotateLeft, Value(_rotationText, 52), rotateRight));

        var reset = Step("Alles zurücksetzen", "Bildeinstellungen zurücksetzen", () => ChangePicture(p => PictureSettings.Default with { Fit = p.Fit }, "Bild: Standard"));
        reset.Margin = new Thickness(0, 16, 0, 0);
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        PictureContent.Children.Add(reset);
        RefreshPicture();
    }

    /// <summary>Überträgt <see cref="_picture"/> auf alle Elemente (Panel, Tempo-Anzeige in der Leiste), ohne Ereignisse auszulösen.</summary>
    private void RefreshPicture()
    {
        _refreshingPicture = true;
        try
        {
            foreach (var (option, button) in _aspectButtons) button.IsChecked = option == _picture.Aspect;
            foreach (var (speed, button) in _speedButtons) button.IsChecked = Math.Abs(speed - _picture.Speed) < 1e-9;
            _zoomText.Text = $"{_picture.ZoomPercent} %";
            _rotationText.Text = $"{_picture.Rotation}°";
            _panXSlider.Value = _picture.PanX;
            _panYSlider.Value = _picture.PanY;
            _panXText.Text = $"{_picture.PanX} %";
            _panYText.Text = $"{_picture.PanY} %";
            _panXSlider.IsEnabled = _picture.PanRangeX > 0;
            _panYSlider.IsEnabled = _picture.PanRangeY > 0;
            var canPan = _panXSlider.IsEnabled || _panYSlider.IsEnabled;
            _panHint.Text = canPan ? "" : "Zum Verschieben zuerst den Zoom erhöhen, bis das Bild über das Fenster hinausragt.";
            _panHint.Visibility = canPan ? Visibility.Collapsed : Visibility.Visible;

            SpeedButton.Content = SpeedText(_picture.Speed);
            SpeedButton.Visibility = _picture.Speed == 1 ? Visibility.Collapsed : Visibility.Visible;
        }
        finally
        {
            _refreshingPicture = false;
        }
    }
}
