using System.IO;
using System.Windows;
using System.Windows.Controls;
using MediaCenter.App.Controls;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App.Pages;

/// <summary>„Folge wählen“: alle Folgen einer Serie nach Staffeln; ein Klick spielt die Folge ab (setzt dort fort, wo sie stand). Erscheint als Fenster über der Seite.</summary>
public sealed class EpisodePickerPage : UserControl, IModalContent
{
    private readonly IShell _shell;

    public string? BlockClose => null;

    public static void Show(IShell shell, long titleId)
    {
        var title = shell.Services.Model.FindTitle(titleId) ?? throw new InvalidOperationException($"Titel {titleId} existiert nicht (mehr).");
        shell.OpenModal("Folge wählen", title.Name, new EpisodePickerPage(shell, titleId), ModalSize.Medium);
    }

    private EpisodePickerPage(IShell shell, long titleId)
    {
        _shell = shell;
        var model = shell.Services.Model;
        var videos = model.VideosOf(titleId);
        var current = PlayTarget.For(model, titleId)?.Video.VideoId; // die Folge, die „Fortsetzen“ spielen würde

        var stack = new StackPanel();
        if (videos.Count == 0)
        {
            stack.Children.Add(PageParts.Muted("Zu dieser Serie gibt es keine Folgen."));
        }
        foreach (var season in videos.GroupBy(v => v.Season).OrderBy(g => g.Key ?? int.MaxValue))
        {
            var header = new TextBlock { Text = season.Key is { } number ? $"Staffel {number}" : "Folgen", FontSize = 26, Margin = new Thickness(0, stack.Children.Count == 0 ? 0 : 18, 0, 6) };
            header.SetResourceReference(StyleProperty, "DisplayText");
            stack.Children.Add(header);

            foreach (var video in season.OrderBy(v => v.Episode ?? int.MaxValue).ThenBy(v => v.RelPath, StringComparer.OrdinalIgnoreCase))
            {
                stack.Children.Add(Row(model, video, video.VideoId == current));
            }
        }
        Content = stack;
    }

    private UIElement Row(LibraryModel model, VideoRow video, bool isNext)
    {
        var playback = model.Db.GetPlayback(video.VideoId);
        var online = model.PathOf(video) is not null;
        var label = video.Episode is { } n ? $"Folge {n}" : Path.GetFileNameWithoutExtension(video.RelPath);

        var details = new List<string>();
        if (video.DurationSeconds is { } seconds) details.Add($"{Math.Round(seconds / 60):0} min");
        if (playback is { Watched: true }) details.Add("gesehen");
        else if (playback is { PositionSeconds: > 0 } && video.DurationSeconds is { } total) details.Add($"noch {Math.Max(1, Math.Round((total - playback.PositionSeconds) / 60)):0} min");
        if (isNext) details.Add(playback is { Watched: false, PositionSeconds: > 0 } ? "hier geht es weiter" : "als Nächstes");
        if (!online) details.Add("offline");

        var name = PageParts.Semi(label, 15);
        name.SetResourceReference(TextBlock.ForegroundProperty, isNext ? "AccentBrush" : "InkBrush");
        var meta = PageParts.Muted(string.Join(" · ", details), 12);
        var text = new StackPanel { Children = { name, meta } };
        text.Opacity = online ? 1 : 0.55;

        return PageParts.ClickableRow(text, () => _ = PlayAsync(video), new Thickness(0, 0, 0, 2), (video.Season is { } s ? $"Staffel {s} " : "") + label);
    }

    private async Task PlayAsync(VideoRow video)
    {
        _shell.CloseModal();
        await _shell.PlayAsync(video, resume: true);
    }
}
