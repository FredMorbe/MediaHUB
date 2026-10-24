using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using MediaCenter.App.Controls;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App.Pages;

/// <summary>
/// Suche in der ganzen Bibliothek (Strg+F oder die Lupe in der Navigation): Filme und Serien nach Name oder Schauspieler, Musik nach Album,
/// Künstler oder Song. Schauspieler kennt die Suche nur bei Titeln, deren Infotexte schon geholt sind (Einstellungen → „Fehlende Cover suchen“).
/// </summary>
public sealed class SearchPage : UserControl
{
    /// <summary>Die letzte Suche; die Seite baut sich beim Neuladen der Ansicht neu auf und soll nichts vergessen.</summary>
    private static string s_last = "";

    private readonly IShell _shell;
    private readonly LibraryModel _model;
    private readonly TextBox _box;
    private readonly TextBlock _count = new() { FontSize = 14, Margin = new Thickness(0, 0, 0, 14) };
    private readonly StackPanel _results = new();
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(180) };

    /// <param name="query">Wonach gleich gesucht wird; null zeigt die letzte Suche.</param>
    public SearchPage(IShell shell, string? query = null)
    {
        _shell = shell;
        _model = shell.Services.Model;
        _count.SetResourceReference(StyleProperty, "MutedText");

        var (field, box) = PageParts.HintBox("Film, Serie, Schauspieler, Album, Künstler, Song …", 620, 18);
        _box = box;
        box.Padding = new Thickness(14, 10, 14, 10);
        AutomationProperties.SetName(box, "Suchfeld");
        box.Text = query ?? s_last;
        box.TextChanged += (_, _) =>
        {
            _debounce.Stop();
            _debounce.Start();
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Fill();
        };
        field.HorizontalAlignment = HorizontalAlignment.Left;
        field.Margin = new Thickness(0, 4, 0, 18);

        var stack = new StackPanel { Margin = new Thickness(48, PageParts.NavInset + 28, 48, 60) };
        stack.Children.Add(PageParts.Header("Bibliothek", "Suche", _count));
        stack.Children.Add(field);
        stack.Children.Add(_results);
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = stack,
        };

        Loaded += (_, _) => Focus(null);
        Unloaded += (_, _) => _debounce.Stop();
        Fill();
    }

    /// <summary>Setzt den Cursor ins Suchfeld (Strg+F auf der Suche); mit <paramref name="query"/> wird gleich danach gesucht.</summary>
    public void Focus(string? query)
    {
        if (query is not null) _box.Text = query;
        _box.Focus();
        System.Windows.Input.Keyboard.Focus(_box);
        if (query is null) _box.SelectAll();
        else _box.CaretIndex = _box.Text.Length;
    }

    private void Fill()
    {
        var query = _box.Text;
        s_last = query;
        _results.Children.Clear();
        var result = LibrarySearch.Run(query, _model.Titles, _model.Casts(), _model.Albums);

        if (SearchText.Terms(query).Count == 0)
        {
            _count.Visibility = Visibility.Collapsed;
            _results.Children.Add(PageParts.EmptyState("", "Was suchst du?",
                "Gib einen Titel, einen Schauspieler, ein Album, einen Künstler oder einen Song ein. Mit „Tom Hardy“ findest du alle Filme und Serien mit ihm."));
            return;
        }
        if (result.IsEmpty)
        {
            _count.Visibility = Visibility.Collapsed;
            _results.Children.Add(PageParts.EmptyState("", "Nichts gefunden",
                $"Zu „{query.Trim()}“ gibt es keine Treffer. Schauspieler findet die Suche nur bei Titeln mit Infotexten: unter Einstellungen → Bibliothek holt „Fehlende Cover suchen“ sie nach."));
            return;
        }

        var parts = new List<string>();
        if (result.Titles.Count > 0) parts.Add($"{result.Titles.Count} {(result.Titles.Count == 1 ? "Film oder Serie" : "Filme und Serien")}");
        if (result.Albums.Count > 0) parts.Add($"{result.Albums.Count} {(result.Albums.Count == 1 ? "Album" : "Alben")}");
        _count.Text = string.Join(" · ", parts);
        _count.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (result.Actors.Count > 0) _results.Children.Add(ActorSection(result.Actors));
        if (result.Titles.Count > 0) _results.Children.Add(TitleSection(result.Titles));
        if (result.Albums.Count > 0) _results.Children.Add(AlbumSection(result.Albums));
    }

    private UIElement ActorSection(IReadOnlyList<ActorHit> actors)
    {
        var panel = new WrapPanel();
        foreach (var actor in actors)
        {
            var button = PageParts.Compact(PageParts.Button($"{actor.Name} · {actor.Titles} {(actor.Titles == 1 ? "Titel" : "Titel")}", "GhostButton",
                (_, _) => _box.Text = actor.Name, new Thickness(0, 0, 8, 8)));
            AutomationProperties.SetName(button, $"Schauspieler {actor.Name}");
            panel.Children.Add(button);
        }
        return new StackPanel { Children = { PageParts.SectionTitle("Schauspieler", new Thickness(0, 6, 0, 10)), panel } };
    }

    private UIElement TitleSection(IReadOnlyList<TitleHit> hits)
    {
        var grid = new WrapPanel();
        foreach (var hit in hits)
        {
            var card = new CardView(_model.ToCard(hit.Title, meta: hit.Via is { } actor ? $"mit {actor}" : null))
            {
                Margin = new Thickness(0, 0, 20, 30),
                Menu = item => LibraryActions.CardMenu(_shell, item),
            };
            card.Activated += item => _shell.OpenTitle(item.TitleId);
            grid.Children.Add(card);
        }
        return new StackPanel { Children = { PageParts.SectionTitle("Filme und Serien", new Thickness(0, 14, 0, 10)), grid } };
    }

    private UIElement AlbumSection(IReadOnlyList<AlbumHit> hits)
    {
        var grid = new WrapPanel();
        foreach (var hit in hits)
        {
            var card = new CardView(_model.ToCard(hit.Album, hit.Song is { } song ? $"Song: {song}" : null)) { Margin = new Thickness(0, 0, 20, 30) };
            card.Activated += item =>
            {
                if (item.Tag is AlbumInfo album) _shell.OpenAlbum(album.Key);
            };
            grid.Children.Add(card);
        }
        return new StackPanel { Children = { PageParts.SectionTitle("Musik", new Thickness(0, 14, 0, 10)), grid } };
    }
}
