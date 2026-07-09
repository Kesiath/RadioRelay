using System.Net;
using RadioRelay.Server;

namespace RadioRelay.Tests;

public class PersistentBanListTests
{
    [Fact]
    public void Load_returns_empty_collection_when_file_is_missing()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "server-banlist.txt");
        var banList = new PersistentBanList(path);

        var loaded = banList.Load();

        Assert.Empty(loaded);
    }

    [Fact]
    public void Save_round_trips_ipv4_and_ipv6_addresses()
    {
        string directory = CreateTempDirectory();
        string path = Path.Combine(directory, "server-banlist.txt");
        var banList = new PersistentBanList(path);
        var addresses = new[] { IPAddress.Loopback, IPAddress.IPv6Loopback };

        banList.Save(addresses);
        var loaded = banList.Load();

        Assert.Contains(IPAddress.Loopback, loaded);
        Assert.Contains(IPAddress.IPv6Loopback, loaded);
        Assert.Equal(2, loaded.Count);
    }

    [Fact]
    public void Load_ignores_invalid_blank_and_comment_lines()
    {
        string directory = CreateTempDirectory();
        string path = Path.Combine(directory, "server-banlist.txt");
        File.WriteAllLines(path, new[]
        {
            "# RadioRelay banlist",
            "",
            "not an ip",
            "127.0.0.1",
            "::1"
        });
        var banList = new PersistentBanList(path);

        var loaded = banList.Load();

        Assert.Contains(IPAddress.Loopback, loaded);
        Assert.Contains(IPAddress.IPv6Loopback, loaded);
        Assert.Equal(2, loaded.Count);
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RadioRelayTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
