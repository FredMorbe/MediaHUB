using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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
        BuildGenres();
        BuildPlayButton();
        BuildCoverPanel();
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

    // --- Genres --------------------------------------------------------------------------------------------

    private void BuildGenres()
    {
        GenrePanel.Children.Clear();
        var mine = _model.GenresOf(_title.Id);

        foreach (var genre in mine)
        {
            var chip = new ToggleButton { Content = $"{genre}  ✕", IsChecked = true, ToolTip = "Genre entfernen" };
            chip.SetResourceReference(StyleProperty, "Chip");
            chip.Click += (_, _) => ChangeGenres(() => _model.Db.RemoveGenre(_title.Id, genre));
            GenrePanel.Children.Add(chip);
        }
        foreach (var genre in _model.AllGenres().Except(mine, StringComparer.OrdinalIgnoreCase))
        {
            var chip = new ToggleButton { Content = $"+ {genre}", IsChecked = false, ToolTip = "Genre hinzufügen" };
            chip.SetResourceReference(StyleProperty, "Chip");
            chip.Click += (_, _) => ChangeGenres(() => _model.Db.AddGenre(_title.Id, genre));
            GenrePanel.Children.Add(chip);
        }

        var input = new TextBox { Width = 170, Padding = new Thickness(12, 5, 12, 5), ToolTip = "Neues Genre, mit Enter bestätigen" };
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
        GenrePanel.Children.Add(field);
    }

    private void ChangeGenres(Action change)
    {
        change();
        _model.Reload();
        BuildGenres();
    }

    // --- Cover ---------------------------------------------------------------------------------------------

    private void BuildCoverPanel()
    {
        CoverPanel.Children.Add(CoverChip("Poster ersetzen", () => ReplaceCoverAsync(ArtworkKind.Poster)));
        CoverPanel.Children.Add(CoverChip("Hintergrund ersetzen", () => ReplaceCoverAsync(ArtworkKind.Landscape)));
        CoverPanel.Children.Add(CoverChip("Automatische Cover wiederherstellen", ResetCoversAsync));
    }

    private static ToggleButton CoverChip(string text, Func<Task> action)
    {
        var chip = new ToggleButton { Content = text };
        chip.SetResourceReference(StyleProperty, "Chip");
        chip.Click += async (_, _) =>
        {
            chip.IsChecked = false;
            await action();
        };
        return chip;
    }

    private async Task ReplaceCoverAsync(ArtworkKind kind)
    {
        var filter = string.Join(";", MediaExtensions.Image.Select(e => "*" + e));
        var dialog = new OpenFileDialog { Title = kind == ArtworkKind.Poster ? "Poster wählen" : "Hintergrundbild wählen", Filter = $"Bilder|{filter}" };
        if (dialog.ShowDialog() != true) return;

        await _shell.RunBusyAsync("Bild wird übernommen …",
            () => _model.Artwork.SetUserArtworkAsync(ArtworkOwner.Title, _title.Id, kind, dialog.FileName));
        _shell.ReloadView();
    }

    private async Task ResetCoversAsync()
    {
        _model.Artwork.ClearArtwork(ArtworkOwner.Title, _title.Id, ArtworkKind.Poster);
        _model.Artwork.ClearArtwork(ArtworkOwner.Title, _title.Id, ArtworkKind.Landscape);
        await _shell.RefreshArtworkAsync();
    }

    // --- Folgen --------------------------------------------------------------------------------------------

    private void BuildEpisodes()
    {
        Episodes.Children.Clear();
        foreach (var season in _model.VideosOf(_title.Id).GroupBy(v => v.Season).OrderBy(g => g.Key ?? int.MaxValue))
        {
            var header = new TextBlock
            {
                Text = season.Key is { } number ? $"Staffel {number}" : "Folgen",
                FontSize = 38,
                Margin = new Thickness(0, 14, 0, 8),
            };
            header.SetResourceReference(StyleProperty, "DisplayText");
            Episodes.Children.Add(header);

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

        var label = new TextBlock { Text = video.Episode is { } n ? $"Folge {n}" : System.IO.Path.GetFileNameWithoutExtension(video.RelPath), FontSize = 17 };
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
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 12, 0), Children = { label, meta } };
        Grid.SetColumn(info, 1);
        Grid.SetColumn(watchedToggle, 2);
        grid.Children.Add(thumb);
        grid.Children.Add(info);
        grid.Children.Add(watchedToggle);

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
        return row;
    }
}
