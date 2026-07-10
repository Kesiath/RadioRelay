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

    [Fact]
    public void Parse_uses_env_file_values_when_no_arguments_given()
    {
        var env = EnvFile.Parse(new[] { "PORT=5060", "PASSWORD=swordfish" });

        var settings = ServerCommandLineSettings.Parse(System.Array.Empty<string>(), 2302, env);

        Assert.Equal(5060, settings.Port);
        Assert.Equal("swordfish", settings.Password);
    }

    [Fact]
    public void Parse_lets_command_line_arguments_override_env_file()
    {
        var env = EnvFile.Parse(new[] { "PORT=5060", "PASSWORD=fromenv" });

        var settings = ServerCommandLineSettings.Parse(new[] { "7000", "fromargs" }, 2302, env);

        Assert.Equal(7000, settings.Port);
        Assert.Equal("fromargs", settings.Password);
    }

    [Fact]
    public void Parse_ignores_out_of_range_env_port()
    {
        var env = EnvFile.Parse(new[] { "PORT=99999" });

        var settings = ServerCommandLineSettings.Parse(System.Array.Empty<string>(), 2302, env);

        Assert.Equal(2302, settings.Port);
    }
}
