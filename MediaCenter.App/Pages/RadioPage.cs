using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App.Pages;

/// <summary>Gespeicherte Sender und Suche im Verzeichnis radio-browser.info.</summary>
public sealed class RadioPage : UserControl
{
    private static readonly string[] Genres = { "Pop", "Rock", "Jazz", "Klassik", "Nachrichten", "Electronic", "Hip Hop", "Ambient" };

    private readonly IShell _shell;
    private readonly LibraryModel _model;
    private readonly StackPanel _saved = new();
    private readonly StackPanel _results = new();
    private readonly TextBox _query;

    public RadioPage(IShell shell)
    {
        _shell = shell;
        _model = shell.Services.Model;

        var stack = new StackPanel();
        stack.Children.Add(PageParts.Heading("Radio"));
        stack.Children.Add(PageParts.Muted("Sender stammen aus dem offenen Verzeichnis radio-browser.info. Gespeicherte Sender bleiben hier, auch wenn du sie nicht neu suchst."));

        stack.Children.Add(Section("Gespeichert"));
        stack.Children.Add(_saved);

        stack.Children.Add(Section("Sender suchen"));
        var (field, box) = PageParts.HintBox("Name des Senders …", 300);
        _query = box;
        _query.KeyDown += async (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) await SearchAsync(_query.Text, null);
        };
        var bar = new WrapPanel { Margin = new Thickness(0, 4, 0, 10) };
        field.Margin = new Thickness(0, 0, 10, 8);
        bar.Children.Add(field);
        var search = PageParts.Button("Suchen", "PrimaryButton", async (_, _) => await SearchAsync(_query.Text, null), new Thickness(0, 0, 10, 8));
        search.Padding = new Thickness(22, 8, 22, 8);
        bar.Children.Add(search);
        stack.Children.Add(bar);

        var genres = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var genre in Genres)
        {
            var chip = new Button { Content = genre };
            chip.SetResourceReference(StyleProperty, "GhostButton");
            chip.Padding = new Thickness(14, 5, 14, 5);
            chip.Margin = new Thickness(0, 0, 8, 8);
            chip.FontSize = 13;
            chip.Click += async (_, _) => await SearchAsync(null, genre);
            genres.Children.Add(chip);
        }
        stack.Children.Add(genres);
        stack.Children.Add(_results);

        Content = PageParts.Scrolling(stack);
        BuildSaved();
    }

    private static TextBlock Section(string text)
    {
        var header = new TextBlock { Text = text, FontSize = 34, Margin = new Thickness(0, 26, 0, 8) };
        header.SetResourceReference(StyleProperty, "DisplayText");
        return header;
    }

    private void BuildSaved()
    {
        _saved.Children.Clear();
        if (_model.Stations.Count == 0)
        {
            _saved.Children.Add(PageParts.Muted("Noch keine Sender gespeichert. Suche unten einen Sender und speichere ihn mit „Speichern“."));
            return;
        }
        foreach (var saved in _model.Stations) _saved.Children.Add(Row(saved.Station, saved));
    }

    private async Task SearchAsync(string? name, string? tag)
    {
        if (string.IsNullOrWhiteSpace(name) && tag is null)
        {
            _shell.ShowMessage("Bitte einen Sendernamen eingeben oder ein Genre wählen.", isError: true);
            return;
        }

        IReadOnlyList<RadioStation> found = Array.Empty<RadioStation>();
        await _shell.RunBusyAsync("Sender werden gesucht …", async () => found = await _shell.Services.RadioSearch.SearchAsync(name, tag));

        _results.Children.Clear();
        var header = PageParts.Muted(found.Count == 0 ? "Keine Sender gefunden." : $"{found.Count} Sender, beliebteste zuerst", 14);
        header.Margin = new Thickness(0, 4, 0, 8);
        _results.Children.Add(header);
        foreach (var station in found)
        {
            var saved = _model.Stations.FirstOrDefault(s => s.Station.Url == station.Url || (station.Uuid is not null && s.Station.Uuid == station.Uuid));
            _results.Children.Add(Row(station, saved));
        }
    }

    private UIElement Row(RadioStation station, SavedStation? saved)
    {
        // Gespeicherte Sender haben ihr Logo im Cache; Suchergebnisse laden es direkt von der angegebenen Adresse.
        System.Windows.Media.ImageSource? logo = saved?.FaviconFile is { } file
            ? ImageCache.Load(_model.CachePath(file), 96)
            : station.FaviconUrl is { } url ? RemoteLogo(url) : null;
        var thumb = PageParts.Thumb(logo, 52, "");

        var name = PageParts.Semi(station.Name, 16);
        var details = string.Join(" · ", new[] { station.Country, station.Codec, station.Bitrate is > 0 ? $"{station.Bitrate} kbit/s" : null, station.Tags is { Length: > 0 } t ? string.Join(", ", t.Split(',').Take(3)) : null }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        var meta = PageParts.Muted(details, 13);
        meta.TextTrimming = TextTrimming.CharacterEllipsis;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 14, 0), Children = { name, meta } };

        var play = PageParts.Button("Abspielen", "PrimaryButton", async (_, _) => await PlayAsync(station, saved), new Thickness(0, 0, 8, 0));
        play.Padding = new Thickness(18, 7, 18, 7);
        var keep = saved is null
            ? PageParts.Button("Speichern", "GhostButton", async (_, _) => await SaveAsync(station), new Thickness(0))
            : PageParts.Button("Entfernen", "GhostButton", (_, _) =>
            {
                _model.Db.RemoveStation(saved.Id);
                _model.Reload();
                BuildSaved();
            }, new Thickness(0));
        keep.Padding = new Thickness(18, 7, 18, 7);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { play, keep } };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(thumb);
        grid.Children.Add(text);
        grid.Children.Add(buttons);
        return PageParts.Card(grid, new Thickness(0, 0, 0, 8));
    }

    /// <summary>Logos von Sendern sind oft kleine PNG/ICO-Dateien; WPF lädt sie selbst nach. Schlägt das fehl, bleibt das Notensymbol.</summary>
    private static System.Windows.Media.ImageSource? RemoteLogo(string url)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = HttpFetch.RequireWebUri(url);
            image.DecodePixelWidth = 96; // ohne OnLoad lädt WPF im Hintergrund, die Oberfläche wartet nicht
            image.EndInit();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException)
        {
            return null; // Ein fehlendes Logo ist kein Fehler der Suche.
        }
    }

    private async Task PlayAsync(RadioStation station, SavedStation? saved)
    {
        var cover = saved?.FaviconFile is { } file ? _model.CachePath(file) : null;
        await _shell.RunBusyAsync("Sender wird geöffnet …", () => _shell.Services.Audio.PlayAsync(new[] { AudioQueues.ForStation(station, cover) }));
    }

    private async Task SaveAsync(RadioStation station)
    {
        string? warning = null;
        await _shell.RunBusyAsync("Sender wird gespeichert …", async () => warning = (await _shell.Services.RadioStations.SaveAsync(station)).Warning);
        _model.Reload();
        BuildSaved();
        _shell.ShowMessage($"„{station.Name}“ gespeichert." + (warning is null ? "" : " " + warning), warning is not null);
    }
}
