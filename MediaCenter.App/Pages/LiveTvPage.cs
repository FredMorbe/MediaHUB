using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using MediaCenter.App.Controls;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App.Pages;

/// <summary>
/// Live-TV: Sender aus M3U-Listen (IPTV) mit Suche, Gruppen, Favoriten und, wo ein Programmführer (XMLTV) da ist, „Jetzt“ und „Danach“.
/// Ein Klick auf einen Sender spielt ihn im Player; dort wechseln Bild auf/ab und das Sendermenü zwischen den Sendern der Ansicht.
/// </summary>
public sealed class LiveTvPage : UserControl
{
    /// <summary>So viele Sender zeigt die Liste auf einmal (große Listen haben um die 20 000); „Weitere anzeigen“ holt die nächsten.</summary>
    private const int PageSize = 100;

    /// <summary>So viele Sender der Ansicht stehen im Player zum Umschalten bereit.</summary>
    private const int ZapLimit = 500;

    /// <summary>So viele Gruppen zeigt die Leiste, die übrigen finden sich über die Suche.</summary>
    private const int MaxGroupChips = 60;

    private readonly IShell _shell;
    private readonly LibraryDb _db;
    private readonly StackPanel _content = new();
    private readonly StackPanel _channels = new();
    private readonly Dictionary<long, LiveSource> _sources = new();
    private LiveChannelFilter _filter = new();
    private string _text = "";
    private int _shown;

    public LiveTvPage(IShell shell)
    {
        _shell = shell;
        _db = shell.Services.Db;
        Content = PageParts.Scrolling(_content);
        Build();
    }

    private void Build()
    {
        _content.Children.Clear();
        _channels.Children.Clear();
        _shown = 0;
        _sources.Clear();
        foreach (var source in _db.ListLiveSources()) _sources[source.Id] = source;

        _content.Children.Add(PageParts.Header("Fernsehen", "Live-TV",
            "Sender aus einer M3U-Liste (IPTV), dazu optional ein Programmführer (XMLTV). Die App liefert keine Sender mit: Die Liste kommt von dir oder deinem Anbieter."));

        if (_sources.Count == 0)
        {
            var add = PageParts.Button("Senderliste hinzufügen", "PrimaryButton", (_, _) => LiveSourcePage.Show(_shell, Build));
            AutomationProperties.SetName(add, "Senderliste hinzufügen");
            _content.Children.Add(PageParts.EmptyState("", "Noch keine Senderliste",
                "Füge die Adresse einer M3U-Liste oder eine M3U-Datei hinzu. Die Sender erscheinen danach hier; Favoriten, Suche und Gruppen helfen bei langen Listen.", add));
            return;
        }

        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        var (field, box) = PageParts.HintBox("Sender suchen …", 300);
        AutomationProperties.SetName(box, "Sender suchen");
        box.Text = _text;
        field.Margin = new Thickness(0, 0, 10, 8);
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            _text = box.Text.Trim();
            ApplyFilter();
            e.Handled = true;
        };
        var search = PageParts.Compact(PageParts.Button("Suchen", "PrimaryButton", (_, _) =>
        {
            _text = box.Text.Trim();
            ApplyFilter();
        }, new Thickness(0, 0, 10, 8)));
        var add2 = PageParts.Compact(PageParts.Button("Senderliste hinzufügen", "GhostButton", (_, _) => LiveSourcePage.Show(_shell, Build), new Thickness(0, 0, 10, 8)));
        var update = PageParts.Compact(PageParts.Button("Alles aktualisieren", "GhostButton", async (_, _) => await RefreshAllAsync(), new Thickness(0, 0, 0, 8)));
        AutomationProperties.SetName(add2, "Senderliste hinzufügen");
        AutomationProperties.SetName(update, "Listen und Programmführer aktualisieren");
        toolbar.Children.Add(field);
        toolbar.Children.Add(search);
        toolbar.Children.Add(add2);
        toolbar.Children.Add(update);
        _content.Children.Add(toolbar);

        _content.Children.Add(BuildGroupChips());
        _content.Children.Add(_channels);
        FillChannels();

