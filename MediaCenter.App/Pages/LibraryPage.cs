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

        var heading = new TextBlock { Text = kind == TitleKind.Movie ? "Filme" : "Serien", FontSize = 64 };
        heading.SetResourceReference(StyleProperty, "DisplayText");
        _count.SetResourceReference(StyleProperty, "MutedText");

        var chips = new WrapPanel { Margin = new Thickness(0, 6, 0, 10) };
        var genres = _model.Titles.Where(t => t.Kind == kind).SelectMany(t => _model.GenresOf(t.Id))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
        if (genres.Count > 0)
        {
            chips.Children.Add(Chip("Alle", null, true, chips));
            foreach (var genre in genres) chips.Children.Add(Chip(genre, genre, false, chips));
        }

        var stack = new StackPanel { Margin = new Thickness(48, 92, 48, 60) };
        stack.Children.Add(heading);
        stack.Children.Add(_count);
        stack.Children.Add(chips);
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
            var card = new CardView(_model.ToCard(title)) { Margin = new Thickness(0, 0, 22, 28) };
            card.Activated += item => _shell.OpenTitle(item.TitleId);
            if (title.Kind == TitleKind.Movie) card.EditRequested += item => LibraryActions.EditMovie(_shell, item.TitleId);
            _grid.Children.Add(card);
        }

        var noun = _kind == TitleKind.Movie ? (titles.Count == 1 ? "Film" : "Filme") : (titles.Count == 1 ? "Serie" : "Serien");
        _count.Text = titles.Count == 0
            ? $"Keine {(_kind == TitleKind.Movie ? "Filme" : "Serien")}. Füge unter „Laufwerke“ einen Ordner hinzu oder ordne Dateien aus der Inbox zu."
            : $"{titles.Count} {noun}";
    }
}
