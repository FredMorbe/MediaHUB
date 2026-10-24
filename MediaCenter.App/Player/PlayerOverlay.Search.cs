using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using MediaCenter.Core;

namespace MediaCenter.App.Player;

/// <summary>Das Panel „Untertitel online suchen“: Trefferliste von OpenSubtitles mit „Laden“ je Treffer.</summary>
public partial class PlayerOverlay
{
    private readonly StackPanel _searchList = new();
    private readonly TextBlock _searchStatus = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10), MaxWidth = 520 };
    private bool _searchBuilt;
    private bool _searchBusy;

    /// <summary>„Online suchen …“ im Untertitelmenü: der Empfänger soll suchen und das Ergebnis über die Methoden dieses Panels zeigen.</summary>
    public event Action? SubtitleSearchRequested;

    /// <summary>„Laden“ bei einem Treffer.</summary>
    public event Action<SubtitleHit>? SubtitleHitChosen;

    private void BuildSearchPanel()
    {
        var heading = new TextBlock { Text = "Untertitel online suchen", FontSize = 22 };
        heading.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        _searchStatus.Foreground = new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF));

        var close = new Button { Content = "Schließen", Foreground = Brushes.White, Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        close.SetResourceReference(StyleProperty, "GhostButton");
        AutomationProperties.SetName(close, "Untertitelsuche schließen");
        close.Click += (_, _) => SearchPanel.Visibility = Visibility.Collapsed;

        SearchContent.Children.Add(heading);
        SearchContent.Children.Add(_searchStatus);
        SearchContent.Children.Add(new ScrollViewer { Content = _searchList, MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        SearchContent.Children.Add(close);
    }

    private void EnsureSearchPanel()
    {
        if (_searchBuilt) return;
        BuildSearchPanel();
        _searchBuilt = true;
    }

    /// <summary>Öffnet das Panel (mit Statuszeile, ohne Treffer).</summary>
    public void ShowSubtitleSearch(string status)
    {
        EnsureSearchPanel();
        CloseMenu();
        AdjustPanel.Visibility = TranslatePanel.Visibility = PicturePanel.Visibility = ColorPanel.Visibility = Visibility.Collapsed;
        _searchList.Children.Clear();
        _searchStatus.Text = status;
        SearchPanel.Visibility = Visibility.Visible;
    }

    public void SetSubtitleSearchStatus(string status)
    {
        EnsureSearchPanel();
        _searchStatus.Text = status;
    }

    /// <summary>Sperrt die „Laden“-Knöpfe, solange ein Download läuft.</summary>
    public void SetSubtitleSearchBusy(bool busy)
    {
        _searchBusy = busy;
        foreach (var button in _searchList.Children.OfType<Grid>().SelectMany(g => g.Children.OfType<Button>())) button.IsEnabled = !busy;
    }

    public void SetSubtitleHits(IReadOnlyList<SubtitleHit> hits, string summary)
    {
        EnsureSearchPanel();
        _searchStatus.Text = summary;
        _searchList.Children.Clear();
        foreach (var hit in hits) _searchList.Children.Add(HitRow(hit));
    }

    private Grid HitRow(SubtitleHit hit)
    {
        var language = Languages.All.FirstOrDefault(l => l.Id == hit.Language)?.Name ?? hit.Language;
        var details = new List<string> { language, $"{hit.Downloads:N0} Downloads" };
        if (hit.HearingImpaired) details.Add("für Hörgeschädigte");
        if (hit.Machine) details.Add("maschinell übersetzt");

        var name = new TextBlock { Text = hit.Release, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brushes.White };
        name.SetResourceReference(TextBlock.FontFamilyProperty, "BodySemiFont");
        name.ToolTip = hit.Release;
        var info = new TextBlock { Text = string.Join(" · ", details), FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), TextTrimming = TextTrimming.CharacterEllipsis };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name, info } };

        var badge = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            Visibility = hit.HashMatch ? Visibility.Visible : Visibility.Collapsed, ToolTip = "Gehört nachweislich zu genau dieser Videodatei: die Zeiten stimmen",
            Child = new TextBlock { Text = "passt zur Datei", FontSize = 11, Foreground = Brushes.Black },
        };
        badge.SetResourceReference(Border.BackgroundProperty, "AccentBrush");

        var load = new Button { Content = "Laden", Foreground = Brushes.White, Padding = new Thickness(16, 7, 16, 7), IsEnabled = !_searchBusy };
        load.SetResourceReference(StyleProperty, "GhostButton");
        AutomationProperties.SetName(load, $"Untertitel laden: {hit.Release}");
        load.Click += (_, _) => SubtitleHitChosen?.Invoke(hit);

        var row = new Grid { Margin = new Thickness(0, 0, 0, 8), Width = 520 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(badge, 1);
        Grid.SetColumn(load, 2);
        row.Children.Add(text);
        row.Children.Add(badge);
        row.Children.Add(load);
        return row;
    }
}