        _content.Children.Add(PageParts.SectionTitle("Senderlisten", new Thickness(0, 34, 0, 12)));
        foreach (var source in _sources.Values.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)) _content.Children.Add(SourceRow(source));
        _content.Children.Add(PageParts.Muted(
            "Hinweis: Anbieter betten oft Zugangsdaten in die Adressen ein. Die Adressen liegen unverschlüsselt in der Bibliothek (library.db) und damit auch in Sicherungen; die App zeigt sie nie an.", 12));
    }

    // --- Filter --------------------------------------------------------------------------------------------

    private UIElement BuildGroupChips()
    {
        var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        void Chip(string label, string name, bool selected, Action choose)
        {
            var chip = new RadioButton { Content = label, GroupName = "live-groups", IsChecked = selected };
            chip.SetResourceReference(StyleProperty, "Chip");
            chip.Margin = new Thickness(0, 0, 8, 8);
            AutomationProperties.SetName(chip, name);
            chip.Checked += (_, _) =>
            {
                choose();
                ApplyFilter();
            };
            chips.Children.Add(chip);
        }

        var favorites = _db.CountLiveChannels(new LiveChannelFilter(FavoritesOnly: true));
        var all = _filter is { Group: null, FavoritesOnly: false };
        Chip("Alle", "Gruppe Alle", all, () => _filter = new LiveChannelFilter());
        Chip($"★ Favoriten ({favorites})", "Gruppe Favoriten", _filter.FavoritesOnly, () => _filter = new LiveChannelFilter(FavoritesOnly: true));
        var groups = _db.ListLiveGroups();
        foreach (var group in groups.Take(MaxGroupChips))
        {
            var name = group.Name.Length == 0 ? "Ohne Gruppe" : group.Name;
            var selected = !_filter.FavoritesOnly && _filter.Group is { } current && string.Equals(current, group.Name, StringComparison.OrdinalIgnoreCase);
            Chip($"{name} ({group.Count})", $"Gruppe {name}", selected, () => _filter = new LiveChannelFilter(Group: group.Name));
        }
        if (groups.Count > MaxGroupChips) chips.Children.Add(PageParts.Muted($"… und {groups.Count - MaxGroupChips} weitere Gruppen (über die Suche erreichbar)", 12));

        var scroll = new ScrollViewer { MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = chips };
        return scroll;
    }

    private void ApplyFilter()
    {
        _shown = 0;
        _filter = _filter with { Text = _text.Length == 0 ? null : _text };
        _channels.Children.Clear();
        FillChannels();
    }

    // --- Sender --------------------------------------------------------------------------------------------

    private void FillChannels()
    {
        var filter = _filter with { Text = _text.Length == 0 ? null : _text };
        var total = _db.CountLiveChannels(filter);
        var batch = _db.ListLiveChannels(filter, PageSize, _shown);
        _shown += batch.Count;

        // Den Kopf (Anzahl) und den „Weitere“-Knopf neu setzen, die Zeilen dazwischen bleiben.
        _channels.Children.Cast<UIElement>().Where(e => e is FrameworkElement { Tag: "live-footer" or "live-header" }).ToList().ForEach(e => _channels.Children.Remove(e));
        var header = PageParts.Muted(total == 0 ? "Keine Sender gefunden." : $"{total} Sender", 14);
        header.Tag = "live-header";
        header.Margin = new Thickness(0, 4, 0, 8);
        _channels.Children.Insert(0, header);

        var now = DateTime.UtcNow;
        foreach (var channel in batch) _channels.Children.Add(Row(channel, now));

        if (_shown < total)
        {
            var more = PageParts.Button($"Weitere anzeigen ({total - _shown} übrig)", "GhostButton", (_, _) => FillChannels(), new Thickness(0, 6, 0, 0));
            more.Tag = "live-footer";
            AutomationProperties.SetName(more, "Weitere Sender anzeigen");
            _channels.Children.Add(more);
        }
    }

    private UIElement Row(LiveChannel channel, DateTime nowUtc)
    {
        var thumb = PageParts.Thumb(null, 52, "");
        ApplyLogo(thumb, channel.LogoUrl);

        var name = PageParts.Semi(channel.Number is { } n ? $"{n}  {channel.Name}" : channel.Name, 16);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 14, 0), Children = { name } };
        var line = NowNextText(channel, nowUtc) ?? channel.Group;
        if (line is not null)
        {
            var meta = PageParts.Muted(line, 13);
            meta.TextTrimming = TextTrimming.CharacterEllipsis;
            meta.TextWrapping = TextWrapping.NoWrap;
            text.Children.Add(meta);
        }

        var star = new Button { Content = channel.Favorite ? "" : "", ToolTip = channel.Favorite ? "Aus den Favoriten entfernen" : "Zu den Favoriten" };
        star.SetResourceReference(StyleProperty, "GhostButton");
        star.SetResourceReference(FontFamilyProperty, "IconFont");
        star.Padding = new Thickness(11, 8, 11, 8);
        star.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(star, $"{(channel.Favorite ? "Favorit entfernen" : "Favorit")} {channel.Name}");
        star.Click += (_, _) =>
        {
            _db.SetLiveFavorite(channel.Id, !channel.Favorite);
            Build();
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(star, 2);
        grid.Children.Add(thumb);
        grid.Children.Add(text);
        grid.Children.Add(star);

        var row = PageParts.ClickableRow(grid, () => _ = PlayAsync(channel), new Thickness(0, 0, 0, 6), $"Sender {channel.Name}");
        row.ToolTip = "Abspielen";
        return row;
    }

    /// <summary>„Jetzt: Tagesschau (bis 20:15) · Danach: Wetter“; null, wenn der Sender keinen Eintrag im Programmführer hat.</summary>
    private string? NowNextText(LiveChannel channel, DateTime nowUtc)
    {
        if (channel.EpgId is null) return null;
        var (now, next) = _db.GetLiveNowNext(channel.SourceId, channel.EpgId, nowUtc);
        if (now is null && next is null) return null;

        var parts = new List<string>();
        if (now is not null) parts.Add($"Jetzt: {now.Title} (bis {now.StopUtc.ToLocalTime():HH:mm})");
        if (next is not null) parts.Add($"Danach: {next.Title}");
        return string.Join("  ·  ", parts);
    }

    private async Task PlayAsync(LiveChannel channel)
    {
        var filter = _filter with { Text = _text.Length == 0 ? null : _text };
        var zap = _db.ListLiveChannels(filter, ZapLimit);
        if (zap.All(c => c.Id != channel.Id)) zap.Insert(0, channel);
        try
        {
            await _shell.PlayLiveAsync(channel, zap);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _shell.ShowMessage($"Der Sender „{channel.Name}“ lässt sich nicht starten: {ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// Legt das Logo über das Fernsehsymbol, sobald es geladen ist. Logos sind kleine Bilder im Netz; fehlt eines oder ist es kaputt, bleibt das Symbol
    /// (ein fehlendes Logo ist kein Fehler).
    /// </summary>
    private static void ApplyLogo(Border thumb, string? url)
    {
        if (url is null) return;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = HttpFetch.RequireWebUri(url);
            image.DecodePixelWidth = 96;
            image.EndInit();

            void Show()
            {
                thumb.Background = new System.Windows.Media.ImageBrush(image) { Stretch = System.Windows.Media.Stretch.Uniform };
                thumb.Child = null;
            }
            if (!image.IsDownloading) Show();
            else image.DownloadCompleted += (_, _) => Show();
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException)
        {
            // bleibt beim Symbol
        }
    }

    // --- Listen verwalten ----------------------------------------------------------------------------------

    private UIElement SourceRow(LiveSource source)
    {
        var name = PageParts.Semi(source.Name, 16);
        var details = new List<string> { $"{source.ChannelCount} Sender" };
        details.Add(source.RefreshedUtc is { } refreshed ? $"geladen {refreshed.ToLocalTime():dd.MM.yyyy HH:mm}" : "noch nicht geladen");
        details.Add(source.EpgLocation is null
            ? "kein Programmführer"
            : source.GuideRefreshedUtc is { } guide ? $"Programmführer {guide.ToLocalTime():dd.MM.yyyy HH:mm}" : "Programmführer noch nicht geladen");
        var meta = PageParts.Muted(string.Join(" · ", details), 13);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name, meta } };

        var refresh = PageParts.Compact(PageParts.Button("Aktualisieren", "GhostButton", async (_, _) => await RefreshAsync(source), new Thickness(0, 0, 8, 0)));
        var guideButton = PageParts.Compact(PageParts.Button("Programmführer …", "GhostButton", (_, _) => GuidePage.Show(_shell, source, Build), new Thickness(0, 0, 8, 0)));
        var remove = PageParts.Compact(PageParts.Button("Entfernen", "GhostButton", (_, _) => Remove(source), new Thickness(0)));
        AutomationProperties.SetName(refresh, $"Liste aktualisieren {source.Name}");
        AutomationProperties.SetName(guideButton, $"Programmführer {source.Name}");
        AutomationProperties.SetName(remove, $"Liste entfernen {source.Name}");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { refresh, guideButton, remove } };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(text);
        grid.Children.Add(buttons);
        return PageParts.Card(grid, new Thickness(0, 0, 0, 8));
    }

    private void Remove(LiveSource source)
    {
        if (!_shell.Confirm("Senderliste entfernen", $"„{source.Name}“ mit ihren {source.ChannelCount} Sendern, Favoriten und dem Programmführer aus dem Mediacenter entfernen? Die Liste beim Anbieter bleibt unberührt.", "Entfernen", danger: true)) return;
        _db.RemoveLiveSource(source.Id);
        _filter = new LiveChannelFilter();
        _text = "";
        Build();
    }

    private async Task RefreshAsync(LiveSource source)
    {
        LiveRefresh? refresh = null;
        LiveGuideRefresh? guide = null;
        string? guideProblem = null;
        await _shell.RunBusyAsync($"„{source.Name}“ wird geladen …", async () =>
        {
            refresh = await _shell.Services.LiveTv.RefreshSourceAsync(source.Id);
            if (_db.GetLiveSource(source.Id)?.EpgLocation is null) return;
            try
            {
                guide = await _shell.Services.LiveTv.RefreshGuideAsync(source.Id);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or System.Xml.XmlException or TaskCanceledException)
            {
                guideProblem = ex.Message; // die Sender sind da; der Führer darf fehlen
            }
        });
        Build();
        if (refresh is null) return;

        var text = $"„{source.Name}“: {refresh.Total} Sender ({refresh.Added} neu, {refresh.Removed} entfernt).";
        if (guide is not null) text += $" Programmführer: {guide.WithGuide} Sender mit Programm.";
        _shell.ShowMessage(text + (guideProblem is null ? "" : $" Programmführer nicht geladen: {guideProblem}"), guideProblem is not null);
    }

    private async Task RefreshAllAsync()
    {
        foreach (var source in _db.ListLiveSources()) await RefreshAsync(source);
    }
}

