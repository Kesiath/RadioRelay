using RadioRelay.Client.UI;

namespace RadioRelay.Tests;

public class NumericTextBoxTests
{
    [Fact]
    public void Value_is_formatted_without_a_spinner_control()
    {
        using var box = new NumericTextBox
        {
            Minimum = 2m,
            Maximum = 999m,
            DecimalPlaces = 3,
            Value = 303.45m
        };

        Assert.Equal(303.45m, box.Value);
        Assert.Equal("303.450", box.Text);
        Assert.Empty(box.Controls.Cast<Control>());
    }

    [Fact]
    public void Value_is_clamped_to_configured_bounds()
    {
        using var box = new NumericTextBox { Minimum = 1m, Maximum = 65535m };

        box.Value = 70000m;

        Assert.Equal(65535m, box.Value);
        Assert.Equal("65535", box.Text);
    }
}
