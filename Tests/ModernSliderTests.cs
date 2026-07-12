using RadioRelay.Client.UI;

namespace RadioRelay.Tests;

public class ModernSliderTests
{
    [Fact]
    public void Value_is_clamped_and_change_event_only_fires_for_real_changes()
    {
        using var slider = new ModernSlider { Minimum = 0, Maximum = 100, Value = 25 };
        var changes = 0;
        slider.ValueChanged += (_, _) => changes++;

        slider.Value = 25;
        slider.Value = 150;
        slider.Value = -10;

        Assert.Equal(0, slider.Value);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Accent_color_can_follow_each_radios_hud_color()
    {
        using var slider = new ModernSlider();
        var accent = System.Drawing.Color.CornflowerBlue;

        slider.AccentColor = accent;

        Assert.Equal(accent, slider.AccentColor);
    }

    [Fact]
    public void Constructor_supports_the_transparent_background_used_by_the_modern_layout()
    {
        using var slider = new ModernSlider();

        slider.BackColor = System.Drawing.Color.Transparent;

        Assert.Equal(System.Drawing.Color.Transparent, slider.BackColor);
    }

    [Fact]
    public void Mouse_wheel_does_not_change_slider_value()
    {
        using var slider = new ModernSlider { Minimum = 0, Maximum = 100, Value = 50 };
        var method = typeof(ModernSlider).GetMethod(
            "OnMouseWheel",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        method.Invoke(slider, new object[] { new System.Windows.Forms.MouseEventArgs(
            System.Windows.Forms.MouseButtons.None, 0, 0, 0, 120) });

        Assert.Equal(50, slider.Value);
    }
}
