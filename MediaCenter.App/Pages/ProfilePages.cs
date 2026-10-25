using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MediaCenter.App.Controls;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App.Pages;

/// <summary>
/// „Wer schaut?“: die Profile als große Kacheln. Ein Profil mit PIN fragt sie ab; aus einem Kinderprofil kommt man nur in Profile mit PIN.
/// Beim Start (<c>startup</c>) lässt sich das Fenster nicht schließen, ohne zu wählen.
/// </summary>
public sealed class ProfilePickerPage : UserControl, IModalContent
{
    private readonly IShell _shell;
    private readonly bool _startup;
    private bool _chosen;

    public string? BlockClose => _startup && !_chosen ? "Bitte erst ein Profil wählen." : null;

    public static void Show(IShell shell, bool startup) =>
        shell.OpenModal(startup ? "Willkommen" : "Profile", "Wer schaut?", new ProfilePickerPage(shell, startup), ModalSize.Medium);

    private ProfilePickerPage(IShell shell, bool startup)
    {
        _shell = shell;
        _startup = startup;
        ShowTiles();
    }

    private ProfileService Profiles => _shell.Services.Profiles;

    private static string Detail(ProfileRow profile, bool active)
    {
        var parts = new List<string>();
        if (active) parts.Add("aktiv");
        if (profile.Kids) parts.Add("Kinder");
        if (profile.HasPin) parts.Add("PIN");
        return parts.Count == 0 ? " " : string.Join(" · ", parts);
    }

    private void ShowTiles()
    {
        var tiles = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var profile in Profiles.All)
        {
            var avatar = ProfileAvatar.Create(profile, 76);
            avatar.HorizontalAlignment = HorizontalAlignment.Center;
            var name = PageParts.Semi(profile.Name, 14);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.Margin = new Thickness(0, 10, 0, 0);
            name.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
            var detail = PageParts.Muted(Detail(profile, profile.Id == Profiles.Current.Id), 11);
            detail.HorizontalAlignment = HorizontalAlignment.Center;
            detail.TextAlignment = TextAlignment.Center;
            var column = new StackPanel { Width = 112, Children = { avatar, name, detail } };
            var captured = profile;
            var tile = PageParts.ClickableRow(column, () => Choose(captured), new Thickness(0, 0, 10, 10), $"Profil {profile.Name}");
            tile.Padding = new Thickness(6, 12, 6, 10);
            tile.CornerRadius = new CornerRadius(14);
            tiles.Children.Add(tile);
        }

        var stack = new StackPanel { Children = { tiles } };
        if (!Profiles.Current.Kids)
        {
            var manage = PageParts.Compact(PageParts.Button("Profile verwalten …", "GhostButton", (_, _) =>
            {
                _chosen = true;
                SettingsPage.SelectTab("Profile");
                _shell.CloseModal();
                _shell.Navigate(Section.Settings);
            }, new Thickness(0, 10, 0, 0)));
            AutomationProperties.SetName(manage, "Profile verwalten");
            stack.Children.Add(manage);
        }
        Content = stack;
    }

    private void Choose(ProfileRow profile)
    {
        var access = Profiles.Check(profile);
        switch (access.Kind)
        {
            case ProfileAccessKind.Allowed:
                Switch(profile, null);
                break;
            case ProfileAccessKind.NeedsPin:
                ShowPin(profile);
                break;
            default:
                _shell.ShowMessage(access.Message!, isError: true);
                break;
        }
    }

    private void Switch(ProfileRow profile, string? pin)
    {
        try
        {
            Profiles.SwitchTo(profile.Id, pin);
        }
        catch (ProfileAccessException ex)
        {
            _shell.ShowMessage(ex.Message, isError: true);
            return;
        }
        _chosen = true;
        _shell.CloseModal();
    }

    private void ShowPin(ProfileRow profile)
    {
        var avatar = ProfileAvatar.Create(profile, 56);
        avatar.HorizontalAlignment = HorizontalAlignment.Left;
        var heading = PageParts.Semi($"PIN für {profile.Name}", 18);
        heading.Margin = new Thickness(0, 12, 0, 12);
        heading.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");

        var box = new PasswordBox { MaxLength = ProfilePin.MaxLength, Width = 200, HorizontalAlignment = HorizontalAlignment.Left, FontSize = 18 };
        AutomationProperties.SetName(box, "PIN");

        void Open() => Switch(profile, box.Password);
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Open();
        };
        var open = PageParts.Button("Öffnen", "PrimaryButton", (_, _) => Open(), new Thickness(0, 0, 10, 0));
        AutomationProperties.SetName(open, "Profil öffnen");
        var back = PageParts.Button("Zurück", "GhostButton", (_, _) => ShowTiles(), new Thickness(0));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0), Children = { open, back } };

        Content = new StackPanel { Children = { avatar, heading, box, buttons } };
        box.Loaded += (_, _) => box.Focus();
    }
}

