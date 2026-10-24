using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using MediaCenter.App.Controls;
using MediaCenter.App.Services;
using MediaCenter.Core;
using Microsoft.Win32;

namespace MediaCenter.App.Pages;

/// <summary>Film oder Serie: Beschreibung, Genres bearbeiten, abspielen, bei Serien die Folgen nach Staffel.</summary>
public partial class DetailPage : UserControl
{
    private readonly IShell _shell;
    private readonly LibraryModel _model;
    private readonly TitleRow _title;

    public DetailPage(IShell shell, long titleId)
    {
        InitializeComponent();
        _shell = shell;
        _model = shell.Services.Model;
        _title = _model.FindTitle(titleId) ?? throw new InvalidOperationException($"Titel {titleId} existiert nicht (mehr).");

        SizeChanged += (_, _) => HeroZone.MinHeight = Math.Max(460, ActualHeight * 0.52);
        BackButton.Click += (_, _) => _shell.Back();

        Kicker.Text = _title.Kind == TitleKind.Movie ? "FILM" : "SERIE";
        TitleText.Text = _title.Name;
        MetaText.Text = BuildMeta();
        BuildPoster();
        BuildInfo();
        BuildCollections();
        BuildGenres();
        BuildPlayButton();
        BuildListButton();
        BuildWatchedButton();
        BuildEditButton();
        if (_title.Kind == TitleKind.Series) BuildEpisodes();

        _shell.SetBackdrop(_model.ArtPath(ArtworkOwner.Title, _title.Id, ArtworkKind.Landscape));
    }

    private string BuildMeta()
    {
        var basic = _model.MetaOf(_title);
        if (_title.Kind != TitleKind.Movie || _model.VideosOf(_title.Id).FirstOrDefault() is not { } video) return basic;

        var tech = new List<string>();
        if (video.Height is { } height) tech.Add($"{height}p");
        if (video.VideoCodec is { } codec) tech.Add(codec.ToUpperInvariant());
        if (video.IsHdr) tech.Add("HDR");
        return string.Join(" · ", new[] { basic }.Concat(tech).Where(s => s.Length > 0));
    }

    /// <summary>Das Poster links neben Titel und Beschreibung; ohne Poster fehlt die Spalte (das Querformat-Bild bleibt der Hintergrund).</summary>
    private void BuildPoster()
    {
        var image = ImageCache.Load(_model.ArtPath(ArtworkOwner.Title, _title.Id, ArtworkKind.Poster), 480);
        if (image is null) return;

        PosterFrame.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        PosterFrame.Visibility = Visibility.Visible;
    }

    /// <summary>Handlung, Wertung, Regie und Besetzung aus dem Internet; ohne gespeicherte Infotexte bleibt der Platz leer.</summary>
    private void BuildInfo()
    {
        if (_model.Db.GetTitleInfo(_title.Id) is not { } info) return;

        var source = info.Provider switch { "omdb" => "OMDb", "tvmaze" => "TVmaze", _ => info.Provider };
        // OMDb liefert die IMDb-Wertung, TVmaze die eigene der Nutzer dort.
        var credits = new List<string>();
        if (info.Rating is { } rating) credits.Add($"{(info.Provider == "omdb" ? "IMDb" : source)} {rating.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture)}");
        if (info.Director is { } director) credits.Add($"Regie: {director}");

        ShowText(PlotText, info.Plot);
        CreditsText.Inlines.Clear();
        CreditsText.Inlines.Add(new Run(string.Join(" · ", credits)));
        if (info.Cast is { } cast)
        {
            if (credits.Count > 0) CreditsText.Inlines.Add(new Run(" · "));
            CreditsText.Inlines.Add(new Run("Besetzung: "));
            var actors = LibrarySearch.Actors(cast).ToList();
            for (var i = 0; i < Math.Min(actors.Count, MaxActorsShown); i++)
            {
                if (i > 0) CreditsText.Inlines.Add(new Run(", "));
                CreditsText.Inlines.Add(ActorLink(actors[i]));
            }
            if (actors.Count > MaxActorsShown) CreditsText.Inlines.Add(new Run(" …"));
        }
        CreditsText.Visibility = CreditsText.Inlines.Count > 0 && (credits.Count > 0 || info.Cast is not null) ? Visibility.Visible : Visibility.Collapsed;

        if (PlotText.Visibility == Visibility.Visible || CreditsText.Visibility == Visibility.Visible || _model.Db.GetEpisodeInfos(_title.Id).Count > 0)
        {
            ShowText(SourceText, info.UserEdited
                ? $"Handlung, Regie und Besetzung selbst geschrieben; Wertung und Folgentexte von {source}"
                : $"Infotexte von {source}, auf Englisch");
        }
    }

