using System.Net;
using System.Net.Sockets;
using RadioRelay.Client.Networking;
using RadioRelay.Shared.Protocol;

namespace RadioRelay.Tests;

public class RelayClientNetworkQualityTests
{
    [Fact]
    public void Tracker_reports_heartbeat_loss_ack_age_and_packets_per_second()
    {
        var tracker = new NetworkQualityTracker();
        var start = new DateTime(2026, 7, 8, 21, 0, 0, DateTimeKind.Utc);

        tracker.RecordHeartbeatSent(start);
        tracker.RecordHeartbeatSent(start.AddSeconds(1));
        tracker.RecordAck(start.AddSeconds(2));
        tracker.RecordPacket(start.AddSeconds(2));
        tracker.RecordPacket(start.AddSeconds(2.5));

        var snapshot = tracker.Snapshot(start.AddSeconds(3), RelayConnectionState.Connected);

        Assert.Equal(50, snapshot.PacketLossPercent);
        Assert.Equal(TimeSpan.FromSeconds(1), snapshot.LastServerAckAge);
        Assert.Equal(2, snapshot.PacketsPerSecond);
        Assert.Equal(RelayConnectionState.Connected, snapshot.ConnectionState);
    }

    [Fact]
    public void Tracker_estimates_interarrival_jitter_from_audio_packets()
    {
        var tracker = new NetworkQualityTracker();
        var start = new DateTime(2026, 7, 8, 21, 0, 0, DateTimeKind.Utc);

        tracker.RecordInboundAudio(1, start);
        tracker.RecordInboundAudio(2, start.AddMilliseconds(20));
        tracker.RecordInboundAudio(3, start.AddMilliseconds(55));

        var snapshot = tracker.Snapshot(start.AddMilliseconds(60), RelayConnectionState.Connected);

        Assert.InRange(snapshot.JitterMs, 14, 16);
    }

    [Fact]
    public async Task RelayClient_exposes_connecting_connected_unhealthy_and_disconnected_quality_state()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        using var client = new RelayClient(Guid.NewGuid());
        var qualityChanges = new List<NetworkQualitySnapshot>();
        client.NetworkQualityChanged += qualityChanges.Add;

        client.Connect("127.0.0.1", port);
        Assert.Equal(RelayConnectionState.Connecting, client.QualitySnapshot.ConnectionState);

        var received = await ReceiveFromClient(server);
        var ack = new HeartbeatPacket { ClientId = client.ClientId }.Encode(PacketType.HeartbeatAck);
        await server.SendAsync(ack, ack.Length, received.RemoteEndPoint);
        await WaitUntil(() => client.QualitySnapshot.ConnectionState == RelayConnectionState.Connected);

        InvokeHealthCheck(client);
        Assert.Equal(RelayConnectionState.Connected, client.QualitySnapshot.ConnectionState);

        ForceLastAck(client, DateTime.UtcNow.AddSeconds(-30));
        InvokeHealthCheck(client);
        Assert.Equal(RelayConnectionState.Unhealthy, client.QualitySnapshot.ConnectionState);

        client.Disconnect();
        Assert.Equal(RelayConnectionState.Disconnected, client.QualitySnapshot.ConnectionState);
        Assert.Contains(qualityChanges, s => s.ConnectionState == RelayConnectionState.Connected);
    }

    private static async Task<UdpReceiveResult> ReceiveFromClient(UdpClient server)
    {
        var receiveTask = server.ReceiveAsync();
        var completed = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(receiveTask, completed);
        return await receiveTask;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        Assert.True(condition());
    }

    private static void InvokeHealthCheck(RelayClient client) =>
        typeof(RelayClient).GetMethod("CheckHealth", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(client, null);

    private static void ForceLastAck(RelayClient client, DateTime value) =>
        typeof(RelayClient).GetField("_lastAckReceived", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(client, value);
}
