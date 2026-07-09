using RadioRelay.Client.UI;

namespace RadioRelay.Tests;

public class RadioEncryptionVisualStateTests
{
    [Fact]
    public void Open_state_uses_the_same_muted_color_before_and_after_refresh()
    {
        var state = RadioEncryptionVisualState.ForPasscode("");

        Assert.Equal("OPEN", state.Text);
        Assert.Equal(Theme.MutedText, state.ForeColor);
    }

    [Fact]
    public void Encrypted_state_uses_accent_green()
    {
        var state = RadioEncryptionVisualState.ForPasscode("red");

        Assert.Equal("ENCRYPTED", state.Text);
        Assert.Equal(Theme.AccentGreen, state.ForeColor);
    }
}
