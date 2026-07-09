using System.IO;
using System.Net;
using System.Net.Sockets;
using RadioRelay.Server;
using RadioRelay.Shared.Protocol;

namespace RadioRelay.Tests;

public class ServerAdminCommandTests
{
    [Fact]
    public void Clients_command_with_no_clients_prints_no_connected_clients()
    {
        var server = new RelayServer(0, "", new RelayServerOptions { BanListPath = CreateTempBanListPath() });
        using var writer = new StringWriter();
        var admin = new ServerAdminCommandProcessor(server, writer);

        Assert.True(admin.TryExecute("clients"));

        Assert.Contains("No connected clients.", writer.ToString());
    }

    [Fact]
    public async Task Clients_command_lists_connected_client_details()
    {
        int port = GetAvailableUdpPort();
        using var cts = new CancellationTokenSource();
        var server = new RelayServer(port, "", new RelayServerOptions { BanListPath = CreateTempBanListPath() });
        var runTask = server.RunAsync(cts.Token);

        try
        {
            await Task.Delay(150);
            using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var clientId = Guid.NewGuid();
            var key = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            await SendSubscribe(client, port, clientId, "Eagle1", 251.000f, key);
            Assert.True(await ReceivesAck(client, TimeSpan.FromSeconds(2)));

            using var writer = new StringWriter();
            var admin = new ServerAdminCommandProcessor(server, writer);
            Assert.True(admin.TryExecute("clients"));

            var output = writer.ToString();
            Assert.Contains("Eagle1", output);
            Assert.Contains(clientId.ToString("N")[..8], output);
            Assert.Contains("127.0.0.1", output);
            Assert.Contains("251.000", output);
            Assert.Contains("0102030405060708", output);
            Assert.Contains("lastSeen=", output);
        }
        finally
        {
            cts.Cancel();
            await WaitForServerToStop(runTask);
        }
    }

    [Fact]
    public async Task Kick_command_removes_matching_client_by_callsign()
    {
        int port = GetAvailableUdpPort();
        using var cts = new CancellationTokenSource();
        var server = new RelayServer(port, "", new RelayServerOptions { BanListPath = CreateTempBanListPath() });
        var runTask = server.RunAsync(cts.Token);

        try
        {
            await Task.Delay(150);
            using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            await SendSubscribe(client, port, Guid.NewGuid(), "KickMe", 251.000f, new byte[8]);
            Assert.True(await ReceivesAck(client, TimeSpan.FromSeconds(2)));
            Assert.Equal(1, server.ConnectedClients);

            using var writer = new StringWriter();
            var admin = new ServerAdminCommandProcessor(server, writer);
            Assert.True(admin.TryExecute("kick KickMe"));

            Assert.Equal(0, server.ConnectedClients);
            Assert.Contains("Kicked", writer.ToString());
            Assert.Contains("KickMe", writer.ToString());
        }
        finally
        {
            cts.Cancel();
            await WaitForServerToStop(runTask);
        }
    }

    [Fact]
    public async Task Stats_command_prints_server_counts_and_uptime()
    {
        int port = GetAvailableUdpPort();
        using var cts = new CancellationTokenSource();
        var server = new RelayServer(port, "", new RelayServerOptions { BanListPath = CreateTempBanListPath() });
        server.BanAddress(IPAddress.Parse("127.0.0.9"));
        var runTask = server.RunAsync(cts.Token);

        try
        {
            await Task.Delay(150);
            using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            await SendSubscribe(client, port, Guid.NewGuid(), "Stats", 251.000f, new byte[8]);
            Assert.True(await ReceivesAck(client, TimeSpan.FromSeconds(2)));

            using var writer = new StringWriter();
            var admin = new ServerAdminCommandProcessor(server, writer);
            Assert.True(admin.TryExecute("stats"));

            var output = writer.ToString();
            Assert.Contains("Connected clients: 1", output);
            Assert.Contains("Banned IPs: 1", output);
            Assert.Contains("Uptime:", output);
            Assert.Contains("Datagrams received: 1", output);
            Assert.Contains("Datagrams relayed: 0", output);
            Assert.Contains("Datagrams dropped: 0", output);
        }
        finally
        {
            cts.Cancel();
            await WaitForServerToStop(runTask);
        }
    }

    [Fact]
    public void Help_command_lists_all_admin_commands()
    {
        var server = new RelayServer(0, "", new RelayServerOptions { BanListPath = CreateTempBanListPath() });
        using var writer = new StringWriter();
        var admin = new ServerAdminCommandProcessor(server, writer);

        Assert.True(admin.TryExecute("help"));

        var output = writer.ToString();
        Assert.Contains("clients", output);
        Assert.Contains("kick <client|ip>", output);
        Assert.Contains("stats", output);
        Assert.Contains("banlist", output);
        Assert.Contains("ban <ip>", output);
        Assert.Contains("unban <ip>", output);
        Assert.Contains("quit", output);
    }

    private static Task SendSubscribe(UdpClient client, int port, Guid clientId, string callsign, float frequency, byte[] netIdHash)
    {
        var packet = new SubscribePacket
        {
            ClientId = clientId,
            Callsign = callsign,
            Subscriptions = new[] { new PresenceSubscription { Frequency = frequency, NetIdHash = netIdHash } }
        }.Encode();
        return client.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));
    }

    private static async Task<bool> ReceivesAck(UdpClient client, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var received = await TryReceiveAsync(client, deadline - DateTime.UtcNow);
            if (received == null) return false;
            if (received.Value.Buffer.Length > 0 && PacketPeek.GetType(received.Value.Buffer) == PacketType.HeartbeatAck)
                return true;
        }

        return false;
    }

    private static async Task<UdpReceiveResult?> TryReceiveAsync(UdpClient client, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero) return null;
        var receiveTask = client.ReceiveAsync();
        var completed = await Task.WhenAny(receiveTask, Task.Delay(timeout));
        if (completed != receiveTask) return null;

        return await receiveTask;
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
