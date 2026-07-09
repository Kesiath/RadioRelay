using System.IO;
using System.Net;
using System.Net.Sockets;
using RadioRelay.Server;
using RadioRelay.Shared.Protocol;

namespace RadioRelay.Tests;

public class RelayServerStatsTests
{
    [Fact]
    public async Task Stats_count_received_relayed_and_dropped_datagrams()
    {
        int port = GetAvailableUdpPort();
        using var cts = new CancellationTokenSource();
        var server = new RelayServer(port, "", new RelayServerOptions { BanListPath = CreateTempBanListPath() });
        var runTask = server.RunAsync(cts.Token);

        try
        {
            await Task.Delay(150);
            using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var unknownSender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var senderId = Guid.NewGuid();
            var receiverId = Guid.NewGuid();

            await SendSubscribe(sender, port, senderId, "Sender");
            Assert.True(await ReceivesAck(sender, TimeSpan.FromSeconds(2)));
            await SendSubscribe(receiver, port, receiverId, "Receiver");
            Assert.True(await ReceivesAck(receiver, TimeSpan.FromSeconds(2)));

            await SendAudio(sender, port, senderId);
            Assert.True(await ReceivesAudio(receiver, TimeSpan.FromSeconds(2)));

            await SendAudio(unknownSender, port, Guid.NewGuid());
            await Task.Delay(150);

            var stats = server.GetStatsSnapshot();
            Assert.Equal(4, stats.DatagramsReceived);
            Assert.Equal(1, stats.DatagramsRelayed);
            Assert.Equal(1, stats.DatagramsDropped);
        }
        finally
        {
            cts.Cancel();
            await WaitForServerToStop(runTask);
        }
    }

    [Fact]
    public async Task Stats_count_banned_and_auth_rejected_datagrams_as_dropped()
    {
        int port = GetAvailableUdpPort();
        using var cts = new CancellationTokenSource();
        var server = new RelayServer(port, "swordfish", new RelayServerOptions
        {
            BanListPath = CreateTempBanListPath(),
            LogFloodLimitWindow = TimeSpan.Zero
        });
        server.BanAddress(IPAddress.Parse("127.0.0.2"));
        var runTask = server.RunAsync(cts.Token);

        try
        {
            await Task.Delay(150);
            using var badPasswordClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var bannedClient = new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));

            await SendSubscribe(badPasswordClient, port, Guid.NewGuid(), "Bad", serverPassword: "wrong");
            await SendSubscribe(bannedClient, port, Guid.NewGuid(), "Banned", serverPassword: "swordfish");
            await Task.Delay(150);

            var stats = server.GetStatsSnapshot();
            Assert.Equal(2, stats.DatagramsReceived);
            Assert.Equal(0, stats.DatagramsRelayed);
            Assert.Equal(2, stats.DatagramsDropped);
        }
        finally
        {
            cts.Cancel();
            await WaitForServerToStop(runTask);
        }
    }

    private static Task SendSubscribe(UdpClient client, int port, Guid clientId, string callsign, string serverPassword = "")
    {
        var packet = new SubscribePacket
        {
            ClientId = clientId,
            Callsign = callsign,
            ServerPassword = serverPassword,
            Subscriptions = new[] { new PresenceSubscription { Frequency = 251.000f, NetIdHash = new byte[8] } }
        }.Encode();
        return client.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));
    }

    private static Task SendAudio(UdpClient client, int port, Guid clientId)
    {
        var packet = new AudioPacket
        {
            ClientId = clientId,
            Frequency = 251.000f,
            Sequence = 1,
            SenderName = "Sender",
            RadioName = "RADIO 1",
            Payload = new byte[] { 1, 2, 3 }
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

    private static async Task<bool> ReceivesAudio(UdpClient client, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var received = await TryReceiveAsync(client, deadline - DateTime.UtcNow);
            if (received == null) return false;
            if (received.Value.Buffer.Length > 0 && PacketPeek.GetType(received.Value.Buffer) == PacketType.Audio)
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
