using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using MediaCenter.App.Pages;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App.Controls;

/// <summary>
/// Rechtes Panel der Musik-Seite wie bei Spotify: großes Cover, Titel und Interpret des laufenden Titels, darunter wahlweise die nächsten Titel
/// der Warteschlange oder der Songtext (mitlaufend, wenn er Zeitmarken hat).
/// Es erscheint nur, solange ein Musiktitel läuft (nicht bei Radio und Podcasts) und wenn <see cref="Suppressed"/> nicht gesetzt ist.
/// </summary>
public sealed class NowPlayingPanel : Border
{
    private const int UpcomingShown = 40;
    private const double CoverSize = 268;

    /// <summary>Die Zeile wird ein wenig vor ihrer Zeitmarke hervorgehoben, damit das Auge sie rechtzeitig findet.</summary>
    private const double LyricLead = 0.25;

    private enum Tab
    {
        Queue,
        Lyrics,
    }

    private readonly IShell _shell;
    private readonly AudioController _audio;
    private readonly StackPanel _content = new();
    private readonly ScrollViewer _scroll;
    private readonly List<TextBlock> _lines = new();
    private IReadOnlyList<QueueItem>? _shownQueue;
    private int _shownIndex = -2;
    private bool _suppressed;
    private bool _building;
    private Tab _tab = Tab.Queue;
    private Lyrics? _lyrics;
    private int _line = -1;
    private int _lyricsRequest;

    public NowPlayingPanel(IShell shell)
    {
        _shell = shell;
        _audio = shell.Services.Audio;
        Width = 304;
        CornerRadius = new CornerRadius(12);
        Visibility = Visibility.Collapsed;
        this.SetResourceReference(BackgroundProperty, "BgSoftBrush");
        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = _content,
        };
        Child = _scroll;
        _content.Margin = new Thickness(18, 14, 18, 16);

