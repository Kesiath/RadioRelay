using System.Drawing;
using RadioRelay.Client.UI;

namespace RadioRelay.Tests;

public class ModernScrollBarTests
{
    [Fact]
    public void Thumb_fills_the_track_when_content_does_not_overflow()
    {
        var thumb = ModernScrollBar.CalculateThumbBounds(new Size(14, 200), maximum: 0, largeChange: 200, value: 0);

        Assert.Equal(3, thumb.Top);
        Assert.Equal(194, thumb.Height);
    }

    [Fact]
    public void Thumb_position_tracks_the_clamped_scroll_value()
    {
        var start = ModernScrollBar.CalculateThumbBounds(new Size(14, 300), maximum: 900, largeChange: 300, value: 0);
        var middle = ModernScrollBar.CalculateThumbBounds(new Size(14, 300), maximum: 900, largeChange: 300, value: 450);
        var end = ModernScrollBar.CalculateThumbBounds(new Size(14, 300), maximum: 900, largeChange: 300, value: 900);

        Assert.True(start.Top < middle.Top);
        Assert.True(middle.Top < end.Top);
        Assert.Equal(297, end.Bottom);
    }
}
