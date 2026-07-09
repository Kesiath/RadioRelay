using RadioRelay.Client.UI;

namespace RadioRelay.Tests;

public class MainFormLayoutPolicyTests
{
    [Theory]
    [InlineData(500, 460)]
    [InlineData(640, 460)]
    [InlineData(900, 460)]
    public void Content_width_scales_to_readable_side_window_width(int clientWidth, int expected)
    {
        Assert.Equal(expected, MainFormLayoutPolicy.ContentWidthFor(clientWidth));
    }

    [Theory]
    [InlineData(460, true)]
    [InlineData(760, false)]
    public void Radio_cards_use_single_column_controls_only_when_width_is_tight(int contentWidth, bool expected)
    {
        Assert.Equal(expected, MainFormLayoutPolicy.UseCompactRadioGrid(contentWidth));
    }

    [Fact]
    public void Main_page_height_budget_keeps_three_radios_and_core_controls_in_side_window_scroll_stack()
    {
        var budget = MainFormLayoutPolicy.EstimatedMainPageHeight(radioCount: 3);

        Assert.InRange(budget, 1, 1030);
    }
}
