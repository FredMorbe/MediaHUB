using MediaCenter.Core;

namespace MediaCenter.Core.Tests;

public class VolumeMatcherTests
{
    private static VolumeInfo Mounted(string serial, string root = @"E:\", long capacity = 1000, string fs = "NTFS", string label = "Data") =>
        new(serial, label, fs, capacity, root);

    private static KnownVolume Known(long id, string serial, long capacity = 1000, string fs = "NTFS", string label = "Data") =>
        new(id, serial, label, fs, capacity, label, null, null);

    private static VolumeMatch Match(VolumeInfo mounted, params KnownVolume[] known) =>
        VolumeMatcher.Match(mounted, known, new[] { mounted });

    [Fact]
    public void Unknown_serial_is_new()
    {
        var match = Match(Mounted("AAAA0001"), Known(1, "BBBB0002"));

        Assert.Equal(VolumeMatchKind.New, match.Kind);
    }

    [Fact]
    public void Same_serial_filesystem_and_capacity_is_matched_regardless_of_drive_letter_or_label()
    {
        var match = Match(Mounted("AAAA0001", root: @"G:\", label: "Umbenannt"), Known(7, "AAAA0001", label: "Alt"));

        Assert.Equal(VolumeMatchKind.Matched, match.Kind);
        Assert.Equal(7, match.VolumeId);
    }

    [Fact]
    public void Serial_comparison_ignores_case()
    {
        var match = Match(Mounted("aaaa0001"), Known(1, "AAAA0001"));

        Assert.Equal(VolumeMatchKind.Matched, match.Kind);
    }

    [Theory]
    [InlineData(2000, "NTFS")]
    [InlineData(1000, "exFAT")]
    public void Known_serial_with_different_capacity_or_filesystem_needs_confirmation(long capacity, string fs)
    {
        var match = Match(Mounted("AAAA0001", capacity: capacity, fs: fs), Known(1, "AAAA0001"));

        Assert.Equal(VolumeMatchKind.NeedsConfirmation, match.Kind);
        Assert.Null(match.VolumeId);
        Assert.Single(match.Candidates);
    }

    [Fact]
    public void Two_mounted_volumes_with_same_serial_need_confirmation()
    {
        var a = Mounted("AAAA0001", @"E:\");
        var b = Mounted("AAAA0001", @"F:\");

        var match = VolumeMatcher.Match(a, new[] { Known(1, "AAAA0001") }, new[] { a, b });

        Assert.Equal(VolumeMatchKind.NeedsConfirmation, match.Kind);
    }

    [Fact]
    public void Two_known_volumes_with_same_serial_are_told_apart_by_capacity()
    {
        var match = Match(Mounted("AAAA0001", capacity: 2000), Known(1, "AAAA0001", capacity: 1000), Known(2, "AAAA0001", capacity: 2000));

        Assert.Equal(VolumeMatchKind.Matched, match.Kind);
        Assert.Equal(2, match.VolumeId);
    }
}
