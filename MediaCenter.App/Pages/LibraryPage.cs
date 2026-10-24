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
    private enum WatchFilter
    {
        All,
        Unwatched,
        InProgress,
        Watched,
    }

    // Sortierung und Ansicht bleiben je Filme/Serien für die Dauer der Sitzung, auch wenn die Seite neu aufgebaut wird (Zurück aus einem Titel).
    private static readonly Dictionary<TitleKind, TitleSort> s_sort = new();
    private static readonly Dictionary<TitleKind, WatchFilter> s_watch = new();

    private TitleSort Sort
    {
        get => s_sort.GetValueOrDefault(_kind);
        set => s_sort[_kind] = value;
    }

    private WatchFilter Watch
    {
        get => s_watch.GetValueOrDefault(_kind);
        set => s_watch[_kind] = value;
    }

    private readonly Button _sortButton;
    private readonly Button _watchButton;
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
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        _sortButton = PageParts.Compact(PageParts.Button("", "GhostButton", (_, _) => ShowSortMenu(), new Thickness(16, 0, 0, 8)));
        _watchButton = PageParts.Compact(PageParts.Button("", "GhostButton", (_, _) => ShowWatchMenu(), new Thickness(8, 0, 0, 8)));
        System.Windows.Automation.AutomationProperties.SetName(_sortButton, "Sortierung");
        System.Windows.Automation.AutomationProperties.SetName(_watchButton, "Ansicht");
        UpdateButtons();
        actions.Children.Add(_sortButton);
        actions.Children.Add(_watchButton);
        var refresh = PageParts.LibraryRefreshButton(_shell);
        refresh.Margin = new Thickness(8, 0, 0, 8);
        actions.Children.Add(refresh);
        if (kind == TitleKind.Movie)
        {
            var collections = PageParts.Compact(PageParts.Button("Sammlungen", "GhostButton", (_, _) => _shell.Open(() => new CollectionsPage(_shell)), new Thickness(8, 0, 0, 8)));
            collections.VerticalAlignment = VerticalAlignment.Top;
            System.Windows.Automation.AutomationProperties.SetName(collections, "Sammlungen");
            actions.Children.Add(collections);
            var rename = PageParts.Compact(PageParts.Button("Dateien umbenennen …", "GhostButton", (_, _) => RenamePage.Show(_shell), new Thickness(16, 0, 0, 8)));
            rename.VerticalAlignment = VerticalAlignment.Top;
            System.Windows.Automation.AutomationProperties.SetName(rename, "Filme umbenennen");
            actions.Children.Add(rename);
        }
        Grid.SetColumn(actions, 1);
        toolbar.Children.Add(actions);
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

    private static string SortText(TitleSort sort) => sort switch
    {
        TitleSort.Name => "Name (A–Z)",
        TitleSort.Year => "Jahr (neueste zuerst)",
        TitleSort.Added => "Zuletzt hinzugefügt",
        TitleSort.Rating => "Wertung",
        TitleSort.Resolution => "Auflösung",
        _ => throw new ArgumentOutOfRangeException(nameof(sort), sort, null),
    };

    private static string WatchText(WatchFilter filter) => filter switch
    {
        WatchFilter.All => "Alle",
        WatchFilter.Unwatched => "Ungesehen",
        WatchFilter.InProgress => "Angefangen",
        WatchFilter.Watched => "Gesehen",
        _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, null),
    };

    private void UpdateButtons()
    {
        _sortButton.Content = $"Sortierung: {SortText(Sort)}  ▾";
        _watchButton.Content = $"Ansicht: {WatchText(Watch)}  ▾";
    }

    private void ShowSortMenu() => ContextPopup.Show(_sortButton,
        Enum.GetValues<TitleSort>().Select(sort => ((Sort == sort ? "✓ " : "    ") + SortText(sort), (Action)(() =>
        {
            Sort = sort;
            UpdateButtons();
            Fill();
        }))).ToList(), "Sortierung");

    private void ShowWatchMenu() => ContextPopup.Show(_watchButton,
        Enum.GetValues<WatchFilter>().Select(filter => ((Watch == filter ? "✓ " : "    ") + WatchText(filter), (Action)(() =>
        {
            Watch = filter;
            UpdateButtons();
            Fill();
        }))).ToList(), "Ansicht");

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
        var states = Watch == WatchFilter.All ? null : _model.WatchStatesByTitle();
        var wanted = Watch switch
        {
            WatchFilter.Unwatched => WatchState.Unwatched,
            WatchFilter.InProgress => WatchState.InProgress,
            _ => WatchState.Watched,
        };
        var titles = TitleOrdering.Order(
            _model.Titles.Where(t => t.Kind == _kind)
                .Where(t => !_listOnly || _model.IsOnWatchlist(t.Id))
                .Where(t => _genre is null || _model.GenresOf(t.Id).Contains(_genre, StringComparer.OrdinalIgnoreCase))
                .Where(t => states is null || states[t.Id] == wanted),
            Sort, _model.Ratings(), _model.Heights());

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
            var filtered = _genre is not null || _listOnly || Watch != WatchFilter.All;
            _grid.Children.Add(PageParts.EmptyState(_kind == TitleKind.Movie ? "\uE714" : "\uE7F4",
                filtered ? $"Keine {what} in dieser Ansicht" : $"Noch keine {what}",
                filtered ? "Setze Genre und Ansicht auf „Alle“, um wieder alles zu sehen." : "Füge unter „Laufwerke“ einen Ordner hinzu oder ordne Dateien aus der Inbox zu.",
                filtered ? [] : [PageParts.Button("Zu den Laufwerken", "PrimaryButton", (_, _) => _shell.Navigate(Section.Sources), new Thickness(0))]));
        }
    }
}
