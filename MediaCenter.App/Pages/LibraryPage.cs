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
    private string? _genre; // null = alle

    public LibraryPage(IShell shell, TitleKind kind)
    {
        _shell = shell;
        _model = shell.Services.Model;
        _kind = kind;

        _count.SetResourceReference(StyleProperty, "MutedText");

        var chips = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        var genres = _model.Titles.Where(t => t.Kind == kind).SelectMany(t => _model.GenresOf(t.Id))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
        if (genres.Count > 0)
        {
            chips.Children.Add(Chip("Alle", null, true, chips));
            foreach (var genre in genres) chips.Children.Add(Chip(genre, genre, false, chips));
        }

        var stack = new StackPanel { Margin = new Thickness(48, PageParts.NavInset + 28, 48, 60) };
        stack.Children.Add(PageParts.Header("Bibliothek", kind == TitleKind.Movie ? "Filme" : "Serien", _count));

        // Werkzeugleiste: Genre-Filter links, Aktionen rechts
        var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.Children.Add(chips);
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

    private ToggleButton Chip(string text, string? genre, bool on, Panel group)
    {
        var chip = new ToggleButton { Content = text, IsChecked = on };
        chip.SetResourceReference(StyleProperty, "Chip");
        chip.Click += (_, _) =>
        {
            foreach (var other in group.Children.OfType<ToggleButton>()) other.IsChecked = ReferenceEquals(other, chip);
            _genre = genre;
            Fill();
        };
        return chip;
    }

    private void Fill()
    {
        var titles = _model.Titles.Where(t => t.Kind == _kind)
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
