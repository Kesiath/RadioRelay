using System.IO;
using System.Net;
using System.Net.Sockets;
using RadioRelay.Server;
using RadioRelay.Shared.Protocol;

namespace RadioRelay.Tests;

public class ServerAdminBanTests
{
    [Fact]
    public void Admin_commands_banlist_ban_and_unban_ip_addresses()
    {
        var server = new RelayServer(0, "", new RelayServerOptions { BanListPath = CreateTempBanListPath() });
        using var writer = new StringWriter();
        var admin = new ServerAdminCommandProcessor(server, writer);

        Assert.True(admin.TryExecute("ban 127.0.0.1"));
        Assert.Contains(IPAddress.Loopback, server.BannedAddresses);
        Assert.Contains("Banned 127.0.0.1", writer.ToString());

        writer.GetStringBuilder().Clear();
        Assert.True(admin.TryExecute("banlist"));
        Assert.Contains("127.0.0.1", writer.ToString());

        writer.GetStringBuilder().Clear();
        Assert.True(admin.TryExecute("unban 127.0.0.1"));
        Assert.DoesNotContain(IPAddress.Loopback, server.BannedAddresses);
        Assert.Contains("Unbanned 127.0.0.1", writer.ToString());
    }

    [Fact]
    public void Admin_ban_and_unban_output_distinguishes_changed_vs_noop()
    {
        var server = new RelayServer(0, "", new RelayServerOptions { BanListPath = CreateTempBanListPath() });
        using var writer = new StringWriter();
        var admin = new ServerAdminCommandProcessor(server, writer);

        Assert.True(admin.TryExecute("ban 127.0.0.1"));
        Assert.Contains("Banned 127.0.0.1", writer.ToString());

        writer.GetStringBuilder().Clear();
        Assert.True(admin.TryExecute("ban 127.0.0.1"));
        Assert.Contains("Already banned 127.0.0.1", writer.ToString());

        writer.GetStringBuilder().Clear();
        Assert.True(admin.TryExecute("unban 127.0.0.1"));
        Assert.Contains("Unbanned 127.0.0.1", writer.ToString());

        writer.GetStringBuilder().Clear();
        Assert.True(admin.TryExecute("unban 127.0.0.1"));
        Assert.Contains("Not banned 127.0.0.1", writer.ToString());
    }

    [Fact]
    public void Ban_and_unban_persist_when_banlist_path_is_configured()
    {
        string directory = Path.Combine(Path.GetTempPath(), "RadioRelayTests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "server-banlist.txt");
        var options = new RelayServerOptions { BanListPath = path };

        var firstServer = new RelayServer(0, "", options);
        firstServer.BanAddress(IPAddress.Loopback);

        var secondServer = new RelayServer(0, "", options);
        Assert.Contains(IPAddress.Loopback, secondServer.BannedAddresses);

        secondServer.UnbanAddress(IPAddress.Loopback);
        var thirdServer = new RelayServer(0, "", options);
        Assert.DoesNotContain(IPAddress.Loopback, thirdServer.BannedAddresses);
    }

    [Fact]
    public async Task Banned_ip_is_rejected_and_unban_allows_subscribe_again()
    {
        int port = GetAvailableUdpPort();
        using var cts = new CancellationTokenSource();
        var server = new RelayServer(port, "", new RelayServerOptions { BanListPath = CreateTempBanListPath() });
        server.BanAddress(IPAddress.Loopback);
        var runTask = server.RunAsync(cts.Token);

        try
        {
            await Task.Delay(150);
            using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            await SendSubscribe(client, port, Guid.NewGuid());

            Assert.False(await ReceivesAck(client, TimeSpan.FromMilliseconds(350)));
            Assert.Equal(0, server.ConnectedClients);

            server.UnbanAddress(IPAddress.Loopback);
            using var unbannedClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            await SendSubscribe(unbannedClient, port, Guid.NewGuid());

            Assert.True(await ReceivesAck(unbannedClient, TimeSpan.FromSeconds(2)));
            Assert.Equal(1, server.ConnectedClients);
        }
        finally
        {
            cts.Cancel();
            await WaitForServerToStop(runTask);
        }
    }

    private static async Task SendSubscribe(UdpClient client, int port, Guid clientId)
    {
        var packet = new SubscribePacket
        {
            ClientId = clientId,
            Callsign = "AdminTest",
            Frequencies = new[] { 251.000f }
        }.Encode();
        await client.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));
    }

    private static async Task<bool> ReceivesAck(UdpClient client, TimeSpan timeout)
    {
        var receiveTask = client.ReceiveAsync();
        var completed = await Task.WhenAny(receiveTask, Task.Delay(timeout));
        if (completed != receiveTask) return false;

        var result = await receiveTask;
        return result.Buffer.Length > 0 && PacketPeek.GetType(result.Buffer) == PacketType.HeartbeatAck;
    }

    private static string CreateTempBanListPath()
    {
        string directory = Path.Combine(Path.GetTempPath(), "RadioRelayTests", Guid.NewGuid().ToString("N"));
        return Path.Combine(directory, "server-banlist.txt");
    }

    private static int GetAvailableUdpPort()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }

    private static async Task WaitForServerToStop(Task runTask)
    {
        var completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(2)));
        if (completed == runTask)
        {
            try { await runTask; }
            catch { }
        }
    }
}
