using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MediaCenter.App.Services;
using MediaCenter.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MediaCenter.App.Pages;

/// <summary>
/// Eingebetteter Browser (WebView2, die Edge-Laufzeit von Windows) auf der IMDb-Suche zum Titel. Rechtsklick auf ein Bild bietet
/// „Als Poster verwenden“: die App lädt das Bild selbst, speichert es als eigenes Poster des Titels und geht zurück.
/// Meldungen stehen in der Leiste der Seite, denn ein Browserfenster liegt immer über den Meldungen der App.
/// </summary>
public sealed class PosterBrowserPage : UserControl
{
    private readonly IShell _shell;
    private readonly TitleRow _title;
    private readonly WebView2 _web = new();
    private readonly TextBlock _status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _address = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
    private readonly Button _backward;
    private readonly Button _forward;
    private bool _saving;

    public PosterBrowserPage(IShell shell, TitleRow title)
    {
        _shell = shell;
        _title = title;

        var back = new Button { Content = "", ToolTip = "Zurück (Esc)", Margin = new Thickness(0, 0, 10, 0) };
        back.SetResourceReference(StyleProperty, "IconButton");
        AutomationProperties.SetName(back, "Zurück");
        back.Click += (_, _) => _shell.Back();

        _backward = PageParts.Button("◀ Seite", "GhostButton", (_, _) => _web.GoBack(), new Thickness(0, 0, 8, 0));
        _forward = PageParts.Button("Seite ▶", "GhostButton", (_, _) => _web.GoForward(), new Thickness(0, 0, 8, 0));
        var search = PageParts.Button("IMDb-Suche", "GhostButton", (_, _) => GoToSearch(), new Thickness(0, 0, 8, 0));
        AutomationProperties.SetName(_backward, "Browser zurück");
        AutomationProperties.SetName(_forward, "Browser vor");
        AutomationProperties.SetName(search, "IMDb-Suche");
        foreach (var button in new[] { _backward, _forward, search }) button.Padding = new Thickness(14, 6, 14, 6);
        _backward.IsEnabled = _forward.IsEnabled = false;

        _status.SetResourceReference(StyleProperty, "MutedText");
        _address.SetResourceReference(StyleProperty, "MutedText");
        _status.Text = $"Rechtsklick auf das Poster von „{_title.Name}“ → „Als Poster verwenden“.";

        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = true };
        foreach (var element in new UIElement[] { back, _backward, _forward, search }) DockPanel.SetDock(element, Dock.Left);
        bar.Children.Add(back);
        bar.Children.Add(_backward);
        bar.Children.Add(_forward);
        bar.Children.Add(search);
        bar.Children.Add(_address);

        var note = new Border { Margin = new Thickness(0, 0, 0, 10), Child = _status };

        var top = new StackPanel();
        top.Children.Add(bar);
        top.Children.Add(note);

        var layout = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        layout.Children.Add(top);
        layout.Children.Add(_web);
        Content = layout;

        Loaded += async (_, _) => await StartAsync();
        Unloaded += (_, _) => _web.Dispose();
    }

    private async Task StartAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(AppSettings.DataDirectory, "webview"));
            await _web.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException or COMException or IOException)
        {
            // Ohne die Edge-WebView2-Laufzeit gibt es keinen Browser; die Seite sagt das, statt still leer zu bleiben.
            _web.Visibility = Visibility.Collapsed;
            _status.Text = $"Der eingebettete Browser lässt sich nicht starten: {ex.Message} (Benötigt wird die „Microsoft Edge WebView2-Laufzeit“.)";
            _shell.ShowMessage(_status.Text, isError: true);
            return;
        }

        var core = _web.CoreWebView2;
        core.NewWindowRequested += (_, e) =>
        {
            // Links mit „neuer Tab“ bleiben in dieser Ansicht.
            e.Handled = true;
            core.Navigate(e.Uri);
        };
        core.SourceChanged += (_, _) => _address.Text = core.Source;
        core.HistoryChanged += (_, _) =>
        {
            _backward.IsEnabled = _web.CanGoBack;
            _forward.IsEnabled = _web.CanGoForward;
        };
        await core.AddScriptToExecuteOnDocumentCreatedAsync(RememberImageScript);
        core.ContextMenuRequested += OnContextMenu;
        GoToSearch();
    }

    private void GoToSearch() => _web.CoreWebView2.Navigate(ImdbLinks.SearchUrl(_title.Name, _title.Year));

    /// <summary>
    /// Merkt sich bei jedem Rechtsklick das oberste Bild unter dem Zeiger. IMDb legt über das Poster eine Link-Fläche; der Browser
    /// meldet dort kein Bild, die Seite selbst (elementsFromPoint) sieht es aber durch die Fläche hindurch.
    /// </summary>
    private const string RememberImageScript = """
        document.addEventListener('contextmenu', e => {
          const img = document.elementsFromPoint(e.clientX, e.clientY).find(n => n instanceof HTMLImageElement);
          window.__mcImage = img ? (img.currentSrc || img.src) : '';
        }, true);
        """;

    private async void OnContextMenu(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var target = e.ContextMenuTarget;
            var source = target.Kind == CoreWebView2ContextMenuTargetKind.Image ? target.SourceUri : "";
            if (string.IsNullOrEmpty(source))
            {
                source = JsonSerializer.Deserialize<string>(await _web.CoreWebView2.ExecuteScriptAsync("window.__mcImage || ''")) ?? "";
            }
            if (source.Length == 0) return;

            var item = _web.CoreWebView2.Environment.CreateContextMenuItem($"Als Poster für „{_title.Name}“ verwenden", null, CoreWebView2ContextMenuItemKind.Command);
            item.CustomItemSelected += async (_, _) => await UsePosterAsync(source);
            e.MenuItems.Insert(0, item);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or COMException)
        {
            _status.Text = $"Das Bild unter dem Zeiger ließ sich nicht bestimmen: {ex.Message}";
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>Lädt das Bild (bei IMDb in Originalgröße), speichert es als eigenes Poster und kehrt zur vorigen Seite zurück.</summary>
    internal async Task UsePosterAsync(string imageUrl)
    {
        if (_saving) return;
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            _status.Text = "Dieses Bild liegt nicht auf einer sicheren Webadresse (https) und lässt sich nicht übernehmen.";
            return;
        }

        _saving = true;
        _status.Text = "Poster wird geladen …";
        try
        {
            var services = _shell.Services;
            await services.Model.Artwork.SetUserArtworkFromUrlAsync(services.Images, ArtworkOwner.Title, _title.Id, ArtworkKind.Poster, ImdbLinks.FullSizeImageUrl(imageUrl));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or InvalidOperationException or FileNotFoundException or ArgumentException or TimeoutException or TaskCanceledException)
        {
            _status.Text = $"Das Bild konnte nicht übernommen werden: {ex.Message}";
            _saving = false;
            return;
        }

        _shell.Services.Model.Reload();
        _shell.Back();
        _shell.ShowMessage($"Poster für „{_title.Name}“ übernommen.");
    }
}
