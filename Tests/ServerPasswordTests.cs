using System.Net;
using System.Net.Sockets;
using RadioRelay.Client.Networking;
using RadioRelay.Server;
using RadioRelay.Shared.Protocol;

namespace RadioRelay.Tests;

public class ServerPasswordTests
{
    [Fact]
    public async Task Protected_server_rejects_subscribe_without_matching_password()
    {
        int port = GetAvailableUdpPort();
        using var cts = new CancellationTokenSource();
        var server = new RelayServer(port, "swordfish");
        var runTask = server.RunAsync(cts.Token);

        try
        {
            await Task.Delay(150);
            using var udp = new UdpClient();
            var packet = new SubscribePacket
            {
                ClientId = Guid.NewGuid(),
                Callsign = "Wrong",
                Frequencies = new[] { 251.000f },
                ServerPassword = "wrong"
            }.Encode();

            await udp.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));

            Assert.Null(await TryReceiveAsync(udp, TimeSpan.FromMilliseconds(300)));
            Assert.Equal(0, server.ConnectedClients);
        }
        finally
        {
            cts.Cancel();
            await WaitForServerToStop(runTask);
        }
    }

    [Fact]
    public async Task Protected_server_accepts_subscribe_with_matching_password()
    {
        int port = GetAvailableUdpPort();
        using var cts = new CancellationTokenSource();
        var server = new RelayServer(port, "swordfish");
        var runTask = server.RunAsync(cts.Token);
        var clientId = Guid.NewGuid();

        try
        {
            await Task.Delay(150);
            using var udp = new UdpClient();
            var packet = new SubscribePacket
            {
                ClientId = clientId,
                Callsign = "Right",
                Frequencies = new[] { 251.000f },
                ServerPassword = "swordfish"
            }.Encode();

            await udp.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));

            var received = await TryReceiveAsync(udp, TimeSpan.FromSeconds(2));
            Assert.NotNull(received);
            var ack = HeartbeatPacket.Decode(received.Value.Buffer);
            Assert.Equal(clientId, ack.ClientId);
            Assert.Equal(1, server.ConnectedClients);
        }
        finally
        {
            cts.Cancel();
            await WaitForServerToStop(runTask);
        }
    }

    [Fact]
    public async Task RelayClient_sends_server_password_on_heartbeat_and_subscribe()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        using var client = new RelayClient(Guid.NewGuid());

        client.Connect("127.0.0.1", port, "swordfish");
        var heartbeat = await ReceiveFromClient(server);
        client.SendSubscribe(new[] { 251.000f });
        var subscribe = await ReceiveFromClient(server);

        Assert.Equal("swordfish", HeartbeatPacket.Decode(heartbeat.Buffer).ServerPassword);
        Assert.Equal("swordfish", SubscribePacket.Decode(subscribe.Buffer).ServerPassword);
    }

    private static int GetAvailableUdpPort()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }

    private static async Task<UdpReceiveResult?> TryReceiveAsync(UdpClient udp, TimeSpan timeout)
    {
        var receiveTask = udp.ReceiveAsync();
        var completed = await Task.WhenAny(receiveTask, Task.Delay(timeout));
        if (completed != receiveTask) return null;
        return await receiveTask;
    }

    private static async Task<UdpReceiveResult> ReceiveFromClient(UdpClient server)
    {
        var receiveTask = server.ReceiveAsync();
        var completed = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(receiveTask, completed);
        return await receiveTask;
    }

    private static async Task WaitForServerToStop(Task runTask)
    {
        var completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(2)));
        if (completed == runTask)
        {
            try { await runTask; }
            catch { /* Preserve the original assertion failure when the server crashed before cancellation. */ }
        }
    }
}
