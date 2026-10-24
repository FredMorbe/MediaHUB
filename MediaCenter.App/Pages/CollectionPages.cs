using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MediaCenter.App.Controls;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App.Pages;

/// <summary>Kleines Fenster mit einem Namensfeld (neue Sammlung, Sammlung umbenennen). Fehler (Name leer, vergeben) stehen im Fenster, es bleibt offen.</summary>
public sealed class CollectionNamePage : UserControl, IModalContent
{
    public string? BlockClose => null;

    /// <param name="accept">Nimmt den Namen an; wirft <see cref="ArgumentException"/>, wenn er nicht geht (die Meldung steht dann im Fenster).</param>
    public static void Show(IShell shell, string kicker, string title, string initial, string confirmText, Action<string> accept) =>
        shell.OpenModal(kicker, title, new CollectionNamePage(shell, initial, confirmText, accept), ModalSize.Small);

    private CollectionNamePage(IShell shell, string initial, string confirmText, Action<string> accept)
    {
        var (field, box) = PageParts.HintBox("Name der Sammlung", 360);
        box.Text = initial;
        AutomationProperties.SetName(box, "Name der Sammlung");
        var status = PageParts.Muted("", 13);
        status.Margin = new Thickness(0, 10, 0, 0);
        status.SetResourceReference(TextBlock.ForegroundProperty, "Accent2Brush");

        void Commit()
        {
            try
            {
                accept(box.Text);
            }
            catch (ArgumentException ex)
            {
                status.Text = ex.Message;
                return;
            }
            shell.CloseModal();
        }
        var ok = PageParts.Button(confirmText, "PrimaryButton", (_, _) => Commit(), new Thickness(0, 16, 0, 0));
        AutomationProperties.SetName(ok, confirmText);
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Commit();
            e.Handled = true;
        };
        Content = new StackPanel { Children = { field, status, ok } };
        Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
    }
}

/// <summary>Fenster „Zu Sammlung hinzufügen“: vorhandene Sammlungen an- oder abwählen, oder eine neue mit diesem Film anlegen.</summary>
public sealed class CollectionPickerPage : UserControl, IModalContent
{
    private readonly IShell _shell;
    private readonly TitleRow _title;
    private readonly WrapPanel _chips = new();

    public string? BlockClose => null;

    public static void Show(IShell shell, long titleId)
    {
        var title = shell.Services.Model.FindTitle(titleId);
        if (title is null)
        {
            shell.ShowMessage("Der Titel ist nicht mehr in der Bibliothek.", isError: true);
            return;
        }
        if (title.Kind != TitleKind.Movie)
        {
            shell.ShowMessage("Sammlungen gibt es nur für Filme.", isError: true);
            return;
        }
        shell.OpenModal("Sammlung", title.Name, new CollectionPickerPage(shell, title), ModalSize.Medium);
    }

    private CollectionPickerPage(IShell shell, TitleRow title)
    {
        _shell = shell;
        _title = title;

        var hint = PageParts.Muted("Ein Film kann in mehreren Sammlungen stehen. Ein Klick nimmt ihn auf oder wieder heraus.", 13);
        var stack = new StackPanel { Children = { hint, _chips } };
        _chips.Margin = new Thickness(0, 14, 0, 0);

        var create = PageParts.Button("Neue Sammlung …", "GhostButton", (_, _) =>
            CollectionNamePage.Show(shell, "Neue Sammlung", "Name der Sammlung", title.Name, "Anlegen", name =>
            {
                shell.Services.Model.Db.CreateCollection(name, new[] { title.Id });
                shell.Services.Model.ReloadCollections();
                shell.ShowMessage($"„{title.Name}“ steht jetzt in der neuen Sammlung „{name.Trim()}“.");
                shell.ReloadView();
            }), new Thickness(0, 14, 0, 0));
        AutomationProperties.SetName(create, "Neue Sammlung anlegen");
        stack.Children.Add(create);
        Content = stack;
        Fill();
    }

    private void Fill()
    {
        _chips.Children.Clear();
        var model = _shell.Services.Model;
        if (model.Collections.Count == 0)
        {
            _chips.Children.Add(PageParts.Muted("Es gibt noch keine Sammlung.", 13));
            return;
        }
        foreach (var collection in model.Collections)
        {
            var member = model.CollectionItems.TryGetValue(collection.Id, out var ids) && ids.Contains(_title.Id);
            var chip = new ToggleButton { Content = collection.Name, IsChecked = member };
            chip.SetResourceReference(StyleProperty, "Chip");
            AutomationProperties.SetName(chip, $"Sammlung {collection.Name}");
            chip.Click += (_, _) =>
            {
                var add = chip.IsChecked == true;
                if (add) model.Db.AddTitlesToCollection(collection.Id, new[] { _title.Id });
                else model.Db.RemoveTitleFromCollection(collection.Id, _title.Id);
                model.ReloadCollections();
                _shell.ShowMessage(add ? $"„{_title.Name}“ steht jetzt in „{collection.Name}“." : $"„{_title.Name}“ ist nicht mehr in „{collection.Name}“.");
            };
            _chips.Children.Add(chip);
        }
    }
}

