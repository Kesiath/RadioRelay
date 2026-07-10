using System.Drawing;
using RadioRelay.Client.UI;

namespace RadioRelay.Tests;

public class ThemeRoundedRectTests
{
    [Fact]
    public void Rounded_path_stays_inside_exclusive_right_and_bottom_edges()
    {
        var rectangle = new Rectangle(1, 1, 98, 28);
        using var path = Theme.RoundedRect(rectangle, 6);
        var pathBounds = path.GetBounds();

        Assert.True(pathBounds.Right <= rectangle.Right - 1f + 0.01f);
        Assert.True(pathBounds.Bottom <= rectangle.Bottom - 1f + 0.01f);
        Assert.True(pathBounds.Left >= rectangle.Left - 0.01f);
        Assert.True(pathBounds.Top >= rectangle.Top - 0.01f);
    }
}
