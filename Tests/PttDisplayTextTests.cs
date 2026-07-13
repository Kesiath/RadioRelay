using RadioRelay.Client;

namespace RadioRelay.Tests;

public class PttDisplayTextTests
{
    [Theory]
    [InlineData("Keyboard: Caps Lock", "Caps Lock")]
    [InlineData("Keyboard: Space", "Space")]
    [InlineData("Mouse Button 4", "Mouse Button 4")]
    [InlineData("Joystick button 3", "Joy Btn 3")]
    [InlineData("Unbound", "Unbound")]
    public void Compact_ptt_display_prioritizes_actual_bind_over_device_type_prefix(string displayName, string expected)
    {
        Assert.Equal(expected, MainForm.CompactPttDisplayName(displayName));
    }

    [Fact]
    public void Icp_binding_display_includes_the_input_type_and_actual_control()
    {
        Assert.Equal("Key F10", MainForm.CompactIcpBindingName(new RadioRelay.Client.Input.PttBinding
        {
            Type = RadioRelay.Client.Input.PttBindingType.Keyboard,
            KeyCode = (int)System.Windows.Forms.Keys.F10,
            DisplayName = "Clear"
        }));
        Assert.Equal("Mouse 4", MainForm.CompactIcpBindingName(new RadioRelay.Client.Input.PttBinding
        {
            Type = RadioRelay.Client.Input.PttBindingType.MouseButton,
            ButtonIndex = RadioRelay.Client.Input.MousePttButtons.XButton1
        }));
        Assert.Equal("Joy 3", MainForm.CompactIcpBindingName(new RadioRelay.Client.Input.PttBinding
        {
            Type = RadioRelay.Client.Input.PttBindingType.JoystickButton,
            ButtonIndex = 2
        }));
    }
}