/// <summary>
/// Ein Profil anlegen oder bearbeiten: Name, Farbe, Erwachsener oder Kind, PIN. Die PIN wirkt sofort; Name, Farbe und Art mit „Speichern“.
/// </summary>
public sealed class ProfileEditorPage : UserControl, IModalContent
{
    private readonly IShell _shell;
    private readonly ProfileRow? _profile;
    private readonly TextBox _name;
    private string _color;
    private bool _kids;
    private byte[]? _avatar;          // neu hochgeladenes Bild, wirkt mit „Speichern“
    private bool _avatarChanged;      // Bild gewählt oder entfernt
    private readonly ContentControl _preview = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private Button? _removeAvatar;

    public string? BlockClose => null;

    /// <param name="profileId">Das Profil; null legt ein neues an.</param>
    public static void Show(IShell shell, long? profileId)
    {
        var profile = profileId is { } id ? shell.Services.Db.GetProfile(id) : null;
        shell.OpenModal("Profile", profile is null ? "Neues Profil" : profile.Name, new ProfileEditorPage(shell, profile), ModalSize.Medium);
    }

    private ProfileEditorPage(IShell shell, ProfileRow? profile)
    {
        _shell = shell;
        _profile = profile;
        _color = profile?.Color ?? LibraryDb.ProfileColors.First(c => !shell.Services.Db.ListProfiles().Any(p => p.Color == c));
        _kids = profile?.Kids ?? false;

        var (nameField, nameBox) = PageParts.HintBox("Name", double.NaN);
        _name = nameBox;
        _name.MaxLength = LibraryDb.MaxProfileNameLength;
        _name.Text = profile?.Name ?? "";
        AutomationProperties.SetName(_name, "Name des Profils");
        _name.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Save();
        };

        var stack = new StackPanel { Children = { Label("Name"), nameField, Label("Bild"), AvatarPicker(), Label("Farbe"), Colors(), Label("Art"), Kind() } };

        if (profile is not null && !profile.Kids) stack.Children.Add(PinSection(profile));

