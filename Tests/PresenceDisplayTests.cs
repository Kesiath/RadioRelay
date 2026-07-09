using RadioRelay.Client;
using RadioRelay.Client.Radio;
using RadioRelay.Shared.Protocol;

namespace RadioRelay.Tests;

public class PresenceDisplayTests
{
    [Fact]
    public void CountFor_matches_frequency_and_key()
    {
        var open = new RadioChannel { Frequency = 251.000f, Passcode = "" };
        var keyed = new RadioChannel { Frequency = 251.000f, Passcode = "secret" };
        var keyedHash = keyed.SelectedNet.NetIdHash;
        var counts = new[]
        {
            new PresenceChannelCount { Frequency = 251.004f, NetIdHash = new byte[8], UserCount = 3 },
            new PresenceChannelCount { Frequency = 251.000f, NetIdHash = keyedHash, UserCount = 2 }
        };

        Assert.Equal(3, PresenceDisplay.CountFor(open, counts));
        Assert.Equal(2, PresenceDisplay.CountFor(keyed, counts));
    }

    [Fact]
    public void FormatCount_uses_singular_and_plural_users()
    {
        Assert.Equal("1 user", PresenceDisplay.FormatCount(1));
        Assert.Equal("4 users", PresenceDisplay.FormatCount(4));
    }
}
