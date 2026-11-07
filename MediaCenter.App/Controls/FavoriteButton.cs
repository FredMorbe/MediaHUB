using System.Windows;
using System.Windows.Controls;
using MediaCenter.App.Services;

namespace MediaCenter.App.Controls;

/// <summary>
/// Herz eines Musiktitels: gefüllt in der Akzentfarbe, wenn der Titel ein Favorit ist, sonst ein gedämpfter Umriss. Ein Klick schaltet um.
/// Der Knopf hält sich selbst aktuell, solange er im Fenster steht (auch wenn derselbe Titel an anderer Stelle umgeschaltet wird).
/// </summary>
public sealed class FavoriteButton : Button
{
    public const string OffGlyph = "\uEB51";
    public const string OnGlyph = "\uEB52";

    private readonly LibraryModel _model;
    private long _trackId;

    public FavoriteButton(LibraryModel model, long trackId, double size = 30)
    {
        _model = model;
        _trackId = trackId;
        Width = Height = size;
        FontSize = 14;
        SetResourceReference(StyleProperty, "IconButton");
        Click += (_, _) => _model.SetFavorite(_trackId, !_model.IsFavorite(_trackId));
        Loaded += (_, _) =>
        {
            _model.FavoritesChanged += OnChanged;
            Update();
        };
        Unloaded += (_, _) => _model.FavoritesChanged -= OnChanged;
        Update();
    }

    /// <summary>Zeigt den Stand eines anderen Titels (für Anzeigen, die ihren Titel wechseln, etwa die Musikleiste).</summary>
    public void SetTrack(long trackId)
    {
        _trackId = trackId;
        Update();
    }

    private void OnChanged(long trackId)
    {
        if (trackId == _trackId) Update();
    }

    private void Update()
    {
        var on = _model.IsFavorite(_trackId);
        Content = on ? OnGlyph : OffGlyph;
        SetResourceReference(ForegroundProperty, on ? "AccentBrush" : "MutedBrush");
        ToolTip = on ? "Aus Favoriten entfernen" : "Zu Favoriten hinzufügen";
        System.Windows.Automation.AutomationProperties.SetName(this, on ? "Aus Favoriten entfernen" : "Zu Favoriten hinzufügen");
    }
}
