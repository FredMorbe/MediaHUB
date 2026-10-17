namespace MediaCenter.Core;

/// <summary>Adressen rund um IMDb, mit denen man ein Poster von Hand findet.</summary>
public static class ImdbLinks
{
    /// <summary>Titelsuche auf imdb.com für den Namen (mit Jahr, wenn bekannt).</summary>
    public static string SearchUrl(string name, int? year)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Titel fehlt.", nameof(name));
        var query = year is { } y ? $"{name.Trim()} {y}" : name.Trim();
        return "https://www.imdb.com/find/?s=tt&q=" + Uri.EscapeDataString(query);
    }

    /// <summary>
    /// IMDb zeigt Poster als Vorschaubild (z. B. „…@._V1_QL75_UX190_CR0,0,190,281_.jpg“); ohne den Größenzusatz hinter „._V1_“ liefert
    /// der Bilderdienst das Original. Andere Adressen bleiben unverändert.
    /// </summary>
    public static string FullSizeImageUrl(string imageUrl)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return imageUrl;
        if (!uri.Host.EndsWith(".media-amazon.com", StringComparison.OrdinalIgnoreCase) && !uri.Host.EndsWith(".ssl-images-amazon.com", StringComparison.OrdinalIgnoreCase)) return imageUrl;

        const string marker = "._V1_";
        var path = uri.AbsolutePath;
        var at = path.IndexOf(marker, StringComparison.Ordinal);
        var dot = path.LastIndexOf('.');
        if (at < 0 || dot <= at + marker.Length - 1) return imageUrl;

        return new UriBuilder(uri) { Path = path[..(at + marker.Length)] + path[dot..], Query = "" }.Uri.ToString();
    }
}
