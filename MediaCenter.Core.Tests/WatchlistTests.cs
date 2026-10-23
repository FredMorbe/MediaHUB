using MediaCenter.Core;
using Microsoft.Data.Sqlite;

namespace MediaCenter.Core.Tests;

public sealed class WatchlistTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-watchlist-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryDb _db;

    public WatchlistTests()
    {
        Directory.CreateDirectory(_base);
        _db = new LibraryDb(Path.Combine(_base, "library.db"));
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_base, recursive: true);
    }

    private long Movie(string name) => _db.CreateTitle(TitleKind.Movie, name, 2020);

    [Fact]
    public void A_new_database_has_an_empty_list()
    {
        Assert.Empty(_db.GetWatchlist());
    }

    [Fact]
    public void Titles_are_listed_newest_first_and_can_be_taken_off_again()
    {
        var a = Movie("A");
        var b = Movie("B");
        var c = _db.CreateTitle(TitleKind.Series, "C");

        _db.SetOnWatchlist(a, true);
        _db.SetOnWatchlist(b, true);
        _db.SetOnWatchlist(c, true);
        Assert.Equal(new[] { c, b, a }, _db.GetWatchlist());

        _db.SetOnWatchlist(b, false);
        Assert.Equal(new[] { c, a }, _db.GetWatchlist());
    }

    [Fact]
    public void Adding_twice_keeps_the_original_position_and_removing_a_title_that_is_not_listed_is_harmless()
    {
        var a = Movie("A");
        var b = Movie("B");
        _db.SetOnWatchlist(a, true);
        _db.SetOnWatchlist(b, true);

        _db.SetOnWatchlist(a, true); // schon drauf: rückt nicht nach vorn
        _db.SetOnWatchlist(Movie("C"), false);

        Assert.Equal(new[] { b, a }, _db.GetWatchlist());
    }

    [Fact]
    public void Adding_a_title_that_does_not_exist_fails_loudly()
    {
        Assert.Throws<SqliteException>(() => _db.SetOnWatchlist(424242, true));
        Assert.Empty(_db.GetWatchlist());
    }

    [Fact]
    public void Removing_a_title_from_the_library_takes_it_off_the_list()
    {
        var a = Movie("A");
        var b = Movie("B");
        _db.SetOnWatchlist(a, true);
        _db.SetOnWatchlist(b, true);

        _db.RemoveTitle(a);

        Assert.Equal(new[] { b }, _db.GetWatchlist());
    }

    [Fact]
    public void Merging_moves_the_list_entry_to_the_target()
    {
        var from = Movie("Alt");
        var into = Movie("Neu");
        _db.SetOnWatchlist(from, true);

        _db.MergeTitles(from, into);

        Assert.Equal(new[] { into }, _db.GetWatchlist());
    }

    [Fact]
    public void Merging_keeps_the_target_entry_when_both_were_listed()
    {
        var from = Movie("Alt");
        var into = Movie("Neu");
        var other = Movie("Dazwischen");
        _db.SetOnWatchlist(from, true);
        _db.SetOnWatchlist(other, true);
        _db.SetOnWatchlist(into, true);

        _db.MergeTitles(from, into);

        Assert.Equal(new[] { into, other }, _db.GetWatchlist()); // der Eintrag des Ziels behält seinen Platz
    }

    [Fact]
    public void Merging_does_nothing_to_the_list_when_the_source_was_not_listed()
    {
        var from = Movie("Alt");
        var into = Movie("Neu");

        _db.MergeTitles(from, into);

        Assert.Empty(_db.GetWatchlist());
    }

    [Fact]
    public void Database_at_schema_10_keeps_its_data_and_gets_a_working_list()
    {
        var path = Path.Combine(_base, "migrate10.db");
        long title;
        using (var db = new LibraryDb(path))
        {
            title = db.CreateTitle(TitleKind.Movie, "Solo", 2020);
            db.CreatePlaylist("Mix");
        }
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DROP TABLE episode_info; DROP TABLE title_info; DROP TABLE album_artists; DROP TABLE track_artists; DROP TABLE album_names; DROP TABLE track_names; DROP TABLE watchlist; ALTER TABLE color_settings DROP COLUMN lut; ALTER TABLE color_settings DROP COLUMN lut_strength; PRAGMA user_version = 10;";
            cmd.ExecuteNonQuery();
        }

        using var migrated = new LibraryDb(path);

        Assert.Equal("Mix", migrated.ListPlaylists().Single().Name);
        Assert.Empty(migrated.GetWatchlist());
        migrated.SetOnWatchlist(title, true);
        Assert.Equal(new[] { title }, migrated.GetWatchlist());
    }
}