    /// <summary>„Sammlung: Matrix“ als Link, wenn der Film in einer Sammlung steht.</summary>
    private void BuildCollections()
    {
        var collections = _model.CollectionsOf(_title.Id).ToList();
        CollectionText.Inlines.Clear();
        if (collections.Count == 0)
        {
            CollectionText.Visibility = Visibility.Collapsed;
            return;
        }
        CollectionText.Inlines.Add(new Run(collections.Count == 1 ? "Sammlung: " : "Sammlungen: "));
        for (var i = 0; i < collections.Count; i++)
        {
            if (i > 0) CollectionText.Inlines.Add(new Run(", "));
            var id = collections[i].Id;
            var link = new Hyperlink(new Run(collections[i].Name)) { TextDecorations = null, Cursor = Cursors.Hand, ToolTip = "Sammlung öffnen" };
            link.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
            link.Click += (_, _) => _shell.Open(() => new CollectionPage(_shell, id));
            CollectionText.Inlines.Add(link);
        }
        CollectionText.Visibility = Visibility.Visible;
    }

    private const int MaxActorsShown = 8;

    /// <summary>Ein Schauspieler als Link: ein Klick sucht alle Filme und Serien mit ihm oder ihr.</summary>
    private Hyperlink ActorLink(string name)
    {
        var link = new Hyperlink(new Run(name)) { TextDecorations = null, Cursor = Cursors.Hand, ToolTip = $"Filme und Serien mit {name} suchen" };
        link.SetResourceReference(TextElement.ForegroundProperty, "MutedBrush");
        link.MouseEnter += (_, _) =>
        {
            link.SetResourceReference(TextElement.ForegroundProperty, "InkBrush");
            link.TextDecorations = System.Windows.TextDecorations.Underline;
        };
        link.MouseLeave += (_, _) =>
        {
            link.SetResourceReference(TextElement.ForegroundProperty, "MutedBrush");
            link.TextDecorations = null;
        };
        link.Click += (_, _) => _shell.OpenSearch(name);
        return link;
    }

