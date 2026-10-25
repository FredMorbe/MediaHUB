using MediaCenter.Core;
using Microsoft.Data.Sqlite;

namespace MediaCenter.Core.Tests;

public sealed class ProfileAvatarTests : IDisposable
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };

    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-avatar-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryDb _db;

    public ProfileAvatarTests()
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

    [Fact]
    public void A_profile_without_an_upload_has_no_avatar()
    {
        Assert.Null(_db.GetProfileAvatar(LibraryDb.DefaultProfileId));
        Assert.False(_db.GetProfile(LibraryDb.DefaultProfileId).HasAvatar);
    }

    [Fact]
    public void The_avatar_is_saved_per_profile_replaced_and_removed()
    {
        var other = _db.CreateProfile("Zweit", LibraryDb.ProfileColors[1], kids: false);

        _db.SetProfileAvatar(LibraryDb.DefaultProfileId, Png);
        Assert.Equal(Png, _db.GetProfileAvatar(LibraryDb.DefaultProfileId));
        Assert.True(_db.GetProfile(LibraryDb.DefaultProfileId).HasAvatar);
        Assert.False(_db.GetProfile(other).HasAvatar);

        var changed = Png.Concat(new byte[] { 9 }).ToArray();
        _db.SetProfileAvatar(LibraryDb.DefaultProfileId, changed);
        Assert.Equal(changed, _db.GetProfileAvatar(LibraryDb.DefaultProfileId));

        _db.SetProfileAvatar(LibraryDb.DefaultProfileId, null);
        Assert.Null(_db.GetProfileAvatar(LibraryDb.DefaultProfileId));
        Assert.False(_db.GetProfile(LibraryDb.DefaultProfileId).HasAvatar);
    }

    [Fact]
    public void Deleting_the_profile_deletes_its_avatar()
    {
        var other = _db.CreateProfile("Zweit", LibraryDb.ProfileColors[1], kids: false);
        _db.SetProfileAvatar(other, Png);

        _db.DeleteProfile(other);

        using var connection = new SqliteConnection($"Data Source={Path.Combine(_base, "library.db")}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM profile_avatars";
        Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
    }

    [Fact]
    public void Something_that_is_not_a_png_is_refused()
    {
        Assert.Throws<ArgumentException>(() => _db.SetProfileAvatar(LibraryDb.DefaultProfileId, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0 }));
        Assert.Throws<ArgumentException>(() => _db.SetProfileAvatar(LibraryDb.DefaultProfileId, Array.Empty<byte>()));
    }

    [Fact]
    public void An_oversized_image_is_refused()
    {
        var big = new byte[LibraryDb.MaxAvatarBytes + 1];
        Png.CopyTo(big, 0);

        Assert.Throws<ArgumentException>(() => _db.SetProfileAvatar(LibraryDb.DefaultProfileId, big));
    }

    [Fact]
    public void An_unknown_profile_is_an_error()
    {
        Assert.Throws<InvalidOperationException>(() => _db.SetProfileAvatar(999, Png));
    }
}