/// <summary>Fenster „Senderliste hinzufügen“: Adresse oder Datei, optional Name und Programmführer. Fehler stehen im Fenster, es bleibt offen.</summary>
public sealed class LiveSourcePage : UserControl, IModalContent
{
    private readonly IShell _shell;
    private readonly TextBlock _status = PageParts.Muted("", 13);
    private bool _busy;

    public string? BlockClose => _busy ? "Die Liste wird gerade geladen." : null;

    public static void Show(IShell shell, Action added) =>
        shell.OpenModal("Live-TV", "Senderliste hinzufügen", new LiveSourcePage(shell, added), ModalSize.Medium);

    private LiveSourcePage(IShell shell, Action added)
    {
        _shell = shell;

        var (addressField, address) = PageParts.HintBox("Adresse der M3U-Liste (http, https) oder Datei", 460);
        var (nameField, name) = PageParts.HintBox("Name (optional)", 460);
        var (guideField, guide) = PageParts.HintBox("Adresse oder Datei des Programmführers (XMLTV, optional)", 460);
        AutomationProperties.SetName(address, "Adresse der Senderliste");
        AutomationProperties.SetName(name, "Name der Senderliste");
        AutomationProperties.SetName(guide, "Adresse des Programmführers");
        foreach (var field in new[] { addressField, nameField, guideField })
        {
            field.Margin = new Thickness(0, 0, 0, 10);
            field.HorizontalAlignment = HorizontalAlignment.Left; // sonst steht der Hinweistext links neben dem schmaleren Feld
        }

        var pick = PageParts.Compact(PageParts.Button("Datei wählen …", "GhostButton", (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Senderliste wählen",
                Filter = "Senderliste (*.m3u;*.m3u8;*.gz)|*.m3u;*.m3u8;*.gz|Alle Dateien (*.*)|*.*",
                CheckFileExists = true,
            };
            if (dialog.ShowDialog() == true) address.Text = dialog.FileName;
        }, new Thickness(0, 0, 10, 0)));
        AutomationProperties.SetName(pick, "Datei wählen");

