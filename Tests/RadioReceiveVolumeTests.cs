using RadioRelay.Client.Radio;

namespace RadioRelay.Tests;

public class RadioReceiveVolumeTests
{
    [Fact]
    public void Radio_receive_volume_supports_zero_to_three_hundred_percent()
    {
        var channel = new RadioChannel();

        channel.Volume = 3f;
        Assert.Equal(3f, channel.Volume);

        channel.Volume = 8f;
        Assert.Equal(RadioChannel.MaxReceiveVolume, channel.Volume);

        channel.Volume = -1f;
        Assert.Equal(0f, channel.Volume);
    }
}
