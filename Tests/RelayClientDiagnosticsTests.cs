using System.Net;
using System.Net.Sockets;
using RadioRelay.Client.Diagnostics;
using RadioRelay.Client.Networking;
using RadioRelay.Shared.Diagnostics;
using RadioRelay.Shared.Protocol;

namespace RadioRelay.Tests;

public class RelayClientDiagnosticsTests
{
    [Fact]
    public void Connect_failure_logs_diagnostics_code()
    {
        var diagnostics = new RecordingClientDiagnostics();
        using var client = new RelayClient(Guid.NewGuid(), diagnostics);

        Assert.ThrowsAny<Exception>(() => client.Connect("definitely-not-a-radiorelay-host.invalid", 25100));

        Assert.Contains(diagnostics.Exceptions, entry => entry.Code == ErrorCodes.ClientConnectFailure && entry.Context.Contains("definitely-not-a-radiorelay-host.invalid"));
    }

    [Fact]
    public async Task Malformed_inbound_packet_logs_diagnostics_code()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var diagnostics = new RecordingClientDiagnostics();
        using var client = new RelayClient(Guid.NewGuid(), diagnostics);

        client.Connect("127.0.0.1", port);
        var received = await ReceiveFromClient(server);
        await server.SendAsync(new byte[] { (byte)PacketType.Audio }, 1, received.RemoteEndPoint);
        await Task.Delay(150);

        Assert.Contains(diagnostics.Exceptions, entry => entry.Code == ErrorCodes.ClientMalformedServerPacket);
    }

    private static async Task<UdpReceiveResult> ReceiveFromClient(UdpClient server)
    {
        var receiveTask = server.ReceiveAsync();
        var completed = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(receiveTask, completed);
        return await receiveTask;
    }

    private sealed class RecordingClientDiagnostics : IClientDiagnostics
    {
        public List<(string Code, string Context, Exception Exception)> Exceptions { get; } = new();
        public void LogLifecycle(string code, string message) { }
        public void LogException(string code, string context, Exception exception) => Exceptions.Add((code, context, exception));
    }
}
