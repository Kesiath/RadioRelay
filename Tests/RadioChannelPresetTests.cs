using RadioRelay.Client.Radio;

namespace RadioRelay.Tests;

public class RadioChannelPresetTests
{
    [Fact]
    public void Unprogrammed_channels_use_the_radios_standard_frequency_and_open_key()
    {
        var radio = new RadioChannel();
        radio.ConfigurePresets(305.000f);

        radio.SelectChannel(9);

        Assert.Equal(RadioChannel.PresetCount, radio.GetPresetSnapshot().Count);
        Assert.Equal(9, radio.SelectedChannel);
        Assert.Equal(305.000f, radio.Frequency, precision: 3);
        Assert.Equal(string.Empty, radio.Passcode);
    }

    [Fact]
    public void Switching_channels_saves_and_restores_frequency_and_key()
    {
        var radio = new RadioChannel();
        radio.ConfigurePresets(251.000f);
        radio.SetActiveFrequency(251.500f);
        radio.SetActivePasscode("guard");

        radio.SelectChannel(2);
        Assert.Equal(251.000f, radio.Frequency, precision: 3);
        Assert.Equal(string.Empty, radio.Passcode);
        radio.SetActiveFrequency(260.250f);
        radio.SetActivePasscode("strike");

        radio.SelectChannel(1);
        Assert.Equal(251.500f, radio.Frequency, precision: 3);
        Assert.Equal("guard", radio.Passcode);
        radio.SelectChannel(2);
        Assert.Equal(260.250f, radio.Frequency, precision: 3);
        Assert.Equal("strike", radio.Passcode);
    }

    [Fact]
    public void Legacy_single_channel_settings_migrate_to_channel_one()
    {
        var radio = new RadioChannel();
        radio.ConfigurePresets(100.000f, legacyFrequency: 120.500f, legacyPasscode: "legacy");

        Assert.Equal(120.500f, radio.Frequency, precision: 3);
        Assert.Equal("legacy", radio.Passcode);
        radio.SelectChannel(2);
        Assert.Equal(100.000f, radio.Frequency, precision: 3);
        Assert.Equal(string.Empty, radio.Passcode);
    }

    [Fact]
    public void Channel_names_follow_their_presets_and_appear_with_the_channel_number()
    {
        var radio = new RadioChannel();
        radio.ConfigurePresets(251.000f);
        radio.SetActiveChannelName("Guard");

        radio.SelectChannel(2);
        radio.SetActiveChannelName("Strike");

        Assert.Equal("2 — Strike", radio.GetChannelDisplayName(2));
        radio.SelectChannel(1);
        Assert.Equal("Guard", radio.SelectedChannelName);
        Assert.Equal("1 — Guard", radio.GetChannelDisplayName(1));
        Assert.Equal("3", radio.GetChannelDisplayName(3));
    }
}