    private static void ShowText(TextBlock block, string? text)
    {
        block.Text = text ?? "";
        block.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    // --- Abspielen -----------------------------------------------------------------------------------------

    private void BuildPlayButton()
    {
        var target = PlayTarget.For(_model, _title.Id);
        if (target is null)
        {
            PlayButton.IsEnabled = false;
            return;
        }

        var (video, resume) = target.Value;
        PlayButton.Content = resume ? "Fortsetzen"
            : _title.Kind == TitleKind.Series && video.Season is { } s && video.Episode is { } e ? $"S{s:00}E{e:00} abspielen"
            : "Abspielen";
        PlayButton.Click += async (_, _) => await _shell.PlayAsync(video, resume);

        if (!_model.IsOnline(_title.Id))
        {
            OfflineText.Text = "Gerade nicht erreichbar (Laufwerk abgesteckt?)";
        }
    }

    // --- Gesehen -------------------------------------------------------------------------------------------

    private void BuildWatchedButton()
    {
        PageParts.Compact(WatchedButton);
        UpdateWatchedButton();
        WatchedButton.Click += (_, _) =>
        {
            LibraryActions.SetTitleWatched(_shell, _title.Id, _model.WatchStateOf(_title.Id) != WatchState.Watched);
            UpdateWatchedButton();
            if (_title.Kind == TitleKind.Series) BuildEpisodes();
        };
    }

    private void UpdateWatchedButton()
    {
        var watched = _model.WatchStateOf(_title.Id) == WatchState.Watched;
        WatchedButton.Content = _title.Kind == TitleKind.Movie
            ? (watched ? "✓ Gesehen" : "Als gesehen markieren")
            : (watched ? "✓ Alles gesehen" : "Alles als gesehen markieren");
        WatchedButton.ToolTip = watched ? "Als ungesehen markieren" : "Als gesehen markieren";
    }

    private void BuildEditButton()
    {
        PageParts.Compact(EditButton);
        EditButton.Click += (_, _) => OpenEditModal();
    }

    // --- Meine Liste ---------------------------------------------------------------------------------------

    private void BuildListButton()
    {
        void Update() => ListButton.Content = _model.IsOnWatchlist(_title.Id) ? "✓ Auf meiner Liste" : "+ Meine Liste";
        Update();
        ListButton.Click += (_, _) =>
        {
            LibraryActions.ToggleWatchlist(_shell, _title.Id);
            Update();
        };
    }

    // --- Genres --------------------------------------------------------------------------------------------

    /// <summary>Die Genres des Titels; ein Klick entfernt eins. Hinzufügen und alles Weitere liegt im Fenster hinter „Bearbeiten“.</summary>
    private void BuildGenres()
    {
        GenrePanel.Children.Clear();
        foreach (var genre in _model.GenresOf(_title.Id))
        {
            var chip = new ToggleButton { Content = $"{genre}  ✕", IsChecked = true, ToolTip = "Genre entfernen" };
            chip.SetResourceReference(StyleProperty, "Chip");
            chip.Click += (_, _) => ChangeGenres(() => _model.Db.RemoveGenre(_title.Id, genre));
            GenrePanel.Children.Add(chip);
        }
        GenrePanel.Visibility = GenrePanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Alle bekannten Genres als Schalter (an = hat der Titel) und ein Feld für ein neues.</summary>
    private void BuildGenreEditor(WrapPanel panel)
    {
        panel.Children.Clear();
        var mine = _model.GenresOf(_title.Id);

        foreach (var genre in _model.AllGenres().Union(mine, StringComparer.OrdinalIgnoreCase))
        {
            var has = mine.Contains(genre, StringComparer.OrdinalIgnoreCase);
            var chip = new ToggleButton { Content = genre, IsChecked = has, ToolTip = has ? "Genre entfernen" : "Genre hinzufügen" };
            chip.SetResourceReference(StyleProperty, "Chip");
            chip.Click += (_, _) => ChangeGenres(() =>
            {
                if (has) _model.Db.RemoveGenre(_title.Id, genre);
                else _model.Db.AddGenre(_title.Id, genre);
            });
            panel.Children.Add(chip);
        }

        var input = new TextBox { Width = 170, Padding = new Thickness(12, 5, 12, 5), ToolTip = "Neues Genre, mit Enter bestätigen" };
        AutomationProperties.SetName(input, "Neues Genre");
        var hint = new TextBlock { Text = "Neues Genre …", FontSize = 14, Margin = new Thickness(14, 0, 0, 0), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
        hint.SetResourceReference(StyleProperty, "MutedText");
        input.TextChanged += (_, _) => hint.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var field = new Grid { Margin = new Thickness(0, 0, 8, 8), Children = { input, hint } };
        input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(input.Text)) return;
            var genre = input.Text.Trim();
            ChangeGenres(() => _model.Db.AddGenre(_title.Id, genre));
            e.Handled = true;
        };
        panel.Children.Add(field);
    }

    private WrapPanel? _genreEditor;

    private void ChangeGenres(Action change)
    {
        change();
        _model.Reload();
        BuildGenres();
        if (_genreEditor is not null) BuildGenreEditor(_genreEditor);
    }

    // --- Bearbeiten ----------------------------------------------------------------------------------------

    /// <summary>Genres, Cover und Verwaltung des Titels in einem Fenster statt als Reihe von Knöpfen auf der Seite.</summary>
    private void OpenEditModal()
    {
        _genreEditor = new WrapPanel();
        BuildGenreEditor(_genreEditor);

        var covers = new WrapPanel
        {
            Children =
            {
                EditAction("Poster ersetzen", () => LibraryActions.ReplaceArtworkAsync(_shell, _title.Id, ArtworkKind.Poster)),
                EditAction("Poster auf IMDb suchen", () =>
                {
                    LibraryActions.SearchPosterOnline(_shell, _title.Id);
                    return Task.CompletedTask;
                }),
                EditAction("Hintergrund ersetzen", () => LibraryActions.ReplaceArtworkAsync(_shell, _title.Id, ArtworkKind.Landscape)),
                EditAction("Automatische Cover wiederherstellen", ResetCoversAsync),
            },
        };

        var manage = new WrapPanel
        {
            Children =
            {

                EditAction("Umbenennen …", () =>
                {
                    LibraryActions.RenameTitle(_shell, _title.Id);
                    return Task.CompletedTask;
                }),
            },
        };
        if (_title.Kind == TitleKind.Movie)
        {
            manage.Children.Add(EditAction("Zu Sammlung hinzufügen …", () =>
            {
                CollectionPickerPage.Show(_shell, _title.Id);
                return Task.CompletedTask;
            }));
        }
        if (_title.Kind == TitleKind.Movie && _model.VideosOf(_title.Id).FirstOrDefault() is { } movie && MkvEditor.CanEdit(movie.RelPath))
        {
            var edit = EditAction("MKV bearbeiten …", () =>
            {
                LibraryActions.EditVideo(_shell, movie);
                return Task.CompletedTask;
            });
            AutomationProperties.SetName(edit, "MKV bearbeiten");
            manage.Children.Add(edit);
        }
        manage.Children.Add(EditAction("Aus Mediacenter entfernen …", () =>
        {
            if (LibraryActions.RemoveTitle(_shell, _title.Id)) _shell.Back(); // den Titel gibt es nicht mehr: zurück zur Liste
            return Task.CompletedTask;
        }));

        var stack = new StackPanel();
        stack.Children.Add(EditBlock("Genres", _genreEditor));
        stack.Children.Add(PageParts.Divider(new Thickness(0, 20, 0, 20)));
        stack.Children.Add(EditBlock("Infotext", InfoEditor()));
        stack.Children.Add(PageParts.Divider(new Thickness(0, 20, 0, 20)));
        stack.Children.Add(EditBlock("Cover", covers));
        stack.Children.Add(PageParts.Divider(new Thickness(0, 20, 0, 20)));
        stack.Children.Add(EditBlock("Titel", manage));

        _shell.OpenModal(_title.Kind == TitleKind.Movie ? "Film bearbeiten" : "Serie bearbeiten", _title.Name, new UserControl { Content = stack }, ModalSize.Medium);
    }

    /// <summary>Handlung, Regie und Besetzung von Hand schreiben oder aus dem Internet neu holen.</summary>
    private UIElement InfoEditor()
    {
        var info = _model.Db.GetTitleInfo(_title.Id);

        TextBox Field(string name, string? text, bool multiline)
        {
            var box = new TextBox { Text = text ?? "", FontSize = 14, Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 10) };
            if (multiline)
            {
                box.AcceptsReturn = true;
                box.TextWrapping = TextWrapping.Wrap;
                box.MinHeight = 110;
                box.MaxHeight = 220;
                box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                box.VerticalContentAlignment = VerticalAlignment.Top;
            }
            AutomationProperties.SetName(box, name);
            return box;
        }
        TextBlock Label(string text)
        {
            var label = PageParts.Muted(text, 13);
            label.Margin = new Thickness(2, 0, 0, 4);
            return label;
        }

        var plot = Field("Handlung", info?.Plot, multiline: true);
        var director = Field("Regie", info?.Director, multiline: false);
        var cast = Field("Besetzung", info?.Cast, multiline: false);

        var save = PageParts.Button("Speichern", "PrimaryButton", (_, _) =>
        {
            _model.Db.SetUserTitleInfo(_title.Id, plot.Text, director.Text, cast.Text);
            _shell.CloseModal();
            _shell.ReloadView();
            _shell.ShowMessage("Infotext gespeichert.");
        }, new Thickness(0, 0, 10, 0));
        AutomationProperties.SetName(save, "Infotext speichern");
        var refetch = PageParts.Button("Online neu holen", "GhostButton", async (_, _) => await RefetchInfoAsync(info), new Thickness(0));
        AutomationProperties.SetName(refetch, "Infotext online neu holen");

        var note = PageParts.Muted(_title.Kind == TitleKind.Movie
            ? "Die Texte kommen von OMDb, auf Englisch. Leere Felder beim Speichern holen sie beim nächsten Durchlauf wieder. Namen der Besetzung mit Komma trennen: Nach ihnen sucht die Suche."
            : "Die Texte kommen von TVmaze, auf Englisch. Leere Felder beim Speichern holen sie beim nächsten Durchlauf wieder. Namen der Besetzung mit Komma trennen: Nach ihnen sucht die Suche.", 12);
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(0, 12, 0, 0);

        return new StackPanel
        {
            Children =
            {
                Label("Handlung"), plot, Label("Regie"), director, Label("Besetzung"), cast,
                new StackPanel { Orientation = Orientation.Horizontal, Children = { save, refetch } },
                note,
            },
        };
    }