        var save = PageParts.Button(profile is null ? "Anlegen" : "Speichern", "PrimaryButton", (_, _) => Save(), new Thickness(0, 22, 0, 0));
        AutomationProperties.SetName(save, "Profil speichern");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { save } };
        if (profile is not null)
        {
            var delete = PageParts.Button("Profil löschen …", "DangerButton", (_, _) => Delete(profile), new Thickness(0, 22, 0, 0));
            AutomationProperties.SetName(delete, "Profil löschen");
            row.Children.Add(delete);
        }
        stack.Children.Add(row);
        Content = stack;
    }

    private static TextBlock Label(string text)
    {
        var label = PageParts.Muted(text, 12);
        label.Margin = new Thickness(0, 14, 0, 4);
        return label;
    }

    /// <summary>Das Bild des Profils: Vorschau, „Bild wählen …“ (Upload) und „Bild entfernen“. Ohne Bild zeigt das Profil seinen Farbkreis.</summary>
    private UIElement AvatarPicker()
    {
        var choose = PageParts.Compact(PageParts.Button("Bild wählen …", "GhostButton", (_, _) => ChooseAvatar(), new Thickness(14, 0, 0, 0)));
        AutomationProperties.SetName(choose, "Profilbild wählen");
        _removeAvatar = PageParts.Compact(PageParts.Button("Bild entfernen", "GhostButton", (_, _) =>
        {
            _avatar = null;
            _avatarChanged = true;
            RefreshAvatar();
        }, new Thickness(8, 0, 0, 0)));
        AutomationProperties.SetName(_removeAvatar, "Profilbild entfernen");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { choose, _removeAvatar } };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { _preview, buttons } };
        var note = PageParts.Muted("Ein Foto oder Bild (PNG, JPG, BMP, GIF, TIFF); die App schneidet die Mitte quadratisch zu. Ohne Bild zeigt das Profil seinen Farbkreis mit dem Anfangsbuchstaben.", 12);
        note.Margin = new Thickness(0, 6, 0, 0);
        RefreshAvatar();
        return new StackPanel { Children = { row, note } };
    }

    /// <summary>Zeigt in der Vorschau das gewählte Bild, sonst das gespeicherte, sonst den Farbkreis in der gewählten Farbe.</summary>
    private void RefreshAvatar()
    {
        var showsImage = _avatarChanged ? _avatar is not null : _profile?.HasAvatar == true;
        if (_avatarChanged && _avatar is not null) _preview.Content = ProfileAvatar.Create(_avatar, 64);
        else if (showsImage) _preview.Content = ProfileAvatar.Create(_profile!, 64);
        else _preview.Content = ProfileAvatar.Create(_name.Text, _color, 64);
        if (_removeAvatar is not null) _removeAvatar.IsEnabled = showsImage;
    }

    private void ChooseAvatar()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Profilbild wählen",
            Filter = "Bilder|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|Alle Dateien|*.*",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            _avatar = AvatarImage.FromFile(dialog.FileName);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _shell.ShowMessage(ex.Message, isError: true);
            return;
        }
        _avatarChanged = true;
        RefreshAvatar();
    }

    private UIElement Colors()
    {
        var row = new WrapPanel();
        foreach (var color in LibraryDb.ProfileColors)
        {
            var dot = ProfileAvatar.Create("", color, 18);
            dot.Margin = new Thickness(0, 0, 8, 0);
            dot.VerticalAlignment = VerticalAlignment.Center;
            var chip = new RadioButton { Content = dot, GroupName = "profile-color", IsChecked = color == _color, Padding = new Thickness(11, 6, 3, 6) };
            chip.SetResourceReference(StyleProperty, "Chip");
            AutomationProperties.SetName(chip, $"Farbe {color}");
            var captured = color;
            chip.Checked += (_, _) =>
            {
                _color = captured;
                RefreshAvatar(); // der Farbkreis in der Vorschau folgt der Farbe
            };
            row.Children.Add(chip);
        }
        return row;
    }

    private UIElement Kind()
    {
        var row = new WrapPanel();
        foreach (var (text, kids) in new[] { ("Erwachsener", false), ("Kind", true) })
        {
            var chip = new RadioButton { Content = text, GroupName = "profile-kind", IsChecked = _kids == kids };
            chip.SetResourceReference(StyleProperty, "Chip");
            AutomationProperties.SetName(chip, $"Art {text}");
            var captured = kids;
            chip.Checked += (_, _) => _kids = captured;
            row.Children.Add(chip);
        }
        var note = PageParts.Muted("Ein Kinderprofil sieht nur Titel, die du dafür freigegeben hast (Einstellungen → Profile), und keine Verwaltung, Musik, Radio oder Podcasts. " +
            "Zurück kommt man nur mit der PIN eines Erwachsenen-Profils; dafür muss mindestens eins eine PIN haben.", 12);
        note.Margin = new Thickness(0, 2, 0, 0);
        return new StackPanel { Children = { row, note } };
    }

    private UIElement PinSection(ProfileRow profile)
    {
        var box = new PasswordBox { MaxLength = ProfilePin.MaxLength, Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(box, "Neue PIN");
        var set = PageParts.Compact(PageParts.Button(profile.HasPin ? "PIN ändern" : "PIN setzen", "GhostButton", (_, _) =>
        {
            if (!TryDb(() => _shell.Services.Db.SetPin(profile.Id, box.Password))) return;
            box.Clear();
            _shell.Services.Profiles.Refresh();
            _shell.CloseModal();
            _shell.ShowMessage($"PIN von „{profile.Name}“ gespeichert.");
        }, new Thickness(10, 0, 0, 0)));
        AutomationProperties.SetName(set, "PIN speichern");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { box, set } };
        if (profile.HasPin)
        {
            var remove = PageParts.Compact(PageParts.Button("PIN entfernen", "GhostButton", (_, _) =>
            {
                if (!TryDb(() => _shell.Services.Db.SetPin(profile.Id, null))) return;
                _shell.Services.Profiles.Refresh();
                _shell.CloseModal();
                _shell.ShowMessage($"„{profile.Name}“ hat jetzt keine PIN mehr.");
            }, new Thickness(8, 0, 0, 0)));
            AutomationProperties.SetName(remove, "PIN entfernen");
            row.Children.Add(remove);
        }
        var note = PageParts.Muted("Vier bis acht Ziffern. Die PIN hält vom Wechseln in dieses Profil ab; sie verschlüsselt nichts – wer an den Datenordner kommt, sieht die Bibliothek trotzdem.", 12);
        note.Margin = new Thickness(0, 6, 0, 0);
        return new StackPanel { Children = { Label(profile.HasPin ? "PIN (gesetzt)" : "PIN (keine)"), row, note } };
    }

    /// <summary>Führt eine Datenbankänderung aus; erwartbare Fehler (Regeln der Profile, ungültige Eingabe) stehen als Meldung, das Fenster bleibt offen.</summary>
    private bool TryDb(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _shell.ShowMessage(ex.Message, isError: true);
            return false;
        }
    }

    private void Save()
    {
        var db = _shell.Services.Db;
        var name = _name.Text;

        if (_profile is null)
        {
            if (!TryDb(() =>
                {
                    var id = db.CreateProfile(name, _color, _kids);
                    if (_avatarChanged && _avatar is not null) db.SetProfileAvatar(id, _avatar);
                })) return;
            _shell.Services.Profiles.Refresh();
            _shell.CloseModal();
            _shell.ShowMessage($"Profil „{name.Trim()}“ angelegt.");
            return;
        }

        if (!TryDb(() =>
            {
                if (name.Trim() != _profile.Name) db.RenameProfile(_profile.Id, name);
                if (_color != _profile.Color) db.SetProfileColor(_profile.Id, _color);
                if (_kids != _profile.Kids) db.SetProfileKids(_profile.Id, _kids);
                if (_avatarChanged)
                {
                    db.SetProfileAvatar(_profile.Id, _avatar);
                    ProfileAvatar.Forget(_profile.Id);
                }
            })) return;

        _shell.Services.Profiles.Refresh();
        _shell.CloseModal();
        _shell.ShowMessage($"Profil „{name.Trim()}“ gespeichert.");
    }

    private void Delete(ProfileRow profile)
    {
        var question = $"Das Profil „{profile.Name}“ löschen?\n\n" +
                       "• Sein Fortschritt, „Meine Liste“ und der Podcast-Fortschritt gehen verloren.\n" +
                       "• Titel, Cover, Sammlungen und Playlists bleiben; sie gehören allen.";
        if (!_shell.Confirm("Profil löschen?", question, "Löschen", danger: true)) return;
        if (!TryDb(() => _shell.Services.Db.DeleteProfile(profile.Id))) return;
        ProfileAvatar.Forget(profile.Id);

        _shell.Services.Profiles.Refresh();
        _shell.CloseModal();
        _shell.ShowMessage($"Profil „{profile.Name}“ gelöscht.");
    }
}

