using System.Windows;
using System.Windows.Controls;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App.Pages;

/// <summary>Quellen verwalten und sehen, welche Laufwerke gerade angeschlossen sind.</summary>
public sealed class SourcesPage : UserControl
{
    private readonly IShell _shell;
    private readonly LibraryModel _model;

    public SourcesPage(IShell shell)
    {
        _shell = shell;
        _model = shell.Services.Model;

        var stack = new StackPanel();
        stack.Children.Add(PageParts.Heading("Laufwerke"));
        stack.Children.Add(PageParts.Muted("Laufwerke werden an ihrer Seriennummer erkannt, nicht am Laufwerksbuchstaben. Ein Stick darf nach dem Abstecken woanders wieder auftauchen, die Bibliothek bleibt erhalten."));

        var actions = new WrapPanel { Margin = new Thickness(0, 20, 0, 22) };
        actions.Children.Add(PageParts.Button("Ordner hinzufügen", "PrimaryButton", async (_, _) => await LibraryActions.AddFolderAsync(_shell)));
        actions.Children.Add(PageParts.Button("Datei hinzufügen", "GhostButton", async (_, _) => await LibraryActions.AddFileAsync(_shell)));
        actions.Children.Add(PageParts.Button("Neu scannen", "GhostButton", async (_, _) => await LibraryActions.RescanAsync(_shell)));
        actions.Children.Add(PageParts.Button("Fehlende Einträge entfernen", "GhostButton", (_, _) => RemoveMissing()));
        stack.Children.Add(actions);

        foreach (var unresolved in _model.Volumes.Unresolved) stack.Children.Add(UnresolvedCard(unresolved));

        var volumes = _model.Db.GetVolumes();
        if (volumes.Count == 0)
        {
            stack.Children.Add(PageParts.Muted("Noch keine Laufwerke. Füge einen Ordner oder eine Datei hinzu."));
        }
        var sources = _model.Db.GetSources();
        var filesPerVolume = _model.VideosByTitle.Values.SelectMany(v => v).Concat(_model.Inbox).GroupBy(v => v.VolumeId).ToDictionary(g => g.Key, g => g.Count());
        foreach (var volume in volumes)
        {
            stack.Children.Add(VolumeCard(volume, sources.Where(s => s.VolumeId == volume.Id).ToList(), filesPerVolume.GetValueOrDefault(volume.Id)));
        }

        Content = PageParts.Scrolling(stack);
    }

    private UIElement VolumeCard(KnownVolume volume, IReadOnlyList<Source> sources, int files)
    {
        var connected = _model.Volumes.Connected.TryGetValue(volume.Id, out var mounted);
        var name = new TextBlock { Text = string.IsNullOrWhiteSpace(volume.Name) ? "(ohne Name)" : volume.Name, FontSize = 22 };
        name.SetResourceReference(TextBlock.FontFamilyProperty, "BodySemiFont");
        var gigabytes = volume.Capacity / 1_000_000_000d;
        var info = PageParts.Muted($"{volume.FileSystem} · {gigabytes:0} GB · Seriennummer {volume.Serial} · {files} Videos", 13);

        var header = new WrapPanel { Children = { name, new Border { Width = 14 }, PageParts.Badge(connected ? $"angeschlossen als {mounted!.RootPath}" : "nicht angeschlossen", connected) } };
        foreach (var child in header.Children.OfType<FrameworkElement>()) child.VerticalAlignment = VerticalAlignment.Center;

        var panel = new StackPanel { Children = { header, info } };
        foreach (var source in sources)
        {
            var where = source.RelPath.Length == 0 ? "ganzes Laufwerk" : source.RelPath;
            var line = PageParts.Muted($"{(source.Kind == SourceKind.Folder ? "Ordner" : "Datei")}:  {where}", 13);
            line.Margin = new Thickness(0, 6, 0, 0);
            panel.Children.Add(line);
        }
        return PageParts.Card(panel);
    }

    private UIElement UnresolvedCard(UnresolvedVolume unresolved)
    {
        var title = new TextBlock { Text = $"{unresolved.Volume.RootPath} ({unresolved.Volume.Label}) ist nicht eindeutig", FontSize = 18 };
        title.SetResourceReference(TextBlock.FontFamilyProperty, "BodySemiFont");
        var panel = new StackPanel { Children = { title, PageParts.Muted(unresolved.Match.Reason ?? "") } };

        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var candidate in unresolved.Match.Candidates)
        {
            buttons.Children.Add(PageParts.Button($"Ist dasselbe wie „{candidate.Name}“", "GhostButton", (_, _) =>
            {
                _model.Db.RelinkVolume(candidate.Id, unresolved.Volume);
                _shell.ReloadView();
            }));
        }
        panel.Children.Add(buttons);

        var card = PageParts.Card(panel);
        card.SetResourceReference(Border.BorderBrushProperty, "Accent2Brush");
        return card;
    }

    private void RemoveMissing()
    {
        var answer = MessageBox.Show(
            "Alle als fehlend markierten Dateien werden aus der Bibliothek entfernt, samt Fortschritt. Titel mit eigenen Genres bleiben erhalten.\n\n" +
            "Abgesteckte Laufwerke sind nicht betroffen, nur Dateien, die auf einem angeschlossenen Laufwerk fehlen.",
            "Fehlende Einträge entfernen", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;

        var removed = _model.Db.RemoveMissingFiles();
        var pruned = _model.Artwork.PruneOrphans();
        _shell.ReloadView();
        _shell.ShowMessage($"{removed} Dateien entfernt, {pruned} verwaiste Cover gelöscht.");
    }
}
