using MediaCenter.Core;

namespace MediaCenter.Core.Tests;

public class FileNameParserTests
{
    [Theory]
    [InlineData(@"D:\Serien\Breaking.Bad.S02E05.1080p.BluRay.x264.mkv", "Breaking Bad", 2, 5)]
    [InlineData(@"D:\Serien\Breaking Bad - s01e10 - Title.mkv", "Breaking Bad", 1, 10)]
    [InlineData(@"D:\Serien\The_Wire_3x07.avi", "The Wire", 3, 7)]
    [InlineData(@"D:\Serien\Dark\Season 2\S02E03.mkv", "Dark", 2, 3)]
    [InlineData(@"D:\Serien\Dark\Staffel 1\S01E01 - Geheimnisse.mkv", "Dark", 1, 1)]
    [InlineData(@"D:\Serien\Show\S01E01.mkv", "Show", 1, 1)]
    public void Parses_episodes(string path, string title, int season, int episode)
    {
        var parsed = FileNameParser.Parse(path);

        Assert.Equal(VideoKind.Episode, parsed.Kind);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(season, parsed.Season);
        Assert.Equal(episode, parsed.Episode);
    }

    [Theory]
    [InlineData(@"D:\Filme\Inception (2010).mkv", "Inception", 2010)]
    [InlineData(@"D:\Filme\The.Matrix.1999.1080p.BluRay.x264.mkv", "The Matrix", 1999)]
    [InlineData(@"D:\Filme\Blade Runner 2049 (2017) [2160p].mkv", "Blade Runner 2049", 2017)]
    [InlineData(@"D:\Filme\2001.A.Space.Odyssey.1968.mkv", "2001 A Space Odyssey", 1968)]
    public void Parses_movies_with_year(string path, string title, int year)
    {
        var parsed = FileNameParser.Parse(path);

        Assert.Equal(VideoKind.Movie, parsed.Kind);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(year, parsed.Year);
    }

    [Theory]
    [InlineData(@"D:\Filme\Das.Boot.German.DL.1080p.BluRay.x264.mkv", "Das Boot")]
    [InlineData(@"D:\Filme\Heat.mkv", "Heat")]
    [InlineData(@"D:\Filme\1080p.Test.mkv", "1080p Test")]
    public void Parses_movies_without_year(string path, string title)
    {
        var parsed = FileNameParser.Parse(path);

        Assert.Equal(VideoKind.Movie, parsed.Kind);
        Assert.Equal(title, parsed.Title);
        Assert.Null(parsed.Year);
    }

    [Fact]
    public void Resolution_in_name_is_not_an_episode()
    {
        var parsed = FileNameParser.Parse(@"D:\Filme\Clip.1920x1080.mkv");

        Assert.Equal(VideoKind.Movie, parsed.Kind);
    }
}
