using System.Reflection;

namespace MediaCenter.App.Services;

/// <summary>Die Version der App; sie steht an einer Stelle: <c>&lt;Version&gt;</c> in MediaCenter.App.csproj.</summary>
public static class AppInfo
{
    public static string Version { get; } = ReadVersion();

    /// <summary>Projektadresse; Online-Dienste (MusicBrainz) verlangen im User-Agent einen Kontakt.</summary>
    public const string ProjectUrl = "https://github.com/MatchaLatte88/MediaHUB";

    private static string ReadVersion()
    {
        var attribute = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?? throw new InvalidOperationException("Die App hat keine Versionsangabe (<Version> in MediaCenter.App.csproj).");
        var version = attribute.InformationalVersion;
        var plus = version.IndexOf('+'); // bei Quellcodeverwaltung hängt das SDK „+Commit“ an
        return plus < 0 ? version : version[..plus];
    }
}