        Loaded += (_, _) =>
        {
            _audio.Changed += Refresh;
            _audio.Tick += OnTick;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            _audio.Changed -= Refresh;
            _audio.Tick -= OnTick;
        };
    }

    /// <summary>Blendet das Panel aus, wenn das Fenster zu schmal ist.</summary>
    public bool Suppressed
    {
        get => _suppressed;
        set
        {
            if (_suppressed == value) return;
            _suppressed = value;
            Refresh();
        }
    }

    private void Refresh()
    {
        if (_suppressed || _audio is not { IsActive: true, Current: { Kind: QueueKind.Track } current })
        {
            Visibility = Visibility.Collapsed;
            _shownQueue = null;
            _lyricsRequest++; // ein noch laufender Abruf soll nichts mehr anzeigen
            return;
        }
        Visibility = Visibility.Visible;

        // Änderungen von Lautstärke, Pause und Zeit berühren die Anzeige nicht; neu aufbauen nur bei neuem Titel oder neuer Warteschlange.
        if (ReferenceEquals(_shownQueue, _audio.Queue) && _shownIndex == _audio.Index) return;
        _shownQueue = _audio.Queue;
        _shownIndex = _audio.Index;
        Build(current);
    }

    private void Build(QueueItem current)
    {
        _content.Children.Clear();
        _lines.Clear();
        _lyrics = null;
        _line = -1;
        _lyricsRequest++;

        var header = PageParts.Semi("Jetzt läuft", 14);
        header.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        header.Margin = new Thickness(0, 0, 0, 10);
        _content.Children.Add(header);

        // Beim Songtext bekommt der Text den Platz: kein großes Cover.
        if (_tab == Tab.Queue)
        {
            var cover = PageParts.Thumb(ImageCache.Load(current.CoverPath, 600), CoverSize, "\uE8D6");
            cover.CornerRadius = new CornerRadius(8);
            cover.HorizontalAlignment = HorizontalAlignment.Left;
            _content.Children.Add(cover);
        }

        var title = PageParts.Semi(current.Title, _tab == Tab.Queue ? 20 : 17);
        title.TextWrapping = TextWrapping.Wrap;
        title.TextTrimming = TextTrimming.None;
        title.Margin = new Thickness(0, _tab == Tab.Queue ? 14 : 0, 0, 0);
        title.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        _content.Children.Add(title);

        var sub = PageParts.Muted(current.Subtitle, 13);
        sub.Margin = new Thickness(0, 2, 0, 0);
        _content.Children.Add(sub);

        _content.Children.Add(Tabs(current));

        if (_tab == Tab.Lyrics) BuildLyrics(current);
        else BuildQueue();
    }

    private UIElement Tabs(QueueItem current)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        _building = true; // das Setzen von IsChecked beim Aufbau ist keine Wahl des Nutzers
        foreach (var (tab, text) in new[] { (Tab.Queue, "Als Nächstes"), (Tab.Lyrics, "Songtext") })
        {
            var chip = new RadioButton { Content = text, GroupName = "nowplaying-tab", IsChecked = tab == _tab };
            chip.SetResourceReference(StyleProperty, "Chip");
            System.Windows.Automation.AutomationProperties.SetName(chip, $"Jetzt läuft {text}");
            chip.Checked += (_, _) =>
            {
                if (_building || _tab == tab) return;
                _tab = tab;
                if (_audio.Current is { Kind: QueueKind.Track } now) Build(now);
            };
            row.Children.Add(chip);
        }
        _building = false;
        return row;
    }

    // --- Warteschlange -------------------------------------------------------------------------------------

    private void BuildQueue()
    {
        var queue = _audio.Queue;
        var upcoming = Enumerable.Range(_audio.Index + 1, Math.Max(0, queue.Count - _audio.Index - 1)).Take(UpcomingShown).ToList();

        var next = PageParts.Semi("Als Nächstes", 13);
        next.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        next.Margin = new Thickness(0, 14, 0, 4);
        _content.Children.Add(next);

        if (upcoming.Count == 0)
        {
            _content.Children.Add(PageParts.Muted("Das ist der letzte Titel der Warteschlange.", 13));
            return;
        }

        foreach (var index in upcoming)
        {
            var item = queue[index];
            var thumb = PageParts.Thumb(ImageCache.Load(item.CoverPath, 80), 34, "\uE8D6");
            thumb.CornerRadius = new CornerRadius(4);
            var name = PageParts.Semi(item.Title, 13);
            name.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
            var artist = PageParts.Muted(item.Subtitle, 11);
            artist.TextWrapping = TextWrapping.NoWrap;
            artist.TextTrimming = TextTrimming.CharacterEllipsis;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { name, artist } };
            Grid.SetColumn(text, 1);
            grid.Children.Add(thumb);
            grid.Children.Add(text);

            var captured = index;
            var row = PageParts.ClickableRow(grid, async () => await _shell.RunBusyAsync("Wiedergabe startet …", () => _audio.PlayAsync(_audio.Queue, captured)), new Thickness(-8, 0, -8, 0), item.Title);
            row.Padding = new Thickness(8, 4, 8, 4);
            row.CornerRadius = new CornerRadius(8);
            _content.Children.Add(row);
        }
    }

    // --- Songtext ------------------------------------------------------------------------------------------

    private async void BuildLyrics(QueueItem current)
    {
        var host = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        host.Children.Add(PageParts.Muted("Songtext wird gesucht …", 13));
        _content.Children.Add(host);

        var request = _lyricsRequest;
        var model = _shell.Services.Model;
        var track = current.Id is { } id ? model.Tracks.FirstOrDefault(t => t.TrackId == id) : null;
        if (track is null)
        {
            ShowNoLyrics(host, "Zu diesem Titel gibt es keinen Eintrag in der Bibliothek.");
            return;
        }

        Lyrics? lyrics;
        try
        {
            lyrics = await _shell.Services.Lyrics.GetAsync(
                track.TrackId, current.Source, string.IsNullOrWhiteSpace(track.Artist) ? "" : track.Artist, track.Title, track.Album, track.DurationSeconds ?? current.Duration);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            // Dienst nicht erreichbar oder Datei nicht lesbar: dem Nutzer sagen, nicht still leer lassen.
            if (request == _lyricsRequest) ShowNoLyrics(host, $"Der Songtext ließ sich nicht laden: {ex.Message}");
            return;
        }
        if (request != _lyricsRequest) return; // inzwischen läuft ein anderer Titel

        if (lyrics is null)
        {
            ShowNoLyrics(host, _shell.Services.Settings.OnlineLyrics
                ? "Kein Songtext gefunden. Die App erkennt eine .lrc-Datei neben dem Titel, einen Text im Tag und sucht sonst bei LRCLIB."
                : "Kein Songtext gefunden. Die Suche im Internet ist ausgeschaltet (Einstellungen → Bibliothek).");
            return;
        }
        ShowLyrics(host, lyrics);
    }

    private static void ShowNoLyrics(StackPanel host, string message)
    {
        host.Children.Clear();
        host.Children.Add(PageParts.Muted(message, 13));
    }

    private void ShowLyrics(StackPanel host, Lyrics lyrics)
    {
        _lyrics = lyrics;
        host.Children.Clear();
        _lines.Clear();
        foreach (var line in lyrics.Lines)
        {
            var block = new TextBlock { Text = line.Text.Length == 0 ? " " : line.Text, FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            block.SetResourceReference(TextBlock.ForegroundProperty, lyrics.Synced ? "MutedBrush" : "InkBrush");
            host.Children.Add(block);
            _lines.Add(block);
        }

        var source = PageParts.Muted($"Quelle: {lyrics.Source}", 11);
        source.Margin = new Thickness(0, 12, 0, 0);
        host.Children.Add(source);
        OnTick();
    }

    /// <summary>Hebt die Zeile hervor, die gerade gesungen wird, und hält sie in der Mitte des Panels.</summary>
    private void OnTick()
    {
        if (_tab != Tab.Lyrics || _lyrics is not { Synced: true } lyrics) return;
        var index = lyrics.LineAt(_audio.Position + LyricLead);
        if (index == _line) return;

        if (_line >= 0 && _line < _lines.Count)
        {
            _lines[_line].SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            _lines[_line].ClearValue(TextBlock.FontFamilyProperty);
        }
        _line = index;
        if (index < 0 || index >= _lines.Count) return;

        var current = _lines[index];
        current.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        current.SetResourceReference(TextBlock.FontFamilyProperty, "BodySemiFont");
        if (!current.IsLoaded) return;
        var top = current.TransformToAncestor(_content).Transform(new Point(0, 0)).Y + _content.Margin.Top;
        _scroll.ScrollToVerticalOffset(Math.Max(0, top - _scroll.ViewportHeight / 2 + current.ActualHeight / 2));
    }
}