    private async Task RefetchInfoAsync(TitleInfoRow? info)
    {
        if (info is { UserEdited: true }
            && !_shell.Confirm("Eigenen Text ersetzen?", "Handlung, Regie und Besetzung wurden von dir geschrieben. Die Texte aus dem Internet ersetzen sie.", "Ersetzen"))
        {
            return;
        }

        _shell.CloseModal();
        InfoRefreshResult? result = null;
        await _shell.RunBusyAsync("Infotexte werden geholt …", async () => result = await _model.Artwork.RefreshInfoAsync(_title.Id));
        _shell.ReloadView();
        if (result is { Found: true }) _shell.ShowMessage("Infotexte aktualisiert.");
        else _shell.ShowMessage($"Keine Infotexte geholt: {result?.Problem}", isError: true);
    }

    private static UIElement EditBlock(string title, UIElement content)
    {
        var header = new TextBlock { Text = title, FontSize = 26, Margin = new Thickness(0, 0, 0, 10) };
        header.SetResourceReference(StyleProperty, "DisplayText");
        return new StackPanel { Children = { header, content } };
    }

    /// <summary>Aktion im Bearbeiten-Fenster: schließt es zuerst, damit die Aktion (Dateiauswahl, Rückfrage, anderes Fenster) allein steht.</summary>
    private Button EditAction(string text, Func<Task> action)
    {
        var button = PageParts.Compact(new Button { Content = text, Margin = new Thickness(0, 0, 8, 8) });
        button.SetResourceReference(StyleProperty, "GhostButton");
        button.Click += async (_, _) =>
        {
            _shell.CloseModal();
            await action();
        };
        return button;
    }

