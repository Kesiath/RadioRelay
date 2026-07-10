using RadioRelay.Client.UI;

namespace RadioRelay.Tests;

public class MainFormLayoutPolicyTests
{
    [Theory]
    [InlineData(500, 680)]
    [InlineData(740, 704)]
    [InlineData(900, 760)]
    public void Content_width_scales_between_readable_minimum_and_centered_maximum(int clientWidth, int expected)
    {
        Assert.Equal(expected, MainFormLayoutPolicy.ContentWidthFor(clientWidth));
    }

    [Theory]
    [InlineData(680, true)]
    [InlineData(720, false)]
    [InlineData(760, false)]
    public void Radio_cards_use_compact_rules_only_below_the_breakpoint(int contentWidth, bool expected)
    {
        Assert.Equal(expected, MainFormLayoutPolicy.UseCompactRadioGrid(contentWidth));
    }

    [Fact]
    public void Main_window_uses_a_fixed_non_compact_geometry()
    {
        Assert.Equal(820, MainFormLayoutPolicy.FixedWindowWidth);
        Assert.Equal(860, MainFormLayoutPolicy.FixedWindowHeight);
        Assert.True(MainFormLayoutPolicy.FixedWindowWidth > MainFormLayoutPolicy.MinimumWindowWidth);
    }

    [Fact]
    public void Main_page_height_budget_keeps_three_radios_and_core_controls_in_one_scroll_stack()
    {
        var budget = MainFormLayoutPolicy.EstimatedMainPageHeight(radioCount: 3);

        Assert.InRange(budget, 1120, 1250);
    }
}
