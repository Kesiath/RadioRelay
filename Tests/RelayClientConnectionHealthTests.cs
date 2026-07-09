using System.Net;
using System.Net.Sockets;
using System.Reflection;
using RadioRelay.Client.Networking;
using RadioRelay.Shared.Protocol;

namespace RadioRelay.Tests;

public class RelayClientConnectionHealthTests
{
    [Fact]
    public void Connect_marks_client_unhealthy_until_server_acknowledges()
    {
        int port = GetAvailableUdpPort();
        using var client = new RelayClient(Guid.NewGuid());

        client.Connect("127.0.0.1", port);

        Assert.True(client.IsConnected);
        Assert.False(client.IsHealthy);
    }

    [Fact]
    public async Task Connect_accepts_hostname_and_sends_initial_heartbeat()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        using var client = new RelayClient(Guid.NewGuid());

        client.Connect("localhost", port);

        var received = await ReceiveFromClient(server);
        Assert.Equal(PacketType.Heartbeat, PacketPeek.GetType(received.Buffer));
    }

    [Fact]
    public void Connect_failure_disposes_partial_udp_client_and_leaves_client_disconnected()
    {
        using var client = new RelayClient(Guid.NewGuid());

        Assert.Throws<SocketException>(() => client.Connect("definitely-not-a-radiorelay-host.invalid", 25100));

        Assert.False(client.IsConnected);
        Assert.Null(GetUdpClient(client));
    }

    [Fact]
    public async Task Heartbeat_ack_marks_connection_healthy_after_initial_connect()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        using var client = new RelayClient(Guid.NewGuid());
        var healthyChanged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionHealthChanged += healthy => healthyChanged.TrySetResult(healthy);

        client.Connect("127.0.0.1", port);

        var received = await ReceiveFromClient(server);
        await SendAck(server, received, client.ClientId);

        var completed = await Task.WhenAny(healthyChanged.Task, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(healthyChanged.Task, completed);
        Assert.True(await healthyChanged.Task);
        Assert.True(client.IsHealthy);
    }


    [Fact]
    public async Task Heartbeat_reannounces_cached_subscription_so_restarted_server_can_recreate_client_state()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        using var client = new RelayClient(Guid.NewGuid()) { Callsign = "Rejoin" };

        client.Connect("127.0.0.1", port, "swordfish");
        await ReceivePacketFromClient(server, PacketType.Heartbeat);

        client.SendSubscribe(new[]
        {
            new PresenceSubscription { Frequency = 251.000f, NetIdHash = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 } }
        });
        await ReceivePacketFromClient(server, PacketType.Subscribe);

        InvokeHeartbeat(client);
        var refreshed = await ReceivePacketFromClient(server, PacketType.Subscribe);
        var subscribe = SubscribePacket.Decode(refreshed.Buffer);

        Assert.Equal(client.ClientId, subscribe.ClientId);
        Assert.Equal("Rejoin", subscribe.Callsign);
        Assert.Equal("swordfish", subscribe.ServerPassword);
        Assert.Single(subscribe.Subscriptions);
        Assert.Equal(251.000f, subscribe.Subscriptions[0].Frequency, precision: 3);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, subscribe.Subscriptions[0].NetIdHash);
    }

    [Fact]
    public async Task Heartbeat_ack_for_different_client_is_ignored()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        using var client = new RelayClient(Guid.NewGuid());
        var healthyChanged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionHealthChanged += healthy => healthyChanged.TrySetResult(healthy);

        client.Connect("127.0.0.1", port);

        var received = await ReceiveFromClient(server);
        await SendAck(server, received, Guid.NewGuid());
        var completed = await Task.WhenAny(healthyChanged.Task, Task.Delay(250));

        Assert.NotSame(healthyChanged.Task, completed);
        Assert.False(client.IsHealthy);
    }

    [Fact]
    public async Task Disconnect_clears_healthy_state()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        using var client = new RelayClient(Guid.NewGuid());
        var healthyChanged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionHealthChanged += healthy => healthyChanged.TrySetResult(healthy);

        client.Connect("127.0.0.1", port);
        var received = await ReceiveFromClient(server);
        await SendAck(server, received, client.ClientId);
        await healthyChanged.Task.WaitAsync(TimeSpan.FromSeconds(2));

        client.Disconnect();

        Assert.False(client.IsConnected);
        Assert.False(client.IsHealthy);
    }

    [Fact]
    public async Task Initial_missing_ack_reports_unhealthy_on_health_check()
    {
        int port = GetAvailableUdpPort();
        using var client = new RelayClient(Guid.NewGuid());
        var healthChanged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionHealthChanged += healthy => healthChanged.TrySetResult(healthy);

        client.Connect("127.0.0.1", port);
        InvokeHealthCheck(client);
        var completed = await Task.WhenAny(healthChanged.Task, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(healthChanged.Task, completed);
        Assert.False(await healthChanged.Task);
        Assert.False(client.IsHealthy);
    }

    [Fact]
    public async Task Malformed_inbound_audio_packet_does_not_stop_receive_loop()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        using var client = new RelayClient(Guid.NewGuid());
        var healthyChanged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionHealthChanged += healthy => healthyChanged.TrySetResult(healthy);

        client.Connect("127.0.0.1", port);

        var received = await ReceiveFromClient(server);
        await server.SendAsync(new byte[] { (byte)PacketType.Audio }, 1, received.RemoteEndPoint);
        await SendAck(server, received, client.ClientId);
        var completed = await Task.WhenAny(healthyChanged.Task, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(healthyChanged.Task, completed);
        Assert.True(await healthyChanged.Task);
        Assert.True(client.IsHealthy);
    }

    [Fact]
    public async Task Exception_in_audio_received_callback_does_not_stop_receive_loop()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        using var client = new RelayClient(Guid.NewGuid());
        var healthyChanged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.AudioReceived += _ => throw new InvalidOperationException("UI is shutting down");
        client.ConnectionHealthChanged += healthy => healthyChanged.TrySetResult(healthy);

        client.Connect("127.0.0.1", port);

        var received = await ReceiveFromClient(server);
        var audio = new AudioPacket
        {
            ClientId = Guid.NewGuid(),
            Frequency = 251.000f,
            SenderName = "Thrower",
            RadioName = "Radio",
            Payload = new byte[] { 1, 2, 3 }
        }.Encode();
        await server.SendAsync(audio, audio.Length, received.RemoteEndPoint);
        await SendAck(server, received, client.ClientId);

        var completed = await Task.WhenAny(healthyChanged.Task, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(healthyChanged.Task, completed);
        Assert.True(await healthyChanged.Task);
    }

    private static int GetAvailableUdpPort()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }

    private static async Task<UdpReceiveResult> ReceiveFromClient(UdpClient server)
    {
        var receiveTask = server.ReceiveAsync();
        var completed = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(receiveTask, completed);
        return await receiveTask;
    }

    private static Task SendAck(UdpClient server, UdpReceiveResult received, Guid clientId)
    {
        var ack = new HeartbeatPacket { ClientId = clientId }.Encode(PacketType.HeartbeatAck);
        return server.SendAsync(ack, ack.Length, received.RemoteEndPoint);
    }


    private static async Task<UdpReceiveResult> ReceivePacketFromClient(UdpClient server, PacketType packetType)
    {
        for (int i = 0; i < 5; i++)
        {
            var receiveTask = server.ReceiveAsync();
            var completed = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(receiveTask, completed);

            var result = await receiveTask;
            if (result.Buffer.Length > 0 && PacketPeek.GetType(result.Buffer) == packetType)
                return result;
        }

        throw new TimeoutException($"Timed out waiting for {packetType} from client.");
    }

    private static void InvokeHeartbeat(RelayClient client)
    {
        typeof(RelayClient)
            .GetMethod("SendHeartbeat", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client, null);
    }

    private static void InvokeHealthCheck(RelayClient client)
    {
        typeof(RelayClient)
            .GetMethod("CheckHealth", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client, null);
    }

    private static UdpClient? GetUdpClient(RelayClient client) =>
        (UdpClient?)typeof(RelayClient)
            .GetField("_udp", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client);
}