/// <summary>Einstellungen → Profile: alle Profile, neues Profil, „Wer schaut?“ beim Start und die Freigaben für Kinderprofile.</summary>
public sealed class ProfileSettingsPanel : UserControl
{
    private readonly IShell _shell;

    public ProfileSettingsPanel(IShell shell)
    {
        _shell = shell;
        Build();
        Loaded += (_, _) => _shell.Services.Profiles.Edited += Build;
        Unloaded += (_, _) => _shell.Services.Profiles.Edited -= Build;
    }

    private void Build()
    {
        var profiles = _shell.Services.Profiles;
        var settings = _shell.Services.Settings;
        var list = new StackPanel();
        foreach (var profile in profiles.All)
        {
            var avatar = ProfileAvatar.Create(profile, 40);
            var name = PageParts.Semi(profile.Name, 15);
            name.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
            var details = new List<string> { profile.Kids ? "Kinderprofil" : "Erwachsener" };
            if (profile.HasPin) details.Add("mit PIN");
            if (profile.Id == profiles.Current.Id) details.Add("aktiv");
            var info = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { name, PageParts.Muted(string.Join(" · ", details), 12) } };
            var edit = PageParts.Compact(PageParts.Button("Bearbeiten …", "GhostButton", (_, _) => ProfileEditorPage.Show(_shell, profile.Id), new Thickness(0)));
            AutomationProperties.SetName(edit, $"Profil bearbeiten: {profile.Name}");
            edit.VerticalAlignment = VerticalAlignment.Center;

            var grid = new Grid { Margin = new Thickness(0, 12, 0, 12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(info, 1);
            Grid.SetColumn(edit, 2);
            grid.Children.Add(avatar);
            grid.Children.Add(info);
            grid.Children.Add(edit);
            list.Children.Add(grid);
        }

        var add = PageParts.Compact(PageParts.Button("Neues Profil …", "PrimaryButton", (_, _) => ProfileEditorPage.Show(_shell, null), new Thickness(0, 4, 0, 14)));
        AutomationProperties.SetName(add, "Neues Profil");
        list.Children.Add(add);

        var ask = PageParts.Segmented("Beim Start fragen", new[] { ("An", settings.AskProfileAtStart, (Action)(() => Save(true))), ("Aus", !settings.AskProfileAtStart, (Action)(() => Save(false))) });
        var kids = PageParts.Compact(PageParts.Button("Titel auswählen …", "GhostButton", (_, _) => KidsTitlesPage.Show(_shell), new Thickness(0)));
        AutomationProperties.SetName(kids, "Kinderfreigaben");

        var note = PageParts.Muted(
            "Jedes Profil hat eigenen Fortschritt („Weiterschauen“, gesehen), eigene „Meine Liste“ und eigenen Podcast-Fortschritt. " +
            "Titel, Cover, Sammlungen, Playlists und Untertitel-, Farb- und Spracheinstellungen teilen sich alle Profile. " +
            "Eine PIN hält vom Wechseln ab, sie verschlüsselt nichts.", 13);
        note.Margin = new Thickness(4, 0, 0, 0);

        Content = new StackPanel
        {
            Children =
            {
                PageParts.Group(PageParts.SettingRow("Profile", null, list, stacked: true)),
                PageParts.Group(
                    PageParts.SettingRow("„Wer schaut?“ beim Start",
                        "Mit mehr als einem Profil fragt die App beim Start. Auch bei „Aus“ fragt sie, wenn das zuletzt benutzte Profil eine PIN hat.", ask),
                    PageParts.SettingRow("Freigaben für Kinderprofile",
                        "Kinderprofile sehen nur die Titel, die du hier freigibst (oder per Rechtsklick auf einen Titel).", kids)),
                note,
            },
        };
    }

    private void Save(bool ask)
    {
        _shell.Services.Settings.AskProfileAtStart = ask;
        _shell.Services.Settings.Save();
    }
}

/// <summary>Welche Titel Kinderprofile sehen dürfen: Liste aller Titel mit Schalter, Suchfeld und „alle sichtbaren freigeben / sperren“.</summary>
public sealed class KidsTitlesPage : UserControl, IModalContent
{
    private const int MaxRows = 150;

