using RadioRelay.Server;

namespace RadioRelay.Tests;

public class ServerCommandLineSettingsTests
{
    [Fact]
    public void Parse_accepts_port_and_password_from_terminal_arguments()
    {
        var settings = ServerCommandLineSettings.Parse(new[] { "5060", "swordfish" }, 2302);

        Assert.Equal(5060, settings.Port);
        Assert.Equal("swordfish", settings.Password);
        Assert.False(settings.PortFallbackUsed);
    }

    [Fact]
    public void Parse_accepts_password_without_port()
    {
        var settings = ServerCommandLineSettings.Parse(new[] { "swordfish" }, 2302);

        Assert.Equal(2302, settings.Port);
        Assert.Equal("swordfish", settings.Password);
        Assert.False(settings.PortFallbackUsed);
    }
}
