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
    public async Task Encrypted_audio_only_forwards_to_matching_net_id_hash_subscribers()
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
            Assert.False(await ReceivesAudio(wrongKeyReceiver, TimeSpan.FromMilliseconds(350)));
        }
        finally
        {
            cts.Cancel();
            await WaitForServerToStop(runTask);
        }
    }

    [Fact]
    public async Task Open_audio_forwards_to_open_subscription_not_keyed_only_subscription()
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
            Assert.False(await ReceivesAudio(keyedOnlyReceiver, TimeSpan.FromMilliseconds(350)));
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
    public async Task Legacy_frequency_only_subscriber_does_not_receive_encrypted_audio()
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

            Assert.False(await ReceivesAudio(legacyReceiver, TimeSpan.FromMilliseconds(350)));
        }
        finally
        {
            cts.Cancel();
            await WaitForServerToStop(runTask);
        }
    }

    private static Task SendSubscribe(UdpClient client, int port, Guid clientId, float frequency, byte[] netIdHash)
    {
        var packet = new SubscribePacket
        {
            ClientId = clientId,
            Callsign = "ForwardingTest",
            Subscriptions = new[] { new PresenceSubscription { Frequency = frequency, NetIdHash = netIdHash } }
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

    private static Task SendAudio(UdpClient client, int port, Guid clientId, float frequency, bool isEncrypted, byte[] netIdHash)
    {
        var audio = new AudioPacket
        {
            ClientId = clientId,
            Frequency = frequency,
            Sequence = 1,
            IsTransmissionStart = true,
            IsEncrypted = isEncrypted,
            NetIdHash = netIdHash,
            Nonce = isEncrypted ? new byte[12] : null,
            Tag = isEncrypted ? new byte[16] : null,
            SenderName = "Sender",
            RadioName = "RADIO 1",
            Payload = new byte[] { 1, 2, 3 }
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
