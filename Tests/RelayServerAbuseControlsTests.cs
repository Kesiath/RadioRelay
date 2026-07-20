using System.IO;
using System.Net;
using System.Net.Sockets;
using RadioRelay.Server;
using RadioRelay.Shared.Protocol;

namespace RadioRelay.Tests;

public class RelayServerAbuseControlsTests
{
    [Fact]
    public async Task Oversized_datagram_is_dropped_before_decode_and_server_keeps_running()
    {
        int port = GetAvailableUdpPort();
        using var cts = new CancellationTokenSource();
        var options = new RelayServerOptions { MaxDatagramBytes = 64 };
        var server = new RelayServer(port, "", options);
        var runTask = server.RunAsync(cts.Token);

        try
        {
            await Task.Delay(150);

            using var udp = new UdpClient();
            var oversized = new byte[options.MaxDatagramBytes + 1];
            oversized[0] = (byte)PacketType.Subscribe;
            await udp.SendAsync(oversized, oversized.Length, new IPEndPoint(IPAddress.Loopback, port));
            await SendSubscribe(udp, port, Guid.NewGuid(), 251.000f);

            Assert.True(await ReceivesAck(udp, TimeSpan.FromSeconds(2)));
            Assert.False(runTask.IsCompleted);
        }
        finally
        {
            cts.Cancel();
            await WaitForServerToStop(runTask);
        }
    }

    [Fact]
    public async Task Registered_clients_behind_same_ip_have_independent_control_buckets()
    {
        int port = GetAvailableUdpPort();
        using var cts = new CancellationTokenSource();
        var server = new RelayServer(port, "", new RelayServerOptions
        {
            MaxControlDatagramsPerClientPerSecond = 1,
            ControlDatagramBurstPerClient = 1
        });
        var runTask = server.RunAsync(cts.Token);

        try
        {
            await Task.Delay(150);

            using var first = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var second = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var firstId = Guid.NewGuid();
            var secondId = Guid.NewGuid();
            await SendSubscribe(first, port, firstId, 251.000f);
            await SendSubscribe(second, port, secondId, 251.000f);
            Assert.True(await ReceivesAck(first, TimeSpan.FromSeconds(2)));
            Assert.True(await ReceivesAck(second, TimeSpan.FromSeconds(2)));

            await SendHeartbeat(first, port, firstId);
            Assert.True(await ReceivesAck(first, TimeSpan.FromSeconds(2)));

            await SendHeartbeat(first, port, firstId);
            await SendHeartbeat(second, port, secondId);

            Assert.False(await ReceivesAck(first, TimeSpan.FromMilliseconds(300)));
            Assert.True(await ReceivesAck(second, TimeSpan.FromSeconds(2)));
            Assert.False(runTask.IsCompleted);
        }
        finally
        {
            cts.Cancel();
            await WaitForServerToStop(runTask);
        }
    }

    [Fact]
    public async Task Audio_bucket_exhaustion_does_not_suppress_control_traffic()
    {
        int port = GetAvailableUdpPort();
        using var cts = new CancellationTokenSource();
        var server = new RelayServer(port, "", new RelayServerOptions
        {
            MaxAudioDatagramsPerClientPerSecond = 1,
            AudioDatagramBurstPerClient = 1
        });
        var runTask = server.RunAsync(cts.Token);

        try
        {
            await Task.Delay(150);
            using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var senderId = Guid.NewGuid();
            var receiverId = Guid.NewGuid();
            await SendSubscribe(sender, port, senderId, 251.000f);
            await SendSubscribe(receiver, port, receiverId, 251.000f);
            Assert.True(await ReceivesAck(sender, TimeSpan.FromSeconds(2)));
            Assert.True(await ReceivesAck(receiver, TimeSpan.FromSeconds(2)));

            await SendAudio(sender, port, senderId, 251.000f, sequence: 0);
            await SendAudio(sender, port, senderId, 251.000f, sequence: 1);
            Assert.Equal(1, await CountAudio(receiver, TimeSpan.FromMilliseconds(300)));

            await SendHeartbeat(sender, port, senderId);
            Assert.True(await ReceivesAck(sender, TimeSpan.FromSeconds(2)));
            Assert.False(runTask.IsCompleted);
        }
        finally
        {
            cts.Cancel();
            await WaitForServerToStop(runTask);
        }
    }

