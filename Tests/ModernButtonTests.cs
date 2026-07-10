using RadioRelay.Client.UI;

namespace RadioRelay.Tests;

public class ModernButtonTests
{
    [Fact]
    public void Modern_button_keeps_native_button_click_contract()
    {
        using var button = new ModernButton { Text = "Connect" };
        var clicks = 0;
        button.Click += (_, _) => clicks++;

        button.PerformClick();

        Assert.Equal(1, clicks);
        Assert.Equal(System.Windows.Forms.FlatStyle.Flat, button.FlatStyle);
    }
}
