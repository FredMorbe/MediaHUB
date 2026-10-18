using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MediaCenter.App.Controls;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App.Pages;

/// <summary>Alle Filme oder alle Serien als Raster, mit Filter nach Genre.</summary>
public sealed class LibraryPage : UserControl
{
    private readonly IShell _shell;
    private readonly LibraryModel _model;
    private readonly TitleKind _kind;
    private readonly WrapPanel _grid = new();
    private readonly TextBlock _count = new() { FontSize = 14, Margin = new Thickness(0, 0, 0, 14) };
    private readonly WrapPanel _chips = new() { VerticalAlignment = VerticalAlignment.Center };
    private string? _genre; // null = alle
    private bool _listOnly; // nur „Meine Liste“
    private ToggleButton? _selectedChip;
    private bool _syncingChips;

    public LibraryPage(IShell shell, TitleKind kind)
    {
        _shell = shell;
        _model = shell.Services.Model;
        _kind = kind;

        _count.SetResourceReference(StyleProperty, "MutedText");
        BuildChips();
        // „Meine Liste“ ändert sich auch von dieser Seite aus (Kartenmenü): Filter und Raster ziehen nach.
        Loaded += (_, _) => _model.WatchlistChanged += OnWatchlistChanged;
        Unloaded += (_, _) => _model.WatchlistChanged -= OnWatchlistChanged;

        var stack = new StackPanel { Margin = new Thickness(48, PageParts.NavInset + 28, 48, 60) };
        stack.Children.Add(PageParts.Header("Bibliothek", kind == TitleKind.Movie ? "Filme" : "Serien", _count));

        // Werkzeugleiste: Genre-Filter links, Aktionen rechts
        var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.Children.Add(_chips);
        if (kind == TitleKind.Movie)
        {
            var rename = PageParts.Compact(PageParts.Button("Dateien umbenennen …", "GhostButton", (_, _) => RenamePage.Show(_shell), new Thickness(16, 0, 0, 8)));
            rename.VerticalAlignment = VerticalAlignment.Top;
            System.Windows.Automation.AutomationProperties.SetName(rename, "Filme umbenennen");
            Grid.SetColumn(rename, 1);
            toolbar.Children.Add(rename);
        }
        stack.Children.Add(toolbar);
        stack.Children.Add(_grid);

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = stack,
        };
        Fill();
    }

    /// <summary>Filter: „Alle“, „Meine Liste“ (solange etwas darauf steht) und die Genres der Titel dieser Seite; ohne beides gibt es keine Filter.</summary>
    private void BuildChips()
    {
        _chips.Children.Clear();
        _selectedChip = null;
        var hasList = _model.WatchlistTitles().Any(t => t.Kind == _kind);
        if (!hasList) _listOnly = false; // der Filter wäre leer
        var genres = _model.Titles.Where(t => t.Kind == _kind).SelectMany(t => _model.GenresOf(t.Id))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
        if (genres.Count == 0 && !hasList) return;

        _chips.Children.Add(Chip("Alle", _genre is null && !_listOnly, () => (_genre, _listOnly) = (null, false)));
        if (hasList) _chips.Children.Add(Chip("Meine Liste", _listOnly, () => (_genre, _listOnly) = (null, true)));
        foreach (var genre in genres)
        {
            var selected = !_listOnly && string.Equals(_genre, genre, StringComparison.OrdinalIgnoreCase);
            _chips.Children.Add(Chip(genre, selected, () => (_genre, _listOnly) = (genre, false)));
        }
    }

    private void OnWatchlistChanged()
    {
        BuildChips();
        Fill();
    }

    private ToggleButton Chip(string text, bool on, Action select)
    {
        var chip = new ToggleButton { Content = text, IsChecked = on };
        chip.SetResourceReference(StyleProperty, "Chip");
        if (on) _selectedChip = chip;

        // Gewählt wird bei Checked: das löst auch das Umschalten per Bedienhilfe oder Automation aus, nicht nur der Mausklick.
        chip.Checked += (_, _) =>
        {
            if (_syncingChips || ReferenceEquals(_selectedChip, chip)) return;
            _selectedChip = chip;
            _syncingChips = true;
            foreach (var other in _chips.Children.OfType<ToggleButton>()) other.IsChecked = ReferenceEquals(other, chip);
            _syncingChips = false;
            select();
            Fill();
        };
        // Ein Klick auf den gewählten Filter würde ihn abwählen; einer bleibt immer gewählt.
        chip.Click += (_, _) => chip.IsChecked = true;
        return chip;
    }

    private void Fill()
    {
        var titles = _model.Titles.Where(t => t.Kind == _kind)
            .Where(t => !_listOnly || _model.IsOnWatchlist(t.Id))
            .Where(t => _genre is null || _model.GenresOf(t.Id).Contains(_genre, StringComparer.OrdinalIgnoreCase))
            .ToList();

        _grid.Children.Clear();
        foreach (var title in titles)
        {
            var card = new CardView(_model.ToCard(title)) { Margin = new Thickness(0, 0, 20, 30), Menu = item => LibraryActions.CardMenu(_shell, item) };
            card.Activated += item => _shell.OpenTitle(item.TitleId);
            _grid.Children.Add(card);
        }

        var noun = _kind == TitleKind.Movie ? (titles.Count == 1 ? "Film" : "Filme") : (titles.Count == 1 ? "Serie" : "Serien");
        _count.Visibility = titles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _count.Text = $"{titles.Count} {noun}";
        if (titles.Count == 0)
        {
            var what = _kind == TitleKind.Movie ? "Filme" : "Serien";
            _grid.Children.Add(PageParts.EmptyState(_kind == TitleKind.Movie ? "\uE714" : "\uE7F4",
                _genre is null ? $"Noch keine {what}" : $"Keine {what} in diesem Genre",
                _genre is null ? "Füge unter „Laufwerke“ einen Ordner hinzu oder ordne Dateien aus der Inbox zu." : "Wähle oben „Alle“, um wieder alles zu sehen.",
                PageParts.Button("Zu den Laufwerken", "PrimaryButton", (_, _) => _shell.Navigate(Section.Sources), new Thickness(0))));
        }
    }
}
