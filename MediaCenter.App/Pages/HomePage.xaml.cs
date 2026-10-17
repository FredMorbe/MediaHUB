using System.Windows;
using System.Windows.Controls;
using MediaCenter.App.Controls;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App.Pages;

public partial class HomePage : UserControl
{
    private readonly IShell _shell;
    private readonly LibraryModel _model;

    public HomePage(IShell shell)
    {
        InitializeComponent();
        _shell = shell;
        _model = shell.Services.Model;

        SizeChanged += (_, _) => HeroZone.Height = Math.Max(440, ActualHeight * 0.56);
        AddFolderButton.Click += async (_, _) => await LibraryActions.AddFolderAsync(_shell);
        AddFileButton.Click += async (_, _) => await LibraryActions.AddFileAsync(_shell);

        BuildHero();
        BuildRows();
    }

    private void BuildHero()
    {
        if (_model.Titles.Count == 0)
        {
            HeroPanel.Visibility = Visibility.Collapsed;
            EmptyPanel.Visibility = Visibility.Visible;
            return;
        }

        // Zuerst das, was man gerade schaut; sonst der zuletzt angelegte Titel.
        TitleRow title;
        string kicker;
        double progress = 0;
        var meta = "";
        if (_model.Continue.FirstOrDefault(c => _model.FindTitle(c.TitleId) is not null) is { } item)
        {
            title = _model.FindTitle(item.TitleId)!;
            kicker = "WEITERSCHAUEN";
            progress = item.DurationSeconds is > 0 ? Math.Clamp(item.PositionSeconds / item.DurationSeconds.Value, 0, 1) : 0;
            var episode = item.Season is { } s && item.Episode is { } e ? $"Staffel {s} · Folge {e} · " : "";
            var left = item.DurationSeconds is { } d ? $"noch {Math.Max(1, Math.Round((d - item.PositionSeconds) / 60)):0} min" : "";
            meta = episode + left;
            PlayButton.Content = "Fortsetzen";
        }
        else
        {
            title = _model.Titles.MaxBy(t => t.Id)!;
            kicker = "ZULETZT HINZUGEFÜGT";
            meta = _model.MetaOf(title);
            PlayButton.Content = "Abspielen";
        }

        Kicker.Text = kicker;
        HeroTitle.Text = title.Name;
        HeroMeta.Text = meta;
        if (progress > 0)
        {
            HeroProgress.Visibility = Visibility.Visible;
            HeroProgress.SizeChanged += (_, _) => HeroProgressFill.Width = HeroProgress.ActualWidth * progress;
        }

        PlayButton.Click += async (_, _) => await PlayAsync(title.Id);
        DetailsButton.Click += (_, _) => _shell.OpenTitle(title.Id);

        // Das Querformat-Bild ist als Hintergrund gedacht; ein Poster wäre hier nur unscharf aufgeblasen.
        _shell.SetBackdrop(_model.ArtPath(ArtworkOwner.Title, title.Id, ArtworkKind.Landscape));
    }

    private void BuildRows()
    {
        var continueCards = _model.ContinueCards().ToList();
        if (continueCards.Count > 0) AddRow("Weiterschauen", continueCards, async card => await PlayAsync(card.TitleId));

        AddTitleRow("Filme", _model.Titles.Where(t => t.Kind == TitleKind.Movie));
        AddTitleRow("Serien", _model.Titles.Where(t => t.Kind == TitleKind.Series));
        foreach (var genre in _model.AllGenres())
        {
            AddTitleRow(genre, _model.Titles.Where(t => _model.GenresOf(t.Id).Contains(genre, StringComparer.OrdinalIgnoreCase)));
        }
    }

    private void AddTitleRow(string heading, IEnumerable<TitleRow> titles)
    {
        var cards = titles.Select(t => _model.ToCard(t)).ToList();
        if (cards.Count > 0) AddRow(heading, cards, card => _shell.OpenTitle(card.TitleId));
    }

    private void AddRow(string heading, IReadOnlyList<CardItem> cards, Action<CardItem> onActivated)
    {
        var row = new CardRow(heading, cards, onActivated) { Margin = new Thickness(0, 0, 0, 22) };
        Rows.Children.Add(row);
    }

    private async Task PlayAsync(long titleId)
    {
        if (PlayTarget.For(_model, titleId) is { } target) await _shell.PlayAsync(target.Video, target.Resume);
    }
}
