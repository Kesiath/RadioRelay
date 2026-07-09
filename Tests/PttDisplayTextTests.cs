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
}
