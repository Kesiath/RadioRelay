using RadioRelay.Client;

namespace RadioRelay.Tests;

public class RadioControlLockTests
{
    [Theory]
    [InlineData(false, true, "Lock Controls")]
    [InlineData(true, false, "Unlock Controls")]
    public void Lock_state_controls_whether_radio_tuning_and_ptt_bind_controls_are_enabled(
        bool locked,
        bool expectedProtectedControlEnabled,
        string expectedToggleText)
    {
        var state = RadioControlLock.For(locked);

        Assert.Equal(expectedProtectedControlEnabled, state.ProtectedControlsEnabled);
        Assert.Equal(expectedToggleText, state.ToggleButtonText);
    }

    [Fact]
    public void Lock_prevents_editing_frequency_passcode_and_ptt_bindings_but_not_general_audio_controls()
    {
        var locked = RadioControlLock.For(locked: true);

        Assert.False(locked.CanEditFrequency);
        Assert.False(locked.CanEditPasscode);
        Assert.False(locked.CanChangePttBinding);
        Assert.True(locked.CanAdjustVolume);
    }

    [Fact]
    public void App_settings_persist_control_lock_preference()
    {
        var settings = new AppSettings { ControlLockEnabled = true };

        Assert.True(settings.ControlLockEnabled);
    }
}
