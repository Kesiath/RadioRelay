using RadioRelay.Client.UI;

namespace RadioRelay.Tests;

public class MainFormLayoutFeatureTests
{
    [Fact]
    public void Radio_cards_budget_room_for_readable_vertical_controls()
    {
        Assert.InRange(MainFormLayoutPolicy.RadioCardHeight, 180, 210);
    }

    [Fact]
    public void Setup_strip_keeps_text_fields_from_clipping()
    {
        Assert.InRange(MainFormLayoutPolicy.SetupStripHeight, 126, 150);
    }

    [Fact]
    public void Minimum_window_width_supports_readable_side_monitor_window()
    {
        Assert.InRange(MainFormLayoutPolicy.MinimumWindowWidth, 500, 540);
    }

    [Fact]
    public void Radio_header_reserves_breathing_room_for_activity_badge()
    {
        Assert.InRange(MainFormLayoutPolicy.RadioActivityBadgeColumnWidth, 76, 96);
    }

    [Fact]
    public void Radio_header_breathing_room_sits_to_left_of_right_aligned_activity_badge()
    {
        Assert.Equal(56, MainFormLayoutPolicy.RadioActivityBadgeWidth);
        Assert.True(MainFormLayoutPolicy.RadioActivityBadgeColumnWidth > MainFormLayoutPolicy.RadioActivityBadgeWidth);
    }

    [Fact]
    public void Radio_title_font_is_larger_than_top_wordmark_title_font()
    {
        Assert.True(Theme.RadioTitleFont.Size > Theme.TitleFont.Size);
    }
}
