using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RadioRelay.Server;
using RadioRelay.Shared.Protocol;

namespace RadioRelay.Tests;

public class RelayServerForwardingTests
{
    private static readonly byte[] ZeroHash = new byte[8];

    [Fact]
    public async Task Encrypted_ciphertext_forwards_to_every_same_frequency_subscriber()
    {
        int port = GetAvailableUdpPort();
        using var cts = new CancellationTokenSource();
        var server = new RelayServer(port);
        var runTask = server.RunAsync(cts.Token);

        try
        {
            await Task.Delay(150);
            using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var matchingReceiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var wrongKeyReceiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

            var senderId = Guid.NewGuid();
            var matchingReceiverId = Guid.NewGuid();
            var wrongKeyReceiverId = Guid.NewGuid();
            var keyA = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            var keyB = new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 };

            await SendSubscribe(sender, port, senderId, 251.000f, keyA);
            Assert.True(await ReceivesAck(sender, TimeSpan.FromSeconds(2)));
            await SendSubscribe(matchingReceiver, port, matchingReceiverId, 251.004f, keyA);
            Assert.True(await ReceivesAck(matchingReceiver, TimeSpan.FromSeconds(2)));
            await SendSubscribe(wrongKeyReceiver, port, wrongKeyReceiverId, 251.000f, keyB);
            Assert.True(await ReceivesAck(wrongKeyReceiver, TimeSpan.FromSeconds(2)));

            await SendAudio(sender, port, senderId, 251.000f, isEncrypted: true, keyA);

            Assert.True(await ReceivesAudio(matchingReceiver, TimeSpan.FromSeconds(2)));
            Assert.True(await ReceivesAudio(wrongKeyReceiver, TimeSpan.FromSeconds(2)));
        }
        finally
        {
            cts.Cancel();
            await WaitForServerToStop(runTask);
        }
    }

    [Fact]
    public async Task Open_audio_forwards_to_every_same_frequency_subscription()
    {
        int port = GetAvailableUdpPort();
        using var cts = new CancellationTokenSource();
        var server = new RelayServer(port);
        var runTask = server.RunAsync(cts.Token);

        try
        {
            await Task.Delay(150);
            using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var openReceiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var keyedOnlyReceiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

            var senderId = Guid.NewGuid();
            var openReceiverId = Guid.NewGuid();
            var keyedOnlyReceiverId = Guid.NewGuid();
            var keyB = new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 };

            await SendSubscribe(sender, port, senderId, 251.000f, ZeroHash);
            Assert.True(await ReceivesAck(sender, TimeSpan.FromSeconds(2)));
            await SendSubscribe(openReceiver, port, openReceiverId, 251.004f, ZeroHash);
            Assert.True(await ReceivesAck(openReceiver, TimeSpan.FromSeconds(2)));
            await SendSubscribe(keyedOnlyReceiver, port, keyedOnlyReceiverId, 251.000f, keyB);
            Assert.True(await ReceivesAck(keyedOnlyReceiver, TimeSpan.FromSeconds(2)));

            await SendAudio(sender, port, senderId, 251.000f, isEncrypted: false, ZeroHash);

            Assert.True(await ReceivesAudio(openReceiver, TimeSpan.FromSeconds(2)));
            Assert.True(await ReceivesAudio(keyedOnlyReceiver, TimeSpan.FromSeconds(2)));
        }
        finally
        {
            cts.Cancel();
            await WaitForServerToStop(runTask);
        }
    }

    [Fact]
    public async Task Legacy_frequency_only_subscriber_receives_open_audio()
    {
        int port = GetAvailableUdpPort();
        using var cts = new CancellationTokenSource();
        var server = new RelayServer(port);
        var runTask = server.RunAsync(cts.Token);

        try
        {
            await Task.Delay(150);
            using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var legacyReceiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

            var senderId = Guid.NewGuid();
            var legacyReceiverId = Guid.NewGuid();

            await SendSubscribe(sender, port, senderId, 251.000f, ZeroHash);
            Assert.True(await ReceivesAck(sender, TimeSpan.FromSeconds(2)));
            await SendLegacySubscribe(legacyReceiver, port, legacyReceiverId, 251.004f);
            Assert.True(await ReceivesAck(legacyReceiver, TimeSpan.FromSeconds(2)));

            await SendAudio(sender, port, senderId, 251.000f, isEncrypted: false, ZeroHash);

            Assert.True(await ReceivesAudio(legacyReceiver, TimeSpan.FromSeconds(2)));
        }
        finally
        {
            cts.Cancel();
            await WaitForServerToStop(runTask);
        }
    }

    [Fact]
    public async Task Legacy_frequency_only_subscriber_receives_encrypted_carrier_ciphertext()
    {
        int port = GetAvailableUdpPort();
        using var cts = new CancellationTokenSource();
        var server = new RelayServer(port);
        var runTask = server.RunAsync(cts.Token);

        try
        {
            await Task.Delay(150);
            using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var legacyReceiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

            var senderId = Guid.NewGuid();
            var legacyReceiverId = Guid.NewGuid();
            var keyA = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

            await SendSubscribe(sender, port, senderId, 251.000f, keyA);
            Assert.True(await ReceivesAck(sender, TimeSpan.FromSeconds(2)));
            await SendLegacySubscribe(legacyReceiver, port, legacyReceiverId, 251.004f);
            Assert.True(await ReceivesAck(legacyReceiver, TimeSpan.FromSeconds(2)));

            await SendAudio(sender, port, senderId, 251.000f, isEncrypted: true, keyA);

            Assert.True(await ReceivesAudio(legacyReceiver, TimeSpan.FromSeconds(2)));
        }
        finally
        {
            cts.Cancel();
            await WaitForServerToStop(runTask);
        }
    }

    [Fact]
    public async Task Transmission_identity_latches_route_but_allows_distinct_radio_epochs()
    {
        int port = GetAvailableUdpPort();
        using var cts = new CancellationTokenSource();
        var server = new RelayServer(port);
        var runTask = server.RunAsync(cts.Token);

        try
        {
            await Task.Delay(150);
            using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var firstReceiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var secondReceiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var senderId = Guid.NewGuid();
            var firstReceiverId = Guid.NewGuid();
            var secondReceiverId = Guid.NewGuid();

            await SendSubscribe(sender, port, senderId,
                new PresenceSubscription { Frequency = 251.000f, NetIdHash = ZeroHash },
                new PresenceSubscription { Frequency = 305.000f, NetIdHash = ZeroHash });
            await SendSubscribe(firstReceiver, port, firstReceiverId, 251.000f, ZeroHash);
            await SendSubscribe(secondReceiver, port, secondReceiverId, 305.000f, ZeroHash);
            Assert.True(await ReceivesAck(sender, TimeSpan.FromSeconds(2)));
            Assert.True(await ReceivesAck(firstReceiver, TimeSpan.FromSeconds(2)));
            Assert.True(await ReceivesAck(secondReceiver, TimeSpan.FromSeconds(2)));

            const ulong firstTransmissionId = 0x1001;
            const ulong secondTransmissionId = 0x1002;
            await SendAudio(sender, port, senderId, 251.000f, false, ZeroHash,
                transmissionId: firstTransmissionId, sequence: 1, isStart: true);
            Assert.True(await ReceivesAudio(firstReceiver, TimeSpan.FromSeconds(2)));

            long droppedBeforeMutation = server.GetStatsSnapshot().DatagramsDropped;
            await SendAudio(sender, port, senderId, 305.000f, false, ZeroHash,
                transmissionId: firstTransmissionId, sequence: 2, isStart: true);
            await Task.Delay(100);
            Assert.Equal(droppedBeforeMutation + 1, server.GetStatsSnapshot().DatagramsDropped);

            await SendAudio(sender, port, senderId, 305.000f, false, ZeroHash,
                transmissionId: secondTransmissionId, sequence: 1, isStart: true,
                radioName: "RADIO 2");
            Assert.True(await ReceivesAudio(secondReceiver, TimeSpan.FromSeconds(2)));
        }
        finally
        {
            cts.Cancel();
            await WaitForServerToStop(runTask);
        }
    }

    [Fact]
    public async Task Ended_transmission_rejects_media_past_terminal_but_relays_redundant_end_controls()
    {
        int port = GetAvailableUdpPort();
        using var cts = new CancellationTokenSource();
        var server = new RelayServer(port);
        var runTask = server.RunAsync(cts.Token);

        try
        {
            await Task.Delay(150);
            using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var senderId = Guid.NewGuid();
            var receiverId = Guid.NewGuid();
            const ulong transmissionId = 0x2001;

            await SendSubscribe(sender, port, senderId, 251.000f, ZeroHash);
            await SendSubscribe(receiver, port, receiverId, 251.000f, ZeroHash);
            Assert.True(await ReceivesAck(sender, TimeSpan.FromSeconds(2)));
            Assert.True(await ReceivesAck(receiver, TimeSpan.FromSeconds(2)));

            await SendAudio(sender, port, senderId, 251.000f, false, ZeroHash,
                transmissionId: transmissionId, sequence: 7, isStart: true);
            Assert.True(await ReceivesAudio(receiver, TimeSpan.FromSeconds(2)));

            for (int i = 0; i < 3; i++)
                await SendAudio(sender, port, senderId, 251.000f, false, ZeroHash,
                    transmissionId: transmissionId, sequence: 7, isStart: false, isEnd: true);
            Assert.Equal(3, await CountAudio(receiver, TimeSpan.FromMilliseconds(300)));

            // Only media beyond the terminal sequence proves epoch reuse.
            long droppedBeforeRestart = server.GetStatsSnapshot().DatagramsDropped;
            await SendAudio(sender, port, senderId, 251.000f, false, ZeroHash,
                transmissionId: transmissionId, sequence: 8, isStart: true);
            await Task.Delay(100);
            Assert.Equal(droppedBeforeRestart + 1, server.GetStatsSnapshot().DatagramsDropped);
        }
        finally
        {
            cts.Cancel();
            await WaitForServerToStop(runTask);
        }
    }

    [Fact]
    public async Task Reordered_end_allows_first_three_start_marked_media_through_terminal_during_grace()
    {
        int port = GetAvailableUdpPort();
        using var cts = new CancellationTokenSource();
        var server = new RelayServer(port);
        var runTask = server.RunAsync(cts.Token);

        try
        {
            await Task.Delay(150);
            using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var senderId = Guid.NewGuid();
            var receiverId = Guid.NewGuid();
            const ulong transmissionId = 0x3001;

            await SendSubscribe(sender, port, senderId, 251.000f, ZeroHash);
            await SendSubscribe(receiver, port, receiverId, 251.000f, ZeroHash);
            Assert.True(await ReceivesAck(sender, TimeSpan.FromSeconds(2)));
            Assert.True(await ReceivesAck(receiver, TimeSpan.FromSeconds(2)));

            // Final media remains valid when UDP delivers End first.
            await SendAudio(sender, port, senderId, 251.000f, false, ZeroHash,
                transmissionId: transmissionId, sequence: 3, isStart: false, isEnd: true);
            Assert.True(await ReceivesAudio(receiver, TimeSpan.FromSeconds(2)));

            for (ushort sequence = 1; sequence <= 3; sequence++)
            {
                await SendAudio(sender, port, senderId, 251.000f, false, ZeroHash,
                    transmissionId: transmissionId, sequence: sequence, isStart: true);
            }

            var reorderedMedia = await ReceiveAudioPackets(
                receiver,
                expectedCount: 3,
                TimeSpan.FromSeconds(1));
            Assert.Equal(new ushort[] { 1, 2, 3 }, reorderedMedia.Select(packet => packet.Sequence));
            Assert.All(reorderedMedia, packet =>
            {
                Assert.Equal(transmissionId, packet.TransmissionId);
                Assert.True(packet.IsTransmissionStart);
                Assert.False(packet.IsTransmissionEnd);
            });

            long droppedBeforePastTerminal = server.GetStatsSnapshot().DatagramsDropped;
            await SendAudio(sender, port, senderId, 251.000f, false, ZeroHash,
                transmissionId: transmissionId, sequence: 4, isStart: false);
            await Task.Delay(100);
            Assert.Equal(droppedBeforePastTerminal + 1, server.GetStatsSnapshot().DatagramsDropped);
        }
        finally
        {
            cts.Cancel();
            await WaitForServerToStop(runTask);
        }
    }

    private static Task SendSubscribe(UdpClient client, int port, Guid clientId, float frequency, byte[] netIdHash)
        => SendSubscribe(client, port, clientId,
            new PresenceSubscription { Frequency = frequency, NetIdHash = netIdHash });

    private static Task SendSubscribe(
        UdpClient client,
        int port,
        Guid clientId,
        params PresenceSubscription[] subscriptions)
    {
        var packet = new SubscribePacket
        {
            ClientId = clientId,
            Callsign = "ForwardingTest",
            Subscriptions = subscriptions
        }.Encode();
        return client.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));
    }

    private static Task SendLegacySubscribe(UdpClient client, int port, Guid clientId, float frequency)
    {
        var packet = EncodeLegacySubscribe(clientId, frequency, "LegacyTest");
        return client.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));
    }

    private static byte[] EncodeLegacySubscribe(Guid clientId, float frequency, string callsign)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write((byte)PacketType.Subscribe);
        writer.Write(clientId.ToByteArray());
        writer.Write((byte)1);
        writer.Write(frequency);
        var callsignBytes = Encoding.UTF8.GetBytes(callsign);
        writer.Write((byte)callsignBytes.Length);
        writer.Write(callsignBytes);
        return ms.ToArray();
    }

    private static Task SendAudio(
        UdpClient client,
        int port,
        Guid clientId,
        float frequency,
        bool isEncrypted,
        byte[] netIdHash,
        ulong transmissionId = 0,
        ushort sequence = 1,
        bool isStart = true,
        bool isEnd = false,
        string radioName = "RADIO 1")
    {
        var audio = new AudioPacket
        {
            ClientId = clientId,
            Frequency = frequency,
            Sequence = sequence,
            IsTransmissionStart = isStart,
            IsTransmissionEnd = isEnd,
            IsEncrypted = isEncrypted,
            NetIdHash = netIdHash,
            Nonce = isEncrypted ? new byte[12] : null,
            Tag = isEncrypted ? new byte[16] : null,
            SenderName = "Sender",
            RadioName = radioName,
            TransmissionId = transmissionId,
            Payload = isEnd ? Array.Empty<byte>() : new byte[] { 1, 2, 3 }
        }.Encode();
        return client.SendAsync(audio, audio.Length, new IPEndPoint(IPAddress.Loopback, port));
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

    private static async Task<IReadOnlyList<AudioPacket>> ReceiveAudioPackets(
        UdpClient client,
        int expectedCount,
        TimeSpan timeout)
    {
        var packets = new List<AudioPacket>(expectedCount);
        var deadline = DateTime.UtcNow + timeout;
        while (packets.Count < expectedCount && DateTime.UtcNow < deadline)
        {
            var received = await TryReceiveAsync(client, deadline - DateTime.UtcNow);
            if (received == null) break;
            if (received.Value.Buffer.Length > 0 &&
                PacketPeek.GetType(received.Value.Buffer) == PacketType.Audio)
            {
                packets.Add(AudioPacket.Decode(received.Value.Buffer));
            }
        }

        return packets;
    }

    private static async Task<UdpReceiveResult?> TryReceiveAsync(UdpClient client, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero) return null;
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
}
