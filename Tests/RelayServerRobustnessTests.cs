using System.Net;
using System.Net.Sockets;
using RadioRelay.Server;
using RadioRelay.Shared.Protocol;

namespace RadioRelay.Tests;

public class RelayServerRobustnessTests
{
    [Fact]
    public async Task RunAsync_ignores_malformed_subscribe_packet_and_keeps_running()
    {
        int port = GetAvailableUdpPort();
        using var cts = new CancellationTokenSource();
        var server = new RelayServer(port);
        var runTask = server.RunAsync(cts.Token);

        try
        {
            await Task.Delay(150);

            using var udp = new UdpClient();
            await udp.SendAsync(new byte[] { 2 }, 1, new IPEndPoint(IPAddress.Loopback, port));

            await Task.Delay(250);

            Assert.False(runTask.IsCompleted, "RelayServer should drop malformed UDP packets instead of letting decode exceptions stop the receive loop.");
        }
        finally
        {
            cts.Cancel();
            await WaitForServerToStop(runTask);
        }
    }

    [Fact]
    public async Task RunAsync_ignores_audio_relay_send_failures_and_keeps_running()
    {
        int port = GetAvailableUdpPort();
        using var cts = new CancellationTokenSource();
        var server = new ThrowingRelayServer(port);
        var runTask = server.RunAsync(cts.Token);

        try
        {
            await Task.Delay(150);

            using var talker = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var talkerId = Guid.NewGuid();
            var listenerId = Guid.NewGuid();

            await SendSubscribe(talker, port, talkerId, 251.000f);
            await SendSubscribe(listener, port, listenerId, 251.000f);
            await Task.Delay(150);

            server.ThrowWhenSendingTo(((IPEndPoint)listener.Client.LocalEndPoint!).Port);
            var audio = new AudioPacket
            {
                ClientId = talkerId,
                Frequency = 251.000f,
                SenderName = "Talker",
                RadioName = "Radio",
                Payload = new byte[] { 1, 2, 3, 4 }
            }.Encode();

            await talker.SendAsync(audio, audio.Length, new IPEndPoint(IPAddress.Loopback, port));
            await Task.Delay(250);

            Assert.False(runTask.IsCompleted, "A send failure for one unreachable listener must not stop the relay server receive loop.");
        }
        finally
        {
            cts.Cancel();
            await WaitForServerToStop(runTask);
        }
    }

    [Fact]
    public async Task Heartbeat_from_different_endpoint_does_not_hijack_registered_client()
    {
        int port = GetAvailableUdpPort();
        using var cts = new CancellationTokenSource();
        var server = new RelayServer(port);
        var runTask = server.RunAsync(cts.Token);

        try
        {
            await Task.Delay(150);

            using var talker = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var attacker = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var talkerId = Guid.NewGuid();

            await SendSubscribe(talker, port, talkerId, 251.000f);
            await SendSubscribe(listener, port, Guid.NewGuid(), 251.000f);
            await SendHeartbeat(attacker, port, talkerId);
            await Task.Delay(150);

            var audio = new AudioPacket
            {
                ClientId = talkerId,
                Frequency = 251.000f,
                SenderName = "Talker",
                RadioName = "Radio",
                Payload = new byte[] { 9, 8, 7, 6 }
            }.Encode();
            await talker.SendAsync(audio, audio.Length, new IPEndPoint(IPAddress.Loopback, port));

            var relayed = await ReceivePacket(listener, PacketType.Audio, TimeSpan.FromSeconds(2));
            Assert.NotNull(relayed);
        }
        finally
        {
            cts.Cancel();
            await WaitForServerToStop(runTask);
        }
    }

    [Fact]
    public async Task Subscribe_from_different_endpoint_does_not_hijack_registered_client()
    {
        int port = GetAvailableUdpPort();
        using var cts = new CancellationTokenSource();
        var server = new RelayServer(port);
        var runTask = server.RunAsync(cts.Token);

        try
        {
            await Task.Delay(150);

            using var talker = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var attacker = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var talkerId = Guid.NewGuid();

            await SendSubscribe(talker, port, talkerId, 251.000f);
            await SendSubscribe(listener, port, Guid.NewGuid(), 251.000f);
            await SendSubscribe(attacker, port, talkerId, 305.000f);
            await Task.Delay(150);

            var audio = new AudioPacket
            {
                ClientId = talkerId,
                Frequency = 251.000f,
                SenderName = "Talker",
                RadioName = "Radio",
                Payload = new byte[] { 5, 6, 7, 8 }
            }.Encode();
            await talker.SendAsync(audio, audio.Length, new IPEndPoint(IPAddress.Loopback, port));

            var relayed = await ReceivePacket(listener, PacketType.Audio, TimeSpan.FromSeconds(2));
            Assert.NotNull(relayed);
            Assert.Equal(2, server.ConnectedClients);
        }
        finally
        {
            cts.Cancel();
            await WaitForServerToStop(runTask);
        }
    }

    [Fact]
    public async Task Disconnect_from_different_endpoint_does_not_remove_registered_client()
    {
        int port = GetAvailableUdpPort();
        using var cts = new CancellationTokenSource();
        var server = new RelayServer(port);
        var runTask = server.RunAsync(cts.Token);

        try
        {
            await Task.Delay(150);

            using var talker = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var attacker = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var talkerId = Guid.NewGuid();

            await SendSubscribe(talker, port, talkerId, 251.000f);
            await SendSubscribe(listener, port, Guid.NewGuid(), 251.000f);
            await SendDisconnect(attacker, port, talkerId);
            await Task.Delay(150);

            Assert.Equal(2, server.ConnectedClients);

            var audio = new AudioPacket
            {
                ClientId = talkerId,
                Frequency = 251.000f,
                SenderName = "Talker",
                RadioName = "Radio",
                Payload = new byte[] { 4, 3, 2, 1 }
            }.Encode();
            await talker.SendAsync(audio, audio.Length, new IPEndPoint(IPAddress.Loopback, port));

            var relayed = await ReceivePacket(listener, PacketType.Audio, TimeSpan.FromSeconds(2));
            Assert.NotNull(relayed);
        }
        finally
        {
            cts.Cancel();
            await WaitForServerToStop(runTask);
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

    private static Task SendDisconnect(UdpClient client, int port, Guid clientId)
    {
        var packet = new HeartbeatPacket { ClientId = clientId }.Encode(PacketType.Disconnect);
        return client.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));
    }

    private static async Task<byte[]?> ReceivePacket(UdpClient client, PacketType packetType, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var receiveTask = client.ReceiveAsync();
            var completed = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromMilliseconds(100)));
            if (completed != receiveTask) continue;

            var result = await receiveTask;
            if (result.Buffer.Length > 0 && PacketPeek.GetType(result.Buffer) == packetType)
                return result.Buffer;
        }

        return null;
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
            catch
            {
                // Preserve the original assertion failure.
            }
        }
    }

    private sealed class ThrowingRelayServer : RelayServer
    {
        private readonly HashSet<int> _throwingPorts = new();

        public ThrowingRelayServer(int port)
            : base(port)
        {
        }

        public void ThrowWhenSendingTo(int port) => _throwingPorts.Add(port);

        protected override void SendDatagram(byte[] data, IPEndPoint to)
        {
            if (_throwingPorts.Contains(to.Port))
                throw new SocketException((int)SocketError.NetworkUnreachable);

            base.SendDatagram(data, to);
        }
    }
}