        _status.Margin = new Thickness(0, 10, 0, 0);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Accent2Brush");

        async Task CommitAsync()
        {
            if (_busy) return;
            if (address.Text.Trim().Length == 0)
            {
                _status.Text = "Bitte die Adresse oder Datei der Senderliste angeben.";
                return;
            }

            _busy = true;
            _status.Text = "Die Liste wird geladen …";
            try
            {
                var (source, refresh) = await _shell.Services.LiveTv.AddSourceAsync(name.Text, address.Text, guide.Text);
                _shell.CloseModal();
                added();
                _shell.ShowMessage($"„{source.Name}“ hinzugefügt: {refresh.Total} Sender"
                    + (refresh.Skipped > 0 ? $" ({refresh.Skipped} {(refresh.Skipped == 1 ? "Eintrag" : "Einträge")} mit nicht abspielbarer Adresse übersprungen)." : "."));
                if (source.EpgLocation is not null) _ = LoadGuideAsync(source);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or ArgumentException or InvalidOperationException or IOException or TaskCanceledException)
            {
                _status.Text = ex.Message;
            }
            finally
            {
                _busy = false;
            }
        }

        async Task LoadGuideAsync(LiveSource source)
        {
            try
            {
                var result = await shell.Services.LiveTv.RefreshGuideAsync(source.Id);
                shell.ShowMessage($"Programmführer geladen: {result.WithGuide} von {result.Channels} Sendern haben Programm.");
                added();
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or System.Xml.XmlException or TaskCanceledException)
            {
                shell.ShowMessage($"Programmführer nicht geladen: {ex.Message}", isError: true);
            }
        }