    private async Task ResetCoversAsync()
    {
        _model.Artwork.ClearArtwork(ArtworkOwner.Title, _title.Id, ArtworkKind.Poster);
        _model.Artwork.ClearArtwork(ArtworkOwner.Title, _title.Id, ArtworkKind.Landscape);
        await _shell.RefreshArtworkAsync();
    }

    // --- Folgen --------------------------------------------------------------------------------------------

    private Dictionary<(int Season, int Episode), EpisodeText> _episodeInfo = new();

    private void BuildEpisodes()
    {
        Episodes.Children.Clear();
        _episodeInfo = _model.Db.GetEpisodeInfos(_title.Id).ToDictionary(e => (e.Season, e.Episode));
        var playback = _model.Db.GetAllPlayback();
        foreach (var season in _model.VideosOf(_title.Id).GroupBy(v => v.Season).OrderBy(g => g.Key ?? int.MaxValue))
        {
            var header = new TextBlock
            {
                Text = season.Key is { } number ? $"Staffel {number}" : "Folgen",
                FontSize = 38,
                Margin = new Thickness(0, 14, 0, 8),
            };
            header.SetResourceReference(StyleProperty, "DisplayText");
            var seasonIds = season.Select(v => v.VideoId).ToList();
            var seasonWatched = WatchStates.Of(season.ToList(), playback) == WatchState.Watched;
            var mark = PageParts.Compact(PageParts.Button(seasonWatched ? "✓ Staffel gesehen" : "Staffel als gesehen markieren", "GhostButton", (_, _) =>
            {
                _model.Db.SetVideosWatched(seasonIds, !seasonWatched);
                UpdateWatchedButton();
                BuildEpisodes();
            }, new Thickness(0, 0, 8, 0)));
            mark.VerticalAlignment = VerticalAlignment.Bottom;
            mark.ToolTip = seasonWatched ? "Staffel als ungesehen markieren" : "Alle Folgen dieser Staffel als gesehen markieren";
            AutomationProperties.SetName(mark, season.Key is { } n ? $"Staffel {n} gesehen" : "Folgen gesehen");
            var headerRow = new Grid();
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(mark, 1);
            headerRow.Children.Add(header);
            headerRow.Children.Add(mark);
            Episodes.Children.Add(headerRow);

            foreach (var video in season.OrderBy(v => v.Episode ?? int.MaxValue).ThenBy(v => v.RelPath, StringComparer.OrdinalIgnoreCase))
            {
                Episodes.Children.Add(EpisodeRow(video));
            }
        }
    }

