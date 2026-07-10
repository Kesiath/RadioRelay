using RadioRelay.Client;

namespace RadioRelay.Tests;

public class ApplicationVersionTests
{
    [Fact]
    public void Current_application_version_is_1_5_5()
    {
        Assert.Equal("1.5.6", ApplicationVersion.Current);
        Assert.Equal("RadioRelay 1.5.6", ApplicationVersion.DisplayName);
    }
}