    [Fact]
    public async Task Max_client_count_rejects_new_clients_but_allows_existing_client_updates()
    {
        int port = GetAvailableUdpPort();
        using var cts = new CancellationTokenSource();
        var server = new RelayServer(port, "", new RelayServerOptions { MaxClientCount = 1 });
        var runTask = server.RunAsync(cts.Token);

        try
        {
            await Task.Delay(150);

            using var first = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var second = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var firstId = Guid.NewGuid();

            await SendSubscribe(first, port, firstId, 251.000f);
            Assert.True(await ReceivesAck(first, TimeSpan.FromSeconds(2)));

            await SendSubscribe(second, port, Guid.NewGuid(), 251.000f);
            Assert.False(await ReceivesAck(second, TimeSpan.FromMilliseconds(300)));
            Assert.Equal(1, server.ConnectedClients);

            await SendSubscribe(first, port, firstId, 252.000f);
            Assert.True(await ReceivesAck(first, TimeSpan.FromSeconds(2)));
            Assert.Equal(1, server.ConnectedClients);
        }
        finally
        {
            cts.Cancel();
            await WaitForServerToStop(runTask);
        }
    }

    [Fact]
    public async Task Malformed_and_auth_failure_logs_are_flood_limited_per_source()
    {
        int port = GetAvailableUdpPort();
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        using var cts = new CancellationTokenSource();
        Console.SetOut(writer);
        var server = new RelayServer(port, "swordfish", new RelayServerOptions { LogFloodLimitWindow = TimeSpan.FromMinutes(1) });
        var runTask = server.RunAsync(cts.Token);

        try
        {
            await Task.Delay(150);

            using var udp = new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.3"), 0));
            var source = (IPEndPoint)udp.Client.LocalEndPoint!;
            for (int i = 0; i < 5; i++)
                await udp.SendAsync(new byte[] { (byte)PacketType.Subscribe }, 1, new IPEndPoint(IPAddress.Loopback, port));
            for (int i = 0; i < 5; i++)
            {
                var badAuth = new SubscribePacket
                {
                    ClientId = Guid.NewGuid(),
                    Callsign = "BadAuth",
                    Frequencies = new[] { 251.000f },
                    ServerPassword = "wrong"
                }.Encode();
                await udp.SendAsync(badAuth, badAuth.Length, new IPEndPoint(IPAddress.Loopback, port));
            }
            await Task.Delay(250);

            var output = writer.ToString();
            Assert.Equal(1, CountOccurrences(output, $"Dropped malformed packet from {source}"));
            Assert.Equal(1, CountOccurrences(output, $"Rejected subscribe from {source}"));
        }
        finally
        {
            cts.Cancel();
            await WaitForServerToStop(runTask);
            Console.SetOut(originalOut);
        }
    }

    private static Task SendSubscribe(UdpClient client, int port, Guid clientId, float frequency)
    {
        var packet = new SubscribePacket
        {
            ClientId = clientId,
            Callsign = "Test",
            Frequencies = new[] { frequency }
        }.Encode();
        return client.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));
    }

    private static Task SendHeartbeat(UdpClient client, int port, Guid clientId)
    {
        var packet = new HeartbeatPacket { ClientId = clientId }.Encode();
        return client.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));
    }

    private static Task SendAudio(UdpClient client, int port, Guid clientId, float frequency, ushort sequence)
    {
        var packet = new AudioPacket
        {
            ClientId = clientId,
            Frequency = frequency,
            Sequence = sequence,
            IsTransmissionStart = sequence == 0,
            SenderName = "Test",
            RadioName = "RADIO 1",
            Payload = new byte[] { 1, 2, 3 }
        }.Encode();
        return client.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));
    }

    private static async Task<int> CountAudio(UdpClient client, TimeSpan duration)
    {
        int count = 0;
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            var received = await TryReceiveAsync(client, deadline - DateTime.UtcNow);
            if (received == null) break;
            if (received.Value.Buffer.Length > 0 &&
                PacketPeek.GetType(received.Value.Buffer) == PacketType.Audio)
                count++;
        }
        return count;
    }

    private static async Task<bool> ReceivesAck(UdpClient client, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;

            var received = await TryReceiveAsync(client, remaining);
            if (received == null) return false;
            if (received.Value.Buffer.Length > 0 &&
                PacketPeek.GetType(received.Value.Buffer) == PacketType.HeartbeatAck)
                return true;
        }

        return false;
    }

    private static async Task<UdpReceiveResult?> TryReceiveAsync(UdpClient client, TimeSpan timeout)
    {
        var receiveTask = client.ReceiveAsync();
        var completed = await Task.WhenAny(receiveTask, Task.Delay(timeout));
        if (completed != receiveTask) return null;

        return await receiveTask;
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

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
