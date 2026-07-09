using RadioRelay.Client;

namespace RadioRelay.Tests;

public class ApplicationVersionTests
{
    [Fact]
    public void Current_application_version_is_1_4_5_after_rx_display_lifecycle_fix()
    {
        Assert.Equal("1.4.9", ApplicationVersion.Current);
        Assert.Equal("RadioRelay 1.4.9", ApplicationVersion.DisplayName);
    }
}