    private readonly IShell _shell;
    private readonly StackPanel _list = new();
    private readonly TextBox _search;
    private IReadOnlyList<TitleRow> _shown = Array.Empty<TitleRow>();

    public string? BlockClose => null;

    public static void Show(IShell shell) => shell.OpenModal("Profile", "Freigaben für Kinderprofile", new KidsTitlesPage(shell), ModalSize.Large);

    private KidsTitlesPage(IShell shell)
    {
        _shell = shell;
        var (field, box) = PageParts.HintBox("Titel suchen", double.NaN);
        _search = box;
        AutomationProperties.SetName(_search, "Titel suchen");
        _search.TextChanged += (_, _) => Fill();

        var all = PageParts.Compact(PageParts.Button("Alle angezeigten freigeben", "GhostButton", (_, _) => SetAll(true), new Thickness(0, 0, 8, 0)));
        var none = PageParts.Compact(PageParts.Button("Alle angezeigten sperren", "GhostButton", (_, _) => SetAll(false), new Thickness(0)));
        AutomationProperties.SetName(all, "Alle angezeigten freigeben");
        AutomationProperties.SetName(none, "Alle angezeigten sperren");
        var bulk = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 6), Children = { all, none } };

        var hint = PageParts.Muted("Leuchtet „Freigegeben“, sehen Kinderprofile diesen Titel (bei Serien alle Folgen).", 13);
        hint.Margin = new Thickness(0, 0, 0, 10);
        Content = new StackPanel { Children = { hint, field, bulk, _list } };
        Fill();
    }

    private LibraryModel Model => _shell.Services.Model;

    private void Fill()
    {
        var text = _search.Text.Trim();
        // Der Erwachsene sieht alle Titel; die Momentaufnahme ist nur im Kinderprofil gefiltert, und hier sitzt immer ein Erwachsener.
        var matches = Model.Titles
            .Where(t => text.Length == 0 || t.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _shown = matches.Take(MaxRows).ToList();

        _list.Children.Clear();
        foreach (var title in _shown)
        {
            var name = PageParts.Semi(title.Kind == TitleKind.Movie && title.Year is { } year ? $"{title.Name} ({year})" : title.Name, 14);
            name.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
            var kind = PageParts.Muted(title.Kind == TitleKind.Movie ? "Film" : "Serie", 12);
            var toggle = new ToggleButton { Content = "Freigegeben", IsChecked = Model.KidsTitleIds.Contains(title.Id), VerticalAlignment = VerticalAlignment.Center };
            toggle.SetResourceReference(StyleProperty, "Chip");
            AutomationProperties.SetName(toggle, $"Freigabe {title.Name}");
            var id = title.Id;
            toggle.Click += (_, _) => Apply(new[] { id }, toggle.IsChecked == true);

            var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            kind.VerticalAlignment = VerticalAlignment.Center;
            kind.Margin = new Thickness(0, 0, 16, 0);
            name.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(kind, 1);
            Grid.SetColumn(toggle, 2);
            grid.Children.Add(name);
            grid.Children.Add(kind);
            grid.Children.Add(toggle);
            _list.Children.Add(grid);
        }
        if (matches.Count > MaxRows)
        {
            var more = PageParts.Muted($"{matches.Count - MaxRows} weitere Titel: Suche eingrenzen.", 12);
            more.Margin = new Thickness(0, 10, 0, 0);
            _list.Children.Add(more);
        }
        if (matches.Count == 0) _list.Children.Add(PageParts.Muted(text.Length == 0 ? "Es gibt noch keine Titel." : "Kein Titel gefunden.", 13));
    }

    private void Apply(IReadOnlyCollection<long> ids, bool on)
    {
        try
        {
            Model.SetKidsTitles(ids, on);
        }
        catch (InvalidOperationException ex)
        {
            _shell.ShowMessage(ex.Message, isError: true);
        }
    }

    private void SetAll(bool on)
    {
        if (_shown.Count == 0) return;
        Apply(_shown.Select(t => t.Id).ToList(), on);
        Fill();
    }
}
