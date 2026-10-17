using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using MediaCenter.Core;

namespace MediaCenter.App.Player;

/// <summary>Das Panel „Untertitel übersetzen“: Quelle und Zielsprachen wählen, Fortschritt anzeigen.</summary>
public partial class PlayerOverlay
{
    private readonly StackPanel _sourceList = new();
    private readonly StackPanel _targetList = new();
    private readonly TextBlock _translateStatus = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), MaxWidth = 480 };
    private readonly ProgressBar _translateBar = new() { Minimum = 0, Maximum = 1, Height = 6, Margin = new Thickness(0, 10, 0, 10), BorderThickness = new Thickness(0), Visibility = Visibility.Collapsed };
    private readonly Button _translateStart = new() { Content = "Übersetzen", Foreground = Brushes.White, Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(0, 14, 8, 0) };
    private readonly Button _translateCancel = new() { Content = "Abbrechen", Foreground = Brushes.White, Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _translateClose = new() { Content = "Schließen", Foreground = Brushes.White, Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(0, 14, 0, 0) };
    private bool _translateRunning;
    private bool _translateBuilt;

    /// <summary>Starten der Übersetzung: Spur-Id der Quelle und die gewählten Zielsprachen (<see cref="TranslationTarget.Id"/>).</summary>
    public event Action<int, IReadOnlyList<string>>? TranslateRequested;

    /// <summary>„Abbrechen“ im Übersetzungs-Panel.</summary>
    public event Action? TranslateCancelled;

    private void BuildTranslatePanel()
    {
        var heading = new TextBlock { Text = "Untertitel übersetzen", FontSize = 22, Margin = new Thickness(0, 0, 0, 4) };
        heading.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");

        TextBlock Label(string text) => new() { Text = text, FontSize = 13, Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 12, 0, 6) };

        foreach (var button in new[] { _translateStart, _translateCancel, _translateClose })
        {
            button.SetResourceReference(StyleProperty, "GhostButton");
        }
        _translateStart.SetResourceReference(StyleProperty, "PrimaryButton");
        AutomationProperties.SetName(_translateStart, "Übersetzung starten");
        AutomationProperties.SetName(_translateCancel, "Übersetzung abbrechen");
        AutomationProperties.SetName(_translateClose, "Übersetzung schließen");
        _translateStatus.Foreground = new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF));
        _translateBar.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
        _translateBar.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

        _translateStart.Click += (_, _) =>
        {
            var source = _sourceList.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as int?;
            var targets = _targetList.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
            if (source is null)
            {
                _translateStatus.Text = "Wähle den Untertitel, der übersetzt werden soll.";
                return;
            }
            if (targets.Count == 0)
            {
                _translateStatus.Text = "Wähle mindestens eine Zielsprache.";
                return;
            }
            TranslateRequested?.Invoke(source.Value, targets);
        };
        _translateCancel.Click += (_, _) =>
        {
            _translateCancel.IsEnabled = false;
            TranslateCancelled?.Invoke();
        };
        _translateClose.Click += (_, _) => TranslatePanel.Visibility = Visibility.Collapsed;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_translateStart);
        buttons.Children.Add(_translateClose);

        TranslateContent.Children.Add(heading);
        TranslateContent.Children.Add(Label("Quelle"));
        TranslateContent.Children.Add(new ScrollViewer { Content = _sourceList, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        TranslateContent.Children.Add(Label("Übersetzen nach"));
        TranslateContent.Children.Add(_targetList);
        TranslateContent.Children.Add(buttons);
        TranslateContent.Children.Add(_translateBar);
        TranslateContent.Children.Add(_translateCancel);
        TranslateContent.Children.Add(_translateStatus);
    }

    private void ShowTranslatePanel()
    {
        if (!_translateBuilt)
        {
            BuildTranslatePanel();
            _translateBuilt = true;
        }
        CloseMenu();
        AdjustPanel.Visibility = Visibility.Collapsed;
        PicturePanel.Visibility = Visibility.Collapsed;
        ColorPanel.Visibility = Visibility.Collapsed;
        if (!_translateRunning) FillTranslateChoices();
        TranslatePanel.Visibility = Visibility.Visible;
    }

    private void FillTranslateChoices()
    {
        var sources = _tracks.Where(t => t.Type == "sub" && t.Translatable).ToList();
        // Vorauswahl: der gerade angezeigte Untertitel, sonst ein englischer, sonst der erste.
        var preferred = sources.FirstOrDefault(t => t.Selected) ?? sources.FirstOrDefault(t => t.Label.StartsWith("Englisch", StringComparison.Ordinal)) ?? sources.FirstOrDefault();

        _sourceList.Children.Clear();
        foreach (var track in sources)
        {
            var radio = new RadioButton { Content = track.Label, GroupName = "translate-source", Tag = track.Id, IsChecked = track == preferred, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 4) };
            radio.SetResourceReference(StyleProperty, "Chip");
            AutomationProperties.SetName(radio, $"Quelle {track.Label}");
            _sourceList.Children.Add(radio);
        }
        if (sources.Count == 0)
        {
            _sourceList.Children.Add(new TextBlock
            {
                Text = "Kein Textuntertitel vorhanden. Lade zuerst eine Untertitel-Datei (SRT) oder wähle ein Video mit Text-Untertiteln. Bilduntertitel (PGS, DVD) lassen sich nicht übersetzen.",
                FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 480, Foreground = new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)),
            });
        }

        if (_targetList.Children.Count == 0)
        {
            foreach (var target in TranslationTarget.All)
            {
                var box = new CheckBox { Content = target.DisplayName, Tag = target.Id, IsChecked = target == TranslationTarget.German, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 6) };
                AutomationProperties.SetName(box, $"Ziel {target.DisplayName}");
                _targetList.Children.Add(box);
            }
        }
        _translateStart.IsEnabled = sources.Count > 0;
        if (_translateStatus.Text.Length > 0 && !_translateRunning) _translateStatus.Text = "";
    }

    /// <summary>Zeigt den Stand einer laufenden oder beendeten Übersetzung im Panel (auch wenn es gerade geschlossen ist).</summary>
    /// <param name="fraction">Anteil 0 bis 1; null blendet den Balken aus.</param>
    public void SetTranslateState(bool running, string status, double? fraction = null)
    {
        if (!_translateBuilt)
        {
            BuildTranslatePanel();
            _translateBuilt = true;
        }
        _translateRunning = running;
        _translateStatus.Text = status;
        _translateBar.Visibility = running && fraction is not null ? Visibility.Visible : Visibility.Collapsed;
        if (fraction is { } f) _translateBar.Value = Math.Clamp(f, 0, 1);
        _translateCancel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        if (running) _translateCancel.IsEnabled = true;
        _translateStart.IsEnabled = !running && _sourceList.Children.OfType<RadioButton>().Any();
        foreach (var child in _sourceList.Children.OfType<RadioButton>()) child.IsEnabled = !running;
        foreach (var child in _targetList.Children.OfType<CheckBox>()) child.IsEnabled = !running;
    }

    /// <summary>Öffnet das Panel mit dem aktuellen Stand (ein Klick auf „Untertitel übersetzen“ während eines Laufs).</summary>
    public bool IsTranslating => _translateRunning;
}
