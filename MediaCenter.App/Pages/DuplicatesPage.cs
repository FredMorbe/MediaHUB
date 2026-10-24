using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App.Pages;

/// <summary>
/// Doppelte Einträge: Filme und Folgen in mehreren Dateien und gleichnamige Einträge. Die Seite zeigt nur; jede Änderung (aus dem Mediacenter entfernen,
/// in den Papierkorb legen, Einträge zusammenführen) braucht eine Bestätigung.
/// </summary>
public sealed class DuplicatesPage : UserControl
{
    private readonly IShell _shell;
    private readonly StackPanel _groups = new();
    private readonly TextBlock _summary = PageParts.Muted("", 14);

    public DuplicatesPage(IShell shell)
    {
        _shell = shell;
        var back = PageParts.Compact(PageParts.Button("Zurück", "GhostButton", (_, _) => shell.Back(), new Thickness(0, 0, 0, 18)));
        AutomationProperties.SetName(back, "Zurück");

        var stack = new StackPanel { Margin = new Thickness(48, PageParts.NavInset + 28, 48, 60), MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Left };
        stack.Children.Add(back);
        stack.Children.Add(PageParts.Header("Bibliothek", "Duplikate", _summary));
        stack.Children.Add(_groups);
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = stack,
        };
        Fill();
    }

    private void Fill()
    {
        _groups.Children.Clear();
        var model = _shell.Services.Model;
        var videos = model.VideosByTitle.Values.SelectMany(v => v).ToList();
        var sizes = model.Db.GetFileSizes();
        var found = DuplicateFinder.Find(videos, model.Titles, sizes);

        var reclaimable = found.Sum(g => g.ReclaimableBytes);
        _summary.Text = found.Count == 0
            ? "Keine Duplikate gefunden."
            : $"{found.Count} {(found.Count == 1 ? "Gruppe" : "Gruppen")}" + (reclaimable > 0 ? $" · bis zu {FormatSize(reclaimable)} frei, wenn jeweils nur die größte Datei bleibt" : "");
        if (found.Count == 0)
        {
            _groups.Children.Add(PageParts.EmptyState("", "Alles sauber",
                "Kein Film und keine Folge liegt in mehreren Dateien, und es gibt keine gleichnamigen Einträge."));
            return;
        }

        foreach (var kind in new[] { DuplicateKind.MovieFiles, DuplicateKind.EpisodeFiles, DuplicateKind.SimilarTitles })
        {
            var ofKind = found.Where(g => g.Kind == kind).ToList();
            if (ofKind.Count == 0) continue;
            _groups.Children.Add(PageParts.SectionTitle(kind switch
            {
                DuplicateKind.MovieFiles => "Film in mehreren Dateien",
                DuplicateKind.EpisodeFiles => "Folge in mehreren Dateien",
                _ => "Gleichnamige Einträge",
            }, new Thickness(0, 26, 0, 4)));
            if (kind == DuplicateKind.SimilarTitles)
                _groups.Children.Add(PageParts.Muted("Weichen die Jahre ab, sind es meist verschiedene Filme (Neuverfilmung). „Zusammenführen“ nimmt alle Videos in den gewählten Eintrag auf.", 13));
            foreach (var group in ofKind) _groups.Children.Add(kind == DuplicateKind.SimilarTitles ? TitleGroup(group) : FileGroup(group));
        }
    }

    private UIElement FileGroup(DuplicateGroup group)
    {
        var model = _shell.Services.Model;
        var panel = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        if (group.ReclaimableBytes > 0)
        {
            var badge = PageParts.Badge($"bis zu {FormatSize(group.ReclaimableBytes)} frei", highlighted: true);
            DockPanel.SetDock(badge, Dock.Right);
            head.Children.Add(badge);
        }
        head.Children.Add(PageParts.Semi(group.Label, 17));
        panel.Children.Add(head);

        foreach (var file in group.Files.OrderByDescending(f => f.Size))
        {
            var video = file.Video;
            var online = model.PathOf(video);
            var detail = string.Join(" · ", new[]
            {
                video.Height is { } h ? $"{h}p" : null,
                video.VideoCodec?.ToUpperInvariant(),
                video.IsHdr ? "HDR" : null,
                FormatSize(file.Size),
                online is null ? "nicht erreichbar" : null,
            }.Where(x => x is not null));

            var name = PageParts.Semi(Path.GetFileName(video.RelPath), 14);
            name.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
            var text = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children = { name, PageParts.Cell(Path.GetDirectoryName(video.RelPath) is { Length: > 0 } dir ? $"{dir} · {detail}" : detail) },
            };

            var remove = PageParts.Compact(PageParts.Button("Aus Mediacenter", "GhostButton", (_, _) =>
            {
                if (LibraryActions.RemoveVideo(_shell, video)) Refresh();
            }, new Thickness(8, 0, 0, 0)));
            remove.ToolTip = "Nimmt den Eintrag aus dem Mediacenter; die Datei bleibt auf der Festplatte";
            AutomationProperties.SetName(remove, $"Aus Mediacenter entfernen: {Path.GetFileName(video.RelPath)}");
            var trash = PageParts.Compact(PageParts.Button("In den Papierkorb", "DangerButton", (_, _) => RecycleVideo(video, online), new Thickness(8, 0, 0, 0)));
            trash.IsEnabled = online is not null;
            AutomationProperties.SetName(trash, $"In den Papierkorb: {Path.GetFileName(video.RelPath)}");

            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { remove, trash } };
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons);
            row.Children.Add(text);
            panel.Children.Add(PageParts.Divider());
            panel.Children.Add(row);
        }
        return PageParts.Card(panel);
    }

    private UIElement TitleGroup(DuplicateGroup group)
    {
        var model = _shell.Services.Model;
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Margin = new Thickness(0, 0, 0, 8), Text = group.Label, FontSize = 17 });
        ((TextBlock)panel.Children[0]).SetResourceReference(TextBlock.FontFamilyProperty, "BodySemiFont");
        ((TextBlock)panel.Children[0]).SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");

        foreach (var title in group.Titles)
        {
            var count = model.VideosOf(title.Id).Count;
            var line = PageParts.Cell($"{(title.Kind == TitleKind.Movie ? "Film" : "Serie")}{(title.Year is { } y ? $" · {y}" : " · ohne Jahr")} · {count} {(count == 1 ? "Datei" : "Dateien")}", 14);
            var open = PageParts.Compact(PageParts.Button("Öffnen", "GhostButton", (_, _) => _shell.OpenTitle(title.Id), new Thickness(8, 0, 0, 0)));
            var merge = PageParts.Compact(PageParts.Button("Hierhin zusammenführen", "GhostButton", (_, _) => Merge(group, title), new Thickness(8, 0, 0, 0)));
            AutomationProperties.SetName(open, $"Öffnen: {title.Name} {title.Year}");
            AutomationProperties.SetName(merge, $"Zusammenführen in: {title.Name} {title.Year}");

            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { open, merge } };
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons);
            row.Children.Add(line);
            panel.Children.Add(PageParts.Divider());
            panel.Children.Add(row);
        }
        return PageParts.Card(panel);
    }

    private void Merge(DuplicateGroup group, TitleRow target)
    {
        var others = group.Titles.Where(t => t.Id != target.Id).ToList();
        var names = string.Join(", ", others.Select(t => t.Year is { } y ? $"„{t.Name}“ ({y})" : $"„{t.Name}“"));
        var question = $"{names} in „{target.Name}“{(target.Year is { } year ? $" ({year})" : "")} zusammenführen?\n\n" +
                       "• Alle Videos, Genres, Sammlungen und der Platz auf „Meine Liste“ wandern zum gewählten Eintrag.\n" +
                       "• Die anderen Einträge verschwinden; Dateien auf der Festplatte bleiben unberührt.\n" +
                       "• Weichen die Jahre ab, sind es womöglich zwei verschiedene Filme.";
        if (!_shell.Confirm("Einträge zusammenführen?", question, "Zusammenführen", danger: true)) return;
        try
        {
            foreach (var other in others) _shell.Services.Db.MergeTitles(other.Id, target.Id);
        }
        catch (InvalidOperationException ex)
        {
            _shell.ShowMessage(ex.Message, isError: true);
        }
        Refresh();
    }

    private void RecycleVideo(VideoRow video, string? path)
    {
        if (path is null) return;
        var question = $"Die Datei „{Path.GetFileName(path)}“ in den Papierkorb legen und aus dem Mediacenter entfernen?\n\n{path}\n\n" +
                       "• Auf Laufwerken ohne Papierkorb (z. B. USB-Sticks) wird die Datei endgültig gelöscht.\n" +
                       "• Fortschritt und Einstellungen zu dieser Datei gehen verloren.";
        if (!_shell.Confirm("Datei löschen?", question, "In den Papierkorb", danger: true)) return;
        try
        {
            LibraryActions.Recycle(path);
            _shell.Services.Db.RemoveVideos(new[] { video.VideoId });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            _shell.ShowMessage($"Die Datei ließ sich nicht entfernen: {ex.Message}", isError: true);
            Refresh();
            return;
        }
        _shell.Services.Model.Artwork.PruneOrphans();
        _shell.ShowMessage($"„{Path.GetFileName(path)}“ ist im Papierkorb.");
        Refresh();
    }

    private void Refresh()
    {
        _shell.Services.Model.Reload();
        Fill();
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0} MB";
}
