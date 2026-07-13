using System.Drawing;
using RadioRelay.Client.UI;

namespace RadioRelay.Tests;

public class MembershipTooltipLayoutTests
{
    [Fact]
    public void Twenty_four_members_use_readable_columns_without_crossing_margins_or_each_other()
    {
        const int memberCount = 24;
        var layout = MembershipTooltipLayout.Calculate(memberCount, widestText: 176, maxWidth: 760);
        var bounds = Enumerable.Range(0, memberCount).Select(layout.ItemBounds).ToArray();

        Assert.Equal(2, layout.Columns);
        Assert.Equal(12, layout.Rows);
        Assert.All(bounds, item =>
        {
            Assert.True(item.Left >= MembershipTooltipLayout.OuterPadding);
            Assert.True(item.Top >= MembershipTooltipLayout.OuterPadding + MembershipTooltipLayout.HeaderHeight);
            Assert.True(item.Right <= layout.Width - MembershipTooltipLayout.OuterPadding);
            Assert.True(item.Bottom <= layout.Height - MembershipTooltipLayout.OuterPadding);
        });

        for (int first = 0; first < bounds.Length; first++)
        for (int second = first + 1; second < bounds.Length; second++)
            Assert.False(bounds[first].IntersectsWith(bounds[second]));
    }

    [Fact]
    public void Very_long_names_are_constrained_to_a_fixed_readable_column_width()
    {
        var layout = MembershipTooltipLayout.Calculate(itemCount: 25, widestText: 2000, maxWidth: 760);

        Assert.Equal(220, layout.ColumnWidth);
        Assert.True(layout.Width <= 760);
        Assert.True(layout.ItemBounds(24).Right <= layout.Width - MembershipTooltipLayout.OuterPadding);
    }
}