    private UIElement EpisodeRow(VideoRow video)
    {
        var playback = _model.Db.GetPlayback(video.VideoId);
        var online = _model.PathOf(video) is not null;

        var thumb = new Border { Width = 200, Height = 112, CornerRadius = new CornerRadius(10), ClipToBounds = true };
        var image = ImageCache.Load(_model.ArtPath(ArtworkOwner.Video, video.VideoId, ArtworkKind.Landscape), 400);
        if (image is not null) thumb.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        else thumb.SetResourceReference(Border.BackgroundProperty, "CardBgBrush");
        if (playback is { Watched: false, PositionSeconds: > 0 } && video.DurationSeconds is > 0)
        {
            var fraction = Math.Clamp(playback.PositionSeconds / video.DurationSeconds.Value, 0, 1);
            var fill = new Border { Height = 4, HorizontalAlignment = HorizontalAlignment.Left, Width = 200 * fraction, VerticalAlignment = VerticalAlignment.Bottom };
            fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            thumb.Child = fill;
        }
        if (!online) thumb.Opacity = 0.5;

        var caption = video.Episode is { } n ? $"Folge {n}" : System.IO.Path.GetFileNameWithoutExtension(video.RelPath);
        var episodeText = video.Season is { } season && video.Episode is { } number && _episodeInfo.TryGetValue((season, number), out var found) ? found : null;
        var label = new TextBlock { Text = episodeText?.Name is { } episodeName ? $"{caption} · {episodeName}" : caption, FontSize = 17, TextTrimming = TextTrimming.CharacterEllipsis };
        label.SetResourceReference(TextBlock.FontFamilyProperty, "BodySemiFont");
        var details = new List<string>();
        if (video.DurationSeconds is { } seconds) details.Add($"{Math.Round(seconds / 60):0} min");
        if (video.Height is { } height) details.Add($"{height}p");
        if (playback is { Watched: true }) details.Add("gesehen");
        else if (playback is { PositionSeconds: > 0 } && video.DurationSeconds is { } total) details.Add($"noch {Math.Max(1, Math.Round((total - playback.PositionSeconds) / 60)):0} min");
        if (!online) details.Add("offline");
        var meta = new TextBlock { Text = string.Join(" · ", details), FontSize = 13, Margin = new Thickness(0, 4, 0, 0) };
        meta.SetResourceReference(StyleProperty, "MutedText");

        var watchedToggle = new Button
        {
            Content = playback is { Watched: true } ? "" : "",
            Style = (Style)FindResource("IconButton"),
            ToolTip = playback is { Watched: true } ? "Als ungesehen markieren" : "Als gesehen markieren",
            VerticalAlignment = VerticalAlignment.Center,
        };
        watchedToggle.Click += (_, e) =>
        {
            _model.Db.SavePlayback(video.VideoId, 0, watched: playback is not { Watched: true });
            _model.Reload();
            BuildEpisodes();
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 12, 0), Children = { label, meta } };
        if (episodeText?.Summary is { } summary)
        {
            var text = new TextBlock
            {
                Text = summary, FontSize = 13, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 36, Margin = new Thickness(0, 6, 0, 0),
                ToolTip = new TextBlock { Text = summary, MaxWidth = 480, TextWrapping = TextWrapping.Wrap },
            };
            text.SetResourceReference(StyleProperty, "MutedText");
            info.Children.Add(text);
        }
        Grid.SetColumn(info, 1);
        Grid.SetColumn(watchedToggle, 2);
        grid.Children.Add(thumb);
        grid.Children.Add(info);
        grid.Children.Add(watchedToggle);

        if (MkvEditor.CanEdit(video.RelPath))
        {
            var editButton = new Button { Content = "", Style = (Style)FindResource("IconButton"), ToolTip = "MKV bearbeiten …", VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(editButton, $"Bearbeiten: {caption}");
            editButton.Click += (_, _) => LibraryActions.EditVideo(_shell, video);
            Grid.SetColumn(editButton, 3);
            grid.Children.Add(editButton);
        }

        var row = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 6),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = grid,
        };
        row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "LineBrush");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += async (_, _) => await _shell.PlayAsync(video, resume: true);
        var menu = new List<(string, Action)>();
        if (MkvEditor.CanEdit(video.RelPath)) menu.Add(("Bearbeiten …", () => LibraryActions.EditVideo(_shell, video)));
        menu.Add(("Aus Mediacenter entfernen …", () => RemoveEpisode(video)));
        row.MouseRightButtonUp += (_, e) =>
        {
            ContextPopup.Show(row, menu, caption);
            e.Handled = true;
        };
        return row;
    }

    /// <summary>Nimmt eine Folge aus der Bibliothek; war es die letzte, ist die Serie weg und die Seite wird verlassen.</summary>
    private void RemoveEpisode(VideoRow video)
    {
        if (!LibraryActions.RemoveVideo(_shell, video)) return;
        if (_model.FindTitle(_title.Id) is null) _shell.Back();
        else _shell.ReloadView();
    }
}
