using System.Net;
using System.Net.Sockets;
using RadioRelay.Client.Networking;
using RadioRelay.Shared.Protocol;

namespace RadioRelay.Tests;

public class RelayClientPresenceTests
{
    [Fact]
    public async Task Presence_update_packet_raises_presence_updated_event()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        using var client = new RelayClient(Guid.NewGuid());
        var updated = new TaskCompletionSource<PresenceChannelCount[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PresenceUpdated += counts => updated.TrySetResult(counts);

        client.Connect("127.0.0.1", port);
        var received = await ReceiveFromClient(server);
        var packet = new PresenceUpdatePacket
        {
            Counts = new[]
            {
                new PresenceChannelCount { Frequency = 251.000f, NetIdHash = new byte[8], UserCount = 4 }
            }
        }.Encode();
        await server.SendAsync(packet, packet.Length, received.RemoteEndPoint);

        var completed = await Task.WhenAny(updated.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(updated.Task, completed);
        Assert.Single(await updated.Task);
        Assert.Equal(4, (await updated.Task)[0].UserCount);
    }

    private static async Task<UdpReceiveResult> ReceiveFromClient(UdpClient server)
    {
        var receiveTask = server.ReceiveAsync();
        var completed = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(receiveTask, completed);
        return await receiveTask;
    }
}
