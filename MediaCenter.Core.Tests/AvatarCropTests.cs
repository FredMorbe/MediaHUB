using MediaCenter.Core;

namespace MediaCenter.Core.Tests;

public sealed class AvatarCropTests
{
    // Fläche 280, Kreis 240 mit 20 Rand
    private const double Start = 20;
    private const double Circle = 240;

    [Fact]
    public void The_shorter_side_fills_the_circle_at_the_smallest_scale()
    {
        Assert.Equal(0.4, AvatarCrop.MinScale(1200, 600, Circle));
        Assert.Equal(0.4, AvatarCrop.MinScale(600, 1200, Circle));
    }

    [Fact]
    public void An_empty_image_or_circle_is_an_error()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AvatarCrop.MinScale(0, 100, Circle));
        Assert.Throws<ArgumentOutOfRangeException>(() => AvatarCrop.MinScale(100, 100, 0));
    }

    [Fact]
    public void The_image_stays_behind_the_circle_on_every_side()
    {
        var scale = AvatarCrop.MinScale(1200, 600, Circle); // 480 x 240 Anzeigepunkte

        // zu weit nach rechts geschoben: linke Bildkante darf nicht rechts vom Kreisanfang liegen
        Assert.Equal(Start, AvatarCrop.ClampOffset(500, scale, 1200, Start, Circle));
        // zu weit nach links: rechte Bildkante muss mindestens am Kreisende liegen
        Assert.Equal(Start + Circle - 1200 * scale, AvatarCrop.ClampOffset(-900, scale, 1200, Start, Circle));
        // Achse, auf der das Bild genau den Kreis füllt: keine Bewegung möglich
        Assert.Equal(Start, AvatarCrop.ClampOffset(Start + 30, scale, 600, Start, Circle));
        Assert.Equal(Start, AvatarCrop.ClampOffset(Start - 30, scale, 600, Start, Circle));
    }

    [Fact]
    public void The_start_position_is_centered()
    {
        var scale = AvatarCrop.MinScale(1200, 600, Circle);

        var x = AvatarCrop.CenteredOffset(scale, 1200, Start, Circle);

        Assert.Equal(Start + Circle / 2 - 1200 * scale / 2, x);
        Assert.Equal(AvatarCrop.ClampOffset(x, scale, 1200, Start, Circle), x);
    }

    [Fact]
    public void Zooming_keeps_the_point_in_the_middle_of_the_circle()
    {
        const double oldScale = 0.4, newScale = 1.0;
        var offset = AvatarCrop.CenteredOffset(oldScale, 1200, Start, Circle);
        var center = Start + Circle / 2;
        var pointUnderCenter = (center - offset) / oldScale;

        var zoomed = AvatarCrop.ZoomedOffset(offset, oldScale, newScale, Start, Circle);

        Assert.Equal(pointUnderCenter, (center - zoomed) / newScale, 6);
    }

    [Fact]
    public void The_centered_square_of_a_landscape_image_is_the_middle()
    {
        var scale = AvatarCrop.MinScale(1200, 600, Circle);
        var x = AvatarCrop.CenteredOffset(scale, 1200, Start, Circle);
        var y = AvatarCrop.CenteredOffset(scale, 600, Start, Circle);

        var square = AvatarCrop.SourceSquare(1200, 600, scale, x, y, Start, Start, Circle);

        Assert.Equal(new CropSquare(300, 0, 600), square);
    }

    [Fact]
    public void Zooming_in_shrinks_the_square_and_it_stays_inside_the_image()
    {
        var min = AvatarCrop.MinScale(1200, 600, Circle);
        var scale = min * 2;
        // ganz nach links/oben geschoben (Bildkante am Kreisanfang)
        var square = AvatarCrop.SourceSquare(1200, 600, scale, Start, Start, Start, Start, Circle);
        Assert.Equal(new CropSquare(0, 0, 300), square);

        // über den Rand hinaus gerechnet (z. B. durch Rundung): wird ins Bild gezogen
        var outside = AvatarCrop.SourceSquare(1200, 600, scale, Start - 5000, Start - 5000, Start, Start, Circle);
        Assert.Equal(new CropSquare(900, 300, 300), outside);
    }

    [Fact]
    public void The_square_is_never_larger_than_the_image()
    {
        var square = AvatarCrop.SourceSquare(100, 80, 0.5, 0, 0, 0, 0, 240); // Kreis größer als das Bild bei diesem Maßstab

        Assert.Equal(80, square.Size);
        Assert.InRange(square.X, 0, 20);
        Assert.Equal(0, square.Y);
    }
}
