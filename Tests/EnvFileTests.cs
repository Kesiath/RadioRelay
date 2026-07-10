using RadioRelay.Server;

namespace RadioRelay.Tests;

public class EnvFileTests
{
    [Fact]
    public void Parse_reads_key_value_pairs()
    {
        var env = EnvFile.Parse(new[] { "PORT=5060", "PASSWORD=swordfish" });

        Assert.True(env.TryGetInt("PORT", out var port));
        Assert.Equal(5060, port);
        Assert.Equal("swordfish", env.Get("PASSWORD"));
    }

    [Fact]
    public void Parse_ignores_comments_blank_lines_and_export_prefix()
    {
        var env = EnvFile.Parse(new[]
        {
            "# a comment",
            "",
            "   ",
            "export PORT = 7000 ",
        });

        Assert.True(env.TryGetInt("PORT", out var port));
        Assert.Equal(7000, port);
    }

    [Fact]
    public void Parse_strips_surrounding_quotes_and_is_case_insensitive()
    {
        var env = EnvFile.Parse(new[] { "password=\"secret value\"", "port='8080'" });

        Assert.Equal("secret value", env.Get("PASSWORD"));
        Assert.True(env.TryGetInt("Port", out var port));
        Assert.Equal(8080, port);
    }

    [Fact]
    public void Get_returns_null_for_missing_key()
    {
        Assert.Null(EnvFile.Empty.Get("PASSWORD"));
        Assert.False(EnvFile.Empty.TryGetInt("PORT", out _));
    }
}
