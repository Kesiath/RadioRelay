using RadioRelay.Client.UI;

namespace RadioRelay.Tests;

public class MainFormLayoutPolicyTests
{
    [Fact]
    public void Main_window_keeps_a_fixed_width_but_supports_a_smaller_height()
    {
        Assert.Equal(820, MainFormLayoutPolicy.FixedWindowWidth);
        Assert.Equal(860, MainFormLayoutPolicy.FixedWindowHeight);
        Assert.Equal(760, MainFormLayoutPolicy.MaxContentWidth);
        Assert.InRange(MainFormLayoutPolicy.MinimumWindowHeight, 520, 640);
        Assert.Equal(short.MaxValue, MainFormLayoutPolicy.MaximumWindowHeight);
    }

    [Fact]
    public void Main_page_height_budget_keeps_three_radios_and_core_controls_in_one_scroll_stack()
    {
        var budget = MainFormLayoutPolicy.EstimatedMainPageHeight(radioCount: 3);

        Assert.InRange(budget, 1120, 1250);
    }
}