/// <summary>Alle Sammlungen (Filmreihen) und Vorschläge, die die App aus den Filmnamen erkennt.</summary>
public sealed class CollectionsPage : UserControl
{
    private readonly IShell _shell;
    private readonly LibraryModel _model;
    private readonly StackPanel _body = new();

    public CollectionsPage(IShell shell)
    {
        _shell = shell;
        _model = shell.Services.Model;

        var stack = new StackPanel { Margin = new Thickness(48, PageParts.NavInset + 28, 48, 60) };
        var back = PageParts.Compact(PageParts.Button("Zurück", "GhostButton", (_, _) => shell.Back(), new Thickness(0, 0, 0, 18)));
        AutomationProperties.SetName(back, "Zurück");
        stack.Children.Add(back);
        stack.Children.Add(PageParts.Header("Filme", "Sammlungen", "Filmreihen wie „Matrix“ oder „Iron Man“. Du legst sie an oder übernimmst Vorschläge."));
        stack.Children.Add(_body);
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Content = stack };
        Fill();
    }

    private void Fill()
    {
        _body.Children.Clear();
        var create = PageParts.Compact(PageParts.Button("Neue Sammlung …", "PrimaryButton", (_, _) =>
            CollectionNamePage.Show(_shell, "Neue Sammlung", "Name der Sammlung", "", "Anlegen", name =>
            {
                _model.Db.CreateCollection(name);
                _model.ReloadCollections();
                Fill();
            }), new Thickness(0, 0, 0, 8)));
        AutomationProperties.SetName(create, "Neue Sammlung");
        _body.Children.Add(create);

        var suggestions = CollectionSuggestions.Find(_model.Titles, _model.Collections, _model.CollectionItems);
        if (suggestions.Count > 0)
        {
            _body.Children.Add(PageParts.SectionTitle("Vorschläge", new Thickness(0, 22, 0, 4)));
            _body.Children.Add(PageParts.Muted("Filme, deren Namen nach derselben Reihe aussehen. Nichts davon ist angelegt, bis du es übernimmst.", 13));
            var list = new StackPanel();
            foreach (var suggestion in suggestions) list.Children.Add(SuggestionRow(suggestion));
            _body.Children.Add(PageParts.Card(list, new Thickness(0, 12, 0, 0)));
        }

        _body.Children.Add(PageParts.SectionTitle("Deine Sammlungen", new Thickness(0, 26, 0, 10)));
        if (_model.Collections.Count == 0)
        {
            _body.Children.Add(PageParts.EmptyState("", "Noch keine Sammlung",
                "Lege eine an oder übernimm einen Vorschlag. Im Rechtsklick-Menü eines Films gibt es „Zu Sammlung hinzufügen …“."));
            return;
        }

        var grid = new WrapPanel();
        foreach (var collection in _model.Collections)
        {
            var titles = _model.CollectionItems.GetValueOrDefault(collection.Id)?.Select(_model.FindTitle).OfType<TitleRow>().ToList() ?? new List<TitleRow>();
            var cover = titles.Select(t => _model.BestCover(t.Id)).FirstOrDefault(c => c is not null);
            var card = new CardView(new CardItem
            {
                TitleId = 0,
                Title = collection.Name,
                Meta = $"{titles.Count} {(titles.Count == 1 ? "Film" : "Filme")}",
                Kind = TitleKind.Movie,
                Image = cover is { } c ? ImageCache.Load(c.Path, 360) : null,
                Tag = collection,
            })
            { Margin = new Thickness(0, 0, 20, 30) };
            card.Activated += item =>
            {
                if (item.Tag is CollectionRow row) _shell.Open(() => new CollectionPage(_shell, row.Id));
            };
            grid.Children.Add(card);
        }
        _body.Children.Add(grid);
    }

    private UIElement SuggestionRow(CollectionSuggestion suggestion)
    {
        var names = string.Join(" · ", suggestion.TitleIds.Select(id => _model.FindTitle(id)).OfType<TitleRow>().Select(t => t.Year is { } y ? $"{t.Name} ({y})" : t.Name));
        var title = PageParts.Semi($"{suggestion.Name}  ·  {suggestion.TitleIds.Count} Filme", 15);
        title.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        var detail = PageParts.Muted(names, 12.5);
        detail.TextTrimming = TextTrimming.CharacterEllipsis;
        detail.TextWrapping = TextWrapping.NoWrap;

        var accept = PageParts.Compact(PageParts.Button("Anlegen", "GhostButton", (_, _) =>
        {
            try
            {
                _model.Db.CreateCollection(suggestion.Name, suggestion.TitleIds);
            }
            catch (ArgumentException)
            {
                // Der Name ist schon vergeben (eine Sammlung ohne diese Filme): Namen selbst wählen lassen.
                CollectionNamePage.Show(_shell, "Neue Sammlung", "Anderer Name", suggestion.Name, "Anlegen", name =>
                {
                    _model.Db.CreateCollection(name, suggestion.TitleIds);
                    _model.ReloadCollections();
                    Fill();
                });
                return;
            }
            _model.ReloadCollections();
            _shell.ShowMessage($"Sammlung „{suggestion.Name}“ mit {suggestion.TitleIds.Count} Filmen angelegt.");
            Fill();
        }, new Thickness(12, 0, 0, 0)));
        AutomationProperties.SetName(accept, $"Sammlung anlegen: {suggestion.Name}");

        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 8) };
        DockPanel.SetDock(accept, Dock.Right);
        row.Children.Add(accept);
        row.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, detail } });
        return row;
    }
}

