using MediaCenter.Core;

namespace MediaCenter.Core.Tests;

public sealed class ImdbLinksTests
{
    [Fact]
    public void The_search_url_holds_name_and_year_escaped()
    {
        Assert.Equal("https://www.imdb.com/find/?s=tt&q=The%20Matrix%20Reloaded%202003", ImdbLinks.SearchUrl("The Matrix Reloaded", 2003));
    }

    [Fact]
    public void Without_a_year_only_the_name_is_searched()
    {
        Assert.Equal("https://www.imdb.com/find/?s=tt&q=Das%20Boot", ImdbLinks.SearchUrl("  Das Boot ", null));
    }

    [Fact]
    public void Special_characters_cannot_break_out_of_the_query()
    {
        var url = ImdbLinks.SearchUrl("Tom & Jerry #1?", null);

        Assert.Equal("https://www.imdb.com/find/?s=tt&q=Tom%20%26%20Jerry%20%231%3F", url);
    }

    [Theory]
    [InlineData("https://m.media-amazon.com/images/M/MV5BAbC@._V1_QL75_UX190_CR0,0,190,281_.jpg", "https://m.media-amazon.com/images/M/MV5BAbC@._V1_.jpg")]
    [InlineData("https://m.media-amazon.com/images/M/MV5BAbC@._V1_SX300.jpg", "https://m.media-amazon.com/images/M/MV5BAbC@._V1_.jpg")]
    [InlineData("https://m.media-amazon.com/images/M/MV5BAbC@._V1_.jpg", "https://m.media-amazon.com/images/M/MV5BAbC@._V1_.jpg")]
    [InlineData("https://m.media-amazon.com/images/M/MV5BAbC.png", "https://m.media-amazon.com/images/M/MV5BAbC.png")]
    [InlineData("https://example.org/a@._V1_SX300.jpg", "https://example.org/a@._V1_SX300.jpg")]
    [InlineData("data:image/png;base64,AAAA", "data:image/png;base64,AAAA")]
    public void An_amazon_thumbnail_address_becomes_the_original_and_others_stay(string given, string expected)
    {
        Assert.Equal(expected, ImdbLinks.FullSizeImageUrl(given));
    }

    [Fact]
    public void A_blank_name_is_an_error()
    {
        Assert.Throws<ArgumentException>(() => ImdbLinks.SearchUrl(" ", 2000));
    }
}
