using System.Net;
using System.Net.Sockets;
using RadioRelay.Server;
using RadioRelay.Shared.Protocol;

namespace RadioRelay.Tests;

public class RelayServerPresenceTests
{
    [Fact]
    public async Task Subscribe_broadcasts_presence_counts_grouped_by_frequency_and_key()
    {
        int port = GetAvailableUdpPort();
        using var cts = new CancellationTokenSource();
        var server = new RelayServer(port);
        var runTask = server.RunAsync(cts.Token);

        try
        {
            await Task.Delay(150);
            using var alpha = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var bravo = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var key = new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 };

            await SendSubscribe(alpha, port, Guid.NewGuid(), "Zulu", 251.000f, key);
            await DrainUntilPresence(alpha, expectedCount: 1);

            await SendSubscribe(bravo, port, Guid.NewGuid(), "Alpha", 251.004f, key);

            var alphaPresence = await DrainUntilPresence(alpha, expectedCount: 2);
            var bravoPresence = await DrainUntilPresence(bravo, expectedCount: 2);

            Assert.Contains(alphaPresence.Counts, c => c.Matches(251.000f, key) && c.UserCount == 2);
            Assert.Contains(bravoPresence.Counts, c => c.Matches(251.000f, key) && c.UserCount == 2);
            Assert.Equal(2, alphaPresence.TotalUserCount);
            Assert.Equal(2, bravoPresence.TotalUserCount);
            Assert.Equal(new[] { "Alpha", "Zulu" }, alphaPresence.ConnectedClientNames);
            Assert.Contains(alphaPresence.Counts, count =>
                count.Matches(251.000f, key) && count.ClientNames.SequenceEqual(new[] { "Alpha", "Zulu" }));
        }
        finally
        {
            cts.Cancel();
            await WaitForServerToStop(runTask);
        }
    }

    private static async Task SendSubscribe(UdpClient client, int port, Guid clientId, string callsign, float frequency, byte[] key)
    {
        var packet = new SubscribePacket
        {
            ClientId = clientId,
            Callsign = callsign,
            Subscriptions = new[] { new PresenceSubscription { Frequency = frequency, NetIdHash = key } }
        }.Encode();
        await client.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));
    }

    private static async Task<PresenceUpdatePacket> DrainUntilPresence(UdpClient client, int expectedCount)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            var receiveTask = client.ReceiveAsync();
            var completed = await Task.WhenAny(receiveTask, Task.Delay(100));
            if (completed != receiveTask) continue;

            var result = await receiveTask;
            if (result.Buffer.Length == 0 || PacketPeek.GetType(result.Buffer) != PacketType.PresenceUpdate)
                continue;

            var presence = PresenceUpdatePacket.Decode(result.Buffer);
            if (presence.Counts.Any(c => c.UserCount == expectedCount))
                return presence;
        }

        throw new TimeoutException($"Timed out waiting for presence count {expectedCount}.");
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