/// <summary>Die Filme einer Sammlung, nach Jahr geordnet.</summary>
public sealed class CollectionPage : UserControl
{
    private readonly IShell _shell;
    private readonly LibraryModel _model;
    private readonly long _id;

    public CollectionPage(IShell shell, long collectionId)
    {
        _shell = shell;
        _model = shell.Services.Model;
        _id = collectionId;

        var collection = _model.Collections.FirstOrDefault(c => c.Id == collectionId);
        var stack = new StackPanel { Margin = new Thickness(48, PageParts.NavInset + 28, 48, 60) };
        var back = PageParts.Compact(PageParts.Button("Zurück", "GhostButton", (_, _) => shell.Back(), new Thickness(0, 0, 0, 18)));
        AutomationProperties.SetName(back, "Zurück");
        stack.Children.Add(back);

        if (collection is null)
        {
            stack.Children.Add(PageParts.EmptyState("", "Sammlung nicht gefunden", "Diese Sammlung gibt es nicht mehr."));
            Content = new ScrollViewer { Content = stack, Focusable = false };
            return;
        }

        var titles = _model.CollectionItems.GetValueOrDefault(collectionId)?.Select(_model.FindTitle).OfType<TitleRow>().ToList() ?? new List<TitleRow>();
        var count = PageParts.Muted($"{titles.Count} {(titles.Count == 1 ? "Film" : "Filme")}", 14);
        stack.Children.Add(PageParts.Header("Sammlung", collection.Name, count));

        var rename = PageParts.Compact(PageParts.Button("Umbenennen …", "GhostButton", (_, _) =>
            CollectionNamePage.Show(shell, "Sammlung umbenennen", collection.Name, collection.Name, "Speichern", name =>
            {
                _model.Db.RenameCollection(_id, name);
                _model.ReloadCollections();
                shell.ReloadView();
            }), new Thickness(0, 0, 8, 18)));
        var delete = PageParts.Compact(PageParts.Button("Sammlung löschen …", "GhostButton", (_, _) => Delete(collection), new Thickness(0, 0, 0, 18)));
        AutomationProperties.SetName(rename, "Sammlung umbenennen");
        AutomationProperties.SetName(delete, "Sammlung löschen");
        stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { rename, delete } });

        if (titles.Count == 0)
        {
            stack.Children.Add(PageParts.EmptyState("", "Die Sammlung ist leer", "Im Rechtsklick-Menü eines Films gibt es „Zu Sammlung hinzufügen …“."));
        }
        else
        {
            var grid = new WrapPanel();
            foreach (var title in titles)
            {
                var card = new CardView(_model.ToCard(title))
                {
                    Margin = new Thickness(0, 0, 20, 30),
                    Menu = item => LibraryActions.CardMenu(shell, item).Append(("Aus dieser Sammlung entfernen", () =>
                    {
                        _model.Db.RemoveTitleFromCollection(_id, item.TitleId);
                        _model.ReloadCollections();
                        shell.ReloadView();
                    })).ToList(),
                };
                card.Activated += item => shell.OpenTitle(item.TitleId);
                grid.Children.Add(card);
            }
            stack.Children.Add(grid);
        }
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Content = stack };
    }

    private void Delete(CollectionRow collection)
    {
        var question = $"Die Sammlung „{collection.Name}“ löschen?\n\n• Die Filme bleiben in der Bibliothek und auf der Festplatte.\n• Nur die Sammlung selbst geht verloren.";
        if (!_shell.Confirm("Sammlung löschen?", question, "Löschen", danger: true)) return;
        _model.Db.DeleteCollection(_id);
        _model.ReloadCollections();
        _shell.ShowMessage($"Sammlung „{collection.Name}“ gelöscht.");
        _shell.Back();
    }
}