        var ok = PageParts.Button("Hinzufügen", "PrimaryButton", async (_, _) => await CommitAsync(), new Thickness(0));
        AutomationProperties.SetName(ok, "Senderliste hinzufügen bestätigen");
        foreach (var box in new[] { address, name, guide })
        {
            box.KeyDown += async (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                await CommitAsync();
            };
        }

        var note = PageParts.Muted(
            "Eine Liste enthält Adressen von Streams. Der Anbieter sieht, welche du abspielst. Nenne die Liste einen Programmführer, lädt die App ihn gleich mit.", 12);
        note.Margin = new Thickness(0, 0, 0, 12);
        Content = new StackPanel
        {
            Children =
            {
                note, addressField,
                new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10), Children = { pick } },
                nameField, guideField, _status,
                new StackPanel { Margin = new Thickness(0, 14, 0, 0), Children = { ok } },
            },
        };
        Loaded += (_, _) => address.Focus();
    }
}

/// <summary>Fenster „Programmführer“: Adresse ändern und neu laden.</summary>
public sealed class GuidePage : UserControl, IModalContent
{
    private bool _busy;

    public string? BlockClose => _busy ? "Der Programmführer wird gerade geladen." : null;

    public static void Show(IShell shell, LiveSource source, Action changed) =>
        shell.OpenModal("Live-TV", $"Programmführer: {source.Name}", new GuidePage(shell, source, changed), ModalSize.Medium);

    private GuidePage(IShell shell, LiveSource source, Action changed)
    {
        var (field, box) = PageParts.HintBox("Adresse oder Datei des Programmführers (XMLTV, auch .gz)", 460);
        box.Text = source.EpgLocation ?? "";
        AutomationProperties.SetName(box, "Adresse des Programmführers");
        field.Margin = new Thickness(0, 0, 0, 10);
        field.HorizontalAlignment = HorizontalAlignment.Left;
        var status = PageParts.Muted("", 13);
        status.SetResourceReference(TextBlock.ForegroundProperty, "Accent2Brush");

        async Task LoadAsync()
        {
            if (_busy) return;
            var location = box.Text.Trim();
            _busy = true;
            try
            {
                if (location.Length == 0)
                {
                    shell.Services.Db.SetLiveGuideLocation(source.Id, null);
                    shell.Services.Db.SetLiveEpgIds(source.Id, new Dictionary<long, string?>());
                    shell.Services.Db.ReplaceLiveGuide(source.Id, Array.Empty<XmltvProgramme>());
                    shell.CloseModal();
                    changed();
                    shell.ShowMessage("Programmführer entfernt.");
                    return;
                }
                if (!(Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") && !File.Exists(location))
                {
                    status.Text = "Das ist weder eine Web-Adresse (http oder https) noch eine vorhandene Datei.";
                    return;
                }

                status.Text = "Der Programmführer wird geladen und den Sendern zugeordnet …";
                shell.Services.Db.SetLiveGuideLocation(source.Id, location);
                var result = await shell.Services.LiveTv.RefreshGuideAsync(source.Id);
                shell.CloseModal();
                changed();
                shell.ShowMessage($"Programmführer geladen: {result.WithGuide} von {result.Channels} Sendern haben Programm, {result.Programmes} Sendungen.");
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or System.Xml.XmlException or TaskCanceledException or InvalidOperationException)
            {
                status.Text = ex.Message;
            }
            finally
            {
                _busy = false;
            }
        }

        var ok = PageParts.Button("Laden", "PrimaryButton", async (_, _) => await LoadAsync(), new Thickness(0, 14, 0, 0));
        AutomationProperties.SetName(ok, "Programmführer laden");
        box.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await LoadAsync();
        };
        var note = PageParts.Muted("Ein leeres Feld entfernt den Programmführer. Zugeordnet wird über die „tvg-id“ der Liste, sonst über den Sendernamen (ohne „HD“ und Länderkürzel).", 12);
        note.Margin = new Thickness(0, 0, 0, 12);
        Content = new StackPanel { Children = { note, field, status, ok } };
        Loaded += (_, _) => box.Focus();
    }
}
